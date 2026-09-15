using System.Net;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal sealed class DolaSession : IDisposable
{
    private const string DolaUrl = "https://www.dola.com/chat";

    private readonly string _videoDir;
    private readonly Action<string> _log;
    private readonly Action<VideoJob> _jobChanged;
    private TaskCompletionSource<string>? _resultWaiter;
    private Guid? _activeJobId;
    private CoreWebView2Environment? _environment;

    public int Number { get; }
    public string Name { get; }
    public string ProfileDir { get; }
    public string ProxyUrl { get; }
    public string? LastObservedIp { get; private set; }
    public WebView2 Browser { get; } = new() { Dock = DockStyle.Fill };
    public bool IsBusy { get; private set; }
    public bool IsReady => Browser.CoreWebView2 is not null;

    public DolaSession(
        int number,
        string name,
        string profileDir,
        string proxyUrl,
        string videoDir,
        Action<string> log,
        Action<VideoJob> jobChanged)
    {
        Number = number;
        Name = name;
        ProfileDir = profileDir;
        ProxyUrl = NormalizeProxyAddress(proxyUrl);
        _videoDir = videoDir;
        _log = log;
        _jobChanged = jobChanged;
    }

    public static string NormalizeProxyAddress(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("代理地址格式不正确。示例：http://127.0.0.1:7890 或 socks5://127.0.0.1:1080");

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks5"))
            throw new InvalidOperationException("仅支持 http、https、socks4、socks5 固定代理。");

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            throw new InvalidOperationException("代理地址必须包含主机和端口。");

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            throw new InvalidOperationException("当前版本不把代理账号密码写入浏览器命令行。请使用无认证固定代理，或使用本机代理客户端提供的本地端口。");

        return $"{scheme}://{uri.Host}:{uri.Port}";
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ProfileDir);

        var options = new CoreWebView2EnvironmentOptions();
        var browserArguments = new List<string> { "--autoplay-policy=no-user-gesture-required" };
        if (!string.IsNullOrWhiteSpace(ProxyUrl))
            browserArguments.Add($"--proxy-server={ProxyUrl}");
        options.AdditionalBrowserArguments = string.Join(" ", browserArguments);

        _environment = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
        await Browser.EnsureCoreWebView2Async(_environment);
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.WebMessageReceived += BrowserOnWebMessageReceived;
        Browser.CoreWebView2.NavigationCompleted += BrowserOnNavigationCompleted;
        Browser.Source = new Uri(DolaUrl);
    }

    public void NavigateHome()
    {
        Browser.CoreWebView2?.Navigate(DolaUrl);
    }

    public async Task<int> RepairVideoPreviewsAsync(bool force = true)
    {
        if (Browser.CoreWebView2 is null) return 0;
        await InstallBridgeAsync();
        var result = await Browser.CoreWebView2.ExecuteScriptAsync(
            $"window.AIStudioBridge && window.AIStudioBridge.repair({(force ? "true" : "false")})");
        return int.TryParse(result, out var changed) ? changed : 0;
    }

    public async Task<string> TestNetworkAsync(CancellationToken cancellationToken = default)
    {
        using var handler = CreateHttpHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AIVideoBatchStudio/1.0");
        var result = (await client.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
        if (result.Length > 100) result = result[..100];
        LastObservedIp = result;
        return result;
    }

    public async Task RunJobAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException($"{Name} 正在执行其他任务。");
        IsBusy = true;
        job.Worker = Name;
        job.Status = JobStatus.Running;
        job.Error = null;
        _jobChanged(job);
        Log($"开始任务 {job.Id}");

        try
        {
            var remoteUrl = await SubmitAndWaitAsync(job, cancellationToken);
            job.RemoteUrl = remoteUrl;
            job.Status = JobStatus.Downloading;
            _jobChanged(job);

            var localPath = await DownloadVideoAsync(remoteUrl, job.Id, cancellationToken);
            job.LocalPath = localPath;
            job.Status = JobStatus.Completed;
            _jobChanged(job);
            Log($"完成 {job.Id} -> {localPath}");
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "任务已取消或等待超时";
            _jobChanged(job);
            Log($"取消 {job.Id}");
            throw;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            _jobChanged(job);
            Log($"失败 {job.Id}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void BrowserOnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        try
        {
            await InstallBridgeAsync();
            Log("页面监听脚本已注入。");
        }
        catch (Exception ex)
        {
            Log("监听脚本注入失败: " + ex.Message);
        }
    }

    private async Task InstallBridgeAsync()
    {
        if (Browser.CoreWebView2 is null) return;
        await Browser.CoreWebView2.ExecuteScriptAsync(MainForm.BridgeScript);
    }

    private async Task EnsureVideoModeAsync(CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null) return;
        var result = await Browser.CoreWebView2.ExecuteScriptAsync("""
            (() => {
              const visible = el => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
              };
              const textOf = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const nodes = [...document.querySelectorAll('button,a,[role="button"]')].filter(visible);
              const target = nodes.find(x => /^(视频生成|生成视频|video generation|generate video|video)$/i.test(textOf(x))) ||
                             nodes.find(x => /视频生成|生成视频|video generation|generate video/i.test(textOf(x)));
              if (!target) return 'NOT_FOUND';
              target.click();
              return 'CLICKED';
            })();
            """);

        if (result.Contains("CLICKED", StringComparison.OrdinalIgnoreCase))
        {
            Log("已切换/确认 Dola 视频生成模式。 ");
            await Task.Delay(650, cancellationToken);
        }
        else
        {
            Log("未检测到独立的视频生成入口，将使用当前 Dola 页面直接提交 Prompt。 ");
        }
    }

    private async Task<string> SubmitAndWaitAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException($"{Name} 尚未初始化。");
        if (_resultWaiter is not null)
            throw new InvalidOperationException($"{Name} 已有任务正在等待结果。");

        var current = Browser.CoreWebView2.Source ?? string.Empty;
        if (!current.Contains("dola.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前窗口不在 Dola 页面。请先完成登录并回到 Dola。 ");

        _activeJobId = job.Id;
        _resultWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await EnsureVideoModeAsync(cancellationToken);
            await InstallBridgeAsync();
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.AIStudioBridge && window.AIStudioBridge.reset({JsonSerializer.Serialize(job.Id.ToString())});");

            var promptJson = JsonSerializer.Serialize(job.Prompt);
            var submitJs = $$"""
                (() => {
                    const prompt = {{promptJson}};
                    const visible = el => {
                      const r = el.getBoundingClientRect();
                      const s = getComputedStyle(el);
                      return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
                    };
                    const inputs = [...document.querySelectorAll('textarea,[contenteditable="true"]')]
                      .filter(el => visible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true');
                    const input = inputs.find(el => /消息|prompt|message|描述|想法/i.test((el.getAttribute('placeholder') || el.getAttribute('aria-label') || ''))) || inputs.at(-1);
                    if (!input) return 'NO_PROMPT_INPUT';

                    input.focus();
                    if ('value' in input) {
                        const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set ||
                                       Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                        if (setter) setter.call(input, prompt); else input.value = prompt;
                        input.dispatchEvent(new Event('input', { bubbles: true }));
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    } else {
                        input.textContent = prompt;
                        input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: prompt }));
                    }

                    const root = input.closest('form') || input.parentElement?.parentElement?.parentElement || document;
                    const buttons = [...root.querySelectorAll('button')].filter(b => visible(b) && !b.disabled && b.getAttribute('aria-disabled') !== 'true');
                    let submit = root.querySelector('button[type="submit"]:not([disabled])') ||
                      buttons.find(b => /发送|提交|生成|send|submit|generate|create/i.test((b.innerText || b.getAttribute('aria-label') || b.getAttribute('title') || '').trim()));

                    if (!submit) {
                      const iconButtons = buttons.filter(b => {
                        const label = (b.getAttribute('aria-label') || b.getAttribute('title') || '').trim();
                        return /发送|send|submit/i.test(label) || b.querySelector('svg');
                      });
                      submit = iconButtons.at(-1) || buttons.at(-1);
                    }

                    if (submit) {
                      submit.click();
                      return 'OK_BUTTON';
                    }

                    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                    return 'OK_ENTER';
                })();
                """;

            var result = await Browser.CoreWebView2.ExecuteScriptAsync(submitJs);
            Log("Prompt 提交结果: " + result);
            if (result.Contains("NO_PROMPT_INPUT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("没有找到 Dola Prompt 输入框，请确认已经登录并进入 Dola 创作页面。 ");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            using var registration = timeout.Token.Register(() => _resultWaiter.TrySetCanceled(timeout.Token));
            return await _resultWaiter.Task;
        }
        finally
        {
            _resultWaiter = null;
            _activeJobId = null;
        }
    }

    private void BrowserOnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var text = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (type == "log")
            {
                if (root.TryGetProperty("message", out var msg)) Log(msg.GetString() ?? string.Empty);
                return;
            }

            if (type == "previewRepaired")
            {
                var count = root.TryGetProperty("count", out var countEl) && countEl.TryGetInt32(out var value) ? value : 0;
                Log($"已修复 Dola 页面视频预览：{count} 个播放器。");
                return;
            }

            if (type != "videoCandidate") return;
            var taskId = root.TryGetProperty("taskId", out var taskEl) ? taskEl.GetString() : null;
            var url = root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            var source = root.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : "unknown";
            var score = root.TryGetProperty("score", out var scoreEl) && scoreEl.TryGetInt32(out var scoreValue) ? scoreValue : 0;
            if (_activeJobId?.ToString() != taskId || string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

            Log($"捕获视频地址 score={score} source={source}: {url}");
            _resultWaiter?.TrySetResult(url);
        }
        catch (Exception ex)
        {
            Log("页面消息解析失败: " + ex.Message);
        }
    }

    private HttpClientHandler CreateHttpHandler()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        if (!string.IsNullOrWhiteSpace(ProxyUrl))
        {
            handler.Proxy = new WebProxy(new Uri(ProxyUrl));
            handler.UseProxy = true;
        }

        return handler;
    }

    private async Task<string> DownloadVideoAsync(string remoteUrl, Guid jobId, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadVideoAttemptAsync(remoteUrl, jobId, attempt, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"视频下载第 {attempt}/3 次失败: {ex.Message}");
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        throw new InvalidOperationException("视频下载失败，已重试 3 次。", lastError);
    }

    private async Task<string> DownloadVideoAttemptAsync(
        string remoteUrl,
        Guid jobId,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException("浏览器未初始化。");

        var extension = ".mp4";
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8 && !ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                extension = ext;
        }

        var target = Path.Combine(_videoDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Name.Replace(' ', '_')}_{jobId:N}{extension}");
        var temp = target + $".part{attempt}";
        try
        {
            using var handler = CreateHttpHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(remoteUrl);
            if (cookies.Count > 0)
            {
                var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "video/mp4,video/*;q=0.9,*/*;q=0.7");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.dola.com/");

            using var response = await client.GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"下载地址返回了 {mediaType}，不是视频文件。 ");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temp))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var length = new FileInfo(temp).Length;
            if (length < 10 * 1024)
                throw new InvalidOperationException($"下载文件过小（{length} bytes），可能不是完整视频。 ");

            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private void Log(string message) => _log($"[{Name}] {message}");

    public void Dispose()
    {
        try
        {
            if (Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.WebMessageReceived -= BrowserOnWebMessageReceived;
                Browser.CoreWebView2.NavigationCompleted -= BrowserOnNavigationCompleted;
            }
        }
        catch
        {
            // Ignore disposal-time WebView2 errors.
        }
        Browser.Dispose();
        _environment = null;
    }
}

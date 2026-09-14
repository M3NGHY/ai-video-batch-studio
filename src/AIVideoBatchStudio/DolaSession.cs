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
        if (!string.IsNullOrWhiteSpace(ProxyUrl))
            options.AdditionalBrowserArguments = $"--proxy-server={ProxyUrl}";

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

    private async Task<string> SubmitAndWaitAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException($"{Name} 尚未初始化。");
        if (_resultWaiter is not null)
            throw new InvalidOperationException($"{Name} 已有任务正在等待结果。");

        _activeJobId = job.Id;
        _resultWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await InstallBridgeAsync();
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.AIStudioBridge && window.AIStudioBridge.reset({JsonSerializer.Serialize(job.Id.ToString())});");

            var promptJson = JsonSerializer.Serialize(job.Prompt);
            var submitJs = $$"""
                (() => {
                    const prompt = {{promptJson}};
                    const input = document.querySelector('textarea') || document.querySelector('[contenteditable="true"]');
                    if (!input) return 'NO_PROMPT_INPUT';
                    input.focus();
                    if ('value' in input) {
                        const proto = Object.getPrototypeOf(input);
                        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                        if (setter) setter.call(input, prompt); else input.value = prompt;
                        input.dispatchEvent(new Event('input', { bubbles: true }));
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    } else {
                        input.textContent = prompt;
                        input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: prompt }));
                    }
                    const buttons = [...document.querySelectorAll('button')];
                    const submit = document.querySelector('button[type="submit"]') ||
                        buttons.find(b => /生成|创建|发送|generate|create|send/i.test((b.innerText || b.getAttribute('aria-label') || '').trim()));
                    if (!submit) return 'NO_SUBMIT_BUTTON';
                    submit.click();
                    return 'OK';
                })();
                """;

            var result = await Browser.CoreWebView2.ExecuteScriptAsync(submitJs);
            Log("提交结果: " + result);
            if (result.Contains("NO_PROMPT_INPUT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("没有找到 Dola 提示词输入框，请确认已经进入创作页面。");
            if (result.Contains("NO_SUBMIT_BUTTON", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("没有找到生成/发送按钮，Dola 页面结构可能已变化。");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
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

            if (type != "videoCandidate") return;
            var taskId = root.TryGetProperty("taskId", out var taskEl) ? taskEl.GetString() : null;
            var url = root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (_activeJobId?.ToString() != taskId || string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            if (url.Contains("h265", StringComparison.OrdinalIgnoreCase) || url.Contains("hevc", StringComparison.OrdinalIgnoreCase)) return;

            Log("捕获视频地址: " + url);
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
        if (Browser.CoreWebView2 is null)
            throw new InvalidOperationException("浏览器未初始化。");

        var extension = ".mp4";
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8 && !ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                extension = ext;
        }

        var target = Path.Combine(_videoDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{jobId:N}{extension}");
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
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.dola.com/");

        using var response = await client.GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(target);
        await input.CopyToAsync(output, cancellationToken);
        return target;
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

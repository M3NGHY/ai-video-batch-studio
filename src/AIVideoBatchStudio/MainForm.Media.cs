using System.Net;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    private async Task InstallBridgeAsync()
    {
        if (_browser.CoreWebView2 is null) return;
        await _browser.CoreWebView2.ExecuteScriptAsync(BridgeScript);
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

    private async Task<string> DownloadVideoAsync(string remoteUrl, Guid jobId, CancellationToken cancellationToken)
    {
        if (_browser.CoreWebView2 is null) throw new InvalidOperationException("浏览器未初始化。 ");

        var extension = ".mp4";
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8 && !ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) extension = ext;
        }

        var target = Path.Combine(VideoDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{jobId:N}{extension}");
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(remoteUrl);
        if (cookies.Count > 0)
        {
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
        }
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.dola.com/");

        using var response = await client.GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(target);
        await input.CopyToAsync(output, cancellationToken);
        return target;
    }

    private VideoJob? SelectedJob() => _grid.CurrentRow?.DataBoundItem as VideoJob;

    private void PreviewSelected()
    {
        var job = SelectedJob();
        if (job?.LocalPath is null || !File.Exists(job.LocalPath))
        {
            MessageBox.Show("该任务还没有本地视频。生成完成后会自动下载，无需额外点击无水印下载。", "提示");
            return;
        }
        _preview.CoreWebView2?.Navigate(new Uri(job.LocalPath).AbsoluteUri);
    }

    private void ExportSelected()
    {
        var job = SelectedJob();
        if (job?.LocalPath is null || !File.Exists(job.LocalPath)) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(job.LocalPath)
        };
        if (dialog.ShowDialog() == DialogResult.OK) File.Copy(job.LocalPath, dialog.FileName, true);
    }

    private void UpdateStatus()
    {
        var job = SelectedJob();
        if (job is not null) _status.Text = $"{job.Status} | {job.Error ?? job.LocalPath ?? job.RemoteUrl ?? job.Id.ToString()}";
    }

    private void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}

using System.Diagnostics;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    private VideoJob? SelectedJob() => _grid.CurrentRow?.DataBoundItem as VideoJob;

    private void PreviewSelected()
    {
        var job = SelectedJob();
        if (job?.LocalPath is null || !File.Exists(job.LocalPath))
        {
            MessageBox.Show("该任务还没有本地视频。生成完成后会自动下载到本地。", "提示");
            return;
        }

        var relative = Path.GetRelativePath(VideoDir, job.LocalPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            Process.Start(new ProcessStartInfo(job.LocalPath) { UseShellExecute = true });
            return;
        }

        var mediaUrl = "https://aivideo.local/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <style>html,body{margin:0;height:100%;background:#090d18}body{display:grid;place-items:center}
            video{width:100%;height:100%;object-fit:contain;background:#000}</style></head>
            <body><video src="{{System.Net.WebUtility.HtmlEncode(mediaUrl)}}" controls autoplay playsinline preload="metadata"></video></body></html>
            """;
        _preview.NavigateToString(html);
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
        if (dialog.ShowDialog() == DialogResult.OK)
            File.Copy(job.LocalPath, dialog.FileName, true);
    }

    private void UpdateStatus()
    {
        var job = SelectedJob();
        if (job is null) return;

        var worker = string.IsNullOrWhiteSpace(job.Worker) ? "未分配" : job.Worker;
        _status.Text = $"{job.Status} | {worker} | {job.Error ?? job.LocalPath ?? job.RemoteUrl ?? job.Id.ToString()}";
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}

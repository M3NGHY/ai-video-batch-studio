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

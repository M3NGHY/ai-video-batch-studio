using System.Collections.Concurrent;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    private void AddJob()
    {
        var text = _prompt.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        _jobs.Add(new VideoJob { Prompt = text });
        _prompt.Clear();
        _grid.Refresh();
    }

    private async Task StartQueueAsync()
    {
        if (_running) return;

        var pending = _jobs.Where(x => x.Status == JobStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            _status.Text = "没有待执行任务。";
            return;
        }

        var workers = _sessions.Where(x => x.IsReady).ToList();
        if (workers.Count == 0)
        {
            MessageBox.Show("没有可用的 Dola 窗口。", "提示");
            return;
        }

        _running = true;
        _runCts = new CancellationTokenSource();
        var queue = new ConcurrentQueue<VideoJob>(pending);
        _status.Text = $"并行运行中：{workers.Count} 个 Dola 窗口，待处理 {pending.Count} 个任务。";
        Log($"开始并行批量：窗口 {workers.Count}，任务 {pending.Count}。 ");

        try
        {
            var tasks = workers.Select(worker => RunWorkerLoopAsync(worker, queue, _runCts.Token)).ToArray();
            await Task.WhenAll(tasks);
            _status.Text = "本轮批量任务已完成。";
        }
        catch (OperationCanceledException)
        {
            Log("批量任务已停止。 ");
            _status.Text = "批量任务已停止。";
        }
        finally
        {
            _running = false;
            _runCts.Dispose();
            _runCts = null;
            _grid.Refresh();
        }
    }

    private async Task RunWorkerLoopAsync(
        DolaSession worker,
        ConcurrentQueue<VideoJob> queue,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var job))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await worker.RunJobAsync(job, cancellationToken);
        }
    }
}

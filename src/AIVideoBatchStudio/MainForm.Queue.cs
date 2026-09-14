using System.Text.Json;

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
        _running = true;
        _runCts = new CancellationTokenSource();
        try
        {
            foreach (var job in _jobs.Where(x => x.Status == JobStatus.Pending).ToList())
            {
                _runCts.Token.ThrowIfCancellationRequested();
                await RunOneAsync(job, _runCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Log("批量任务已停止。");
        }
        finally
        {
            _running = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    private async Task RunOneAsync(VideoJob job, CancellationToken cancellationToken)
    {
        try
        {
            job.Status = JobStatus.Running;
            job.Error = null;
            _grid.Refresh();
            _status.Text = $"正在生成: {job.Prompt}";

            var remoteUrl = await SubmitAndWaitAsync(job, cancellationToken);
            job.RemoteUrl = remoteUrl;
            job.Status = JobStatus.Downloading;
            _grid.Refresh();

            var localPath = await DownloadVideoAsync(remoteUrl, job.Id, cancellationToken);
            job.LocalPath = localPath;
            job.Status = JobStatus.Completed;
            _grid.Refresh();
            Log($"完成: {job.Id} -> {localPath}");
            if (_grid.CurrentRow?.DataBoundItem == job) PreviewSelected();
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "任务已取消或等待超时";
            _grid.Refresh();
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            _grid.Refresh();
            Log($"任务失败 {job.Id}: {ex.Message}");
        }
    }

    private async Task<string> SubmitAndWaitAsync(VideoJob job, CancellationToken cancellationToken)
    {
        if (_browser.CoreWebView2 is null) throw new InvalidOperationException("Dola 浏览器尚未初始化。 ");
        if (_resultWaiter is not null) throw new InvalidOperationException("已有生成任务正在等待结果。 ");

        _activeJobId = job.Id;
        _resultWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await InstallBridgeAsync();
            await _browser.CoreWebView2.ExecuteScriptAsync(
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

            var result = await _browser.CoreWebView2.ExecuteScriptAsync(submitJs);
            Log("提交结果: " + result);
            if (result.Contains("NO_PROMPT_INPUT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("没有找到 Dola 提示词输入框，请确认已经进入创作页面。 ");
            if (result.Contains("NO_SUBMIT_BUTTON", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("没有找到生成/发送按钮，Dola 页面结构可能已变化。 ");

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
}

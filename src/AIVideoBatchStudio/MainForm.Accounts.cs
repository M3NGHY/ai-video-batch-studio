namespace AIVideoBatchStudio;

internal sealed partial class MainForm
{
    private async Task ImportGoogleAccountsAsync()
    {
        if (_running)
        {
            MessageBox.Show("当前有任务正在运行，请先停止。", "提示");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "批量导入谷歌账号",
            Filter = "账号文件 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        List<GoogleAccount> accounts;
        try
        {
            accounts = AccountImportParser.ParseGoogleAccounts(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "账号文件读取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (accounts.Count == 0)
        {
            MessageBox.Show("没有读取到有效账号。格式示例：email@gmail.com,password", "未找到账号");
            return;
        }

        var targetCount = Math.Max(_sessions.Count, accounts.Count);
        targetCount = Math.Min(targetCount, (int)_windowCount.Maximum);
        if (_sessions.Count < targetCount)
        {
            _windowCount.Value = targetCount;
            await SetSessionCountAsync(targetCount);
        }

        _running = true;
        _runCts = new CancellationTokenSource();
        var success = 0;
        var manual = 0;
        try
        {
            for (var i = 0; i < accounts.Count && i < _sessions.Count; i++)
            {
                _runCts.Token.ThrowIfCancellationRequested();
                var account = accounts[i];
                var session = _sessions[i];
                _browserTabs.SelectedIndex = i;
                UpdateSessionCaption(session, $"Google {MaskEmail(account.Email)}");
                _status.Text = $"正在登录 {session.Name}: {MaskEmail(account.Email)}";
                Log($"{session.Name} 开始导入 Google 账号 {MaskEmail(account.Email)}");

                try
                {
                    var result = await session.LoginGoogleAccountAsync(account, _runCts.Token);
                    Log($"{session.Name} Google 登录结果: {result}");
                    if (result.Contains("验证码", StringComparison.OrdinalIgnoreCase) ||
                        result.Contains("手动", StringComparison.OrdinalIgnoreCase) ||
                        result.Contains("确认", StringComparison.OrdinalIgnoreCase))
                    {
                        manual++;
                        UpdateSessionCaption(session, $"{MaskEmail(account.Email)} - 待验证");
                    }
                    else
                    {
                        success++;
                        UpdateSessionCaption(session, MaskEmail(account.Email));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"{session.Name} Google 登录失败: {ex.Message}");
                    UpdateSessionCaption(session, $"{MaskEmail(account.Email)} - 失败");
                }
            }

            _status.Text = $"Google 批量导入完成：自动提交 {success}，需要手动验证 {manual}。";
            MessageBox.Show(
                $"已处理 {Math.Min(accounts.Count, _sessions.Count)} 个 Google 账号。\n自动提交：{success}\n需要手动验证码/二次验证：{manual}\n\n密码仅在本次运行内存中使用，不会明文保存。",
                "批量导入完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Google 账号批量导入已停止。";
            Log("Google 账号批量导入已停止。 ");
        }
        finally
        {
            _running = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task ImportDolaCookiesAsync()
    {
        if (_running)
        {
            MessageBox.Show("当前有任务正在运行，请先停止。", "提示");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "批量导入 Dola Cookie",
            Filter = "Cookie 文件 (*.txt;*.json;*.cookies)|*.txt;*.json;*.cookies|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var bundles = new List<CookieBundle>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                bundles.AddRange(AccountImportParser.ParseCookieFile(file));
            }
            catch (Exception ex)
            {
                Log($"Cookie 文件解析失败 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        bundles = bundles.Take(10).ToList();
        if (bundles.Count == 0)
        {
            MessageBox.Show("没有读取到有效 Dola Cookie。支持 Cookie-Editor JSON、Netscape Cookie、以及原始 Cookie Header。", "未找到 Cookie");
            return;
        }

        var targetCount = Math.Max(_sessions.Count, bundles.Count);
        targetCount = Math.Min(targetCount, (int)_windowCount.Maximum);
        if (_sessions.Count < targetCount)
        {
            _windowCount.Value = targetCount;
            await SetSessionCountAsync(targetCount);
        }

        _running = true;
        _runCts = new CancellationTokenSource();
        var success = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < bundles.Count && i < _sessions.Count; i++)
            {
                _runCts.Token.ThrowIfCancellationRequested();
                var bundle = bundles[i];
                var session = _sessions[i];
                _browserTabs.SelectedIndex = i;
                _status.Text = $"正在导入 {session.Name} Cookie ({i + 1}/{bundles.Count})";
                Log($"{session.Name} 开始导入 Cookie：{bundle.Label}，共 {bundle.Cookies.Count} 项");

                try
                {
                    var count = await session.ImportDolaCookiesAsync(bundle.Cookies, _runCts.Token);
                    success++;
                    UpdateSessionCaption(session, $"Cookie已登录 {i + 1}");
                    Log($"{session.Name} Cookie 导入完成，共写入 {count} 项，已刷新 Dola。 ");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    UpdateSessionCaption(session, $"Cookie失败 {i + 1}");
                    Log($"{session.Name} Cookie 导入失败: {ex.Message}");
                }
            }

            _status.Text = $"Dola Cookie 批量导入完成：成功 {success}，失败 {failed}。";
            MessageBox.Show(
                $"Dola Cookie 已处理 {Math.Min(bundles.Count, _sessions.Count)} 个独立 Profile。\n成功：{success}\n失败：{failed}\n\n成功导入后对应窗口会自动刷新 Dola 页面。",
                "Cookie 批量导入完成",
                MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Dola Cookie 批量导入已停止。";
            Log("Dola Cookie 批量导入已停止。 ");
        }
        finally
        {
            _running = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void StopCurrentOperation()
    {
        if (_runCts is null || !_running)
        {
            _status.Text = "当前没有正在运行的批量操作。";
            return;
        }

        _runCts.Cancel();
        _status.Text = "正在停止当前批量操作...";
    }

    private async Task RetryFailedJobsWithFeedbackAsync()
    {
        var failed = _jobs.Where(x => x.Status is JobStatus.Failed or JobStatus.Cancelled).ToList();
        if (failed.Count == 0)
        {
            _status.Text = "没有失败或已取消的视频任务可重试。";
            return;
        }

        foreach (var job in failed)
        {
            job.Status = JobStatus.Pending;
            job.Worker = null;
            job.Error = null;
            job.RemoteUrl = null;
            job.LocalPath = null;
        }
        _grid.Refresh();
        await StartQueueAsync();
    }

    private void UpdateSessionCaption(DolaSession session, string label)
    {
        var index = _sessions.IndexOf(session);
        if (index < 0 || index >= _browserTabs.TabPages.Count) return;
        _browserTabs.TabPages[index].Text = $"{session.Name} [{label}]";
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return email;
        var prefix = email[..at];
        var visible = prefix.Length <= 2 ? prefix[..1] : prefix[..2];
        return $"{visible}***{email[at..]}";
    }
}

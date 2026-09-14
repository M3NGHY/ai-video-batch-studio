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
            Title = "批量导入 Google 账号",
            Filter = "账号文件 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        GoogleAccountImportResult import;
        try
        {
            import = GoogleAccountTextImporter.Parse(dialog.FileName, (int)_windowCount.Maximum);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "账号文件读取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var accounts = import.Accounts.ToList();
        Log($"Google TXT 解析：非空行 {import.TotalNonEmptyLines}，识别账号 {accounts.Count}，未识别 {import.RejectedLines}。 ");

        if (accounts.Count == 0)
        {
            MessageBox.Show(
                "没有读取到有效账号。\n\n当前优先支持格式：\nxxxx@gmail.com----password\n\n请确认邮箱和密码在同一行，中间是 4 个或更多横线。",
                "未找到账号",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var preview = string.Join(Environment.NewLine, accounts.Take(8).Select((x, i) => $"{i + 1}. {MaskEmail(x.Email)}"));
        var confirm = MessageBox.Show(
            $"已识别 {accounts.Count} 个 Google 账号。\n未识别行：{import.RejectedLines}\n\n{preview}\n\n接下来每个账号会分配到独立 Dola Profile。软件会从 Dola 发起 Google OAuth，并自动填写邮箱；Google 密码/验证码请在弹出的 Google 登录窗口中完成一次。登录成功后该 Profile 会保留登录状态。\n\n是否开始？",
            "确认 Google 账号导入",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.OK) return;

        var targetCount = Math.Min(Math.Max(_sessions.Count, accounts.Count), (int)_windowCount.Maximum);
        if (_sessions.Count < targetCount)
        {
            _windowCount.Value = targetCount;
            await SetSessionCountAsync(targetCount);
        }

        _running = true;
        _runCts = new CancellationTokenSource();
        var success = 0;
        var manual = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < accounts.Count && i < _sessions.Count; i++)
            {
                _runCts.Token.ThrowIfCancellationRequested();
                var account = accounts[i];
                var session = _sessions[i];
                _browserTabs.SelectedIndex = i;
                UpdateSessionCaption(session, $"Google {MaskEmail(account.Email)}");
                _status.Text = $"正在通过 Dola Google OAuth 登录 {session.Name}: {MaskEmail(account.Email)} ({i + 1}/{Math.Min(accounts.Count, _sessions.Count)})";
                Log($"{session.Name} 开始处理 Google 账号 {MaskEmail(account.Email)}");

                try
                {
                    var result = await session.OpenGoogleOAuthForAccountAsync(
                        account.Email,
                        _runCts.Token,
                        message => Log($"{session.Name} {message}"));
                    Log($"{session.Name} Google OAuth 结果: {result}");

                    if (string.Equals(result, "DOLA_LOGIN_OK", StringComparison.Ordinal))
                    {
                        success++;
                        UpdateSessionCaption(session, $"{MaskEmail(account.Email)} 已登录");
                    }
                    else if (result.Contains("验证", StringComparison.OrdinalIgnoreCase) ||
                             result.Contains("密码", StringComparison.OrdinalIgnoreCase) ||
                             result.Contains("手动", StringComparison.OrdinalIgnoreCase) ||
                             result.Contains("超时", StringComparison.OrdinalIgnoreCase))
                    {
                        manual++;
                        UpdateSessionCaption(session, $"{MaskEmail(account.Email)} 待完成");
                    }
                    else
                    {
                        failed++;
                        UpdateSessionCaption(session, $"{MaskEmail(account.Email)} 登录失败");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"{session.Name} Google 登录失败: {ex.Message}");
                    UpdateSessionCaption(session, $"{MaskEmail(account.Email)} 登录失败");
                }
            }

            _status.Text = $"Google 账号导入完成：Dola登录 {success}，待完成 {manual}，失败 {failed}。";
            MessageBox.Show(
                $"已处理 {Math.Min(accounts.Count, _sessions.Count)} 个 Google 账号。\n\n已回到 Dola 并登录：{success}\n需要继续完成 Google 密码/验证：{manual}\n失败：{failed}\n\n账号 TXT 已按“邮箱----密码”格式识别。Dola 主页面不会再被带到 Google 个人资料页。",
                "Google 账号导入完成",
                MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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

        var targetCount = Math.Min(Math.Max(_sessions.Count, bundles.Count), (int)_windowCount.Maximum);
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
                _status.Text = $"正在导入 {session.Name} Dola Cookie ({i + 1}/{Math.Min(bundles.Count, _sessions.Count)})";
                Log($"{session.Name} 开始导入 Dola Cookie：{bundle.Label}，共 {bundle.Cookies.Count} 项");

                try
                {
                    var count = await session.ImportDolaCookiesAsync(bundle.Cookies, _runCts.Token);
                    success++;
                    UpdateSessionCaption(session, $"Cookie 已载入 {i + 1}");
                    Log($"{session.Name} Cookie 导入完成，共写入 {count} 项 dola.com Cookie，已刷新 Dola。 ");
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
                $"Dola Cookie 已处理 {Math.Min(bundles.Count, _sessions.Count)} 个独立 Profile。\n成功：{success}\n失败：{failed}\n\n每组 Cookie 只写入对应的独立 Profile，导入后自动返回 Dola 页面。",
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

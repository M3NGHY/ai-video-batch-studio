using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm : Form
{
    private readonly TabControl _browserTabs = new() { Dock = DockStyle.Fill };
    private readonly List<DolaSession> _sessions = [];
    private readonly Dictionary<int, string> _networkConfig = WorkerNetworkStore.Load();
    private readonly WebView2 _preview = new() { Dock = DockStyle.Fill };
    private readonly BindingList<VideoJob> _jobs = [];
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = true,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly TextBox _prompt = new()
    {
        Dock = DockStyle.Top,
        Multiline = true,
        Height = 92,
        PlaceholderText = "输入视频提示词，每次加入一个任务..."
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Bottom,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 110
    };
    private readonly NumericUpDown _windowCount = new()
    {
        Minimum = 1,
        Maximum = 10,
        Value = 3,
        Width = 58
    };
    private readonly TextBox _proxyBox = new()
    {
        Width = 260,
        PlaceholderText = "直连留空；http://host:port；socks5://host:port"
    };
    private readonly Label _networkState = new()
    {
        AutoSize = true,
        Text = "当前窗口：未初始化",
        Margin = new Padding(12, 8, 2, 0)
    };
    private readonly ToolStripStatusLabel _status = new("初始化中...");

    private bool _running;
    private CancellationTokenSource? _runCts;

    private static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIVideoBatchStudio");
    private static string ProfileRoot => Path.Combine(AppRoot, "profiles");
    private static string VideoDir => Path.Combine(AppRoot, "videos");

    public MainForm()
    {
        Text = "AI 视频批量创作台 - 独立 Profile + 固定代理版";
        Width = 1580;
        Height = 960;
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(ProfileRoot);
        Directory.CreateDirectory(VideoDir);
        BuildUi();
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) =>
        {
            _runCts?.Cancel();
            foreach (var session in _sessions.ToArray()) session.Dispose();
        };
    }

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(6),
            WrapContents = false
        };

        var add = new Button { Text = "加入队列", AutoSize = true };
        var start = new Button { Text = "开始并行批量", AutoSize = true };
        var stop = new Button { Text = "停止", AutoSize = true };
        var retry = new Button { Text = "重试失败", AutoSize = true };
        var preview = new Button { Text = "本地预览", AutoSize = true };
        var export = new Button { Text = "导出视频", AutoSize = true };
        var folder = new Button { Text = "打开视频目录", AutoSize = true };
        var windowLabel = new Label
        {
            Text = "独立Dola窗口:",
            AutoSize = true,
            Margin = new Padding(12, 8, 2, 0)
        };
        var applyWindows = new Button { Text = "应用窗口数", AutoSize = true };
        var addWindow = new Button { Text = "+1窗口", AutoSize = true };
        var removeWindow = new Button { Text = "-1窗口", AutoSize = true };
        var dola = new Button { Text = "打开当前Dola", AutoSize = true };
        var refreshAll = new Button { Text = "刷新全部Dola", AutoSize = true };

        toolbar.Controls.AddRange([
            add, start, stop, retry, preview, export, folder,
            windowLabel, _windowCount, applyWindows, addWindow, removeWindow, dola, refreshAll
        ]);

        var networkBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 4, 6, 4),
            WrapContents = false
        };
        var proxyLabel = new Label
        {
            Text = "当前窗口固定代理:",
            AutoSize = true,
            Margin = new Padding(2, 8, 4, 0)
        };
        var applyProxy = new Button { Text = "应用当前代理", AutoSize = true };
        var direct = new Button { Text = "当前窗口直连", AutoSize = true };
        var testNetwork = new Button { Text = "测试当前出口IP", AutoSize = true };
        networkBar.Controls.AddRange([proxyLabel, _proxyBox, applyProxy, direct, testNetwork, _networkState]);

        var taskPanel = new Panel { Dock = DockStyle.Fill };
        taskPanel.Controls.Add(_grid);
        taskPanel.Controls.Add(_prompt);

        var bottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 1010
        };
        bottom.Panel1.Controls.Add(taskPanel);
        bottom.Panel2.Controls.Add(_preview);

        var main = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 510
        };
        main.Panel1.Controls.Add(_browserTabs);
        main.Panel2.Controls.Add(bottom);

        var status = new StatusStrip();
        status.Items.Add(_status);
        Controls.Add(main);
        Controls.Add(_log);
        Controls.Add(networkBar);
        Controls.Add(toolbar);
        Controls.Add(status);

        _grid.DataSource = _jobs;
        add.Click += (_, _) => AddJob();
        start.Click += async (_, _) => await StartQueueAsync();
        stop.Click += (_, _) => _runCts?.Cancel();
        retry.Click += async (_, _) =>
        {
            foreach (var job in _jobs.Where(x => x.Status is JobStatus.Failed or JobStatus.Cancelled))
            {
                job.Status = JobStatus.Pending;
                job.Worker = null;
                job.Error = null;
                job.RemoteUrl = null;
                job.LocalPath = null;
            }
            _grid.Refresh();
            await StartQueueAsync();
        };
        preview.Click += (_, _) => PreviewSelected();
        export.Click += (_, _) => ExportSelected();
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", VideoDir) { UseShellExecute = true });
        applyWindows.Click += async (_, _) => await SetSessionCountAsync((int)_windowCount.Value);
        addWindow.Click += async (_, _) =>
        {
            if (_windowCount.Value < _windowCount.Maximum) _windowCount.Value++;
            await SetSessionCountAsync((int)_windowCount.Value);
        };
        removeWindow.Click += async (_, _) =>
        {
            if (_windowCount.Value > _windowCount.Minimum) _windowCount.Value--;
            await SetSessionCountAsync((int)_windowCount.Value);
        };
        dola.Click += (_, _) => SelectedSession()?.NavigateHome();
        refreshAll.Click += (_, _) =>
        {
            foreach (var session in _sessions) session.NavigateHome();
            _status.Text = $"已刷新 {_sessions.Count} 个独立 Dola 页面。";
        };
        applyProxy.Click += async (_, _) => await ApplyCurrentProxyAsync(_proxyBox.Text);
        direct.Click += async (_, _) => await ApplyCurrentProxyAsync(string.Empty);
        testNetwork.Click += async (_, _) => await TestCurrentNetworkAsync();
        _grid.SelectionChanged += (_, _) => UpdateStatus();
        _browserTabs.SelectedIndexChanged += (_, _) => UpdateSelectedSessionNetworkUi();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _preview.EnsureCoreWebView2Async();
            await SetSessionCountAsync((int)_windowCount.Value);
            _status.Text = $"就绪：{_sessions.Count} 个独立 Profile；每个窗口可绑定自己的固定代理。";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SetSessionCountAsync(int targetCount)
    {
        targetCount = Math.Clamp(targetCount, 1, 10);

        if (_running)
        {
            MessageBox.Show("批量任务运行时不能调整 Dola 窗口或代理，请先停止任务。", "提示");
            _windowCount.Value = Math.Max(1, _sessions.Count);
            return;
        }

        foreach (var session in _sessions.ToArray()) session.Dispose();
        _sessions.Clear();
        _browserTabs.TabPages.Clear();

        for (var number = 1; number <= targetCount; number++)
        {
            var profileDir = Path.Combine(ProfileRoot, $"dola-{number:00}");
            _networkConfig.TryGetValue(number, out var proxy);
            proxy ??= string.Empty;

            var session = new DolaSession(
                number,
                $"Dola {number}",
                profileDir,
                proxy,
                VideoDir,
                Log,
                OnJobChanged);
            var page = new TabPage(session.Name);
            page.Controls.Add(session.Browser);
            _browserTabs.TabPages.Add(page);
            _sessions.Add(session);

            try
            {
                await session.InitializeAsync();
                Log($"{session.Name} 已启动 | 独立Profile={profileDir} | 网络={(string.IsNullOrWhiteSpace(session.ProxyUrl) ? "直连" : session.ProxyUrl)}");
            }
            catch (Exception ex)
            {
                Log($"{session.Name} 初始化失败: {ex.Message}");
                page.Text = session.Name + " [启动失败]";
            }
        }

        _windowCount.Value = targetCount;
        if (_browserTabs.TabPages.Count > 0 && _browserTabs.SelectedIndex < 0)
            _browserTabs.SelectedIndex = 0;
        UpdateSelectedSessionNetworkUi();
        _status.Text = $"独立 Dola 窗口: {_sessions.Count} | 独立 Cookie/Profile | 最大并行任务: {_sessions.Count}";
    }

    private async Task ApplyCurrentProxyAsync(string value)
    {
        var session = SelectedSession();
        if (session is null) return;
        if (_running)
        {
            MessageBox.Show("批量任务运行时不能修改代理。", "提示");
            return;
        }

        try
        {
            var normalized = DolaSession.NormalizeProxyAddress(value);
            if (string.IsNullOrWhiteSpace(normalized))
                _networkConfig.Remove(session.Number);
            else
                _networkConfig[session.Number] = normalized;
            WorkerNetworkStore.Save(_networkConfig);

            var selected = session.Number - 1;
            var count = _sessions.Count;
            _status.Text = $"正在重新加载 {session.Name} 的网络配置...";
            await SetSessionCountAsync(count);
            if (selected >= 0 && selected < _browserTabs.TabPages.Count)
                _browserTabs.SelectedIndex = selected;
            UpdateSelectedSessionNetworkUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "代理配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task TestCurrentNetworkAsync()
    {
        var session = SelectedSession();
        if (session is null) return;

        try
        {
            _networkState.Text = $"{session.Name}：正在测试出口...";
            var ip = await session.TestNetworkAsync();
            var mode = string.IsNullOrWhiteSpace(session.ProxyUrl) ? "直连" : session.ProxyUrl;
            _networkState.Text = $"{session.Name} | {mode} | 出口IP: {ip}";
            if (_browserTabs.SelectedIndex >= 0)
                _browserTabs.TabPages[_browserTabs.SelectedIndex].Text = $"{session.Name} [{ip}]";
            Log($"{session.Name} 网络测试成功，出口IP: {ip}");
        }
        catch (Exception ex)
        {
            _networkState.Text = $"{session.Name}：网络测试失败";
            Log($"{session.Name} 网络测试失败: {ex.Message}");
            MessageBox.Show(ex.Message, "出口测试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateSelectedSessionNetworkUi()
    {
        var session = SelectedSession();
        if (session is null)
        {
            _proxyBox.Text = string.Empty;
            _networkState.Text = "当前窗口：无";
            return;
        }

        _proxyBox.Text = session.ProxyUrl;
        var mode = string.IsNullOrWhiteSpace(session.ProxyUrl) ? "直连" : session.ProxyUrl;
        _networkState.Text = session.LastObservedIp is null
            ? $"{session.Name} | {mode} | 独立Profile"
            : $"{session.Name} | {mode} | 出口IP: {session.LastObservedIp}";
        _status.Text = $"当前页面: {session.Name} | 独立 Profile | 网络: {mode}";
    }

    private DolaSession? SelectedSession()
    {
        var index = _browserTabs.SelectedIndex;
        return index >= 0 && index < _sessions.Count ? _sessions[index] : null;
    }

    private void OnJobChanged(VideoJob job)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnJobChanged(job));
            return;
        }

        _grid.Refresh();
        if (_grid.CurrentRow?.DataBoundItem == job && job.Status == JobStatus.Completed)
            PreviewSelected();
        UpdateStatus();
    }
}

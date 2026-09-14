using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm : Form
{
    private readonly TabControl _browserTabs = new() { Dock = DockStyle.Fill };
    private readonly List<DolaSession> _sessions = [];
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
    private readonly ToolStripStatusLabel _status = new("初始化中...");

    private bool _running;
    private CancellationTokenSource? _runCts;
    private CoreWebView2Environment? _sharedEnvironment;

    private static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIVideoBatchStudio");
    private static string ProfileDir => Path.Combine(AppRoot, "profile");
    private static string VideoDir => Path.Combine(AppRoot, "videos");

    public MainForm()
    {
        Text = "AI 视频批量创作台 - 多 Dola 并行版";
        Width = 1540;
        Height = 940;
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(ProfileDir);
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
            Text = "并行Dola窗口:",
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

        var taskPanel = new Panel { Dock = DockStyle.Fill };
        taskPanel.Controls.Add(_grid);
        taskPanel.Controls.Add(_prompt);

        var bottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 980
        };
        bottom.Panel1.Controls.Add(taskPanel);
        bottom.Panel2.Controls.Add(_preview);

        var main = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 500
        };
        main.Panel1.Controls.Add(_browserTabs);
        main.Panel2.Controls.Add(bottom);

        var status = new StatusStrip();
        status.Items.Add(_status);
        Controls.Add(main);
        Controls.Add(_log);
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
            _status.Text = $"已刷新 {_sessions.Count} 个 Dola 页面；这些页面共享同一登录状态。";
        };
        _grid.SelectionChanged += (_, _) => UpdateStatus();
        _browserTabs.SelectedIndexChanged += (_, _) =>
        {
            var session = SelectedSession();
            if (session is not null) _status.Text = $"当前页面: {session.Name} | 总窗口: {_sessions.Count}";
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, ProfileDir);
            await _preview.EnsureCoreWebView2Async();
            await SetSessionCountAsync((int)_windowCount.Value);
            _status.Text = $"就绪：{_sessions.Count} 个 Dola 页面共享登录，可同时生成 {_sessions.Count} 个任务。";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SetSessionCountAsync(int targetCount)
    {
        if (_sharedEnvironment is null) return;
        targetCount = Math.Clamp(targetCount, 1, 10);

        if (_running)
        {
            MessageBox.Show("批量任务运行时不能调整 Dola 窗口数量，请先停止任务。", "提示");
            _windowCount.Value = _sessions.Count;
            return;
        }

        while (_sessions.Count < targetCount)
        {
            var number = _sessions.Count + 1;
            var session = new DolaSession($"Dola {number}", VideoDir, Log, OnJobChanged);
            var page = new TabPage(session.Name);
            page.Controls.Add(session.Browser);
            _browserTabs.TabPages.Add(page);
            _sessions.Add(session);

            try
            {
                await session.InitializeAsync(_sharedEnvironment);
                Log($"{session.Name} 已启动，使用共享登录 Profile。 ");
            }
            catch
            {
                _sessions.Remove(session);
                _browserTabs.TabPages.Remove(page);
                session.Dispose();
                page.Dispose();
                throw;
            }
        }

        while (_sessions.Count > targetCount)
        {
            var index = _sessions.Count - 1;
            var session = _sessions[index];
            var page = _browserTabs.TabPages[index];
            _sessions.RemoveAt(index);
            _browserTabs.TabPages.RemoveAt(index);
            session.Dispose();
            page.Dispose();
        }

        _windowCount.Value = _sessions.Count;
        _status.Text = $"Dola 窗口数: {_sessions.Count} | 共享登录 | 最大并行任务: {_sessions.Count}";
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

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm : Form
{
    private const string AppTitle = "梦天Dola工作台";
    private const string AppVersion = "v1.01";

    private static readonly Color Bg = Color.FromArgb(5, 10, 22);
    private static readonly Color Panel = Color.FromArgb(9, 18, 36);
    private static readonly Color PanelRaised = Color.FromArgb(12, 24, 47);
    private static readonly Color PanelHover = Color.FromArgb(18, 34, 64);
    private static readonly Color Border = Color.FromArgb(48, 82, 137);
    private static readonly Color Accent = Color.FromArgb(82, 129, 255);
    private static readonly Color AccentSoft = Color.FromArgb(70, 204, 255);
    private static readonly Color TextPrimary = Color.FromArgb(239, 246, 255);
    private static readonly Color TextMuted = Color.FromArgb(139, 161, 193);
    private static readonly Color Success = Color.FromArgb(73, 224, 159);
    private static readonly Color Danger = Color.FromArgb(255, 99, 132);

    private readonly TabControl _browserTabs = new()
    {
        Dock = DockStyle.Fill,
        Appearance = TabAppearance.FlatButtons,
        ItemSize = new Size(0, 1),
        SizeMode = TabSizeMode.Fixed,
        Multiline = true,
        BackColor = Bg
    };
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
        MultiSelect = false,
        BackgroundColor = Bg,
        BorderStyle = BorderStyle.None,
        GridColor = Color.FromArgb(30, 54, 91),
        EnableHeadersVisualStyles = false
    };
    private readonly TextBox _prompt = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        PlaceholderText = "输入视频提示词，然后点击“加入视频任务”...",
        BackColor = Color.FromArgb(7, 15, 30),
        ForeColor = TextPrimary,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(5, 12, 24),
        ForeColor = TextMuted,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly NumericUpDown _windowCount = new()
    {
        Minimum = 1,
        Maximum = 20,
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
        Text = "当前窗口：未初始化"
    };
    private readonly ToolStripStatusLabel _status = new("初始化中...");

    private readonly Label _currentAccountLabel = new()
    {
        AutoSize = true,
        Text = "当前账号：等待初始化",
        ForeColor = TextPrimary,
        Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold)
    };
    private readonly Label _accountCounterLabel = new()
    {
        AutoSize = true,
        Text = "显示 0 / 0",
        ForeColor = TextMuted
    };
    private readonly Label _accountReadyBadge = new()
    {
        AutoSize = true,
        Text = "就绪 0",
        ForeColor = TextPrimary,
        BackColor = Color.FromArgb(18, 42, 75),
        Padding = new Padding(10, 6, 10, 6)
    };
    private readonly TextBox _accountSearch = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(7, 15, 30),
        ForeColor = TextPrimary,
        PlaceholderText = "搜索账号...",
        Dock = DockStyle.Fill
    };
    private readonly TextBox _addressBox = new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(7, 15, 30),
        ForeColor = TextPrimary,
        Dock = DockStyle.Fill
    };
    private readonly FlowLayoutPanel _accountList = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Panel
    };
    private readonly FlowLayoutPanel _workLibrary = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Bg,
        Padding = new Padding(12, 8, 12, 8)
    };
    private readonly Label _workCountLabel = new()
    {
        AutoSize = true,
        Text = "0 个作品",
        ForeColor = TextMuted,
        Margin = new Padding(12, 8, 0, 0)
    };

    private Panel? _libraryPanel;
    private Panel? _taskPanel;
    private bool _running;
    private CancellationTokenSource? _runCts;

    private static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIVideoBatchStudio");
    private static string ProfileRoot => Path.Combine(AppRoot, "profiles");
    private static string VideoDir => Path.Combine(AppRoot, "videos");

    public MainForm()
    {
        Text = $"{AppTitle} {AppVersion}";
        Width = 1600;
        Height = 920;
        MinimumSize = new Size(1280, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextPrimary;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);

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
        ConfigureGridTheme();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));

        var titleBar = BuildTitleBar();
        var workspace = BuildWorkspace();

        var statusBar = new StatusStrip
        {
            BackColor = Color.FromArgb(7, 15, 30),
            ForeColor = TextMuted,
            SizingGrip = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 8, 0)
        };
        _status.ForeColor = TextMuted;
        statusBar.Items.Add(_status);

        root.Controls.Add(titleBar, 0, 0);
        root.Controls.Add(workspace, 0, 1);
        root.Controls.Add(statusBar, 0, 2);
        Controls.Add(root);

        _grid.DataSource = _jobs;
        _grid.SelectionChanged += (_, _) => UpdateStatus();
        _browserTabs.SelectedIndexChanged += (_, _) => UpdateSelectedSessionNetworkUi();
        _accountSearch.TextChanged += (_, _) => ApplyAccountFilter();
        _accountList.Resize += (_, _) => ResizeAccountCards();
        _workLibrary.Resize += (_, _) => RefreshWorkLibrary();
    }

    private Control BuildTitleBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(5, 11, 25),
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 9, 0, 7),
            BackColor = Color.Transparent
        };
        var notice = CreateButton("公告", 64);
        var accountsShop = CreateButton("账号购买", 88);
        var keyShop = CreateButton("卡密购买", 88);
        left.Controls.AddRange([notice, accountsShop, keyShop]);

        notice.Click += (_, _) => MessageBox.Show(
            $"{AppTitle} {AppVersion}\n\n本版重点：\n• 重构为深色 Dola 工作台界面\n• 保留独立 Profile / Cookie / 固定代理\n• 保留视频结果捕获、自动下载与本地预览\n• 保留 Google OAuth 登录修复",
            "公告",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        accountsShop.Click += (_, _) => ShowReservedEntry("账号购买");
        keyShop.Click += (_, _) => ShowReservedEntry("卡密购买");

        var center = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 8)
        };
        center.Anchor = AnchorStyles.None;
        var logo = new Label
        {
            AutoSize = true,
            Text = "✦",
            ForeColor = AccentSoft,
            Font = new Font("Segoe UI Symbol", 15F, FontStyle.Bold),
            Margin = new Padding(0, 3, 10, 0)
        };
        var brand = new Label
        {
            AutoSize = true,
            Text = AppTitle,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Margin = new Padding(0, 5, 18, 0)
        };
        var version = new Label
        {
            AutoSize = true,
            Text = AppVersion,
            ForeColor = TextPrimary,
            BackColor = Color.FromArgb(11, 27, 52),
            Padding = new Padding(14, 5, 14, 5),
            Margin = new Padding(0, 2, 0, 0)
        };
        center.Controls.AddRange([logo, brand, version]);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 11, 8, 8),
            BackColor = Color.Transparent
        };
        var close = CreateWindowButton("×");
        var maximize = CreateWindowButton("□");
        var minimize = CreateWindowButton("—");
        close.ForeColor = Color.FromArgb(255, 147, 166);
        right.Controls.AddRange([close, maximize, minimize]);
        close.Click += (_, _) => Close();
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => ToggleMaximize();

        WireDrag(bar);
        WireDrag(center);
        WireDrag(brand);
        WireDrag(logo);
        brand.DoubleClick += (_, _) => ToggleMaximize();
        center.DoubleClick += (_, _) => ToggleMaximize();

        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(center, 1, 0);
        bar.Controls.Add(right, 2, 0);
        return bar;
    }

    private Control BuildWorkspace()
    {
        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            Panel2MinSize = 300,
            SplitterWidth = 1,
            BackColor = Border
        };
        outer.Panel1.BackColor = Bg;
        outer.Panel2.BackColor = Panel;

        var left = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            Panel2MinSize = 165,
            SplitterWidth = 1,
            BackColor = Border
        };
        left.Panel1.BackColor = Bg;
        left.Panel2.BackColor = Bg;

        left.Panel1.Controls.Add(BuildBrowserPanel());
        left.Panel2.Controls.Add(BuildBottomPanel(left));
        outer.Panel1.Controls.Add(left);
        outer.Panel2.Controls.Add(BuildAccountPanel());

        void LayoutWorkspaceSplitters()
        {
            SetSplitterDistanceSafe(outer, Math.Max(900, outer.ClientSize.Width - 315));
            SetSplitterDistanceSafe(left, Math.Max(280, left.ClientSize.Height - 195));
        }

        outer.Resize += (_, _) => LayoutWorkspaceSplitters();
        left.Resize += (_, _) => LayoutWorkspaceSplitters();
        outer.HandleCreated += (_, _) =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(LayoutWorkspaceSplitters);
        };

        return outer;
    }

    private Control BuildBrowserPanel()
    {
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(8, 4, 8, 6)
        };

        var accountHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Panel
        };
        _currentAccountLabel.Location = new Point(18, 15);
        accountHeader.Controls.Add(_currentAccountLabel);

        var nav = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            BackColor = Panel,
            ColumnCount = 9,
            RowCount = 1,
            Padding = new Padding(8, 7, 8, 6),
            Margin = Padding.Empty
        };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        var back = CreateButton("‹", 34);
        var forward = CreateButton("›", 34);
        var reload = CreateButton("↻", 40);
        var home = CreateButton("⌂", 40);
        var repair = CreateButton("无水印下载", 92);
        var material = CreateButton("+ 素材", 76);
        var duration15 = CreateButton("15 秒", 64);
        var duration30 = CreateButton("30 秒", 64, true);

        back.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        forward.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        reload.Font = new Font("Segoe UI Symbol", 12F);
        home.Font = new Font("Segoe UI Symbol", 12F);

        nav.Controls.Add(back, 0, 0);
        nav.Controls.Add(forward, 1, 0);
        nav.Controls.Add(_addressBox, 2, 0);
        nav.Controls.Add(reload, 3, 0);
        nav.Controls.Add(home, 4, 0);
        nav.Controls.Add(repair, 5, 0);
        nav.Controls.Add(material, 6, 0);
        nav.Controls.Add(duration15, 7, 0);
        nav.Controls.Add(duration30, 8, 0);

        back.Click += (_, _) =>
        {
            var core = SelectedSession()?.Browser.CoreWebView2;
            if (core?.CanGoBack == true) core.GoBack();
        };
        forward.Click += (_, _) =>
        {
            var core = SelectedSession()?.Browser.CoreWebView2;
            if (core?.CanGoForward == true) core.GoForward();
        };
        reload.Click += (_, _) => SelectedSession()?.Browser.CoreWebView2?.Reload();
        home.Click += (_, _) => SelectedSession()?.NavigateHome();
        repair.Click += async (_, _) => await RepairCurrentPreviewAsync();
        material.Click += (_, _) => PickMaterial();
        duration15.Click += async (_, _) =>
        {
            await SetCurrentDurationAsync(15);
            SetDurationButtonState(duration15, duration30, 15);
        };
        duration30.Click += async (_, _) =>
        {
            await SetCurrentDurationAsync(30);
            SetDurationButtonState(duration15, duration30, 30);
        };
        _addressBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            NavigateAddress();
            e.SuppressKeyPress = true;
        };

        var browserHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Padding = new Padding(1)
        };
        browserHost.Controls.Add(_browserTabs);

        shell.Controls.Add(browserHost);
        shell.Controls.Add(nav);
        shell.Controls.Add(accountHeader);
        return shell;
    }

    private Control BuildBottomPanel(SplitContainer hostSplit)
    {
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(8, 6, 8, 8)
        };

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Panel,
            Padding = new Padding(8, 5, 8, 4)
        };
        var title = new Label
        {
            AutoSize = true,
            Text = "作品库",
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 7, 0, 0)
        };
        var refresh = CreateButton("↻ 刷新", 72);
        var openFolder = CreateButton("作品文件夹", 92);
        var library = CreateButton("作品库", 70, true);
        var tasks = CreateButton("任务中心", 78);
        var logs = CreateButton("日志", 62);
        var expand = CreateButton("展开", 62);

        header.Controls.Add(title);
        header.Controls.Add(_workCountLabel);
        header.Controls.Add(refresh);
        header.Controls.Add(openFolder);
        header.Controls.Add(library);
        header.Controls.Add(tasks);
        header.Controls.Add(logs);
        header.Controls.Add(expand);

        _libraryPanel = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _libraryPanel.Controls.Add(_workLibrary);

        _taskPanel = BuildTaskCenterPanel();
        _taskPanel.Dock = DockStyle.Fill;
        _taskPanel.Visible = false;

        shell.Controls.Add(_libraryPanel);
        shell.Controls.Add(_taskPanel);
        shell.Controls.Add(header);

        refresh.Click += (_, _) => RefreshWorkLibrary();
        openFolder.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", VideoDir) { UseShellExecute = true });
        library.Click += (_, _) =>
        {
            ShowBottomPanel(showTasks: false);
            library.BackColor = Accent;
            tasks.BackColor = PanelRaised;
        };
        tasks.Click += (_, _) =>
        {
            ShowBottomPanel(showTasks: true);
            tasks.BackColor = Accent;
            library.BackColor = PanelRaised;
        };
        logs.Click += (_, _) =>
        {
            ShowBottomPanel(showTasks: true);
            _log.Focus();
        };
        expand.Click += (_, _) =>
        {
            var expanded = hostSplit.Height - hostSplit.SplitterDistance > 300;
            var desiredBottom = expanded ? 195 : Math.Min(390, Math.Max(260, hostSplit.Height / 2));
            SetSplitterDistanceSafe(hostSplit, Math.Max(280, hostSplit.ClientSize.Height - desiredBottom));
            expand.Text = expanded ? "展开" : "收起";
        };

        return shell;
    }

    private Panel BuildTaskCenterPanel()
    {
        var taskPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Bg
        };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            Panel2MinSize = 250,
            SplitterWidth = 1,
            BackColor = Border
        };
        split.Panel1.BackColor = Bg;
        split.Panel2.BackColor = Color.Black;
        split.Resize += (_, _) =>
            SetSplitterDistanceSafe(split, Math.Max(480, split.ClientSize.Width - 270));

        var taskLeft = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Bg
        };
        taskLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        taskLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        taskLeft.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var taskToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Panel,
            Padding = new Padding(6, 4, 6, 4)
        };
        var addTask = CreateButton("加入视频任务", 94, true);
        var startBatch = CreateButton("开始并行生成", 104, true);
        var stop = CreateButton("停止", 62, false, true);
        var retry = CreateButton("重试失败", 78);
        var preview = CreateButton("本地预览", 78);
        var export = CreateButton("导出视频", 78);
        taskToolbar.Controls.AddRange([addTask, startBatch, stop, retry, preview, export]);

        taskLeft.Controls.Add(taskToolbar, 0, 0);
        taskLeft.Controls.Add(_prompt, 0, 1);
        taskLeft.Controls.Add(_grid, 0, 2);
        split.Panel1.Controls.Add(taskLeft);
        split.Panel2.Controls.Add(_preview);

        body.Controls.Add(split, 0, 0);
        body.Controls.Add(_log, 0, 1);
        taskPanel.Controls.Add(body);

        addTask.Click += (_, _) => AddJob();
        startBatch.Click += async (_, _) => await StartQueueAsync();
        stop.Click += (_, _) => StopCurrentOperation();
        retry.Click += async (_, _) => await RetryFailedJobsWithFeedbackAsync();
        preview.Click += (_, _) => PreviewSelected();
        export.Click += (_, _) => ExportSelected();

        return taskPanel;
    }

    private Control BuildAccountPanel()
    {
        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Panel,
            Padding = new Padding(12, 10, 12, 10)
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Panel
        };
        var title = new Label
        {
            AutoSize = true,
            Text = "账号管理",
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Location = new Point(2, 8)
        };
        _accountReadyBadge.Location = new Point(186, 3);
        header.Controls.Add(title);
        header.Controls.Add(_accountReadyBadge);

        var imports = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 84,
            BackColor = Panel,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 3, 0, 6)
        };
        imports.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imports.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        imports.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        imports.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var importAccounts = CreateButton("导入账号密码", 0);
        var importDola = CreateButton("导入 Dola Cookie", 0);
        var importFb = CreateButton("导入 FB Cookie", 0);
        var manualLogin = CreateButton("手动登录", 0);
        importAccounts.Dock = DockStyle.Fill;
        importDola.Dock = DockStyle.Fill;
        importFb.Dock = DockStyle.Fill;
        manualLogin.Dock = DockStyle.Fill;
        importFb.Enabled = false;

        imports.Controls.Add(importAccounts, 0, 0);
        imports.Controls.Add(importDola, 1, 0);
        imports.Controls.Add(importFb, 0, 1);
        imports.Controls.Add(manualLogin, 1, 1);

        var searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = Panel,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 4)
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        var clearSearch = CreateButton("清空", 66);
        clearSearch.Dock = DockStyle.Fill;
        searchRow.Controls.Add(_accountSearch, 0, 0);
        searchRow.Controls.Add(clearSearch, 1, 0);

        var summary = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Panel
        };
        _accountCounterLabel.Location = new Point(2, 6);
        var all = CreateButton("全部", 66);
        var add = CreateButton("+ 新建", 70);
        all.Location = new Point(134, 1);
        add.Location = new Point(205, 1);
        summary.Controls.Add(_accountCounterLabel);
        summary.Controls.Add(all);
        summary.Controls.Add(add);

        var listLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "账号列表",
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        };

        shell.Controls.Add(_accountList);
        shell.Controls.Add(listLabel);
        shell.Controls.Add(summary);
        shell.Controls.Add(searchRow);
        shell.Controls.Add(imports);
        shell.Controls.Add(header);

        importAccounts.Click += async (_, _) => await ImportGoogleAccountsAsync();
        importDola.Click += async (_, _) => await ImportDolaCookiesAsync();
        manualLogin.Click += (_, _) =>
        {
            var session = SelectedSession();
            if (session is null) return;
            session.NavigateHome();
            _status.Text = $"{session.Name} 已切换到 Dola 登录/主页，可在中间窗口手动操作。";
        };
        clearSearch.Click += (_, _) => _accountSearch.Clear();
        all.Click += (_, _) => _accountSearch.Clear();
        add.Click += async (_, _) =>
        {
            if (_windowCount.Value >= _windowCount.Maximum)
            {
                _status.Text = $"已达到当前版本最大账号窗口数：{_windowCount.Maximum}。";
                return;
            }

            _windowCount.Value++;
            await SetSessionCountAsync((int)_windowCount.Value);
        };

        var toolTip = new ToolTip();
        toolTip.SetToolTip(importFb, "v1.01 预留入口，当前代码未实现 Facebook Cookie 导入。");

        return shell;
    }

    private void ConfigureGridTheme()
    {
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(13, 27, 52);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(13, 27, 52);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(7, 15, 30);
        _grid.DefaultCellStyle.ForeColor = TextPrimary;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 63, 111);
        _grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        _grid.RowHeadersVisible = false;
    }

    private Button CreateButton(string text, int width = 0, bool accent = false, bool danger = false)
    {
        var button = new Button
        {
            Text = text,
            Height = 32,
            AutoSize = width <= 0,
            Width = width > 0 ? width : 82,
            Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Accent : PanelRaised,
            ForeColor = danger ? Danger : TextPrimary,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = accent ? AccentSoft : Border;
        button.FlatAppearance.MouseOverBackColor = accent
            ? Color.FromArgb(97, 145, 255)
            : PanelHover;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(31, 56, 101);
        return button;
    }

    private Button CreateWindowButton(string text)
    {
        var button = CreateButton(text, 36);
        button.Height = 30;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.Transparent;
        button.Margin = new Padding(2);
        return button;
    }

    private void ShowReservedEntry(string name)
    {
        MessageBox.Show(
            $"{name}入口已按参考界面预留。\n当前仓库没有对应的商城/卡密服务地址，所以 v1.01 暂不跳转外部页面。",
            AppTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowBottomPanel(bool showTasks)
    {
        if (_libraryPanel is null || _taskPanel is null) return;
        _taskPanel.Visible = showTasks;
        _libraryPanel.Visible = !showTasks;
        if (showTasks) _taskPanel.BringToFront();
        else _libraryPanel.BringToFront();
    }

    private void PickMaterial()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择素材",
            Filter = "图片/视频|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov;*.m4v|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            Clipboard.SetText(dialog.FileName);
            _status.Text = $"已选择素材并复制路径：{Path.GetFileName(dialog.FileName)}。可在 Dola 上传区域继续选择/粘贴。";
        }
        catch
        {
            _status.Text = $"已选择素材：{dialog.FileName}";
        }
    }

    private async Task RepairCurrentPreviewAsync()
    {
        var session = SelectedSession();
        if (session is null) return;

        try
        {
            var changed = await session.RepairVideoPreviewsAsync();
            _status.Text = changed > 0
                ? $"已修复当前 Dola 页面中的 {changed} 个视频播放器。"
                : "当前页面未发现需要替换的视频播放器；可先打开生成结果再重试。";
        }
        catch (Exception ex)
        {
            _status.Text = "页面视频处理失败：" + ex.Message;
        }
    }

    private async Task SetCurrentDurationAsync(int seconds)
    {
        var session = SelectedSession();
        if (session?.Browser.CoreWebView2 is null) return;

        var result = await session.Browser.CoreWebView2.ExecuteScriptAsync($$"""
            (() => {
              const target = {{seconds}};
              const visible = el => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
              };
              const textOf = el => (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
              const re = new RegExp('(^|\\D)' + target + '\\s*(秒|s|sec|seconds?)($|\\D)', 'i');
              const nodes = [...document.querySelectorAll('button,[role="button"],label,div,span')].filter(visible);
              const hit = nodes.find(x => re.test(textOf(x)));
              if (!hit) return 'NOT_FOUND';
              (hit.closest('button,[role="button"],label') || hit).click();
              return 'CLICKED';
            })();
            """);

        _status.Text = result.Contains("CLICKED", StringComparison.OrdinalIgnoreCase)
            ? $"已尝试切换 Dola 视频时长为 {seconds} 秒。"
            : $"当前 Dola 页面未找到 {seconds} 秒时长按钮，请先进入视频生成模式。";
    }

    private void SetDurationButtonState(Button duration15, Button duration30, int selected)
    {
        duration15.BackColor = selected == 15 ? Accent : PanelRaised;
        duration30.BackColor = selected == 30 ? Accent : PanelRaised;
    }

    private void NavigateAddress()
    {
        var session = SelectedSession();
        if (session?.Browser.CoreWebView2 is null) return;

        var value = _addressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            _status.Text = "地址格式不正确。";
            return;
        }

        session.Browser.CoreWebView2.Navigate(uri.ToString());
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _preview.EnsureCoreWebView2Async();
            _preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "aivideo.local",
                VideoDir,
                CoreWebView2HostResourceAccessKind.Allow);
            _preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            await SetSessionCountAsync((int)_windowCount.Value);
            RefreshWorkLibrary();
            _status.Text = $"就绪：{_sessions.Count} 个独立 Profile；可批量导入 Google 账号或 Dola Cookie。";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SetSessionCountAsync(int targetCount)
    {
        targetCount = Math.Clamp(targetCount, 1, (int)_windowCount.Maximum);

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
                $"Dola Cookie {number:00}",
                profileDir,
                proxy,
                VideoDir,
                Log,
                OnJobChanged);
            var page = new TabPage(session.Name)
            {
                BackColor = Bg,
                ForeColor = TextPrimary,
                Padding = Padding.Empty
            };
            page.Controls.Add(session.Browser);
            _browserTabs.TabPages.Add(page);
            _sessions.Add(session);

            try
            {
                await session.InitializeAsync();
                session.Browser.CoreWebView2.SourceChanged += (_, _) =>
                {
                    if (IsDisposed || Disposing) return;
                    BeginInvoke(() =>
                    {
                        if (SelectedSession() == session)
                            _addressBox.Text = session.Browser.Source?.ToString() ?? string.Empty;
                    });
                };
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

        RefreshAccountCards();
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
            RefreshAccountCards();
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
            _currentAccountLabel.Text = "当前账号：无";
            _addressBox.Text = string.Empty;
            return;
        }

        _proxyBox.Text = session.ProxyUrl;
        var mode = string.IsNullOrWhiteSpace(session.ProxyUrl) ? "直连" : session.ProxyUrl;
        _networkState.Text = session.LastObservedIp is null
            ? $"{session.Name} | {mode} | 独立Profile"
            : $"{session.Name} | {mode} | 出口IP: {session.LastObservedIp}";
        _currentAccountLabel.Text = $"当前账号：{GetSessionDisplayName(session.Number - 1)}";
        _addressBox.Text = session.Browser.Source?.ToString() ?? string.Empty;
        _status.Text = $"当前页面: {session.Name} | 独立 Profile | 网络: {mode}";
        HighlightSelectedAccountCard();
    }

    private DolaSession? SelectedSession()
    {
        var index = _browserTabs.SelectedIndex;
        return index >= 0 && index < _sessions.Count ? _sessions[index] : null;
    }

    private void RefreshAccountCards()
    {
        if (_accountList.IsDisposed) return;

        _accountList.SuspendLayout();
        _accountList.Controls.Clear();

        for (var i = 0; i < _sessions.Count; i++)
        {
            var index = i;
            var session = _sessions[i];
            var card = new Panel
            {
                Height = 108,
                Width = Math.Max(245, _accountList.ClientSize.Width - 28),
                BackColor = index == _browserTabs.SelectedIndex ? Color.FromArgb(16, 33, 61) : Color.FromArgb(10, 21, 42),
                Margin = new Padding(2, 3, 2, 6),
                Padding = new Padding(10),
                Tag = GetSessionDisplayName(index)
            };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(index == _browserTabs.SelectedIndex ? Accent : Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var title = new Label
            {
                AutoSize = true,
                Text = GetSessionDisplayName(index),
                ForeColor = TextPrimary,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Location = new Point(13, 10)
            };
            var state = new Label
            {
                AutoSize = true,
                Text = session.IsReady ? "● Dola已就绪" : "● 未就绪",
                ForeColor = session.IsReady ? Success : Danger,
                Location = new Point(13, 34)
            };
            var network = new Label
            {
                AutoSize = true,
                Text = string.IsNullOrWhiteSpace(session.ProxyUrl)
                    ? "直连"
                    : session.ProxyUrl,
                ForeColor = TextMuted,
                Location = new Point(128, 34),
                MaximumSize = new Size(Math.Max(80, card.Width - 140), 20),
                AutoEllipsis = true
            };

            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Location = new Point(8, 60),
                BackColor = Color.Transparent
            };
            var select = CreateButton("选择", 54);
            var info = CreateButton("信息", 54);
            var networkButton = CreateButton("网络", 54);
            var refresh = CreateButton("刷新", 54);
            select.Height = info.Height = networkButton.Height = refresh.Height = 28;

            select.Click += (_, _) =>
            {
                if (index < _browserTabs.TabPages.Count)
                    _browserTabs.SelectedIndex = index;
            };
            info.Click += (_, _) => ShowSessionInfo(session, index);
            networkButton.Click += async (_, _) =>
            {
                if (index < _browserTabs.TabPages.Count)
                    _browserTabs.SelectedIndex = index;
                await ShowNetworkDialogAsync(session);
            };
            refresh.Click += (_, _) =>
            {
                if (index < _browserTabs.TabPages.Count)
                    _browserTabs.SelectedIndex = index;
                session.NavigateHome();
            };

            actions.Controls.AddRange([select, info, networkButton, refresh]);
            card.Controls.Add(title);
            card.Controls.Add(state);
            card.Controls.Add(network);
            card.Controls.Add(actions);
            _accountList.Controls.Add(card);
        }

        _accountList.ResumeLayout();
        ApplyAccountFilter();
        HighlightSelectedAccountCard();
        UpdateAccountSummary();
    }

    private void ResizeAccountCards()
    {
        foreach (Control control in _accountList.Controls)
        {
            if (control is Panel card)
                card.Width = Math.Max(245, _accountList.ClientSize.Width - 28);
        }
    }

    private void ApplyAccountFilter()
    {
        var query = _accountSearch.Text.Trim();
        foreach (Control control in _accountList.Controls)
        {
            var text = control.Tag as string ?? string.Empty;
            control.Visible = string.IsNullOrWhiteSpace(query) ||
                              text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        UpdateAccountSummary();
    }

    private void UpdateAccountSummary()
    {
        var visible = _accountList.Controls.Cast<Control>().Count(x => x.Visible);
        _accountCounterLabel.Text = $"显示 {visible} / {_sessions.Count}";
        var ready = _sessions.Count(x => x.IsReady);
        _accountReadyBadge.Text = $"就绪 {ready}";
    }

    private void HighlightSelectedAccountCard()
    {
        for (var i = 0; i < _accountList.Controls.Count; i++)
        {
            if (_accountList.Controls[i] is not Panel card) continue;
            card.BackColor = i == _browserTabs.SelectedIndex
                ? Color.FromArgb(16, 33, 61)
                : Color.FromArgb(10, 21, 42);
            card.Invalidate();
        }
    }

    private string GetSessionDisplayName(int index)
    {
        if (index >= 0 && index < _browserTabs.TabPages.Count)
            return _browserTabs.TabPages[index].Text;
        return $"Dola Cookie {index + 1:00}";
    }

    private void ShowSessionInfo(DolaSession session, int index)
    {
        var mode = string.IsNullOrWhiteSpace(session.ProxyUrl) ? "直连" : session.ProxyUrl;
        MessageBox.Show(
            $"账号：{GetSessionDisplayName(index)}\n" +
            $"状态：{(session.IsReady ? "Dola已就绪" : "未就绪")}\n" +
            $"网络：{mode}\n" +
            $"出口IP：{session.LastObservedIp ?? "未测试"}\n" +
            $"Profile：{session.ProfileDir}",
            "账号信息",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task ShowNetworkDialogAsync(DolaSession session)
    {
        using var dialog = new Form
        {
            Text = $"{session.Name} 网络设置",
            Width = 520,
            Height = 230,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Panel,
            ForeColor = TextPrimary,
            Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var proxyLabel = new Label
        {
            AutoSize = true,
            Text = "固定代理（留空=直连）",
            ForeColor = TextMuted,
            Location = new Point(18, 18)
        };
        var proxy = new TextBox
        {
            Text = session.ProxyUrl,
            Left = 18,
            Top = 44,
            Width = 465,
            BackColor = Color.FromArgb(7, 15, 30),
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };
        var status = new Label
        {
            AutoSize = true,
            Text = session.LastObservedIp is null ? "出口IP：未测试" : $"出口IP：{session.LastObservedIp}",
            ForeColor = TextMuted,
            Location = new Point(18, 79)
        };
        var save = CreateButton("保存并重载", 104, true);
        var direct = CreateButton("设为直连", 84);
        var test = CreateButton("测试出口IP", 96);
        var cancel = CreateButton("关闭", 72);
        save.Location = new Point(18, 116);
        direct.Location = new Point(130, 116);
        test.Location = new Point(222, 116);
        cancel.Location = new Point(386, 116);

        test.Click += async (_, _) =>
        {
            try
            {
                test.Enabled = false;
                status.Text = "出口IP：测试中...";
                var ip = await session.TestNetworkAsync();
                status.Text = $"出口IP：{ip}";
                RefreshAccountCards();
            }
            catch (Exception ex)
            {
                status.Text = "出口IP：测试失败";
                MessageBox.Show(ex.Message, "网络测试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                test.Enabled = true;
            }
        };
        direct.Click += (_, _) => proxy.Clear();
        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            await ApplyCurrentProxyAsync(proxy.Text);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Controls.AddRange([proxyLabel, proxy, status, save, direct, test, cancel]);
        dialog.ShowDialog(this);
    }

    private void RefreshWorkLibrary()
    {
        if (_workLibrary.IsDisposed) return;

        _workLibrary.SuspendLayout();
        _workLibrary.Controls.Clear();

        var files = Directory.Exists(VideoDir)
            ? Directory.GetFiles(VideoDir, "*.mp4", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .Take(40)
                .ToList()
            : [];

        _workCountLabel.Text = $"{files.Count} 个作品";

        if (files.Count == 0)
        {
            _workLibrary.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "还没有本地作品。通过任务中心生成或下载视频后，会自动出现在这里。",
                ForeColor = TextMuted,
                Margin = new Padding(8, 28, 0, 0)
            });
            _workLibrary.ResumeLayout();
            return;
        }

        var cardWidth = Math.Max(220, Math.Min(260, _workLibrary.ClientSize.Width / 4));
        foreach (var file in files)
        {
            var card = new Panel
            {
                Width = cardWidth,
                Height = 112,
                BackColor = PanelRaised,
                Margin = new Padding(4, 2, 8, 4),
                Padding = new Padding(10)
            };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var icon = new Label
            {
                Text = "▶",
                ForeColor = AccentSoft,
                BackColor = Color.FromArgb(5, 12, 25),
                Font = new Font("Segoe UI Symbol", 17F),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 10),
                Size = new Size(58, 58)
            };
            var title = new Label
            {
                Text = file.Name,
                ForeColor = TextPrimary,
                AutoEllipsis = true,
                Location = new Point(78, 12),
                Size = new Size(card.Width - 90, 21),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            var meta = new Label
            {
                Text = $"{FormatFileSize(file.Length)}  ·  {file.LastWriteTime:MM-dd HH:mm}",
                ForeColor = TextMuted,
                AutoEllipsis = true,
                Location = new Point(78, 37),
                Size = new Size(card.Width - 90, 20)
            };
            var open = CreateButton("播放", 54);
            var folder = CreateButton("位置", 54);
            open.Height = folder.Height = 27;
            open.Location = new Point(78, 66);
            folder.Location = new Point(138, 66);
            open.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true });
            folder.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });

            card.Controls.Add(icon);
            card.Controls.Add(title);
            card.Controls.Add(meta);
            card.Controls.Add(open);
            card.Controls.Add(folder);
            _workLibrary.Controls.Add(card);
        }

        _workLibrary.ResumeLayout();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
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
        if (job.Status == JobStatus.Completed)
            RefreshWorkLibrary();
        UpdateStatus();
    }

    private static void SetSplitterDistanceSafe(SplitContainer split, int desired)
    {
        var span = split.Orientation == Orientation.Vertical
            ? split.ClientSize.Width
            : split.ClientSize.Height;
        var minimum = Math.Max(0, split.Panel1MinSize);
        var maximum = span - split.Panel2MinSize - split.SplitterWidth;

        if (maximum < minimum)
            return;

        var distance = Math.Clamp(desired, minimum, maximum);
        if (split.SplitterDistance != distance)
            split.SplitterDistance = distance;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    private void WireDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
        };
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

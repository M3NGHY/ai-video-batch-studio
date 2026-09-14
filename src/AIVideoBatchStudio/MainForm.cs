using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIVideoBatchStudio;

internal sealed partial class MainForm : Form
{
    private const string DolaUrl = "https://www.dola.com/chat";
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
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
    private readonly ToolStripStatusLabel _status = new("初始化中...");

    private bool _running;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource<string>? _resultWaiter;
    private Guid? _activeJobId;

    private static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIVideoBatchStudio");
    private static string ProfileDir => Path.Combine(AppRoot, "profile");
    private static string VideoDir => Path.Combine(AppRoot, "videos");

    public MainForm()
    {
        Text = "AI 视频批量创作台 MVP";
        Width = 1500;
        Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(VideoDir);
        BuildUi();
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => _runCts?.Cancel();
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
        var start = new Button { Text = "开始批量", AutoSize = true };
        var stop = new Button { Text = "停止", AutoSize = true };
        var retry = new Button { Text = "重试失败", AutoSize = true };
        var preview = new Button { Text = "本地预览", AutoSize = true };
        var export = new Button { Text = "导出视频", AutoSize = true };
        var folder = new Button { Text = "打开视频目录", AutoSize = true };
        var dola = new Button { Text = "打开 Dola", AutoSize = true };
        toolbar.Controls.AddRange([add, start, stop, retry, preview, export, folder, dola]);

        var taskPanel = new Panel { Dock = DockStyle.Fill };
        taskPanel.Controls.Add(_grid);
        taskPanel.Controls.Add(_prompt);

        var bottom = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 880 };
        bottom.Panel1.Controls.Add(taskPanel);
        bottom.Panel2.Controls.Add(_preview);

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 470 };
        main.Panel1.Controls.Add(_browser);
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
        dola.Click += (_, _) => _browser.CoreWebView2?.Navigate(DolaUrl);
        _grid.SelectionChanged += (_, _) => UpdateStatus();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir);
            await _browser.EnsureCoreWebView2Async(env);
            await _preview.EnsureCoreWebView2Async();
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.WebMessageReceived += BrowserOnWebMessageReceived;
            _browser.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                try { await InstallBridgeAsync(); Log("页面监听脚本已注入。"); }
                catch (Exception ex) { Log("监听脚本注入失败: " + ex.Message); }
            };
            _browser.Source = new Uri(DolaUrl);
            _status.Text = "就绪：先在上方 Dola 页面登录账号，然后加入任务。";
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

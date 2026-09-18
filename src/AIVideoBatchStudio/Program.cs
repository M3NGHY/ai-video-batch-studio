namespace AIVideoBatchStudio;

internal static class Program
{
    private const string ProductTitle = "梦天Dola工作台 v1.01";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal("UI线程异常", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
            ReportFatal("程序未处理异常", ex);
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatal("启动失败", ex);
        }
    }

    private static void ReportFatal(string stage, Exception ex)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIVideoBatchStudio");
            Directory.CreateDirectory(root);
            var logPath = Path.Combine(root, "startup-error.log");
            File.AppendAllText(
                logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");

            MessageBox.Show(
                $"{ProductTitle} {stage}。\n\n{ex.Message}\n\n错误日志已写入：\n{logPath}",
                ProductTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            try
            {
                MessageBox.Show(
                    $"{ProductTitle} {stage}。\n\n{ex}",
                    ProductTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // Nothing else can safely be done during a fatal startup failure.
            }
        }
    }
}

internal enum JobStatus
{
    Pending,
    Running,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

internal sealed class VideoJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Prompt { get; set; } = string.Empty;
    public string? Worker { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? RemoteUrl { get; set; }
    public string? LocalPath { get; set; }
    public string? Error { get; set; }
}

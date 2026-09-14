namespace AIVideoBatchStudio;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
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

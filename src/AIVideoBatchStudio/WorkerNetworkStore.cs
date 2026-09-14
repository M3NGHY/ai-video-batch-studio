using System.Text.Json;

namespace AIVideoBatchStudio;

internal static class WorkerNetworkStore
{
    private static readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIVideoBatchStudio");

    private static readonly string ConfigPath = Path.Combine(AppRoot, "worker-network.json");

    public static Dictionary<int, string> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return [];
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Dictionary<int, string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(Dictionary<int, string> config)
    {
        Directory.CreateDirectory(AppRoot);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}

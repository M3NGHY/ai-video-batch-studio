using System.Text.RegularExpressions;

namespace AIVideoBatchStudio;

internal sealed record GoogleAccountImportResult(
    IReadOnlyList<GoogleAccount> Accounts,
    int TotalNonEmptyLines,
    int RejectedLines);

internal static partial class GoogleAccountTextImporter
{
    [GeneratedRegex(@"\s*-{4,}\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DashSeparatorRegex();

    public static GoogleAccountImportResult Parse(string path, int maxAccounts = 10)
    {
        var text = File.ReadAllText(path);
        text = text.Replace("\uFEFF", string.Empty)
                   .Replace("\u200B", string.Empty)
                   .Replace("\u2060", string.Empty)
                   .Replace("\r\n", "\n")
                   .Replace('\r', '\n');

        var accounts = new List<GoogleAccount>();
        var total = 0;
        var rejected = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            total++;

            if (!TryParseLine(line, out var account))
            {
                rejected++;
                continue;
            }

            accounts.Add(account);
            if (accounts.Count >= maxAccounts) break;
        }

        return new GoogleAccountImportResult(accounts, total, rejected);
    }

    private static bool TryParseLine(string line, out GoogleAccount account)
    {
        account = new GoogleAccount(string.Empty, string.Empty);

        // User's primary format: email@gmail.com----password
        var match = DashSeparatorRegex().Match(line);
        if (match.Success)
        {
            var email = Clean(line[..match.Index]);
            var password = Clean(line[(match.Index + match.Length)..]);
            return TryCreate(email, password, out account);
        }

        // Compatibility fallbacks for older account lists.
        foreach (var separator in new[] { "\t", "|", "," })
        {
            var index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            var email = Clean(line[..index]);
            var password = Clean(line[(index + separator.Length)..]);
            if (TryCreate(email, password, out account)) return true;
        }

        return false;
    }

    private static bool TryCreate(string email, string password, out GoogleAccount account)
    {
        account = new GoogleAccount(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return false;
        if (!email.Contains('@') || email.Contains(' ')) return false;
        if (email.Equals("谷歌账号", StringComparison.OrdinalIgnoreCase) ||
            email.Equals("google account", StringComparison.OrdinalIgnoreCase) ||
            email.Equals("email", StringComparison.OrdinalIgnoreCase)) return false;

        account = new GoogleAccount(email, password);
        return true;
    }

    private static string Clean(string value)
    {
        value = value.Trim().Trim('\uFEFF', '\u200B', '\u2060');
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\"\"", "\"");
        return value.Trim();
    }
}

using System.Text;
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

    [GeneratedRegex(@"^(?<email>[^\s,;|:\t]+@[^\s,;|:\t]+)\s+(?<password>\S+)(?:\s+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceAccountRegex();

    public static GoogleAccountImportResult Parse(string path, int maxAccounts = 20)
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

            if (accounts.Any(x => x.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)))
                continue;

            accounts.Add(account);
            if (accounts.Count >= maxAccounts) break;
        }

        return new GoogleAccountImportResult(accounts, total, rejected);
    }

    private static bool TryParseLine(string line, out GoogleAccount account)
    {
        account = new GoogleAccount(string.Empty, string.Empty);

        // Most purchased/exported account lists use:
        // email----password
        // email----password----recoveryEmail----2FA...
        // Only the SECOND field is the password. The old implementation treated
        // every field after the first separator as part of the password.
        var dashMatches = DashSeparatorRegex().Matches(line);
        if (dashMatches.Count > 0)
        {
            var first = dashMatches[0];
            var email = Clean(line[..first.Index]);
            var passwordStart = first.Index + first.Length;
            var passwordEnd = dashMatches.Count > 1 ? dashMatches[1].Index : line.Length;
            var password = Clean(line[passwordStart..passwordEnd]);
            return TryCreate(email, password, out account);
        }

        // TSV / pipe / semicolon / colon account lists. Extra columns are ignored.
        foreach (var separator in new[] { "\t", "|", ";", ":" })
        {
            var first = line.IndexOf(separator, StringComparison.Ordinal);
            if (first <= 0) continue;

            var email = Clean(line[..first]);
            var rest = line[(first + separator.Length)..];
            var next = rest.IndexOf(separator, StringComparison.Ordinal);
            var password = Clean(next >= 0 ? rest[..next] : rest);
            if (TryCreate(email, password, out account)) return true;
        }

        // CSV including quoted fields. Only email + password are required.
        if (line.Contains(','))
        {
            var fields = SplitCsv(line);
            if (fields.Count >= 2 &&
                TryCreate(Clean(fields[0]), Clean(fields[1]), out account))
                return true;
        }

        // Last-resort compatibility: "email password [extra...]".
        var whitespace = WhitespaceAccountRegex().Match(line);
        if (whitespace.Success)
        {
            return TryCreate(
                Clean(whitespace.Groups["email"].Value),
                Clean(whitespace.Groups["password"].Value),
                out account);
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
            email.Equals("email", StringComparison.OrdinalIgnoreCase) ||
            email.Equals("账号", StringComparison.OrdinalIgnoreCase)) return false;

        account = new GoogleAccount(email, password);
        return true;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static string Clean(string value)
    {
        value = value.Trim().Trim('\uFEFF', '\u200B', '\u2060');
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\"\"", "\"");
        return value.Trim();
    }
}

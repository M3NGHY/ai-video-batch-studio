using System.Text.Json;

namespace AIVideoBatchStudio;

internal sealed record GoogleAccount(string Email, string Password);

internal sealed record ImportedCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure = false,
    bool HttpOnly = false,
    DateTime? Expires = null);

internal sealed record CookieBundle(string Label, IReadOnlyList<ImportedCookie> Cookies);

internal static class AccountImportParser
{
    public static List<GoogleAccount> ParseGoogleAccounts(string path)
    {
        var lines = File.ReadAllLines(path);
        var result = new List<GoogleAccount>();

        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            if (line.StartsWith("email", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains(',') || line.Contains('|') || line.Contains('\t'))) continue;

            var parts = SplitAccountLine(line);
            if (parts.Items.Count < 2) continue;

            var email = Unquote(parts.Items[0]).Trim();
            var password = Unquote(string.Join(parts.Separator, parts.Items.Skip(1))).Trim();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) continue;
            if (!email.Contains('@')) continue;

            result.Add(new GoogleAccount(email, password));
            if (result.Count >= 10) break;
        }

        return result;
    }

    public static List<CookieBundle> ParseCookieFile(string path)
    {
        var text = File.ReadAllText(path).Trim().Trim('\uFEFF');
        if (string.IsNullOrWhiteSpace(text)) return [];

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                var json = ParseCookieJson(text, Path.GetFileNameWithoutExtension(path));
                if (json.Count > 0) return json;
            }
            catch (JsonException)
            {
                // Fall through to text formats.
            }
        }

        var netscape = ParseNetscape(text, Path.GetFileNameWithoutExtension(path));
        if (netscape.Count > 0) return netscape;

        return ParseRawCookieHeaders(text, Path.GetFileNameWithoutExtension(path));
    }

    private static List<CookieBundle> ParseCookieJson(string text, string label)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var result = new List<CookieBundle>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
            {
                var i = 1;
                foreach (var account in accounts.EnumerateArray())
                {
                    var cookies = ExtractCookieArray(account);
                    if (cookies.Count > 0) result.Add(new CookieBundle($"{label}-{i++}", cookies));
                }
                return result;
            }

            var single = ExtractCookieArray(root);
            if (single.Count > 0) result.Add(new CookieBundle(label, single));
            return result;
        }

        if (root.ValueKind != JsonValueKind.Array) return result;
        var items = root.EnumerateArray().ToList();
        if (items.Count == 0) return result;

        if (items[0].ValueKind == JsonValueKind.Array ||
            (items[0].ValueKind == JsonValueKind.Object && items[0].TryGetProperty("cookies", out _)))
        {
            var i = 1;
            foreach (var item in items)
            {
                var cookies = ExtractCookieArray(item);
                if (cookies.Count > 0) result.Add(new CookieBundle($"{label}-{i++}", cookies));
            }
            return result;
        }

        var flat = ParseCookieElements(items);
        if (flat.Count > 0) result.Add(new CookieBundle(label, flat));
        return result;
    }

    private static List<ImportedCookie> ExtractCookieArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return ParseCookieElements(element.EnumerateArray());

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("cookies", out var cookies) &&
            cookies.ValueKind == JsonValueKind.Array)
            return ParseCookieElements(cookies.EnumerateArray());

        return [];
    }

    private static List<ImportedCookie> ParseCookieElements(IEnumerable<JsonElement> elements)
    {
        var cookies = new List<ImportedCookie>();
        foreach (var item in elements)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(item, "name");
            var value = GetString(item, "value") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var domain = GetString(item, "domain");
            if (string.IsNullOrWhiteSpace(domain)) domain = ".dola.com";
            var path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            var secure = GetBool(item, "secure");
            var httpOnly = GetBool(item, "httpOnly");
            DateTime? expires = null;
            var epoch = GetNumber(item, "expirationDate") ?? GetNumber(item, "expires");
            if (epoch is > 0)
            {
                try { expires = DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).UtcDateTime; }
                catch { }
            }

            cookies.Add(new ImportedCookie(name, value, domain, path, secure, httpOnly, expires));
        }
        return cookies;
    }

    private static List<CookieBundle> ParseNetscape(string text, string label)
    {
        var groups = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<CookieBundle>();
        var index = 1;

        foreach (var group in groups)
        {
            var cookies = new List<ImportedCookie>();
            foreach (var raw in group.Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || (line.StartsWith('#') && !line.StartsWith("#HttpOnly_"))) continue;
                var parts = line.Split('\t');
                if (parts.Length < 7) continue;

                var domain = parts[0];
                var httpOnly = false;
                if (domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                {
                    httpOnly = true;
                    domain = domain[10..];
                }

                var cookiePath = string.IsNullOrWhiteSpace(parts[2]) ? "/" : parts[2];
                var secure = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                DateTime? expires = null;
                if (long.TryParse(parts[4], out var epoch) && epoch > 0)
                {
                    try { expires = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime; }
                    catch { }
                }

                var name = parts[5];
                var value = parts[6];
                if (!string.IsNullOrWhiteSpace(name))
                    cookies.Add(new ImportedCookie(name, value, domain, cookiePath, secure, httpOnly, expires));
            }

            if (cookies.Count > 0)
                result.Add(new CookieBundle(groups.Length == 1 ? label : $"{label}-{index++}", cookies));
        }

        return result;
    }

    private static List<CookieBundle> ParseRawCookieHeaders(string text, string label)
    {
        var lines = text.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !x.StartsWith('#'))
            .ToList();

        var result = new List<CookieBundle>();
        var index = 1;
        foreach (var line in lines)
        {
            var cookies = new List<ImportedCookie>();
            foreach (var pair in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                var name = pair[..equals].Trim();
                var value = pair[(equals + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    cookies.Add(new ImportedCookie(name, value, ".dola.com", "/", true, false, null));
            }
            if (cookies.Count > 0)
                result.Add(new CookieBundle(lines.Count == 1 ? label : $"{label}-{index++}", cookies));
        }
        return result;
    }

    private static (List<string> Items, string Separator) SplitAccountLine(string line)
    {
        foreach (var separator in new[] { "\t", "|", "----", "," })
        {
            if (!line.Contains(separator, StringComparison.Ordinal)) continue;
            var items = separator == "," ? SplitCsv(line) : line.Split(separator, 2, StringSplitOptions.None).ToList();
            return (items, separator);
        }
        return ([], string.Empty);
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        result.Add(current.ToString());
        return result;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\"\"", "\"")
            : value;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static double? GetNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}

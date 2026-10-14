namespace Netgrep.Cli;

/// <summary>
/// Patterns from <c>-f</c> / <c>--file</c>. Empty lines and lines whose first character is
/// <c>#</c> are skipped. A line that starts with <c>\#</c> is a pattern beginning with <c>#</c>.
/// </summary>
internal static class PatternFiles
{
    public static IReadOnlyList<string> Read(string path)
    {
        string text;
        try
        {
            text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Pattern file not found: {path}");
        }

        return Parse(text);
    }

    public static List<string> Parse(string text)
    {
        var patterns = new List<string>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("\\#", StringComparison.Ordinal))
            {
                patterns.Add(line[1..]);
                continue;
            }

            patterns.Add(line);
        }

        return patterns;
    }
}

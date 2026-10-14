using System.Text;
using System.Text.RegularExpressions;

namespace Ngrep.Ignore;

/// <summary>
/// Gitignore matcher scoped the way git scopes nested files.
/// Patterns are evaluated against the path relative to the .gitignore that defined them.
/// The last matching pattern wins. If any parent directory is ignored, the child is ignored.
/// </summary>
internal sealed class IgnoreRuleSet
{
    private readonly IgnoreRule[] _rules;

    private IgnoreRuleSet(IgnoreRule[] rules) => _rules = rules;

    public int Count => _rules.Length;

    public static IgnoreRuleSet Parse(string text)
    {
        var rules = new List<IgnoreRule>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        foreach (var raw in normalized.Split('\n'))
        {
            if (IgnoreRule.TryParse(raw) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return new IgnoreRuleSet(rules.ToArray());
    }

    public bool TryDecide(string localPath, bool isDirectory, out bool ignored)
    {
        var any = false;
        var state = false;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(localPath, isDirectory))
            {
                continue;
            }

            any = true;
            state = !rule.Negation;
        }

        ignored = state;
        return any;
    }
}

internal sealed class IgnoreRule
{
    private readonly Regex _regex;

    private IgnoreRule(Regex regex, bool negation, bool directoryOnly)
    {
        _regex = regex;
        Negation = negation;
        DirectoryOnly = directoryOnly;
    }

    public bool Negation { get; }

    public bool DirectoryOnly { get; }

    public static IgnoreRule? TryParse(string rawLine)
    {
        var line = TrimTrailingWhitespace(rawLine);
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        if (line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        var negation = false;
        if (line.StartsWith("\\!", StringComparison.Ordinal))
        {
            line = line[1..];
        }
        else if (line.StartsWith('!'))
        {
            negation = true;
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var directoryOnly = line.EndsWith('/');
        if (directoryOnly)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var anchored = line.StartsWith('/') || line.Contains('/');
        if (line.StartsWith('/'))
        {
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return null;
        }

        Regex regex;
        try
        {
            regex = new Regex(
                Compile(line, anchored),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (RegexParseException)
        {
            return null;
        }

        return new IgnoreRule(regex, negation, directoryOnly);
    }

    public bool Matches(string localPath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        return _regex.IsMatch(localPath);
    }

    private static string Compile(string pattern, bool anchored)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        if (!anchored)
        {
            sb.Append("(?:.*/)?");
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i++;
                continue;
            }

            if (c == '*')
            {
                var starCount = 1;
                while (i + starCount < pattern.Length && pattern[i + starCount] == '*')
                {
                    starCount++;
                }

                if (starCount >= 2)
                {
                    var atStart = i == 0;
                    var afterSlash = i > 0 && pattern[i - 1] == '/';
                    var atEnd = i + starCount == pattern.Length;
                    var beforeSlash = i + starCount < pattern.Length && pattern[i + starCount] == '/';
                    if ((atStart || afterSlash) && (atEnd || beforeSlash))
                    {
                        if (beforeSlash)
                        {
                            sb.Append("(?:.*/)?");
                            i += starCount;
                            continue;
                        }

                        sb.Append(".*");
                        i += starCount - 1;
                        continue;
                    }
                }

                sb.Append("[^/]*");
                i += starCount - 1;
                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                continue;
            }

            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0)
                {
                    sb.Append("\\[");
                    continue;
                }

                var cls = pattern.Substring(i, end - i + 1);
                if (cls.Length >= 2 && cls[1] == '!')
                {
                    cls = "[^" + cls[2..];
                }

                sb.Append(cls);
                i = end;
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static string TrimTrailingWhitespace(string raw)
    {
        var end = raw.Length;
        while (end > 0 && raw[end - 1] is ' ' or '\t')
        {
            var slashes = 0;
            var i = end - 2;
            while (i >= 0 && raw[i] == '\\')
            {
                slashes++;
                i--;
            }

            if (slashes % 2 == 1)
            {
                break;
            }

            end--;
        }

        return raw[..end];
    }
}

internal sealed class IgnoreStack
{
    private readonly record struct Frame(string BaseRelative, IgnoreRuleSet Rules);

    private readonly Frame[] _frames;

    private IgnoreStack(Frame[] frames) => _frames = frames;

    public static IgnoreStack Empty { get; } = new([]);

    public IgnoreStack Add(string baseRelative, string gitignoreContents)
    {
        var rules = IgnoreRuleSet.Parse(gitignoreContents);
        if (rules.Count == 0)
        {
            return this;
        }

        var next = new Frame[_frames.Length + 1];
        _frames.CopyTo(next, 0);
        next[^1] = new Frame(Normalize(baseRelative), rules);
        return new IgnoreStack(next);
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        relativePath = Normalize(relativePath);
        var slash = relativePath.LastIndexOf('/');
        if (slash > 0 && IsIgnored(relativePath[..slash], true))
        {
            return true;
        }

        var ignored = false;
        foreach (var frame in _frames)
        {
            if (!TryLocal(frame.BaseRelative, relativePath, out var local))
            {
                continue;
            }

            if (frame.Rules.TryDecide(local, isDirectory, out var decision))
            {
                ignored = decision;
            }
        }

        return ignored;
    }

    private static bool TryLocal(string baseRelative, string relativePath, out string local)
    {
        if (baseRelative.Length == 0)
        {
            local = relativePath;
            return true;
        }

        if (relativePath.Equals(baseRelative, StringComparison.Ordinal))
        {
            local = "";
            return false;
        }

        var prefix = baseRelative + "/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            local = "";
            return false;
        }

        local = relativePath[prefix.Length..];
        return local.Length > 0;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

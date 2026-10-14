using System.Buffers;
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
    private readonly bool _hasNegation;

    private IgnoreRuleSet(IgnoreRule[] rules, bool hasNegation)
    {
        _rules = rules;
        _hasNegation = hasNegation;
    }

    public int Count => _rules.Length;

    public static IgnoreRuleSet Parse(string text)
    {
        var rules = new List<IgnoreRule>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        var hasNegation = false;
        foreach (var raw in normalized.Split('\n'))
        {
            if (IgnoreRule.TryParse(raw) is not { } rule)
            {
                continue;
            }

            if (rule.Negation)
            {
                hasNegation = true;
            }

            rules.Add(rule);
        }

        return new IgnoreRuleSet(rules.ToArray(), hasNegation);
    }

    public bool TryDecide(ReadOnlySpan<char> localPath, bool isDirectory, out bool ignored)
    {
        if (!_hasNegation)
        {
            foreach (var rule in _rules)
            {
                if (rule.DirectoryOnly && !isDirectory)
                {
                    continue;
                }

                if (!rule.Matches(localPath, isDirectory))
                {
                    continue;
                }

                ignored = true;
                return true;
            }

            ignored = false;
            return false;
        }

        var any = false;
        var state = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

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
    private readonly Glob? _glob;
    private readonly string _body;
    private readonly bool _anchored;
    private Regex? _regex;

    private IgnoreRule(Glob? glob, Regex? regex, string body, bool anchored, bool negation, bool directoryOnly)
    {
        _glob = glob;
        _regex = regex;
        _body = body;
        _anchored = anchored;
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

        var glob = Glob.TryCreate(line, anchored);
        Regex? regex = null;
        if (glob == null)
        {
            try
            {
                regex = CompileRegex(line, anchored);
            }
            catch (RegexParseException)
            {
                return null;
            }
        }

        return new IgnoreRule(glob, regex, line, anchored, negation, directoryOnly);
    }

    public bool Matches(string localPath, bool isDirectory) => Matches(localPath.AsSpan(), isDirectory);

    public bool Matches(ReadOnlySpan<char> localPath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        if (_glob != null && _glob.TryMatch(localPath, out var matched))
        {
            return matched;
        }

        return PatternRegex().IsMatch(localPath);
    }

    /// <summary>Regex interpreter for the same pattern. Tests compare it with <see cref="Matches"/>.</summary>
    internal bool MatchesRegex(string localPath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        return PatternRegex().IsMatch(localPath);
    }

    private Regex PatternRegex() => _regex ??= CompileRegex(_body, _anchored);

    private static Regex CompileRegex(string pattern, bool anchored) =>
        new(Compile(pattern, anchored), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

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

    public bool IsIgnored(string relativePath, bool isDirectory) =>
        IsIgnored(relativePath.AsSpan(), isDirectory, checkParents: true);

    /// <summary>
    /// <paramref name="checkParents"/> is false while walking: ignored directories are not entered,
    /// so a child does not re-test every ancestor.
    /// </summary>
    public bool IsIgnored(ReadOnlySpan<char> relativePath, bool isDirectory, bool checkParents)
    {
        if (!NeedsNormalize(relativePath))
        {
            return Decide(relativePath, isDirectory, checkParents);
        }

        var rented = ArrayPool<char>.Shared.Rent(relativePath.Length);
        try
        {
            var written = NormalizeInto(relativePath, rented);
            return Decide(rented.AsSpan(0, written), isDirectory, checkParents);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool Decide(ReadOnlySpan<char> relativePath, bool isDirectory, bool checkParents)
    {
        if (checkParents)
        {
            var slash = relativePath.LastIndexOf('/');
            if (slash > 0 && Decide(relativePath[..slash], true, true))
            {
                return true;
            }
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

    private static bool TryLocal(string baseRelative, ReadOnlySpan<char> relativePath, out ReadOnlySpan<char> local)
    {
        if (baseRelative.Length == 0)
        {
            local = relativePath;
            return true;
        }

        if (relativePath.Length == baseRelative.Length && relativePath.Equals(baseRelative, StringComparison.Ordinal))
        {
            local = default;
            return false;
        }

        if (relativePath.Length > baseRelative.Length
            && relativePath.StartsWith(baseRelative, StringComparison.Ordinal)
            && relativePath[baseRelative.Length] == '/')
        {
            local = relativePath[(baseRelative.Length + 1)..];
            return local.Length > 0;
        }

        local = default;
        return false;
    }

    private static bool NeedsNormalize(ReadOnlySpan<char> path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (path[0] == '/' || path[^1] == '/' || path.Contains('\\'))
        {
            return true;
        }

        return false;
    }

    private static int NormalizeInto(ReadOnlySpan<char> path, Span<char> dest)
    {
        var start = 0;
        var end = path.Length;
        while (start < end && path[start] is '/' or '\\')
        {
            start++;
        }

        while (end > start && path[end - 1] is '/' or '\\')
        {
            end--;
        }

        var written = 0;
        for (var i = start; i < end; i++)
        {
            var c = path[i] == '\\' ? '/' : path[i];
            dest[written++] = c;
        }

        return written;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}

using System.Text.RegularExpressions;
using Netgrep.Text;

namespace Netgrep.Search;

internal sealed class MatchPlan
{
    public required string[] Patterns { get; init; }

    public Regex[]? Regexes { get; init; }

    public required bool Regex { get; init; }

    public required bool IgnoreCase { get; init; }

    public required bool Word { get; init; }

    public required bool Invert { get; init; }

    public required int Before { get; init; }

    public required int After { get; init; }

    public required PathFilter Filter { get; init; }

    public LiteralQuery? Literals { get; init; }

    public bool ApplyGitIgnore { get; init; } = true;

    public bool WantsContext => Before > 0 || After > 0;

    public bool CanUseTrigrams => !Invert && Literals is { TrigramSafe: true };

    public static MatchPlan Create(SearchQuery query)
    {
        var patterns = Combine(query);
        Regex[]? regexes = null;
        if (query.Regex)
        {
            var options = RegexOptions.CultureInvariant;
            if (query.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            regexes = new Regex[patterns.Length];
            for (var i = 0; i < patterns.Length; i++)
            {
                regexes[i] = new Regex(patterns[i], options, TimeSpan.FromSeconds(5));
            }
        }

        if (query.BeforeContext < 0 || query.AfterContext < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        return new MatchPlan
        {
            Patterns = patterns,
            Regexes = regexes,
            Regex = query.Regex,
            IgnoreCase = query.IgnoreCase,
            Word = query.WordRegexp,
            Invert = query.InvertMatch,
            Before = query.BeforeContext,
            After = query.AfterContext,
            Filter = PathFilter.Create(query.Globs, query.Types, query.TypeNots, query.TypeAdds),
            Literals = LiteralQuery.FromPatterns(patterns, query.Regex),
            ApplyGitIgnore = !query.NoIgnore && !query.NoIgnoreVcs,
        };
    }

    public static void Collect(string text, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        if (plan.Invert)
        {
            var matchingLines = new HashSet<int>();
            MarkMatchingLines(text, starts, plan, matchingLines);
            for (var line = 1; line <= lineCount; line++)
            {
                if (matchingLines.Contains(line))
                {
                    continue;
                }

                var lineText = LineMap.LineText(text, starts, line);
                var (before, after) = Context(text, starts, lineCount, line, plan);
                matches.Add(new SearchMatch(relativePath, line, 1, lineText, lineText, before, after));
            }

            return;
        }

        var hits = new List<Hit>();
        if (plan.Regex)
        {
            foreach (var regex in plan.Regexes!)
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (plan.Word && !WordBoundary.At(text, match.Index, match.Length))
                    {
                        continue;
                    }

                    hits.Add(new Hit(match.Index, match.Length, match.Value));
                }
            }
        }
        else
        {
            var comparison = plan.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var pattern in plan.Patterns)
            {
                CollectLiteral(text, pattern, comparison, plan.Word, hits);
            }
        }

        hits.Sort(static (left, right) =>
        {
            var index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Length.CompareTo(right.Length);
        });

        var deduped = Dedup(hits);
        foreach (var hit in deduped)
        {
            var (line, column) = LineMap.Map(starts, hit.Index);
            var (before, after) = Context(text, starts, lineCount, line, plan);
            matches.Add(new SearchMatch(
                relativePath,
                line,
                column,
                hit.Value,
                LineMap.LineText(text, starts, line),
                before,
                after));
        }
    }

    private static string[] Combine(SearchQuery query)
    {
        var extra = query.AdditionalPatterns;
        var count = query.Pattern.Length > 0 ? 1 : 0;
        count += extra.Count;
        if (count == 0)
        {
            throw new ArgumentException("Pattern is empty.");
        }

        var patterns = new string[count];
        var index = 0;
        if (query.Pattern.Length > 0)
        {
            patterns[index++] = query.Pattern;
        }

        foreach (var pattern in extra)
        {
            if (pattern.Length == 0)
            {
                throw new ArgumentException("Pattern is empty.");
            }

            patterns[index++] = pattern;
        }

        return patterns;
    }

    private static void MarkMatchingLines(string text, int[] starts, MatchPlan plan, HashSet<int> lines)
    {
        if (plan.Regex)
        {
            foreach (var regex in plan.Regexes!)
            {
                MarkRegex(text, starts, regex, plan.Word, lines);
            }

            return;
        }

        var comparison = plan.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var pattern in plan.Patterns)
        {
            MarkLiteral(text, starts, pattern, comparison, plan.Word, lines);
        }
    }

    private static void MarkLiteral(string text, int[] starts, string pattern, StringComparison comparison, bool word, HashSet<int> lines)
    {
        if (pattern.Length == 0 || pattern.Length > text.Length)
        {
            return;
        }

        var from = 0;
        while (from <= text.Length - pattern.Length)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0)
            {
                return;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                lines.Add(LineMap.Map(starts, idx).Line);
                var newline = text.IndexOf('\n', idx);
                if (newline < 0)
                {
                    return;
                }

                from = newline + 1;
                continue;
            }

            from = idx + 1;
        }
    }

    private static void MarkRegex(string text, int[] starts, Regex regex, bool word, HashSet<int> lines)
    {
        var from = 0;
        while (from <= text.Length)
        {
            var match = regex.Match(text, from);
            if (!match.Success)
            {
                return;
            }

            if (!word || WordBoundary.At(text, match.Index, match.Length))
            {
                lines.Add(LineMap.Map(starts, match.Index).Line);
                if (match.Index >= text.Length)
                {
                    return;
                }

                var newline = text.IndexOf('\n', match.Index);
                if (newline < 0)
                {
                    return;
                }

                from = newline + 1;
                continue;
            }

            var next = match.Index + Math.Max(1, match.Length);
            from = next <= from ? from + 1 : next;
        }
    }

    private static void CollectLiteral(string text, string pattern, StringComparison comparison, bool word, List<Hit> hits)
    {
        if (pattern.Length == 0 || pattern.Length > text.Length)
        {
            return;
        }

        var from = 0;
        while (from <= text.Length - pattern.Length)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0)
            {
                break;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                hits.Add(new Hit(idx, pattern.Length, text.Substring(idx, pattern.Length)));
                from = idx + pattern.Length;
            }
            else
            {
                from = idx + 1;
            }
        }
    }

    private static List<Hit> Dedup(List<Hit> hits)
    {
        if (hits.Count < 2)
        {
            return hits;
        }

        var deduped = new List<Hit>(hits.Count);
        foreach (var hit in hits)
        {
            if (deduped.Count > 0)
            {
                var last = deduped[^1];
                if (last.Index == hit.Index && last.Length == hit.Length)
                {
                    continue;
                }
            }

            deduped.Add(hit);
        }

        return deduped;
    }

    private static (IReadOnlyList<ContextLine>? Before, IReadOnlyList<ContextLine>? After) Context(
        string text,
        int[] starts,
        int lineCount,
        int line,
        MatchPlan plan)
    {
        if (!plan.WantsContext)
        {
            return (null, null);
        }

        return (SliceBefore(text, starts, line, plan.Before), SliceAfter(text, starts, lineCount, line, plan.After));
    }

    private static ContextLine[] SliceBefore(string text, int[] starts, int line, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var startLine = count >= line ? 1 : line - count;
        var length = line - startLine;
        if (length <= 0)
        {
            return [];
        }

        var lines = new ContextLine[length];
        for (var i = 0; i < length; i++)
        {
            var number = startLine + i;
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number));
        }

        return lines;
    }

    private static ContextLine[] SliceAfter(string text, int[] starts, int lineCount, int line, int count)
    {
        if (count <= 0 || line >= lineCount)
        {
            return [];
        }

        var available = lineCount - line;
        var take = count >= available ? available : count;
        var lines = new ContextLine[take];
        for (var i = 0; i < take; i++)
        {
            var number = line + 1 + i;
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number));
        }

        return lines;
    }

    private readonly record struct Hit(int Index, int Length, string Value);
}

internal static class WordBoundary
{
    public static bool At(string text, int index, int length)
    {
        if (length < 0 || index < 0 || index > text.Length || index + length > text.Length)
        {
            return false;
        }

        return IsBoundary(text, index) && IsBoundary(text, index + length);
    }

    public static bool IsWord(char value) => value == '_' || char.IsLetterOrDigit(value);

    private static bool IsBoundary(string text, int index)
    {
        var left = index > 0 && IsWord(text[index - 1]);
        var right = index < text.Length && IsWord(text[index]);
        return left != right;
    }
}

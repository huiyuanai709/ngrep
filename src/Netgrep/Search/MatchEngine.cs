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

    public ReportMode Report { get; init; }

    public bool Multiline { get; init; }

    public bool MultilineDotAll { get; init; }

    public bool IncludeHidden { get; init; }

    public required int Before { get; init; }

    public required int After { get; init; }

    public required PathFilter Filter { get; init; }

    public LiteralQuery? Literals { get; init; }

    public bool ApplyGitIgnore { get; init; } = true;

    public bool WantsContext => Before > 0 || After > 0;

    public bool CollectAll { get; init; }

    public bool StopAtFirst => Report == ReportMode.FilesWithMatches && !CollectAll;

    /// <summary>Regex matches may include a newline. Set by <c>-U</c> or <c>--multiline-dotall</c>.</summary>
    public bool CrossesLines => Multiline || MultilineDotAll;

    public bool CanUseTrigrams => !Invert && Literals is { TrigramSafe: true };

    public static MatchPlan Create(SearchQuery query)
    {
        var patterns = Combine(query);
        Regex[]? regexes = null;
        if (query.Regex)
        {
            // Interpreter only. RegexOptions.Compiled is not AOT-safe for a user pattern.
            // Multiline makes ^ and $ line anchors, matching grep. Singleline is dotall.
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (query.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            if (query.MultilineDotAll)
            {
                options |= RegexOptions.Singleline;
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
            Report = query.Report,
            Multiline = query.Multiline || query.MultilineDotAll,
            MultilineDotAll = query.MultilineDotAll,
            IncludeHidden = query.IncludeHidden,
            CollectAll = query.Stats,
        };
    }

    public static bool HasMatch(string text, MatchPlan plan)
    {
        if (!plan.Invert)
        {
            return FirstHit(text, plan) != null;
        }

        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        if (lineCount == 0)
        {
            return false;
        }

        var matchingLines = new HashSet<int>();
        MarkMatchingLines(text, starts, plan, matchingLines);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!matchingLines.Contains(line))
            {
                return true;
            }
        }

        return false;
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
                var offset = line - 1 < starts.Length ? starts[line - 1] : 0;
                matches.Add(new SearchMatch(relativePath, line, 1, lineText, lineText, before, after, offset));
                if (plan.StopAtFirst)
                {
                    return;
                }
            }

            return;
        }

        if (plan.StopAtFirst)
        {
            if (FirstHit(text, plan) is { } first)
            {
                AddHit(text, starts, lineCount, relativePath, plan, first, matches);
            }

            return;
        }

        var hits = new List<Hit>();
        if (plan.Regex)
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    CollectEmpty(text, plan.Word, hits);
                    continue;
                }

                foreach (Match match in plan.Regexes[i].Matches(text))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!plan.CrossesLines && ContainsLineBreak(text, match.Index, match.Length))
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
                if (pattern.Length == 0)
                {
                    CollectEmpty(text, plan.Word, hits);
                    continue;
                }

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
            AddHit(text, starts, lineCount, relativePath, plan, hit, matches);
        }
    }

    private static void AddHit(
        string text,
        int[] starts,
        int lineCount,
        string relativePath,
        MatchPlan plan,
        Hit hit,
        List<SearchMatch> matches)
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
            after,
            hit.Index));
    }

    private static Hit? FirstHit(string text, MatchPlan plan)
    {
        Hit? best = null;
        if (plan.Regex)
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    ConsiderEmpty(text, plan.Word, ref best);
                    continue;
                }

                ConsiderFirstRegex(text, plan.Regexes[i], plan, ref best);
            }
        }
        else
        {
            var comparison = plan.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var pattern in plan.Patterns)
            {
                ConsiderFirstLiteral(text, pattern, comparison, plan.Word, ref best);
            }
        }

        return best;
    }

    private static void ConsiderFirstLiteral(string text, string pattern, StringComparison comparison, bool word, ref Hit? best)
    {
        if (pattern.Length == 0)
        {
            ConsiderEmpty(text, word, ref best);
            return;
        }

        if (pattern.Length > text.Length)
        {
            return;
        }

        var limit = best?.Index ?? int.MaxValue;
        var from = 0;
        while (from <= text.Length - pattern.Length && from < limit)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0 || idx >= limit)
            {
                return;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                best = new Hit(idx, pattern.Length, text.Substring(idx, pattern.Length));
                return;
            }

            from = idx + 1;
        }
    }

    private static void ConsiderFirstRegex(string text, Regex regex, MatchPlan plan, ref Hit? best)
    {
        var limit = best?.Index ?? int.MaxValue;
        var from = 0;
        while (from <= text.Length && from < limit)
        {
            var match = regex.Match(text, from);
            if (!match.Success || match.Index >= limit)
            {
                return;
            }

            var crosses = ContainsLineBreak(text, match.Index, match.Length);
            if ((plan.CrossesLines || !crosses) && (!plan.Word || WordBoundary.At(text, match.Index, match.Length)))
            {
                best = new Hit(match.Index, match.Length, match.Value);
                return;
            }

            var next = match.Index + Math.Max(1, match.Length);
            from = next <= from ? from + 1 : next;
        }
    }

    private static bool ContainsLineBreak(string text, int index, int length)
    {
        if (length <= 0 || index < 0 || index >= text.Length)
        {
            return false;
        }

        var end = index + length;
        if (end > text.Length)
        {
            end = text.Length;
        }

        return text.IndexOf('\n', index, end - index) >= 0;
    }

    private static string[] Combine(SearchQuery query)
    {
        var extra = query.AdditionalPatterns;
        var patterns = new string[1 + extra.Count];
        patterns[0] = query.Pattern;
        for (var i = 0; i < extra.Count; i++)
        {
            patterns[i + 1] = extra[i];
        }

        return patterns;
    }

    private static void MarkMatchingLines(string text, int[] starts, MatchPlan plan, HashSet<int> lines)
    {
        if (plan.Regex)
        {
            foreach (var regex in plan.Regexes!)
            {
                MarkRegex(text, starts, regex, plan, lines);
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
        if (pattern.Length == 0)
        {
            MarkEmpty(text, starts, word, lines);
            return;
        }

        if (pattern.Length > text.Length)
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
                var startLine = LineMap.Map(starts, idx).Line;
                var endIndex = idx + pattern.Length - 1;
                var endLine = LineMap.Map(starts, endIndex).Line;
                for (var line = startLine; line <= endLine; line++)
                {
                    lines.Add(line);
                }

                if (endLine == startLine)
                {
                    var newline = text.IndexOf('\n', idx);
                    if (newline < 0)
                    {
                        return;
                    }

                    from = newline + 1;
                    continue;
                }

                from = idx + Math.Max(1, pattern.Length);
                continue;
            }

            from = idx + 1;
        }
    }

    private static void MarkRegex(string text, int[] starts, Regex regex, MatchPlan plan, HashSet<int> lines)
    {
        if (regex.ToString().Length == 0)
        {
            MarkEmpty(text, starts, plan.Word, lines);
            return;
        }

        var from = 0;
        while (from <= text.Length)
        {
            var match = regex.Match(text, from);
            if (!match.Success)
            {
                return;
            }

            var crosses = ContainsLineBreak(text, match.Index, match.Length);
            var accept = (plan.CrossesLines || !crosses) && (!plan.Word || WordBoundary.At(text, match.Index, match.Length));
            if (accept)
            {
                var startLine = LineMap.Map(starts, match.Index).Line;
                var endIndex = match.Length == 0 ? match.Index : match.Index + match.Length - 1;
                if (text.Length == 0)
                {
                    endIndex = match.Index;
                }
                else if (endIndex >= text.Length)
                {
                    endIndex = text.Length - 1;
                }

                var endLine = text.Length == 0 ? startLine : LineMap.Map(starts, endIndex).Line;
                for (var line = startLine; line <= endLine; line++)
                {
                    lines.Add(line);
                }

                if (!crosses)
                {
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
            }

            var next = match.Index + Math.Max(1, match.Length);
            from = next <= from ? from + 1 : next;
        }
    }

    /// <summary>
    /// An empty pattern matches every line once, at column 1. Ripgrep's empty regex also
    /// reports a zero-width match at every offset; text output still shows each line once.
    /// </summary>
    private static void CollectEmpty(string text, bool word, List<Hit> hits)
    {
        if (text.Length == 0)
        {
            return;
        }

        var from = 0;
        while (from < text.Length)
        {
            if (!word || WordBoundary.At(text, from, 0))
            {
                hits.Add(new Hit(from, 0, ""));
            }

            var newline = text.IndexOf('\n', from);
            if (newline < 0)
            {
                break;
            }

            from = newline + 1;
        }
    }

    private static void ConsiderEmpty(string text, bool word, ref Hit? best)
    {
        if (text.Length == 0 || (word && !WordBoundary.At(text, 0, 0)))
        {
            return;
        }

        var limit = best?.Index ?? int.MaxValue;
        if (0 < limit)
        {
            best = new Hit(0, 0, "");
        }
    }

    private static void MarkEmpty(string text, int[] starts, bool word, HashSet<int> lines)
    {
        if (text.Length == 0)
        {
            return;
        }

        var from = 0;
        while (from < text.Length)
        {
            if (!word || WordBoundary.At(text, from, 0))
            {
                lines.Add(LineMap.Map(starts, from).Line);
            }

            var newline = text.IndexOf('\n', from);
            if (newline < 0)
            {
                break;
            }

            from = newline + 1;
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
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number), LineStart(starts, number));
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
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number), LineStart(starts, number));
        }

        return lines;
    }

    private static int LineStart(int[] starts, int line) =>
        line >= 1 && line - 1 < starts.Length ? starts[line - 1] : 0;

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

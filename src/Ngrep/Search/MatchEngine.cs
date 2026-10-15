using System.Text;
using System.Text.RegularExpressions;
using Ngrep.Text;

namespace Ngrep.Search;

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

    public bool LineRegexp { get; init; }

    public bool NoMessages { get; init; }

    /// <summary>Default regex engine (also selected by <c>-E</c>). False when <c>-P</c> selects the backtracking interpreter.</summary>
    public bool NonBacktracking { get; init; }

    /// <summary>ASCII byte automata for patterns the linear compiler recognized. Null when any pattern opted out.</summary>
    public ByteProgram[]? BytePrograms { get; init; }

    public bool StopAtFirst =>
        (Report == ReportMode.FilesWithMatches || Report == ReportMode.Quiet) && !CollectAll;

    /// <summary>Regex matches may include a newline. Set by <c>-U</c> or <c>--multiline-dotall</c>.</summary>
    public bool CrossesLines => Multiline || MultilineDotAll;

    public bool CanUseTrigrams => !Invert && Literals is { TrigramSafe: true };

    /// <summary>Regex literals are confirmed per line. Absolute anchors and cross-line matches stay on the full string.</summary>
    public bool UseLinePrefilter { get; init; }

    public static MatchPlan Create(SearchQuery query)
    {
        var patterns = Combine(query);
        var regex = query.Regex;
        if (regex && !query.IgnoreCase && VerbatimPattern.TryAll(patterns, out var literals))
        {
            // The interpreter would match these spans exactly. Searching them as literals
            // keeps the byte engine on the hot path. Ignore-case stays on the regex
            // engine so Unicode case folding does not change.
            patterns = literals;
            regex = false;
        }

        Regex[]? regexes = null;
        ByteProgram[]? bytePrograms = null;
        var nonBacktracking = false;
        if (regex)
        {
            // Compiled is not AOT-safe for a user pattern. NonBacktracking builds a linear
            // automaton in memory and does not emit IL. -P keeps the backtracking interpreter.
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!query.Backtracking)
            {
                options |= RegexOptions.NonBacktracking;
                nonBacktracking = true;
            }

            if (query.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            if (query.MultilineDotAll)
            {
                options |= RegexOptions.Singleline;
            }

            regexes = new Regex[patterns.Length];
            var programs = new ByteProgram[patterns.Length];
            var allBytes = !query.Backtracking && !query.LineRegexp;
            var byteOptions = new ByteAutomaton.Options(query.IgnoreCase, query.MultilineDotAll);
            for (var i = 0; i < patterns.Length; i++)
            {
                var source = query.LineRegexp ? AnchorWholeLine(patterns[i]) : patterns[i];
                try
                {
                    regexes[i] = new Regex(source, options, TimeSpan.FromSeconds(5));
                }
                catch (NotSupportedException ex) when (nonBacktracking)
                {
                    // UseSystemResourceKeys makes ex.Message a resource id, so the
                    // sentence below is the one the user reads. The inner exception
                    // still carries the runtime's reason (backreference, lookaround,
                    // atomic group, or the size limit).
                    throw new ArgumentException(
                        $"'{patterns[i]}' is not supported by the non-backtracking engine. Use -P/--pcre for backreferences, lookaround, and atomic groups.",
                        ex);
                }

                if (allBytes && ByteAutomaton.TryCompile(patterns[i], byteOptions, out var program))
                {
                    programs[i] = program;
                }
                else
                {
                    allBytes = false;
                }
            }

            if (allBytes)
            {
                bytePrograms = programs;
            }
        }

        var literalsQuery = LiteralQuery.FromPatterns(patterns, regex);

        if (query.BeforeContext < 0 || query.AfterContext < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        var crosses = query.Multiline || query.MultilineDotAll;
        return new MatchPlan
        {
            Patterns = patterns,
            Regexes = regexes,
            Regex = regex,
            IgnoreCase = query.IgnoreCase,
            Word = query.WordRegexp,
            Invert = query.InvertMatch,
            Before = query.BeforeContext,
            After = query.AfterContext,
            Filter = PathFilter.Create(query.Globs, query.Types, query.TypeNots, query.TypeAdds),
            Literals = literalsQuery,
            ApplyGitIgnore = !query.NoIgnore && !query.NoIgnoreVcs,
            Report = query.Report,
            Multiline = crosses,
            MultilineDotAll = query.MultilineDotAll,
            IncludeHidden = query.IncludeHidden,
            CollectAll = query.Stats,
            LineRegexp = query.LineRegexp,
            NoMessages = query.NoMessages,
            NonBacktracking = nonBacktracking,
            BytePrograms = bytePrograms,
            UseLinePrefilter = regex && !crosses && literalsQuery is { HasLinePrefilter: true } && !HasAbsoluteAnchor(patterns),
        };
    }

    /// <summary>
    /// Whole-line regex. The caller passes one line with the trailing CR already removed,
    /// so <c>\A</c> and <c>\z</c> mean that line and not the file.
    /// </summary>
    private static string AnchorWholeLine(string pattern) => @"\A(?:" + pattern + @")\z";

    private static bool HasAbsoluteAnchor(string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            for (var i = 0; i < pattern.Length - 1; i++)
            {
                if (pattern[i] != '\\')
                {
                    continue;
                }

                var next = pattern[i + 1];
                if (next is 'A' or 'z' or 'Z' or 'G')
                {
                    return true;
                }

                i++;
            }
        }

        return false;
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

    /// <summary>
    /// Runs a linear byte automaton on an ASCII file. False when the pattern was not compiled,
    /// the file is not ASCII, or the match needs the string engine (context, <c>-x</c>, <c>-w</c>, <c>-v</c>).
    /// </summary>
    public static bool TryCollectAutomaton(
        ReadOnlySpan<byte> bytes,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches,
        out bool matched)
    {
        matched = false;
        if (plan.BytePrograms == null || plan.Invert || plan.LineRegexp || plan.WantsContext || plan.Word)
        {
            return false;
        }

        var countOnly = plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll;
        var hits = countOnly ? null : new List<Hit>();
        foreach (var program in plan.BytePrograms)
        {
            var found = new List<ByteMatch>();
            program.Collect(bytes, plan.CrossesLines, plan.StopAtFirst || countOnly, found);
            foreach (var candidate in found)
            {
                if (countOnly)
                {
                    // An ASCII pattern's bytes are the same in UTF-8, so a hit exists
                    // whether or not a later byte is non-ASCII.
                    matched = true;
                    return true;
                }

                var value = Encoding.ASCII.GetString(bytes.Slice(candidate.Index, candidate.Length));
                hits!.Add(new Hit(candidate.Index, candidate.Length, value));
                if (plan.StopAtFirst)
                {
                    break;
                }
            }

            if (plan.StopAtFirst && hits is { Count: > 0 })
            {
                break;
            }
        }

        if (countOnly)
        {
            return true;
        }

        if (hits!.Count == 0)
        {
            matched = false;
            return true;
        }

        // Columns are byte offsets only while the file is ASCII. A hit in a
        // non-ASCII file is confirmed again on the decoded string.
        if (!Ascii.IsValid(bytes))
        {
            return false;
        }

        EmitByteHits(bytes, relativePath, hits, matches);
        matched = true;
        return true;
    }

    /// <summary>
    /// Confirms a line-prefiltered regex on ASCII bytes. Only candidate lines are decoded.
    /// False when the file is not ASCII, context is required, or the literal filter cannot run.
    /// </summary>
    public static bool TryCollectBytes(
        ReadOnlySpan<byte> bytes,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches,
        out bool matched)
    {
        matched = false;
        if (!plan.UseLinePrefilter || plan.Invert || plan.WantsContext || plan.Literals == null || plan.Regexes == null)
        {
            return false;
        }

        if (!Ascii.IsValid(bytes))
        {
            return false;
        }

        var lineStarts = new List<int>();
        if (!plan.Literals.CandidateLineStarts(bytes, plan.IgnoreCase, lineStarts))
        {
            return false;
        }

        var countOnly = plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll;
        var hits = countOnly ? null : new List<Hit>();
        foreach (var lineStart in lineStarts)
        {
            var end = lineStart;
            while (end < bytes.Length && bytes[end] != (byte)'\n')
            {
                end++;
            }

            var textEnd = end;
            if (textEnd > lineStart && bytes[textEnd - 1] == (byte)'\r')
            {
                textEnd--;
            }

            var slice = Encoding.ASCII.GetString(bytes[lineStart..textEnd]);
            if (countOnly)
            {
                if (SliceHits(slice, slice, 0, plan))
                {
                    matched = true;
                    return true;
                }

                continue;
            }

            Hit? earliest = null;
            for (var i = 0; i < plan.Regexes.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                foreach (Match match in plan.Regexes[i].Matches(slice))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (plan.Word && !WordBoundary.At(slice, match.Index, match.Length))
                    {
                        continue;
                    }

                    var hit = new Hit(lineStart + match.Index, match.Length, match.Value);
                    if (earliest == null || hit.Index < earliest.Value.Index || (hit.Index == earliest.Value.Index && hit.Length < earliest.Value.Length))
                    {
                        earliest = hit;
                    }

                    if (!plan.StopAtFirst)
                    {
                        hits!.Add(hit);
                    }
                }
            }

            if (plan.StopAtFirst && earliest != null)
            {
                hits!.Add(earliest.Value);
                EmitByteHits(bytes, relativePath, hits, matches);
                matched = true;
                return true;
            }
        }

        if (countOnly)
        {
            return true;
        }

        EmitByteHits(bytes, relativePath, hits!, matches);
        matched = hits!.Count > 0;
        return true;
    }

    private static void EmitByteHits(ReadOnlySpan<byte> bytes, string relativePath, List<Hit> hits, List<SearchMatch> matches)
    {
        hits.Sort(static (left, right) =>
        {
            var index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Length.CompareTo(right.Length);
        });
        var lineNo = 1;
        var cursor = 0;
        var sharedStart = -1;
        string? sharedText = null;
        foreach (var hit in Dedup(hits))
        {
            var lineStart = hit.Index;
            while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n')
            {
                lineStart--;
            }

            lineNo += bytes[cursor..lineStart].Count((byte)'\n');
            cursor = lineStart;
            var end = lineStart;
            while (end < bytes.Length && bytes[end] != (byte)'\n')
            {
                end++;
            }

            var textEnd = end;
            if (textEnd > lineStart && bytes[textEnd - 1] == (byte)'\r')
            {
                textEnd--;
            }

            if (sharedText == null || lineStart != sharedStart)
            {
                sharedStart = lineStart;
                sharedText = Encoding.ASCII.GetString(bytes[lineStart..textEnd]);
            }

            matches.Add(new SearchMatch(
                relativePath,
                lineNo,
                hit.Index - lineStart + 1,
                hit.Value,
                sharedText,
                null,
                null,
                hit.Index));
        }
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

        if (plan.LineRegexp)
        {
            CollectLines(text, starts, lineCount, relativePath, plan, matches);
            return;
        }

        if (plan.StopAtFirst)
        {
            if (FirstHit(text, plan) is { } first)
            {
                var firstLine = default(SharedLine);
                AddHit(text, starts, lineCount, relativePath, plan, first, matches, ref firstLine);
            }

            return;
        }

        if (plan.UseLinePrefilter)
        {
            CollectPrefiltered(text, starts, lineCount, relativePath, plan, matches);
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
        var cache = default(SharedLine);
        foreach (var hit in deduped)
        {
            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
        }
    }

    private static void AddHit(
        string text,
        int[] starts,
        int lineCount,
        string relativePath,
        MatchPlan plan,
        Hit hit,
        List<SearchMatch> matches,
        ref SharedLine cache)
    {
        var (line, column) = LineMap.Map(starts, hit.Index);
        if (!cache.Ready || cache.Line != line)
        {
            cache.Ready = true;
            cache.Line = line;
            cache.Text = LineMap.LineText(text, starts, line);
            (cache.Before, cache.After) = Context(text, starts, lineCount, line, plan);
        }

        matches.Add(new SearchMatch(
            relativePath,
            line,
            column,
            hit.Value,
            cache.Text!,
            cache.Before,
            cache.After,
            hit.Index));
    }

    /// <summary>One decoded line, reused by every hit on that line.</summary>
    private struct SharedLine
    {
        public bool Ready;
        public int Line;
        public string? Text;
        public IReadOnlyList<ContextLine>? Before;
        public IReadOnlyList<ContextLine>? After;
    }

    private static void CollectLines(
        string text,
        int[] starts,
        int lineCount,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches)
    {
        var cache = default(SharedLine);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!WholeLineHit(text, starts, line, plan, out var hit))
            {
                continue;
            }

            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
            if (plan.StopAtFirst)
            {
                return;
            }
        }
    }

    private static Hit? FirstWholeLine(string text, MatchPlan plan)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        for (var line = 1; line <= lineCount; line++)
        {
            if (WholeLineHit(text, starts, line, plan, out var hit))
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// A hit whose span is the line text. <see cref="LineMap.LineText"/> already drops a trailing CR.
    /// </summary>
    private static bool WholeLineHit(string text, int[] starts, int line, MatchPlan plan, out Hit hit)
    {
        var lineText = LineMap.LineText(text, starts, line);
        var start = line - 1 < starts.Length ? starts[line - 1] : 0;
        hit = default;
        if (!LineMatches(lineText, plan))
        {
            return false;
        }

        if (plan.Word && !WordBoundary.At(text, start, lineText.Length))
        {
            return false;
        }

        hit = new Hit(start, lineText.Length, lineText);
        return true;
    }

    private static bool LineMatches(string lineText, MatchPlan plan)
    {
        if (plan.Regex)
        {
            foreach (var regex in plan.Regexes!)
            {
                if (regex.IsMatch(lineText))
                {
                    return true;
                }
            }

            return false;
        }

        var comparison = plan.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var pattern in plan.Patterns)
        {
            if (lineText.Equals(pattern, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static Hit? FirstHit(string text, MatchPlan plan)
    {
        if (plan.LineRegexp)
        {
            return FirstWholeLine(text, plan);
        }

        if (plan.UseLinePrefilter)
        {
            return FirstPrefiltered(text, plan);
        }

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

    private static void CollectPrefiltered(string text, int[] starts, int lineCount, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
        var hits = new List<Hit>();
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                foreach (Match match in plan.Regexes[i].Matches(slice))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    var index = start + match.Index;
                    if (plan.Word && !WordBoundary.At(text, index, match.Length))
                    {
                        continue;
                    }

                    hits.Add(new Hit(index, match.Length, match.Value));
                }
            }
        });

        hits.Sort(static (left, right) =>
        {
            var index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Length.CompareTo(right.Length);
        });
        var cache = default(SharedLine);
        foreach (var hit in Dedup(hits))
        {
            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
        }
    }

    private static Hit? FirstPrefiltered(string text, MatchPlan plan)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        Hit? found = null;
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            if (found != null)
            {
                return;
            }

            var bestRel = int.MaxValue;
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                var from = 0;
                while (from <= slice.Length && from < bestRel)
                {
                    var match = plan.Regexes[i].Match(slice, from);
                    if (!match.Success || match.Index >= bestRel)
                    {
                        break;
                    }

                    var index = start + match.Index;
                    if (!plan.Word || WordBoundary.At(text, index, match.Length))
                    {
                        found = new Hit(index, match.Length, match.Value);
                        bestRel = match.Index;
                        break;
                    }

                    var next = match.Index + Math.Max(1, match.Length);
                    from = next <= from ? from + 1 : next;
                }
            }
        });
        return found;
    }

    private static void MarkPrefiltered(string text, int[] starts, MatchPlan plan, HashSet<int> lines)
    {
        var lineCount = LineMap.LineCount(text, starts);
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            if (SliceHits(text, slice, start, plan))
            {
                lines.Add(LineMap.Map(starts, start).Line);
            }
        });
    }

    private static bool SliceHits(string text, string slice, int start, MatchPlan plan)
    {
        foreach (var regex in plan.Regexes!)
        {
            var from = 0;
            while (from <= slice.Length)
            {
                var match = regex.Match(slice, from);
                if (!match.Success)
                {
                    break;
                }

                if (!plan.Word || WordBoundary.At(text, start + match.Index, match.Length))
                {
                    return true;
                }

                var next = match.Index + Math.Max(1, match.Length);
                from = next <= from ? from + 1 : next;
            }
        }

        return false;
    }

    private static void ForCandidateLines(string text, int[] starts, int lineCount, MatchPlan plan, Action<int, string> body)
    {
        if (lineCount <= 0 || plan.Literals == null)
        {
            return;
        }

        var candidates = new bool[lineCount];
        plan.Literals.MarkLines(text, starts, lineCount, plan.IgnoreCase, candidates);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!candidates[line - 1])
            {
                continue;
            }

            var start = starts[line - 1];
            var end = line < starts.Length ? starts[line] - 1 : text.Length;
            if (end < start)
            {
                end = start;
            }

            body(start, text.Substring(start, end - start));
        }
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
        if (plan.LineRegexp)
        {
            var lineCount = LineMap.LineCount(text, starts);
            for (var line = 1; line <= lineCount; line++)
            {
                if (WholeLineHit(text, starts, line, plan, out _))
                {
                    lines.Add(line);
                }
            }

            return;
        }

        if (plan.Regex)
        {
            if (plan.UseLinePrefilter)
            {
                MarkPrefiltered(text, starts, plan, lines);
                return;
            }

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

/// <summary>
/// A regex with no metacharacters (escapes of those metacharacters are unescaped).
/// Case-sensitive search then matches the same spans as the interpreter.
/// </summary>
internal static class VerbatimPattern
{
    public static bool TryAll(string[] patterns, out string[] literals)
    {
        literals = new string[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            if (!TryOne(patterns[i], out literals[i]))
            {
                literals = [];
                return false;
            }
        }

        return true;
    }

    public static bool TryOne(string pattern, out string literal)
    {
        var built = new System.Text.StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 >= pattern.Length || !IsEscapedLiteral(pattern[i + 1]))
                {
                    literal = "";
                    return false;
                }

                built.Append(pattern[++i]);
                continue;
            }

            if (IsMeta(ch))
            {
                literal = "";
                return false;
            }

            built.Append(ch);
        }

        literal = built.ToString();
        // A newline inside a regex is a cross-line match. Without -U those spans are
        // discarded, so this is not the same as a literal search.
        if (literal.IndexOf('\n') >= 0 || literal.IndexOf('\r') >= 0)
        {
            literal = "";
            return false;
        }

        return true;
    }

    private static bool IsMeta(char value) =>
        value is '.' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '*' or '+' or '?';

    private static bool IsEscapedLiteral(char value) => IsMeta(value) || value == '\\';
}

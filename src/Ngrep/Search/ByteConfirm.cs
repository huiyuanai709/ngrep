using System.Text;
using Ngrep.Text;

namespace Ngrep.Search;

/// <summary>
/// Confirms an ASCII literal on the file bytes. Source files are almost all ASCII,
/// where a byte offset is a UTF-16 offset, so a hit does not decode the whole file.
/// First-hit reads (<c>-l</c>) stop once the matching line is in hand.
/// </summary>
internal static class ByteConfirm
{
    [ThreadStatic]
    private static StopState? _state;

    public static bool CanHandle(MatchPlan plan)
    {
        // -x is a whole-line match. A substring hit in the bytes is not enough.
        if (plan.MixedCase || plan.Invert || plan.Regex || plan.LineRegexp || plan.Literals == null)
        {
            return false;
        }

        return plan.Literals.TryAsciiOr(plan.IgnoreCase, out _, out _, out _);
    }

    /// <summary>
    /// A stop callback that is not stored in the thread-static slot.
    /// Reads resume on another pool thread; <see cref="Adopt"/> binds this callback there before confirm.
    /// The callback remembers a miss so confirmation does not scan the file again.
    /// </summary>
    public static ReadStop? ArmFresh(MatchPlan plan)
    {
        if (plan.Word || !CanHandle(plan) || PatternHasNewline(plan))
        {
            return null;
        }

        int? lines = null;
        var after = 0;
        if (plan.MaxCount is int maxCount && maxCount > 0)
        {
            lines = maxCount;
            after = plan.After;
        }

        if (plan.StopAtFirst)
        {
            if (lines is int capped)
            {
                lines = Math.Min(capped, 1);
            }
            else if (!plan.WantsContext)
            {
                lines = 1;
            }
        }

        if (lines is not int limit)
        {
            return null;
        }

        if (plan.MaxCount is null)
        {
            after = 0;
        }

        if (!plan.Literals!.TryAsciiOr(plan.IgnoreCase, out var any, out _, out var lengths))
        {
            return null;
        }

        var max = 1;
        foreach (var length in lengths)
        {
            if (length > max)
            {
                max = length;
            }
        }

        var state = new StopState();
        state.Arm(any, max, limit, after);
        return state.Stop;
    }

    private static bool PatternHasNewline(MatchPlan plan)
    {
        foreach (var pattern in plan.Patterns)
        {
            if (pattern.IndexOf('\n') >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Makes <see cref="TryHandle"/> see the stop state <paramref name="stop"/> closes over.</summary>
    public static void Adopt(ReadStop? stop)
    {
        if (stop?.Target is StopState state)
        {
            _state = state;
        }
    }

    public static void Disarm()
    {
        if (_state != null)
        {
            _state.Active = false;
        }
    }

    public static bool TryHandle(ReadOnlySpan<byte> span, string relativePath, MatchPlan plan, List<SearchMatch> matches, out bool matched)
    {
        matched = false;
        var state = _state;
        var streamed = state is { Active: true };
        var knownMiss = false;
        if (streamed)
        {
            state!.Active = false;
            if (state.NonAscii)
            {
                return false;
            }

            knownMiss = state.Hit < 0;
        }

        if (!CanHandle(plan))
        {
            return false;
        }

        // A byte miss is final for an ASCII needle, including inside a non-ASCII file.
        if (knownMiss)
        {
            return true;
        }

        if (plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll)
        {
            matched = HasHit(span, plan);
            if (!matched)
            {
                return true;
            }

            // -w next to a non-ASCII letter is not an ASCII boundary.
            if (plan.Word && !Ascii.IsValid(span))
            {
                return false;
            }

            return true;
        }

        var before = matches.Count;
        if (!Collect(span, relativePath, plan, matches))
        {
            return false;
        }

        matched = matches.Count > before;
        return true;
    }

    private static bool HasHit(ReadOnlySpan<byte> span, MatchPlan plan)
    {
        plan.Literals!.TryAsciiOr(plan.IgnoreCase, out var any, out var each, out var lengths);
        if (!plan.Word)
        {
            return any.IndexOfAny(span) >= 0;
        }

        for (var n = 0; n < lengths.Length; n++)
        {
            var scanner = each == null ? any : each[n];
            if (Find(scanner, span, 0, lengths[n], word: true) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>False when a hit sits in a file that is not UTF-8 and columns must be decoded.</summary>
    private static bool Collect(ReadOnlySpan<byte> span, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
        span = span[TextFiles.Utf8BomLength(span)..];
        plan.Literals!.TryAsciiOr(plan.IgnoreCase, out var any, out var each, out var lengths);
        var hits = new List<Hit>(4);
        if (plan.StopAtFirst)
        {
            var bestAt = int.MaxValue;
            var bestLen = 0;
            for (var n = 0; n < lengths.Length; n++)
            {
                var scanner = each == null ? any : each[n];
                var idx = Find(scanner, span, 0, lengths[n], plan.Word);
                if (idx >= 0 && idx < bestAt)
                {
                    bestAt = idx;
                    bestLen = lengths[n];
                }
            }

            if (bestAt == int.MaxValue)
            {
                return true;
            }

            hits.Add(new Hit(bestAt, bestLen));
        }
        else if (plan.Word && plan.MaxCount is int wordMax && lengths.Length == 1)
        {
            var len = lengths[0];
            var scanner = each == null ? any : each[0];
            var from = 0;
            var line = 1;
            var cursor = 0;
            var kept = 0;
            var last = 0;
            while (from <= span.Length - len)
            {
                var idx = Find(scanner, span, from, len, word: true);
                if (idx < 0)
                {
                    break;
                }

                for (var i = cursor; i < idx; i++)
                {
                    if (span[i] == (byte)'\n')
                    {
                        line++;
                    }
                }

                cursor = idx;
                if (kept == 0 || line != last)
                {
                    if (kept >= wordMax)
                    {
                        break;
                    }

                    kept++;
                    last = line;
                }

                hits.Add(new Hit(idx, len));
                from = idx + len;
            }
        }
        else
        {
            var exclusive = int.MaxValue;
            if (!plan.Word && plan.MaxCount is int maxLines && maxLines > 0 && !PatternHasNewline(plan))
            {
                exclusive = MatchingPrefixEnd(any, span, maxLines);
            }

            for (var n = 0; n < lengths.Length; n++)
            {
                var scanner = each == null ? any : each[n];
                var len = lengths[n];
                var from = 0;
                while (from < exclusive && from <= span.Length - len)
                {
                    var idx = Find(scanner, span, from, len, plan.Word);
                    if (idx < 0 || idx >= exclusive)
                    {
                        break;
                    }

                    hits.Add(new Hit(idx, len));
                    from = idx + len;
                }
            }

            if (hits.Count > 1)
            {
                hits.Sort(static (left, right) =>
                {
                    var index = left.Index.CompareTo(right.Index);
                    return index != 0 ? index : left.Length.CompareTo(right.Length);
                });
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

                hits = deduped;
            }
        }

        if (hits.Count == 0)
        {
            return true;
        }

        // An ASCII needle matches the same bytes in UTF-8, so the hit is real. Byte
        // offsets stop being character offsets at the first non-ASCII byte; that line's
        // column is counted in characters instead of decoding the whole file again.
        var ascii = Ascii.IsValid(span);
        if (!ascii && !System.Text.Unicode.Utf8.IsValid(span))
        {
            return false;
        }

        // One hit per file is the common confirm. Counting newlines up to that
        // hit avoids building a line-start table for the rest of the file.
        if (!plan.WantsContext && hits.Count == 1)
        {
            EmitOne(span, relativePath, hits[0], matches, ascii, plan.NeedLineText || plan.NeedMatchText);
            return true;
        }

        var starts = LineStarts(span);
        var lineCount = span.Length == 0 ? 0 : span[^1] == (byte)'\n' ? starts.Length - 1 : starts.Length;
        // Hits are in order. A minified line can contain the needle thousands of times;
        // one string (and one context pair) is shared by every hit on that line.
        var sharedLine = 0;
        string? sharedText = null;
        IReadOnlyList<ContextLine>? sharedBefore = null;
        IReadOnlyList<ContextLine>? sharedAfter = null;
        foreach (var hit in hits)
        {
            var (line, column) = LineMap.Map(starts, hit.Index);
            if (!ascii)
            {
                column = TextFiles.Column(span, LineStart(starts, line), hit.Index);
            }

            if (sharedText == null || line != sharedLine)
            {
                sharedLine = line;
                sharedText = plan.NeedLineText ? LineText(span, starts, line) : "";
                (sharedBefore, sharedAfter) = Context(span, starts, lineCount, line, plan);
            }

            matches.Add(new SearchMatch(
                relativePath,
                line,
                column,
                plan.NeedMatchText ? Text(span.Slice(hit.Index, hit.Length)) : "",
                sharedText,
                sharedBefore,
                sharedAfter,
                hit.Index));
        }

        return true;
    }

    private static void EmitOne(ReadOnlySpan<byte> span, string relativePath, Hit hit, List<SearchMatch> matches, bool ascii, bool needText)
    {
        var index = hit.Index;
        var line = 1 + span[..index].Count((byte)'\n');
        var start = index;
        while (start > 0 && span[start - 1] != (byte)'\n')
        {
            start--;
        }

        var end = index + hit.Length;
        while (end < span.Length && span[end] != (byte)'\n')
        {
            end++;
        }

        var textEnd = end;
        if (textEnd > start && span[textEnd - 1] == (byte)'\r')
        {
            textEnd--;
        }

        var column = ascii ? index - start + 1 : TextFiles.Column(span, start, index);
        matches.Add(new SearchMatch(
            relativePath,
            line,
            column,
            needText ? Text(span.Slice(index, hit.Length)) : "",
            needText ? Text(span[start..textEnd]) : "",
            null,
            null,
            index));
    }

    /// <summary>
    /// Byte index where hits past the first <paramref name="maxLines"/> matching lines begin.
    /// The scanner is not word-aware; callers skip this when <c>-w</c> is set.
    /// </summary>
    private static int MatchingPrefixEnd(SimdLiteralScanner scanner, ReadOnlySpan<byte> span, int maxLines)
    {
        var from = 0;
        var lines = 0;
        while (from < span.Length && lines < maxLines)
        {
            var rel = scanner.IndexOfAny(span[from..]);
            if (rel < 0)
            {
                return span.Length;
            }

            var idx = from + rel;
            var nl = span[idx..].IndexOf((byte)'\n');
            lines++;
            if (nl < 0)
            {
                return span.Length;
            }

            from = idx + nl + 1;
        }

        return from;
    }

    private static int Find(SimdLiteralScanner scanner, ReadOnlySpan<byte> span, int from, int length, bool word)
    {
        while (from <= span.Length - length)
        {
            var rel = scanner.IndexOfAny(span[from..]);
            if (rel < 0)
            {
                return -1;
            }

            var idx = from + rel;
            if (!word || AsciiWord(span, idx, length))
            {
                return idx;
            }

            from = idx + 1;
        }

        return -1;
    }

    private static bool AsciiWord(ReadOnlySpan<byte> text, int index, int length)
    {
        if ((uint)index > (uint)text.Length || length < 0 || index + length > text.Length)
        {
            return false;
        }

        return Boundary(text, index) && Boundary(text, index + length);
    }

    private static bool Boundary(ReadOnlySpan<byte> text, int index)
    {
        var left = index > 0 && IsWord(text[index - 1]);
        var right = index < text.Length && IsWord(text[index]);
        return left != right;
    }

    private static bool IsWord(byte value) =>
        value == (byte)'_'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'a' and <= (byte)'z';

    private static int[] LineStarts(ReadOnlySpan<byte> span)
    {
        var count = 1;
        foreach (var value in span)
        {
            if (value == (byte)'\n')
            {
                count++;
            }
        }

        var starts = new int[count];
        var n = 1;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
            {
                starts[n++] = i + 1;
            }
        }

        return starts;
    }

    private static string LineText(ReadOnlySpan<byte> span, int[] starts, int line)
    {
        if (line < 1 || line > starts.Length)
        {
            return "";
        }

        var start = starts[line - 1];
        var end = line < starts.Length ? starts[line] - 1 : span.Length;
        if (end > start && span[end - 1] == (byte)'\r')
        {
            end--;
        }

        if (end < start)
        {
            end = start;
        }

        return Text(span[start..end]);
    }

    /// <summary>1-based line start offset, for the non-ASCII column count.</summary>
    private static int LineStart(int[] starts, int line) =>
        line >= 1 && line <= starts.Length ? starts[line - 1] : 0;

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static (IReadOnlyList<ContextLine>? Before, IReadOnlyList<ContextLine>? After) Context(
        ReadOnlySpan<byte> span,
        int[] starts,
        int lineCount,
        int line,
        MatchPlan plan)
    {
        if (!plan.WantsContext)
        {
            return (null, null);
        }

        return (SliceBefore(span, starts, line, plan.Before), SliceAfter(span, starts, lineCount, line, plan.After));
    }

    private static ContextLine[] SliceBefore(ReadOnlySpan<byte> span, int[] starts, int line, int count)
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
            lines[i] = new ContextLine(number, LineText(span, starts, number), number - 1 < starts.Length ? starts[number - 1] : 0);
        }

        return lines;
    }

    private static ContextLine[] SliceAfter(ReadOnlySpan<byte> span, int[] starts, int lineCount, int line, int count)
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
            lines[i] = new ContextLine(number, LineText(span, starts, number), number - 1 < starts.Length ? starts[number - 1] : 0);
        }

        return lines;
    }

    private readonly record struct Hit(int Index, int Length);

    private sealed class StopState
    {
        public StopState() => Stop = OnRead;

        public ReadStop Stop { get; }

        public bool Active { get; set; }

        public int Hit { get; private set; } = -1;

        public bool NonAscii { get; private set; }

        private SimdLiteralScanner? _scanner;

        private int _scan;

        private int _maxLength;

        private int _lineLimit;

        private int _afterLines;

        private int _matchedLines;

        private bool _awaitingLineEnd;

        private int _holdIndex;

        private bool _collectingAfter;

        private int _afterScan;

        private int _afterSeen;

        public void Arm(SimdLiteralScanner scanner, int maxLength, int lineLimit, int afterLines)
        {
            _scanner = scanner;
            _maxLength = Math.Max(1, maxLength);
            _lineLimit = Math.Max(1, lineLimit);
            _afterLines = Math.Max(0, afterLines);
            _scan = 0;
            _matchedLines = 0;
            _awaitingLineEnd = false;
            _collectingAfter = false;
            _afterScan = 0;
            _afterSeen = 0;
            Hit = -1;
            NonAscii = false;
            Active = true;
        }

        private bool OnRead(ReadOnlySpan<byte> span, bool endOfFile)
        {
            if (!Active || _scanner == null || NonAscii)
            {
                return false;
            }

            if (_collectingAfter)
            {
                return ConsumeAfter(span, endOfFile);
            }

            while (true)
            {
                if (_awaitingLineEnd)
                {
                    var held = span[_holdIndex..].IndexOf((byte)'\n');
                    if (held < 0)
                    {
                        if (!endOfFile)
                        {
                            return false;
                        }

                        return FinishMatchingLine(span, span.Length, endOfFile);
                    }

                    _awaitingLineEnd = false;
                    if (FinishMatchingLine(span, _holdIndex + held + 1, endOfFile))
                    {
                        return true;
                    }

                    continue;
                }

                if (_scan > span.Length)
                {
                    _scan = 0;
                }

                // A miss is final for an ASCII needle, so the bytes are not validated.
                // Only a hit checks that the prefix is ASCII, and only the new window is scanned.
                var rel = _scanner.IndexOfAny(span[_scan..]);
                if (rel < 0)
                {
                    _scan = endOfFile ? span.Length : Math.Max(_scan, span.Length - _maxLength + 1);
                    return false;
                }

                var idx = _scan + rel;
                if (!Ascii.IsValid(span[..(idx + 1)]))
                {
                    NonAscii = true;
                    return false;
                }

                if (Hit < 0)
                {
                    Hit = idx;
                }

                _matchedLines++;
                var newline = span[idx..].IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (!endOfFile)
                    {
                        _awaitingLineEnd = true;
                        _holdIndex = idx;
                        return false;
                    }

                    return true;
                }

                if (FinishMatchingLine(span, idx + newline + 1, endOfFile))
                {
                    return true;
                }
            }
        }

        private bool FinishMatchingLine(ReadOnlySpan<byte> span, int lineEnd, bool endOfFile)
        {
            if (_matchedLines < _lineLimit)
            {
                _scan = lineEnd;
                return false;
            }

            if (_afterLines <= 0)
            {
                return true;
            }

            _collectingAfter = true;
            _afterScan = lineEnd;
            _afterSeen = 0;
            return ConsumeAfter(span, endOfFile);
        }

        private bool ConsumeAfter(ReadOnlySpan<byte> span, bool endOfFile)
        {
            for (var i = _afterScan; i < span.Length; i++)
            {
                if (span[i] == (byte)'\n')
                {
                    _afterSeen++;
                    if (_afterSeen >= _afterLines)
                    {
                        return true;
                    }
                }
            }

            _afterScan = span.Length;
            return endOfFile;
        }
    }
}

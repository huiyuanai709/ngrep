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
        if (plan.Invert || plan.Regex || plan.LineRegexp || plan.Literals == null)
        {
            return false;
        }

        return plan.Literals.TryAsciiOr(plan.IgnoreCase, out _, out _, out _);
    }

    /// <summary>
    /// Arms a per-thread reader callback for <c>-l</c> without context or <c>-w</c>.
    /// The callback remembers a miss so confirmation does not scan the file again.
    /// </summary>
    public static ReadStop? Arm(MatchPlan plan)
    {
        if (!plan.StopAtFirst || plan.WantsContext || plan.Word || !CanHandle(plan))
        {
            return null;
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

        var state = _state ??= new StopState();
        state.Arm(any, max);
        return state.Stop;
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

    /// <summary>False when a hit sits in a non-ASCII file and columns must be decoded.</summary>
    private static bool Collect(ReadOnlySpan<byte> span, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
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
        else
        {
            for (var n = 0; n < lengths.Length; n++)
            {
                var scanner = each == null ? any : each[n];
                var len = lengths[n];
                var from = 0;
                while (from <= span.Length - len)
                {
                    var idx = Find(scanner, span, from, len, plan.Word);
                    if (idx < 0)
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

        // Byte offsets are char offsets only while the file is ASCII.
        if (!Ascii.IsValid(span))
        {
            return false;
        }

        var starts = LineStarts(span);
        var lineCount = span.Length == 0 ? 0 : span[^1] == (byte)'\n' ? starts.Length - 1 : starts.Length;
        foreach (var hit in hits)
        {
            var (line, column) = LineMap.Map(starts, hit.Index);
            var (before, after) = Context(span, starts, lineCount, line, plan);
            matches.Add(new SearchMatch(
                relativePath,
                line,
                column,
                AsciiText(span.Slice(hit.Index, hit.Length)),
                LineText(span, starts, line),
                before,
                after,
                hit.Index));
        }

        return true;
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

        return AsciiText(span[start..end]);
    }

    private static string AsciiText(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

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

        private int _accepted;

        private int _maxLength;

        public void Arm(SimdLiteralScanner scanner, int maxLength)
        {
            _scanner = scanner;
            _maxLength = Math.Max(1, maxLength);
            _accepted = 0;
            Hit = -1;
            NonAscii = false;
            Active = true;
        }

        private bool OnRead(ReadOnlySpan<byte> span, bool endOfFile)
        {
            if (!Active || _scanner == null)
            {
                return false;
            }

            if (!Ascii.IsValid(span))
            {
                NonAscii = true;
                return false;
            }

            var start = _accepted;
            if (start > span.Length)
            {
                start = 0;
            }

            var rel = _scanner.IndexOfAny(span[start..]);
            if (rel < 0)
            {
                _accepted = endOfFile ? span.Length : Math.Max(start, span.Length - _maxLength + 1);
                return false;
            }

            var idx = start + rel;
            // Columns follow bytes only when every byte before the match is ASCII.
            if (!Ascii.IsValid(span[..(idx + 1)]))
            {
                NonAscii = true;
                return false;
            }

            Hit = idx;
            if (endOfFile || span[idx..].IndexOf((byte)'\n') >= 0)
            {
                return true;
            }

            _accepted = idx;
            return false;
        }
    }
}

using Netgrep.Indexing;

namespace Netgrep.Search;

/// <summary>
/// Required literals for a pattern, in disjunctive normal form: any clause may match,
/// and every part inside a clause must occur. Extraction is conservative. Unsupported
/// syntax yields null, and the caller scans every candidate.
/// A clause can use the trigram index only when one of its parts is at least
/// <see cref="Trigrams.Length"/> characters. Shorter parts still reject files during
/// confirmation when they are ASCII, but they cannot drop index documents.
/// </summary>
internal sealed class LiteralQuery
{
    private const int MaxClauses = 64;

    private readonly Clause[] _clauses;

    private LiteralQuery(Clause[] clauses)
    {
        _clauses = clauses;
        TrigramSafe = clauses.Length > 0 && clauses.All(static clause => clause.HasTrigram);
    }

    public bool TrigramSafe { get; }

    public string[][] Parts => _clauses.Select(static clause => clause.Parts).ToArray();

    public static LiteralQuery? FromPatterns(IReadOnlyList<string> patterns, bool regex)
    {
        if (!regex)
        {
            var clauses = new Clause[patterns.Count];
            for (var i = 0; i < patterns.Count; i++)
            {
                clauses[i] = Clause.FromExact(patterns[i]);
            }

            return new LiteralQuery(clauses);
        }

        var all = new List<Clause>();
        foreach (var pattern in patterns)
        {
            var extracted = Extract(pattern);
            if (extracted == null)
            {
                return null;
            }

            all.AddRange(extracted);
            if (all.Count > MaxClauses)
            {
                return null;
            }
        }

        return all.Count == 0 ? null : new LiteralQuery(all.ToArray());
    }

    public bool TrySelect(Dictionary<string, List<int>> postings, out HashSet<int> ids)
    {
        ids = [];
        if (!TrigramSafe)
        {
            return false;
        }

        foreach (var clause in _clauses)
        {
            HashSet<int>? acc = null;
            foreach (var part in clause.Parts)
            {
                if (part.Length < Trigrams.Length)
                {
                    continue;
                }

                var hit = Intersect(postings, Trigrams.ForQuery(part));
                if (acc == null)
                {
                    acc = hit;
                }
                else
                {
                    acc.IntersectWith(hit);
                }

                if (acc.Count == 0)
                {
                    break;
                }
            }

            if (acc == null)
            {
                ids = [];
                return false;
            }

            ids.UnionWith(acc);
        }

        return true;
    }

    /// <summary>
    /// True when the bytes cannot satisfy any clause. Non-ASCII parts are not rejected here.
    /// </summary>
    public bool Misses(ReadOnlySpan<byte> utf8, bool ignoreCase)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        foreach (var clause in _clauses)
        {
            if (ClauseHits(utf8, clause, ignoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClauseHits(ReadOnlySpan<byte> utf8, Clause clause, bool ignoreCase)
    {
        if (clause.Parts.Length == 0)
        {
            return true;
        }

        foreach (var part in clause.Parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (!IsAscii(part))
            {
                return true;
            }

            if (!ContainsAscii(utf8, part, ignoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAscii(string text)
    {
        foreach (var ch in text)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, string needle, bool ignoreCase)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return needle.Length == 0;
        }

        if (!ignoreCase || !HasAsciiLetter(needle))
        {
            Span<byte> raw = needle.Length <= 128 ? stackalloc byte[needle.Length] : new byte[needle.Length];
            for (var i = 0; i < needle.Length; i++)
            {
                raw[i] = (byte)needle[i];
            }

            return haystack.IndexOf(raw) >= 0;
        }

        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (!AsciiEqual(haystack[i + j], (byte)needle[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAsciiLetter(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                return true;
            }
        }

        return false;
    }

    private static bool AsciiEqual(byte left, byte right)
    {
        if (left == right)
        {
            return true;
        }

        if (left is >= (byte)'A' and <= (byte)'Z')
        {
            left += 32;
        }

        if (right is >= (byte)'A' and <= (byte)'Z')
        {
            right += 32;
        }

        return left == right;
    }

    private static HashSet<int> Intersect(Dictionary<string, List<int>> postings, List<string> grams)
    {
        HashSet<int>? acc = null;
        foreach (var gram in grams.Distinct(StringComparer.Ordinal))
        {
            if (!postings.TryGetValue(gram, out var list) || list.Count == 0)
            {
                return [];
            }

            if (acc == null)
            {
                acc = list.ToHashSet();
                continue;
            }

            acc.IntersectWith(list);
            if (acc.Count == 0)
            {
                return acc;
            }
        }

        return acc ?? [];
    }

    private static Clause[]? Extract(string pattern)
    {
        var parser = new Parser(pattern);
        var node = parser.ParseAlt(stopAtGroup: false);
        if (node == null || parser.Failed || parser.Index != pattern.Length)
        {
            return null;
        }

        var analysis = Analyze(node);
        if (analysis == null || analysis.Choices.Length == 0 || analysis.Choices.Length > MaxClauses)
        {
            return null;
        }

        return analysis.Choices;
    }

    private static Analysis? Analyze(Node node)
    {
        switch (node)
        {
            case Lit lit:
                return Analysis.Exact(lit.Text);
            case Any:
                return Analysis.AnyChar;
            case Empty:
                return Analysis.EmptyMatch;
            case Cat cat:
                Analysis? folded = Analysis.EmptyMatch;
                foreach (var part in cat.Parts)
                {
                    var next = Analyze(part);
                    if (next == null || folded == null)
                    {
                        return null;
                    }

                    folded = Analysis.Concat(folded, next);
                    if (folded == null)
                    {
                        return null;
                    }
                }

                return folded;
            case Alt alt:
                Analysis? union = null;
                foreach (var part in alt.Parts)
                {
                    var next = Analyze(part);
                    if (next == null)
                    {
                        return null;
                    }

                    union = union == null ? next : Analysis.Or(union, next);
                    if (union == null)
                    {
                        return null;
                    }
                }

                return union ?? Analysis.EmptyMatch;
            case Rep rep:
                var inner = Analyze(rep.Inner);
                if (inner == null)
                {
                    return null;
                }

                return Analysis.Repeat(inner, rep.Min);
            default:
                return null;
        }
    }

    private sealed class Clause
    {
        private Clause(string[] parts, string? exact, string? suffix)
        {
            Parts = parts;
            Exact = exact;
            Suffix = suffix;
        }

        public string[] Parts { get; }

        public string? Exact { get; }

        /// <summary>Exact text the match ends with, when the next literal can be glued on. Null when a gap or any-char sits at the end.</summary>
        public string? Suffix { get; }

        public bool HasTrigram
        {
            get
            {
                foreach (var part in Parts)
                {
                    if (part.Length >= Trigrams.Length)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static Clause FromExact(string text)
        {
            return text.Length == 0 ? new Clause([], "", "") : new Clause([text], text, text);
        }

        public static Clause Gap() => new([], null, null);

        public static Clause Concat(Clause left, Clause right)
        {
            if (left.Exact != null && right.Exact != null)
            {
                return FromExact(left.Exact + right.Exact);
            }

            if (right.Exact != null && left.Suffix != null)
            {
                if (left.Parts.Length == 0)
                {
                    return FromExact(left.Suffix + right.Exact);
                }

                var extended = (string[])left.Parts.Clone();
                extended[^1] += right.Exact;
                return new Clause(extended, null, left.Suffix + right.Exact);
            }

            if (right.Exact != null)
            {
                var added = new string[left.Parts.Length + (right.Exact.Length == 0 ? 0 : 1)];
                left.Parts.CopyTo(added, 0);
                if (right.Exact.Length > 0)
                {
                    added[^1] = right.Exact;
                }

                return new Clause(added, null, right.Exact);
            }

            var count = left.Parts.Length + right.Parts.Length;
            if (count == 0)
            {
                return new Clause([], null, right.Suffix);
            }

            var parts = new string[count];
            left.Parts.CopyTo(parts, 0);
            right.Parts.CopyTo(parts, left.Parts.Length);
            return new Clause(parts, null, right.Suffix);
        }
    }

    private sealed class Analysis
    {
        private Analysis(bool nullable, Clause[] choices)
        {
            Nullable = nullable;
            Choices = choices;
        }

        public bool Nullable { get; }

        public Clause[] Choices { get; }

        public static Analysis EmptyMatch { get; } = new(true, [Clause.FromExact("")]);

        public static Analysis AnyChar { get; } = new(false, [Clause.Gap()]);

        public static Analysis NullableGap { get; } = new(true, [Clause.Gap()]);

        public static Analysis Exact(string text)
        {
            var clause = Clause.FromExact(text);
            return new Analysis(text.Length == 0, [clause]);
        }

        public static Analysis? Concat(Analysis left, Analysis right)
        {
            var count = left.Choices.Length * right.Choices.Length;
            if (count > MaxClauses)
            {
                return null;
            }

            var choices = new Clause[count];
            var index = 0;
            foreach (var first in left.Choices)
            {
                foreach (var second in right.Choices)
                {
                    choices[index++] = Clause.Concat(first, second);
                }
            }

            return new Analysis(left.Nullable && right.Nullable, choices);
        }

        public static Analysis? Or(Analysis left, Analysis right)
        {
            var count = left.Choices.Length + right.Choices.Length;
            if (count > MaxClauses)
            {
                return null;
            }

            var choices = new Clause[count];
            left.Choices.CopyTo(choices, 0);
            right.Choices.CopyTo(choices, left.Choices.Length);
            return new Analysis(left.Nullable || right.Nullable, choices);
        }

        public static Analysis Repeat(Analysis inner, int min)
        {
            if (min <= 0 || inner.Nullable)
            {
                // A repeat that can match empty may also consume, so neighbors must not be glued together.
                return NullableGap;
            }

            if (inner.Choices.Length == 1 && inner.Choices[0].Exact != null)
            {
                var piece = inner.Choices[0].Exact!;
                if (piece.Length == 0)
                {
                    return EmptyMatch;
                }

                if ((long)piece.Length * min <= 256)
                {
                    return Exact(string.Concat(Enumerable.Repeat(piece, min)));
                }
            }

            return new Analysis(false, inner.Choices);
        }
    }

    private abstract record Node;

    private sealed record Lit(string Text) : Node;

    private sealed record Any : Node;

    private sealed record Empty : Node;

    private sealed record Cat(Node[] Parts) : Node;

    private sealed record Alt(Node[] Parts) : Node;

    private sealed record Rep(Node Inner, int Min) : Node;

    private struct Parser
    {
        private readonly string _pattern;

        public Parser(string pattern) => _pattern = pattern;

        public int Index { get; private set; }

        public bool Failed { get; private set; }

        public Node? ParseAlt(bool stopAtGroup)
        {
            var parts = new List<Node>();
            var first = ParseConcat(stopAtGroup);
            if (first == null)
            {
                return null;
            }

            parts.Add(first);
            while (!Failed && Index < _pattern.Length && _pattern[Index] == '|')
            {
                Index++;
                var next = ParseConcat(stopAtGroup);
                if (next == null)
                {
                    return null;
                }

                parts.Add(next);
            }

            return parts.Count == 1 ? parts[0] : new Alt(parts.ToArray());
        }

        private Node? ParseConcat(bool stopAtGroup)
        {
            var parts = new List<Node>();
            while (!Failed && Index < _pattern.Length)
            {
                var ch = _pattern[Index];
                if (ch == '|' || (stopAtGroup && ch == ')'))
                {
                    break;
                }

                var quant = ParseQuant();
                if (quant == null)
                {
                    return null;
                }

                parts.Add(quant);
            }

            if (parts.Count == 0)
            {
                return new Empty();
            }

            return parts.Count == 1 ? parts[0] : new Cat(parts.ToArray());
        }

        private Node? ParseQuant()
        {
            var atom = ParseAtom();
            if (atom == null)
            {
                return null;
            }

            if (Index >= _pattern.Length)
            {
                return atom;
            }

            var ch = _pattern[Index];
            int min;
            if (ch == '*')
            {
                min = 0;
                Index++;
            }
            else if (ch == '+')
            {
                min = 1;
                Index++;
            }
            else if (ch == '?')
            {
                min = 0;
                Index++;
            }
            else if (ch == '{' && TryParseBrace(out min))
            {
                // consumed
            }
            else
            {
                return atom;
            }

            if (Index < _pattern.Length && _pattern[Index] == '?')
            {
                Index++;
            }

            return new Rep(atom, min);
        }

        private bool TryParseBrace(out int min)
        {
            min = 0;
            if (Index >= _pattern.Length || _pattern[Index] != '{')
            {
                return false;
            }

            var cursor = Index + 1;
            if (!TryDigits(ref cursor, out var first))
            {
                return false;
            }

            int? max = first;
            var lower = first;
            if (cursor < _pattern.Length && _pattern[cursor] == ',')
            {
                cursor++;
                if (cursor < _pattern.Length && char.IsDigit(_pattern[cursor]))
                {
                    if (!TryDigits(ref cursor, out var upper))
                    {
                        return false;
                    }

                    max = upper;
                }
                else
                {
                    max = null;
                }
            }

            if (cursor >= _pattern.Length || _pattern[cursor] != '}' || (max != null && max < lower))
            {
                return false;
            }

            Index = cursor + 1;
            min = lower;
            return true;
        }

        private bool TryDigits(ref int cursor, out int value)
        {
            value = 0;
            if (cursor >= _pattern.Length || !char.IsDigit(_pattern[cursor]))
            {
                return false;
            }

            var any = false;
            while (cursor < _pattern.Length && char.IsDigit(_pattern[cursor]))
            {
                any = true;
                value = (value * 10) + (_pattern[cursor] - '0');
                cursor++;
                if (value > 10_000)
                {
                    return false;
                }
            }

            return any;
        }

        private Node? ParseAtom()
        {
            if (Failed || Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            switch (ch)
            {
                case '.':
                    return new Any();
                case '^':
                case '$':
                    return new Empty();
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseClass();
                case '\\':
                    return ParseEscape();
                case ')' or '*' or '+' or '?' or '|':
                    Failed = true;
                    return null;
                default:
                    return new Lit(ch.ToString());
            }
        }

        private Node? ParseGroup()
        {
            if (Index < _pattern.Length && _pattern[Index] == '?')
            {
                if (Index + 1 < _pattern.Length && _pattern[Index + 1] == ':')
                {
                    Index += 2;
                }
                else
                {
                    Failed = true;
                    return null;
                }
            }

            var body = ParseAlt(stopAtGroup: true);
            if (body == null || Failed || Index >= _pattern.Length || _pattern[Index] != ')')
            {
                Failed = true;
                return null;
            }

            Index++;
            return body;
        }

        private Node? ParseClass()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var negated = false;
            if (_pattern[Index] == '^')
            {
                negated = true;
                Index++;
            }

            var chars = new List<char>();
            var ranged = false;
            var first = true;
            var closed = false;
            while (Index < _pattern.Length)
            {
                var ch = _pattern[Index++];
                if (ch == ']' && !first)
                {
                    closed = true;
                    break;
                }

                first = false;
                if (ch == '\\')
                {
                    var escaped = ParseClassEscape();
                    if (escaped == null)
                    {
                        SkipClass();
                        return Failed ? null : new Any();
                    }

                    chars.Add(escaped.Value);
                    continue;
                }

                if (ch == '-' && chars.Count > 0 && Index < _pattern.Length && _pattern[Index] != ']')
                {
                    ranged = true;
                    continue;
                }

                chars.Add(ch);
            }

            if (!closed)
            {
                Failed = true;
                return null;
            }

            if (!negated && !ranged && chars.Count == 1)
            {
                return new Lit(chars[0].ToString());
            }

            return new Any();
        }

        private void SkipClass()
        {
            while (Index < _pattern.Length)
            {
                var ch = _pattern[Index++];
                if (ch == '\\' && Index < _pattern.Length)
                {
                    Index++;
                    continue;
                }

                if (ch == ']')
                {
                    return;
                }
            }

            Failed = true;
        }

        private char? ParseClassEscape()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            if (ch is 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'p' or 'P')
            {
                if (ch is 'p' or 'P')
                {
                    SkipProperty();
                }

                return null;
            }

            return DecodeEscapeChar(ch);
        }

        private Node? ParseEscape()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            switch (ch)
            {
                case 'd' or 'D' or 's' or 'S' or 'w' or 'W':
                    return new Any();
                case 'b' or 'B' or 'A' or 'z' or 'Z' or 'G':
                    return new Empty();
                case 'p' or 'P':
                    if (!SkipProperty())
                    {
                        Failed = true;
                        return null;
                    }

                    return new Any();
                case >= '0' and <= '9':
                    Failed = true;
                    return null;
                default:
                    var decoded = DecodeEscapeChar(ch);
                    if (decoded == null)
                    {
                        Failed = true;
                        return null;
                    }

                    return new Lit(decoded.Value.ToString());
            }
        }

        private bool SkipProperty()
        {
            if (Index >= _pattern.Length || _pattern[Index] != '{')
            {
                return false;
            }

            var end = _pattern.IndexOf('}', Index + 1);
            if (end < 0)
            {
                return false;
            }

            Index = end + 1;
            return true;
        }

        private char? DecodeEscapeChar(char ch)
        {
            switch (ch)
            {
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'a':
                    return '\a';
                case 'f':
                    return '\f';
                case 'v':
                    return '\v';
                case 'e':
                    return '\x1B';
                case 'x':
                    return ParseHex(2);
                case 'u':
                    return ParseHex(4);
                case 'c':
                    if (Index >= _pattern.Length)
                    {
                        return null;
                    }

                    return (char)(_pattern[Index++] & 0x1F);
                default:
                    return ch;
            }
        }

        private char? ParseHex(int digits)
        {
            if (Index + digits > _pattern.Length)
            {
                return null;
            }

            var value = 0;
            for (var i = 0; i < digits; i++)
            {
                var hex = _pattern[Index++];
                value <<= 4;
                if (hex is >= '0' and <= '9')
                {
                    value += hex - '0';
                }
                else if (hex is >= 'a' and <= 'f')
                {
                    value += hex - 'a' + 10;
                }
                else if (hex is >= 'A' and <= 'F')
                {
                    value += hex - 'A' + 10;
                }
                else
                {
                    return null;
                }
            }

            return (char)value;
        }
    }
}

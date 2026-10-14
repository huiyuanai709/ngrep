using System.Runtime.Intrinsics.X86;
using System.Text;
using Ngrep.Indexing;
using Ngrep.Search;
using Ngrep.Text;

namespace Ngrep.Tests;

public class SimdLiteralTests
{
    [Fact]
    public void Active_isa_follows_hardware_support()
    {
        var active = SimdLiteralScanner.Active;
        if (Avx512BW.IsSupported)
        {
            Assert.Equal(SimdIsa.Avx512, active);
        }
        else if (Avx2.IsSupported)
        {
            Assert.Equal(SimdIsa.Avx2, active);
        }
        else if (Sse2.IsSupported || System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Assert.Equal(SimdIsa.Vector128, active);
        }
        else
        {
            Assert.Equal(SimdIsa.Scalar, active);
        }

        Assert.True(SimdLiteralScanner.Supports(SimdIsa.Scalar));
        Assert.Equal(Avx2.IsSupported, SimdLiteralScanner.Supports(SimdIsa.Avx2));
        Assert.Equal(Avx512BW.IsSupported, SimdLiteralScanner.Supports(SimdIsa.Avx512));
    }

    [Fact]
    public void Empty_needle_matches_at_zero_and_long_needle_misses()
    {
        var empty = SimdLiteralScanner.Create([Array.Empty<byte>()], ignoreCase: false);
        Assert.Equal(0, empty.IndexOfAny([]));
        Assert.Equal(0, empty.IndexOfAny("abc"u8));

        var missing = SimdLiteralScanner.Create(["abcd"u8.ToArray()], ignoreCase: false);
        Assert.Equal(-1, missing.IndexOfAny([]));
        Assert.Equal(-1, missing.IndexOfAny("abc"u8));
        Assert.Equal(-1, SimdLiteralScanner.Create([], ignoreCase: false).IndexOfAny("abc"u8));
    }

    [Fact]
    public void Finds_hits_misses_overlaps_and_chunk_edges()
    {
        AssertFound("netgrep", "xx netgrep yy", 3, ignoreCase: false);
        AssertFound("aa", "aaaa", 0, ignoreCase: false);
        AssertFound("ab", "xbcab", 3, ignoreCase: false);
        AssertAbsent("abc", new string('a', 64) + "abx", ignoreCase: false);
        AssertFound("abc", new string('x', 50) + "axcaxc" + "abc", 50 + 6, ignoreCase: false);

        foreach (var at in new[] { 0, 1, 14, 15, 16, 17, 30, 31, 32, 33, 62, 63, 64, 65, 127, 128, 200 })
        {
            AssertFound("Ab", new string('q', at) + "Ab" + "zz", at, ignoreCase: false);
            AssertFound("Net", new string('q', at) + "nEt" + "zz", at, ignoreCase: true);
            AssertFound("needle-32-bytes-long-value!!", new string('.', at) + "needle-32-bytes-long-value!!", at, ignoreCase: false);
        }

        AssertAbsent("[", "{", ignoreCase: true);
        AssertAbsent("@", "`", ignoreCase: true);
        AssertFound("A", "a", 0, ignoreCase: true);
        AssertAbsent("Net", "net", ignoreCase: false);
    }

    [Fact]
    public void Multi_literal_returns_the_leftmost_hit()
    {
        var needles = new[] { "xyz"u8.ToArray(), "ab"u8.ToArray(), "qqqq"u8.ToArray() };
        var hay = Encoding.ASCII.GetBytes(new string('m', 70) + "xyz" + new string('m', 10) + "ab");
        var scanner = SimdLiteralScanner.Create(needles, ignoreCase: false);
        Assert.Equal(70, scanner.IndexOfAny(hay));

        var earlier = Encoding.ASCII.GetBytes("zzab" + new string('m', 80) + "xyz");
        Assert.Equal(2, scanner.IndexOfAny(earlier));
        Assert.Equal(-1, scanner.IndexOfAny(Encoding.ASCII.GetBytes(new string('m', 180))));

        var folded = SimdLiteralScanner.Create(["Foo"u8.ToArray(), "BAR"u8.ToArray()], ignoreCase: true);
        Assert.Equal(4, folded.IndexOfAny(Encoding.ASCII.GetBytes("xxxxbar")));
        Assert.Equal(-1, folded.IndexOfAny([]));
        Assert.Equal(1, folded.IndexOfAny("{foo}"u8));
    }

    [Fact]
    public void Nine_patterns_exercise_a_second_teddy_group()
    {
        var needles = new byte[9][];
        for (var i = 0; i < needles.Length; i++)
        {
            needles[i] = Encoding.ASCII.GetBytes($"p{i:00}-needle");
        }

        var scanner = SimdLiteralScanner.Create(needles, ignoreCase: false);
        foreach (var at in new[] { 0, 15, 16, 31, 32, 63, 64, 127, 200 })
        {
            var hay = Encoding.ASCII.GetBytes(new string('z', at) + "p08-needle" + new string('z', 40) + "p00-needle");
            Agree(hay, needles, ignoreCase: false);
            Assert.Equal(at, scanner.IndexOfAny(hay));
        }

        var folded = new byte[9][];
        for (var i = 0; i < folded.Length; i++)
        {
            folded[i] = Encoding.ASCII.GetBytes($"Nx{i}");
        }

        var bytes = Encoding.ASCII.GetBytes(new string('q', 70) + "nx8");
        Agree(bytes, folded, ignoreCase: true);
        Assert.Equal(70, SimdLiteralScanner.Create(folded, ignoreCase: true).IndexOfAny(bytes));
    }

    [Fact]
    public void Every_supported_isa_matches_a_scalar_scan()
    {
        var rng = new Random(1234);
        var lengths = new[] { 0, 1, 2, 3, 4, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 512 };
        foreach (var ignoreCase in new[] { false, true })
        {
            for (var nlen = 1; nlen <= 48; nlen += nlen < 8 ? 1 : 7)
            {
                var needle = new byte[nlen];
                for (var i = 0; i < nlen; i++)
                {
                    needle[i] = (byte)rng.Next(1, 127);
                }

                foreach (var hlen in lengths)
                {
                    var hay = new byte[hlen];
                    rng.NextBytes(hay);
                    for (var i = 0; i < hay.Length; i++)
                    {
                        if (hay[i] == 0)
                        {
                            hay[i] = 1;
                        }
                    }

                    if (hlen >= nlen && rng.Next(2) == 0)
                    {
                        var at = rng.Next(hlen - nlen + 1);
                        needle.CopyTo(hay.AsSpan(at));
                    }

                    var extra = new byte[Math.Max(1, nlen / 2)];
                    rng.NextBytes(extra);
                    Agree(hay, [needle, extra], ignoreCase);
                }
            }
        }
    }

    [Fact]
    public void Required_literals_still_reject_only_real_misses()
    {
        var or = LiteralQuery.FromPatterns(["foo", "bar"], regex: false)!;
        Assert.True(or.Misses(Bytes(new string('z', 180)), ignoreCase: false));
        Assert.False(or.Misses(Bytes(new string('z', 80) + "bar"), ignoreCase: false));
        Assert.False(or.Misses(Bytes(new string('z', 90) + "BAR"), ignoreCase: true));
        Assert.True(or.Misses(Bytes(new string('z', 90) + "BAR"), ignoreCase: false));

        var and = LiteralQuery.FromPatterns(["foo.*bar"], regex: true)!;
        Assert.True(and.Misses(Bytes(new string('a', 70) + "foo"), ignoreCase: false));
        Assert.True(and.Misses(Bytes(new string('a', 70) + "bar"), ignoreCase: false));
        Assert.False(and.Misses(Bytes(new string('a', 40) + "foo" + new string('b', 40) + "bar"), ignoreCase: false));
        Assert.False(and.Misses(Bytes(new string('a', 64) + "FOO" + new string('b', 64) + "Bar"), ignoreCase: true));

        Assert.False(LiteralQuery.FromPatterns([""], regex: false)!.Misses("zzz"u8, ignoreCase: false));
        Assert.True(LiteralQuery.FromPatterns(["["], regex: false)!.Misses("{"u8, ignoreCase: true));

        var lead = LiteralQuery.FromPatterns(["foo.*é"], regex: true)!;
        Assert.True(lead.Misses(Bytes(new string('z', 40)), ignoreCase: false));
        Assert.False(lead.Misses(Bytes(new string('z', 40) + "foo"), ignoreCase: false));
        var trail = LiteralQuery.FromPatterns(["é.*foo"], regex: true)!;
        Assert.False(trail.Misses(Bytes(new string('z', 40)), ignoreCase: false));

        var bom = new byte[80];
        bom[0] = 0xEF;
        bom[1] = 0xBB;
        bom[2] = 0xBF;
        "Net"u8.CopyTo(bom.AsSpan(60));
        Assert.False(LiteralQuery.FromPatterns(["Net"], regex: false)!.Misses(bom, ignoreCase: false));
    }

    [Fact]
    public void Newline_scan_matches_a_scalar_walk()
    {
        AssertLines("");
        AssertLines("abc");
        AssertLines("\n");
        AssertLines("\n\n");
        AssertLines("a\nb\n");
        AssertLines("a\r\nb");

        var text = new StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            text.Append((char)('a' + (i % 23)));
            if (i is 0 or 7 or 8 or 15 or 16 or 31 or 32 or 63 or 64 or 127 or 128 or 255)
            {
                text.Append('\n');
            }
        }

        text.Append('\n');
        AssertLines(text.ToString());
        foreach (var isa in new[] { SimdIsa.Scalar, SimdIsa.Vector128, SimdIsa.Avx2, SimdIsa.Avx512 })
        {
            if (!SimdLiteralScanner.Supports(isa))
            {
                continue;
            }

            AssertLines(text.ToString(), isa);
        }
    }

    [Fact]
    public void Indexed_and_scan_agree_past_a_simd_chunk()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("hit.txt"), new string('x', 200) + "netgrep\n");
        File.WriteAllText(tree.File("miss.txt"), new string('y', 400) + "\n");
        File.WriteAllText(tree.File("fold.txt"), new string('z', 70) + "NetGrep\n");
        Indexer.Build(tree.Root);

        var indexed = Search(tree.Root, "netgrep");
        var scan = Search(tree.Root, "netgrep", scan: true);
        Assert.True(indexed.UsedIndex);
        Assert.Equal(["hit.txt"], indexed.Matches.Select(match => match.Path).ToArray());
        Assert.Equal(Texts(scan), Texts(indexed));

        var folded = Search(tree.Root, "netgrep", ignoreCase: true);
        var foldedScan = Search(tree.Root, "netgrep", ignoreCase: true, scan: true);
        Assert.Equal(["fold.txt", "hit.txt"], folded.Matches.Select(match => match.Path).ToArray());
        Assert.Equal(Texts(foldedScan), Texts(folded));

        File.WriteAllText(tree.File("both.txt"), new string('q', 48) + "foo" + new string('q', 80) + "bar\n");
        File.WriteAllText(tree.File("onlyfoo.txt"), new string('q', 90) + "foo\n");
        Indexer.Build(tree.Root);
        var regex = Search(tree.Root, "foo.*bar", regex: true);
        var regexScan = Search(tree.Root, "foo.*bar", regex: true, scan: true);
        Assert.Equal(["both.txt"], regex.Matches.Select(match => match.Path).Distinct().ToArray());
        Assert.Equal(Texts(regexScan), Texts(regex));
    }

    private static void AssertFound(string needle, string haystack, int index, bool ignoreCase)
    {
        var scanner = SimdLiteralScanner.Create([Encoding.ASCII.GetBytes(needle)], ignoreCase);
        var bytes = Encoding.ASCII.GetBytes(haystack);
        Assert.Equal(index, Naive(bytes, [Encoding.ASCII.GetBytes(needle)], ignoreCase));
        Agree(bytes, [Encoding.ASCII.GetBytes(needle)], ignoreCase);
        Assert.Equal(index, scanner.IndexOfAny(bytes));
    }

    private static void AssertAbsent(string needle, string haystack, bool ignoreCase)
    {
        AssertFound(needle, haystack, -1, ignoreCase);
    }

    private static void Agree(byte[] haystack, byte[][] needles, bool ignoreCase)
    {
        var expected = Naive(haystack, needles, ignoreCase);
        var scanner = SimdLiteralScanner.Create(needles, ignoreCase);
        Assert.Equal(expected, scanner.IndexOfAny(haystack, SimdIsa.Scalar));
        Assert.Equal(expected, scanner.IndexOfAny(haystack));
        foreach (var isa in new[] { SimdIsa.Vector128, SimdIsa.Avx2, SimdIsa.Avx512 })
        {
            if (!SimdLiteralScanner.Supports(isa))
            {
                continue;
            }

            Assert.Equal(expected, scanner.IndexOfAny(haystack, isa));
        }
    }

    private static int Naive(ReadOnlySpan<byte> haystack, byte[][] needles, bool ignoreCase)
    {
        foreach (var needle in needles)
        {
            if (needle.Length == 0)
            {
                return 0;
            }
        }

        for (var i = 0; i < haystack.Length; i++)
        {
            foreach (var needle in needles)
            {
                if (i + needle.Length > haystack.Length)
                {
                    continue;
                }

                if (EqualAt(haystack, i, needle, ignoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool EqualAt(ReadOnlySpan<byte> haystack, int pos, byte[] needle, bool ignoreCase)
    {
        for (var j = 0; j < needle.Length; j++)
        {
            var left = haystack[pos + j];
            var right = needle[j];
            if (left == right)
            {
                continue;
            }

            if (!ignoreCase || !SimdLiteralScanner.AsciiEqual(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertLines(string text, SimdIsa? isa = null)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        if (isa == null)
        {
            Assert.Equal(starts.ToArray(), LineMap.Build(text));
            if (text.Length > 0)
            {
                Assert.Equal(LineMap.LineCount(text, starts.ToArray()), LineMap.LineCount(text, LineMap.Build(text)));
            }

            return;
        }

        var found = new List<int> { 0 };
        SimdNewline.Append(text, found, isa.Value);
        Assert.Equal(starts.ToArray(), found.ToArray());
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static SearchOutcome Search(string root, string pattern, bool regex = false, bool scan = false, bool ignoreCase = false)
    {
        return Searcher.Search(new SearchQuery
        {
            Pattern = pattern,
            Path = root,
            Regex = regex,
            ForceScan = scan,
            IgnoreCase = ignoreCase,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
    }

    private static string[] Texts(SearchOutcome outcome) =>
        outcome.Matches.Select(match => $"{match.Path}:{match.Line}:{match.Column}:{match.Text}").ToArray();

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-simd-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

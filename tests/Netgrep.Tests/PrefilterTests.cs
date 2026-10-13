using Netgrep.Cli;
using Netgrep.Search;

namespace Netgrep.Tests;

public class PrefilterTests
{
    [Fact]
    public void Extracts_required_literals_and_refuses_unsafe_syntax()
    {
        AssertParts("n.tgrep", [["n", "tgrep"]], trigram: true);
        AssertParts("foo.*bar", [["foo", "bar"]], trigram: true);
        AssertParts("foo|bar", [["foo"], ["bar"]], trigram: true);
        AssertParts("(?:foo|bar)baz", [["foobaz"], ["barbaz"]], trigram: true);
        AssertParts("(ab){2}", [["abab"]], trigram: true);
        AssertParts("ab{2}", [["abb"]], trigram: true);
        AssertParts("^netgrep$", [["netgrep"]], trigram: true);
        AssertParts(@"\bnetgrep\b", [["netgrep"]], trigram: true);
        AssertParts("foo*", [["fo"]], trigram: false);
        AssertParts("foo?", [["fo"]], trigram: false);
        AssertParts("a|netgrep", [["a"], ["netgrep"]], trigram: false);
        AssertParts(".", [[]], trigram: false);
        AssertParts("a*", [[]], trigram: false);

        Assert.Null(LiteralQuery.FromPatterns(["(?=netgrep)x"], regex: true));
        Assert.Null(LiteralQuery.FromPatterns([@"(netgrep)\1"], regex: true));
        Assert.Null(LiteralQuery.FromPatterns(["(?=a)", "netgrep"], regex: true));
    }

    [Fact]
    public void Ascii_byte_prefilter_respects_case_and_bom()
    {
        var query = LiteralQuery.FromPatterns(["Net"], regex: false)!;
        Assert.True(query.Misses("net"u8, ignoreCase: false));
        Assert.False(query.Misses("net"u8, ignoreCase: true));
        Assert.False(query.Misses("xx Net yy"u8, ignoreCase: false));

        var bom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'N', (byte)'e', (byte)'t' };
        Assert.False(query.Misses(bom, ignoreCase: false));
        Assert.True(LiteralQuery.FromPatterns(["ab"], regex: false)!.Misses("zzzz"u8, ignoreCase: false));
    }

    [Fact]
    public void Regex_literals_shrink_the_index_and_match_a_scan()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("hit.txt"), "foo XX bar\n");
        File.WriteAllText(tree.File("reverse.txt"), "bar then foo\n");
        File.WriteAllText(tree.File("only.txt"), "foo alone\n");
        File.WriteAllText(tree.File("short.txt"), "a\n");
        File.WriteAllText(tree.File("other.txt"), "zzzzzz\n");
        Indexing.Indexer.Build(tree.Root);

        var regex = Search(tree.Root, "foo.*bar", regex: true);
        var scan = Search(tree.Root, "foo.*bar", regex: true, scan: true);
        Assert.True(regex.UsedIndex);
        Assert.True(regex.UsedTrigramPrefilter);
        Assert.Equal(["hit.txt"], regex.Matches.Select(match => match.Path).Distinct().ToArray());
        Assert.Equal(Texts(scan), Texts(regex));

        var alternation = Search(tree.Root, "a|netgrep", regex: true);
        Assert.False(alternation.UsedTrigramPrefilter);
        Assert.Contains(alternation.Matches, match => match.Path == "short.txt");

        var repeated = Search(tree.Root, "(ab){2}", regex: true);
        File.WriteAllText(tree.File("twice.txt"), "xxababxx\n");
        File.WriteAllText(tree.File("once.txt"), "ab\n");
        Indexing.Indexer.Build(tree.Root);
        repeated = Search(tree.Root, "(ab){2}", regex: true);
        Assert.True(repeated.UsedTrigramPrefilter);
        Assert.Equal(["twice.txt"], repeated.Matches.Select(match => match.Path).Distinct().ToArray());

        var dotted = Search(tree.Root, "n.tgrep", regex: true);
        Assert.True(dotted.UsedTrigramPrefilter);
        Assert.Empty(dotted.Matches);
        File.WriteAllText(tree.File("name.txt"), "nXtgrep\n");
        File.WriteAllText(tree.File("tail.txt"), "tgrep\n");
        Indexing.Indexer.Build(tree.Root);
        dotted = Search(tree.Root, "n.tgrep", regex: true);
        var dottedScan = Search(tree.Root, "n.tgrep", regex: true, scan: true);
        Assert.True(dotted.UsedTrigramPrefilter);
        Assert.Equal(["name.txt"], dotted.Matches.Select(match => match.Path).Distinct().ToArray());
        Assert.Equal(Texts(dottedScan), Texts(dotted));

        File.WriteAllText(tree.File("case.txt"), "NetGrep\n");
        Indexing.Indexer.Build(tree.Root);
        var folded = Search(tree.Root, "netgrep", regex: true, ignoreCase: true);
        var sensitive = Search(tree.Root, "netgrep", regex: true);
        Assert.True(folded.UsedTrigramPrefilter);
        Assert.Equal(["case.txt"], folded.Matches.Select(match => match.Path).Distinct().ToArray());
        Assert.Empty(sensitive.Matches);
    }

    [Fact]
    public void Short_and_inverted_patterns_stay_correct_without_trigrams()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("has.txt"), "zz ab zz\nnetgrep\n");
        File.WriteAllText(tree.File("miss.txt"), "nothing\n");
        File.WriteAllText(tree.File("lines.txt"), "netgrep netgrep\nkeep\nalso\n");
        Indexing.Indexer.Build(tree.Root);

        var sequential = Search(tree.Root, "ab", workers: 1);
        var parallel = Search(tree.Root, "ab", workers: 4);
        Assert.False(sequential.UsedTrigramPrefilter);
        Assert.True(sequential.UsedIndex);
        Assert.Equal(Texts(sequential), Texts(parallel));
        Assert.Equal(["has.txt"], sequential.Matches.Select(match => match.Path).Distinct().ToArray());

        var invert = Search(tree.Root, "netgrep", invert: true, workers: 4);
        var invertOne = Search(tree.Root, "netgrep", invert: true, workers: 1);
        Assert.False(invert.UsedTrigramPrefilter);
        Assert.Equal(Texts(invertOne), Texts(invert));
        Assert.Contains(invert.Matches, match => match.Path == "lines.txt" && match.Text == "keep");
        Assert.Contains(invert.Matches, match => match.Path == "lines.txt" && match.Text == "also");
        Assert.DoesNotContain(invert.Matches, match => match.Text.Contains("netgrep", StringComparison.Ordinal));
        Assert.Contains(invert.Matches, match => match.Path == "miss.txt" && match.Text == "nothing");

        var regexInvert = Search(tree.Root, "n.tgrep", regex: true, invert: true, workers: 4);
        Assert.False(regexInvert.UsedTrigramPrefilter);
        Assert.Equal(
            Texts(Search(tree.Root, "n.tgrep", regex: true, invert: true, workers: 1)),
            Texts(regexInvert));
    }

    [Fact]
    public void No_ignore_bypasses_the_index_and_keeps_dot_git_skipped()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("keep.txt"), "netgrep\n");
        File.WriteAllText(tree.File("skip.log"), "netgrep\n");
        Directory.CreateDirectory(Path.Combine(tree.Root, ".git"));
        File.WriteAllText(tree.File(".git/config"), "netgrep\n");
        Indexing.Indexer.Build(tree.Root);

        var indexed = Search(tree.Root, "netgrep");
        Assert.True(indexed.UsedIndex);
        Assert.Equal(["keep.txt"], indexed.Matches.Select(match => match.Path).ToArray());

        var ignored = Search(tree.Root, "netgrep", noIgnore: true);
        Assert.False(ignored.UsedIndex);
        Assert.Contains("--no-ignore", ignored.Note, StringComparison.Ordinal);
        Assert.Equal(["keep.txt", "skip.log"], ignored.Matches.Select(match => match.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());

        using var capture = new Capture();
        var code = NetgrepCli.Run(["netgrep", tree.Root, "--no-ignore-vcs", "-j", "1"]);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("--no-ignore-vcs", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("skip.log", capture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(".git/config", capture.Stdout, StringComparison.Ordinal);
    }

    private static void AssertParts(string pattern, string[][] expected, bool trigram)
    {
        var query = LiteralQuery.FromPatterns([pattern], regex: true);
        Assert.NotNull(query);
        Assert.Equal(trigram, query!.TrigramSafe);
        Assert.Equal(expected, query.Parts);
    }

    private static SearchOutcome Search(
        string root,
        string pattern,
        bool regex = false,
        bool scan = false,
        bool invert = false,
        bool ignoreCase = false,
        bool noIgnore = false,
        int workers = 1)
    {
        return Searcher.Search(new SearchQuery
        {
            Pattern = pattern,
            Path = root,
            Regex = regex,
            ForceScan = scan,
            InvertMatch = invert,
            IgnoreCase = ignoreCase,
            NoIgnore = noIgnore,
            MaxDegreeOfParallelism = workers,
        });
    }

    private static string[] Texts(SearchOutcome outcome) =>
        outcome.Matches.Select(match => $"{match.Path}:{match.Line}:{match.Column}:{match.Text}").ToArray();

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("netgrep-pre-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Capture : IDisposable
    {
        private readonly TextWriter _stdout = Console.Out;
        private readonly TextWriter _stderr = Console.Error;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        public Capture()
        {
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public string Stdout => _out.ToString();

        public string Stderr => _err.ToString();

        public void Dispose()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }
}

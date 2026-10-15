using System.Text;
using Ngrep.Search;

namespace Ngrep.Tests;

public class FastPathTests
{
    [Fact]
    public void Ascii_literal_reports_the_same_spans_as_a_decoded_search()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a.txt"), "alpha\r\nbeta netgrep gamma\r\n");
        File.WriteAllText(tree.File("b.txt"), "aaaa\nzz aa zz\n");
        File.WriteAllText(tree.File("c.txt"), "net\nnet-grep\nthe net.\n");

        var literal = Search(tree.Root, "netgrep");
        Assert.Equal(["a.txt:2:6:netgrep"], Texts(literal));

        var overlapping = Search(tree.Root, "aa");
        Assert.Equal(["b.txt:1:1:aa", "b.txt:1:3:aa", "b.txt:2:4:aa"], Texts(overlapping));

        var word = Search(tree.Root, "net", word: true);
        Assert.Equal([1, 2, 3], word.Matches.Where(match => match.Path == "c.txt").Select(match => match.Line).ToArray());
        Assert.Equal(["net", "net", "net"], word.Matches.Select(match => match.Text).ToArray());
    }

    [Fact]
    public void Non_ascii_prefix_keeps_the_char_column()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("u.txt"), "café netgrep\n");
        var found = Search(tree.Root, "netgrep");
        var match = Assert.Single(found.Matches);
        Assert.Equal(1, match.Line);
        Assert.Equal(6, match.Column);
        Assert.Equal("café netgrep", match.LineText);
    }

    [Fact]
    public void First_hit_past_a_read_chunk_keeps_the_line()
    {
        using var tree = new TempDir();
        var body = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            body.Append("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n");
        }

        body.Append("hit NEEDLE here\n");
        for (var i = 0; i < 100; i++)
        {
            body.Append("yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy\n");
        }

        var text = body.ToString();
        File.WriteAllText(tree.File("big.txt"), text);
        var expectedLine = text.Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains("NEEDLE", StringComparison.Ordinal)).index + 1;

        var listed = Search(tree.Root, "NEEDLE", report: ReportMode.FilesWithMatches);
        var match = Assert.Single(listed.Matches);
        Assert.Equal("big.txt", match.Path);
        Assert.Equal(expectedLine, match.Line);
        Assert.Equal(5, match.Column);

        var missing = Search(tree.Root, "NOT-IN-FILE", report: ReportMode.FilesWithMatches);
        Assert.Empty(missing.Matches);
    }

    [Fact]
    public void Regex_literal_prefilter_confirms_only_real_lines()
    {
        using var tree = new TempDir();
        var lines = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            lines.Append("foo alone\n");
        }

        lines.Append("foo then bar\n");
        lines.Append("bar then foo\n");
        lines.Append("tail\n");
        File.WriteAllText(tree.File("r.txt"), lines.ToString());

        var spanned = Search(tree.Root, "foo.*bar", regex: true);
        var hit = Assert.Single(spanned.Matches);
        Assert.Equal("r.txt", hit.Path);
        Assert.Equal(41, hit.Line);
        Assert.Equal("foo then bar", hit.Text);

        File.WriteAllText(tree.File("anch.txt"), "foo\nfoo bar\n");

        var anchored = Search(tree.Root, "^foo$", regex: true);
        var only = Assert.Single(anchored.Matches);
        Assert.Equal("anch.txt", only.Path);
        Assert.Equal(1, only.Line);
        Assert.Equal("foo", only.Text);

        var verbatim = Search(tree.Root, @"foo\.bar", regex: true);
        File.WriteAllText(tree.File("dot.txt"), "foo.bar\nfooXbar\n");
        verbatim = Search(tree.Root, @"foo\.bar", regex: true);
        Assert.Equal(["dot.txt:1:1:foo.bar"], Texts(verbatim));
    }

    [Fact]
    public void Many_hits_on_one_line_share_that_line()
    {
        using var tree = new TempDir();
        var line = string.Concat(Enumerable.Repeat("enum", 4000));
        File.WriteAllText(tree.File("bundle.js"), line + "\n");
        var before = GC.GetTotalAllocatedBytes(true);
        var found = Search(tree.Root, "enum");
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        Assert.Equal(4000, found.Matches.Count);
        Assert.Equal(1, found.Matches[0].Line);
        Assert.Equal(1, found.Matches[0].Column);
        Assert.Equal(5, found.Matches[1].Column);
        var shared = found.Matches[0].LineText;
        Assert.Equal(line, shared);
        Assert.All(found.Matches, match => Assert.Same(shared, match.LineText));
        // A fresh copy per hit would be about 4000 * 32 KiB. The shared line stays small.
        Assert.True(allocated < 8 * 1024 * 1024, $"allocated {allocated}");

        File.WriteAllText(tree.File("wide.js"), "é" + line);
        var wide = Search(tree.Root, "enum");
        var wideHits = wide.Matches.Where(match => match.Path == "wide.js").ToArray();
        Assert.Equal(4000, wideHits.Length);
        Assert.Equal(2, wideHits[0].Column);
        Assert.All(wideHits, match => Assert.Same(wideHits[0].LineText, match.LineText));

        File.WriteAllText(tree.File("ctx.txt"), "before\nenum enum\nafter\n");
        var context = Search(tree.Root, "enum", before: 1, after: 1);
        var pair = context.Matches.Where(match => match.Path == "ctx.txt").ToArray();
        Assert.Equal(2, pair.Length);
        Assert.Same(pair[0].LineText, pair[1].LineText);
        Assert.Same(pair[0].Before, pair[1].Before);
        Assert.Same(pair[0].After, pair[1].After);
        Assert.Equal("before", Assert.Single(pair[0].Before!).Text);
        Assert.Equal("after", Assert.Single(pair[0].After!).Text);
    }

    private static SearchOutcome Search(
        string root,
        string pattern,
        bool regex = false,
        bool word = false,
        ReportMode report = ReportMode.Matches,
        int before = 0,
        int after = 0)
    {
        return Searcher.Search(new SearchQuery
        {
            Pattern = pattern,
            Path = root,
            Regex = regex,
            WordRegexp = word,
            Report = report,
            BeforeContext = before,
            AfterContext = after,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
    }

    private static string[] Texts(SearchOutcome outcome) =>
        outcome.Matches.Select(match => $"{match.Path}:{match.Line}:{match.Column}:{match.Text}").ToArray();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-fast-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

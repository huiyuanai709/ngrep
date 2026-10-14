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

    private static SearchOutcome Search(
        string root,
        string pattern,
        bool regex = false,
        bool word = false,
        ReportMode report = ReportMode.Matches)
    {
        return Searcher.Search(new SearchQuery
        {
            Pattern = pattern,
            Path = root,
            Regex = regex,
            WordRegexp = word,
            Report = report,
            MaxDegreeOfParallelism = 1,
        });
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

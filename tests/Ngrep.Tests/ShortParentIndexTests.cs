using Ngrep.Cli;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class ShortParentIndexTests
{
    [Fact]
    public void Single_char_under_parent_index_matches_a_scan()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("child/a.txt"), "a\n");
        File.WriteAllText(tree.File("child/only.txt"), "a");
        File.WriteAllText(tree.File("child/mid.txt"), new string('x', 40) + "a" + new string('y', 40));
        File.WriteAllText(tree.File("child/nested/deep.txt"), "banana\n");
        File.WriteAllText(tree.File("child/utf8.txt"), "你好 a\n");
        File.WriteAllText(tree.File("child/late.txt"), new string('q', 9000) + "a\n");
        File.WriteAllText(tree.File("other.txt"), "zzz a zzz\n");
        Indexer.Build(tree.Root);

        var child = Path.Combine(tree.Root, "child");
        var indexed = Search(child, "a");
        var scan = Search(child, "a", scan: true);
        Assert.False(indexed.UsedIndex);
        Assert.Contains("trigram", indexed.Note, StringComparison.Ordinal);
        Assert.Equal(Texts(scan), Texts(indexed));
        Assert.Contains(indexed.Matches, match => match.Path == "a.txt" && match.Text == "a");
        Assert.Contains(indexed.Matches, match => match.Path == "only.txt");
        Assert.Contains(indexed.Matches, match => match.Path == "nested/deep.txt");
        Assert.DoesNotContain(indexed.Matches, match => match.Path == "other.txt");

        var root = Search(tree.Root, "a");
        Assert.True(root.UsedIndex);
        Assert.Contains(root.Matches, match => match.Path == "other.txt");
        Assert.Contains(root.Matches, match => match.Path == "child/a.txt");

        File.WriteAllText(tree.File("child/hit.txt"), "netgrep\n");
        Indexer.Build(tree.Root);
        var literal = Search(child, "netgrep");
        Assert.True(literal.UsedIndex);
        Assert.Equal("child/hit.txt", Assert.Single(literal.Matches).Path);
    }

    [Fact]
    public void Length_two_literal_under_parent_index_matches_a_scan()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("child/hi.txt"), "hi\n");
        File.WriteAllText(tree.File("child/no.txt"), "zz\n");
        Indexer.Build(tree.Root);
        var child = Path.Combine(tree.Root, "child");
        var indexed = Search(child, "hi");
        var scan = Search(child, "hi", scan: true);
        Assert.False(indexed.UsedIndex);
        Assert.Equal(Texts(scan), Texts(indexed));
        Assert.Equal("hi.txt", Assert.Single(indexed.Matches).Path);
    }

    [Fact]
    public void Directory_added_after_index_is_found_for_short_and_long_patterns()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("keep.txt"), "aaa netgrep\n");
        Indexer.Build(tree.Root);
        File.WriteAllText(tree.File("child/new.txt"), "a netgrep\n");
        var child = Path.Combine(tree.Root, "child");

        var letter = Search(child, "a");
        var scan = Search(child, "a", scan: true);
        Assert.Equal(Texts(scan), Texts(letter));
        Assert.Equal("new.txt", Assert.Single(letter.Matches).Path);

        var word = Search(child, "netgrep");
        Assert.False(word.UsedIndex);
        Assert.Contains("not in the index", word.Note, StringComparison.Ordinal);
        Assert.Equal("new.txt", Assert.Single(word.Matches).Path);

        var root = Search(tree.Root, "netgrep");
        Assert.True(root.UsedIndex);
        Assert.Equal("keep.txt", Assert.Single(root.Matches).Path);
    }

    [Fact]
    public void Ignored_child_without_git_root_is_visible_from_inside()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File(".gitignore"), "skip/\n");
        File.WriteAllText(tree.File("skip/a.txt"), "a netgrep\n");
        File.WriteAllText(tree.File("keep.txt"), "a\n");
        Indexer.Build(tree.Root);

        var skip = Path.Combine(tree.Root, "skip");
        var fromSkip = Search(skip, "a");
        Assert.Equal("a.txt", Assert.Single(fromSkip.Matches).Path);
        Assert.Equal(Texts(Search(skip, "a", scan: true)), Texts(fromSkip));

        var fromRoot = Search(tree.Root, "a");
        Assert.True(fromRoot.UsedIndex);
        Assert.Equal("keep.txt", Assert.Single(fromRoot.Matches).Path);

        Directory.CreateDirectory(Path.Combine(tree.Root, ".git"));
        var withGit = Search(skip, "a");
        Assert.Equal(Texts(Search(skip, "a", scan: true)), Texts(withGit));
        Assert.Equal("a.txt", Assert.Single(withGit.Matches).Path);
    }

    [Fact]
    public void Cli_from_child_directory_prints_the_letter()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("child/a.txt"), "a\n");
        File.WriteAllText(tree.File("child/word.txt"), "cat\n");
        Indexer.Build(tree.Root);
        var child = Path.Combine(tree.Root, "child");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(child);
            using var capture = new Capture();
            var code = NgrepCli.Run(["a", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
            Assert.Contains("a.txt", capture.Stdout, StringComparison.Ordinal);
            Assert.Contains("word.txt", capture.Stdout, StringComparison.Ordinal);
            Assert.Contains("trigram", capture.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task Streamed_short_pattern_under_parent_emits_the_file()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("child/a.txt"), "a\n");
        File.WriteAllText(tree.File("child/b.txt"), "banana\n");
        Indexer.Build(tree.Root);
        var emitted = new List<string>();
        var outcome = await Searcher.Search(new SearchQuery
        {
            Pattern = "a",
            Path = Path.Combine(tree.Root, "child"),
            MaxDegreeOfParallelism = 4,
            Events = new SearchEvents
            {
                Batch = batch =>
                {
                    if (batch.Matches.Count > 0)
                    {
                        emitted.Add(batch.Path);
                    }
                },
            },
        });
        Assert.False(outcome.UsedIndex);
        Assert.True(outcome.Streamed);
        Assert.Equal(4, outcome.MatchCount);
        Assert.Equal(["a.txt", "b.txt"], emitted);
    }

    private static SearchOutcome Search(string path, string pattern, bool scan = false) =>
        Searcher.Search(new SearchQuery
        {
            Pattern = pattern,
            Path = path,
            ForceScan = scan,
            MaxDegreeOfParallelism = 4,
        }).GetAwaiter().GetResult();

    private static string[] Texts(SearchOutcome outcome) =>
        outcome.Matches
            .Select(match => $"{match.Path}:{match.Line}:{match.Column}:{match.Text}")
            .ToArray();

    private static TempTree Temp() => new();

    private sealed class TempTree : IDisposable
    {
        public TempTree() => Root = Directory.CreateTempSubdirectory("ngrep-short-").FullName;

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

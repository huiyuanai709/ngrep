using System.Collections.Concurrent;
using Ngrep.Cli;
using Ngrep.Ignore;
using Ngrep.Search;

namespace Ngrep.Tests;

[Collection("walker-hooks")]
public class MaxDepthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("top.txt", 1)]
    [InlineData("a", 1)]
    [InlineData("a/mid.txt", 2)]
    [InlineData("a/b/deep.txt", 3)]
    [InlineData("a/b/c/leaf.txt", 4)]
    public void Depth_counts_like_ripgrep(string relative, int depth)
    {
        Assert.Equal(depth, TreeWalker.DepthOf(relative));
    }

    [Fact]
    public void Walker_caps_depth_and_keeps_ignore_rules()
    {
        using var tree = new Tree();
        var root = tree.Root;

        Assert.Equal([], Walk(root, 0));
        Assert.Equal([], Listed(root, 0));

        Assert.Equal(["top.txt"], Walk(root, 1));
        Assert.Equal([""], Listed(root, 1));

        Assert.Equal(["a/mid.txt", "top.txt"], Walk(root, 2));
        Assert.Equal(["", "a"], Listed(root, 2));

        Assert.Equal(["a/b/deep.txt", "a/mid.txt", "top.txt"], Walk(root, 3));
        Assert.Equal(["", "a", "a/b"], Listed(root, 3));

        Assert.Equal(
            ["a/b/c/leaf.txt", "a/b/deep.txt", "a/mid.txt", "top.txt"],
            Walk(root, null));

        Assert.Equal(
            ["a/local.txt", "a/mid.txt", "secret/nope.txt", "skip.txt", "top.txt"],
            Walk(root, 2, IgnorePolicy.None));
        Assert.Equal(["", "a", "secret"], Listed(root, 2, IgnorePolicy.None));

        Assert.Equal(
            [".gitignore", ".hidden/h.txt", "a/.gitignore", "a/mid.txt", "top.txt"],
            Walk(root, 2, includeHidden: true));
        Assert.DoesNotContain("a/b", Listed(root, 2, includeHidden: true));

        Assert.Equal(["mid.txt"], Walk(tree.Sub, 1));
        Assert.Equal(["b/deep.txt", "mid.txt"], Walk(tree.Sub, 2));
        Assert.Equal(["", "b"], Listed(tree.Sub, 2));
        Assert.DoesNotContain("b/c", Listed(tree.Sub, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Scan_caps_depth_for_one_and_many_workers(int workers)
    {
        using var tree = new Tree();
        Assert.Equal(["top.txt"], await Paths(tree.Root, 1, workers));
        Assert.Equal(["a/mid.txt", "top.txt"], await Paths(tree.Root, 2, workers));
        Assert.Equal(
            ["a/b/c/leaf.txt", "a/b/deep.txt", "a/mid.txt", "top.txt"],
            await Paths(tree.Root, null, workers));
        Assert.Equal(
            ["a/local.txt", "a/mid.txt", "secret/nope.txt", "skip.txt", "top.txt"],
            await Paths(tree.Root, 2, workers, noIgnore: true));
        Assert.Equal(["mid.txt"], await Paths(tree.Sub, 1, workers));
    }

    [Fact]
    public void Cli_max_depth_limits_a_multi_level_tree()
    {
        using var tree = new Tree();
        Assert.Equal(["top.txt"], Lines("-d", "1", "hit", tree.Root, "-l", "-j", "1"));
        Assert.Equal(["a/mid.txt", "top.txt"], Lines("--max-depth", "2", "hit", tree.Root, "-l", "-j", "1"));
        Assert.Equal(["a/mid.txt", "top.txt"], Lines("--maxdepth", "2", "hit", tree.Root, "-l", "-j", "4"));
        Assert.Equal(["a/mid.txt", "top.txt"], Lines("-d2", "hit", tree.Root, "-l", "-j", "1"));
        Assert.Equal(["top.txt"], Lines("-ld1", "hit", tree.Root, "-j", "1"));
        Assert.Equal(["a/b/deep.txt", "a/mid.txt", "top.txt"], Lines("hit", tree.Root, "-l", "-d", "3", "-j", "4"));
        Assert.Equal([], Lines("-d", "0", "hit", tree.Root, "-l", "-j", "1"));
        Assert.Equal(ExitCodes.NoMatches, Run("-d", "0", "hit", tree.Root, "-j", "1").Code);

        var file = Run("-d", "0", "hit", tree.File("top.txt"), "-l", "-j", "1");
        Assert.Equal(ExitCodes.Success, file.Code);
        Assert.Equal("top.txt", file.Stdout.Trim());

        Assert.Equal(["mid.txt"], Lines("-d", "1", "hit", tree.Sub, "-l", "--scan", "-j", "1"));
        Assert.Equal(
            ["a/local.txt", "a/mid.txt", "secret/nope.txt", "skip.txt", "top.txt"],
            Lines("-d", "2", "--no-ignore", "hit", tree.Root, "-l", "-j", "1"));
        Assert.Equal(
            [".hidden/h.txt", "a/mid.txt", "top.txt"],
            Lines("-d", "2", "--hidden", "hit", tree.Root, "-l", "-j", "1"));

        var without = Lines("--files-without-match", "-d", "1", "absent", tree.Root, "-j", "1");
        Assert.Equal(["top.txt"], without);

        var negative = Run("--max-depth=-1", "hit", tree.Root);
        Assert.Equal(ExitCodes.Error, negative.Code);
        Assert.Contains("max-depth", negative.Stderr, StringComparison.Ordinal);
        Assert.Equal("", negative.Stdout.Trim());

        var missing = Run("--max-depth", "hit", tree.Root);
        Assert.Equal(ExitCodes.Error, missing.Code);
    }

    [Fact]
    public void Indexed_search_drops_paths_past_the_cap()
    {
        using var tree = new Tree();
        var indexed = Run("index", tree.Root, "-q");
        Assert.Equal(ExitCodes.Success, indexed.Code);

        var shallow = Run("-d", "1", "hit", tree.Root, "-l", "-j", "1");
        Assert.Equal(ExitCodes.Success, shallow.Code);
        Assert.Equal("top.txt", shallow.Stdout.Trim());
        Assert.DoesNotContain("scanned", shallow.Stderr, StringComparison.Ordinal);

        var mid = Run("--max-depth", "2", "hit", tree.Root, "-l", "-j", "4");
        Assert.Equal(["a/mid.txt", "top.txt"], Split(mid.Stdout));
        Assert.DoesNotContain("scanned", mid.Stderr, StringComparison.Ordinal);

        var nested = Run("-d", "1", "hit", tree.Sub, "-l", "-j", "1");
        Assert.Equal(["a/mid.txt"], Split(nested.Stdout));
        Assert.DoesNotContain("scanned", nested.Stderr, StringComparison.Ordinal);

        var opened = new ConcurrentBag<string>();
        Searcher.BeforeConfirm = opened.Add;
        try
        {
            var run = Run("-d", "2", "hit", tree.Root, "-l", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["a/mid.txt", "top.txt"], opened.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }
    }

    private static string[] Walk(string root, int? maxDepth, IgnorePolicy? policy = null, bool includeHidden = false)
    {
        var stats = new WalkStats();
        return TreeWalker.Enumerate(root, null, stats, policy, includeHidden, maxDepth)
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Listed(string root, int? maxDepth, IgnorePolicy? policy = null, bool includeHidden = false)
    {
        var seen = new List<string>();
        TreeWalker.BeforeCollect = full => seen.Add(Relative(root, full));
        try
        {
            var stats = new WalkStats();
            _ = TreeWalker.Enumerate(root, null, stats, policy, includeHidden, maxDepth).Count();
            return seen.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            TreeWalker.BeforeCollect = null;
        }
    }

    private static async Task<string[]> Paths(string root, int? maxDepth, int workers, bool noIgnore = false)
    {
        var outcome = await Searcher.Search(new SearchQuery
        {
            Pattern = "hit",
            Path = root,
            MaxDepth = maxDepth,
            MaxDegreeOfParallelism = workers,
            NoIgnore = noIgnore,
            Report = ReportMode.FilesWithMatches,
        });
        return outcome.Matches.Select(match => match.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string[] Lines(params string[] args) => Split(Run(args).Stdout);

    private static string[] Split(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string Relative(string root, string full)
    {
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static RunResult Run(params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(args).GetAwaiter().GetResult();
        return new RunResult(
            code,
            capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal),
            capture.Stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private readonly record struct RunResult(int Code, string Stdout, string Stderr);

    private sealed class Tree : IDisposable
    {
        public Tree()
        {
            Root = Directory.CreateTempSubdirectory("ngrep-depth-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, ".git"));
            Write("top.txt", "hit\n");
            Write("skip.txt", "hit\n");
            Write(".gitignore", "skip.txt\nsecret/\n");
            Write("secret/nope.txt", "hit\n");
            Write(".hidden/h.txt", "hit\n");
            Write("a/mid.txt", "hit\n");
            Write("a/local.txt", "hit\n");
            Write("a/.gitignore", "local.txt\n");
            Write("a/b/deep.txt", "hit\n");
            Write("a/b/c/leaf.txt", "hit\n");
            Sub = Path.Combine(Root, "a");
        }

        public string Root { get; }

        public string Sub { get; }

        public string File(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write(string relative, string text)
        {
            var path = File(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, text);
        }
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

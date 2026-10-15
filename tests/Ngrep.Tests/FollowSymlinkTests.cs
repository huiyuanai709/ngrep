using Ngrep.Cli;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class FollowSymlinkTests
{
    [Fact]
    public void Canonical_path_applies_dotdot_to_the_real_parent()
    {
        var root = Directory.CreateTempSubdirectory("ngrep-canon-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "real", "nested"));
            File.WriteAllText(Path.Combine(root, "real", "nested", "file.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), "real");
            Directory.CreateSymbolicLink(Path.Combine(root, "real", "nested", "up"), "../../link/nested");

            var logical = Path.Combine(root, "link", "nested", "up");
            var physical = Path.GetFullPath(Path.Combine(root, "real", "nested"));
            Assert.Equal(physical, SymlinkPath.Canonicalize(logical));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void Walker_skips_symlinks_unless_follow_is_set_and_cycles_stop()
    {
        using var tree = MakeTree();
        var skipped = Relatives(tree.Root, follow: false);
        Assert.Equal(["a.txt", "real/nested/file.txt", "sub/d.txt"], skipped);

        var followed = Relatives(tree.Root, follow: true);
        Assert.Equal(
            [
                "a.txt",
                "link.txt",
                "link/nested/alias.txt",
                "link/nested/file.txt",
                "linkdir/d.txt",
                "real/nested/alias.txt",
                "real/nested/file.txt",
                "sub/d.txt",
            ],
            followed);
        Assert.DoesNotContain(followed, path => path.Contains("/up", StringComparison.Ordinal) || path.StartsWith("back/", StringComparison.Ordinal));
        Assert.DoesNotContain(followed, path => path.Contains(".git", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_follow_reads_link_paths_and_default_does_not()
    {
        using var tree = MakeTree();
        var def = Paths(tree.Root, follow: false, workers: 1);
        var one = Paths(tree.Root, follow: true, workers: 1);
        var many = Paths(tree.Root, follow: true, workers: 4);
        Assert.Equal(["a.txt", "real/nested/file.txt", "sub/d.txt"], def);
        Assert.Equal(
            [
                "a.txt",
                "link.txt",
                "link/nested/alias.txt",
                "link/nested/file.txt",
                "linkdir/d.txt",
                "real/nested/alias.txt",
                "real/nested/file.txt",
                "sub/d.txt",
            ],
            one);
        Assert.Equal(one, many);
    }

    [Fact]
    public void Follow_flag_scans_when_an_index_omits_links()
    {
        using var tree = MakeTree();
        Indexer.Build(tree.Root);
        var indexed = Searcher.Search(new SearchQuery
        {
            Pattern = "NEEDLE",
            Path = tree.Root,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        var followed = Searcher.Search(new SearchQuery
        {
            Pattern = "NEEDLE",
            Path = tree.Root,
            FollowSymlinks = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();

        Assert.True(indexed.UsedIndex);
        Assert.DoesNotContain(indexed.Matches, match => match.Path == "link.txt");
        Assert.False(followed.UsedIndex);
        Assert.Contains(followed.Matches, match => match.Path == "link.txt" && match.Text == "NEEDLE");
        Assert.Contains("scanned content (--follow)", followed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_follow_switches_find_links_and_default_skips_them()
    {
        using var tree = MakeTree();
        using var capture = new Capture();

        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-l", "NEEDLE", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        var def = Lines(capture.Stdout);
        Assert.Contains("a.txt", def);
        Assert.DoesNotContain("link.txt", def);
        Assert.DoesNotContain(def, path => path.StartsWith("linkdir/", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-L", "-l", "NEEDLE", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(
            [
                "a.txt",
                "link.txt",
                "link/nested/alias.txt",
                "link/nested/file.txt",
                "linkdir/d.txt",
                "real/nested/alias.txt",
                "real/nested/file.txt",
                "sub/d.txt",
            ],
            Lines(capture.Stdout));
        Assert.Contains("scanned content (--follow)", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--follow", "-l", "NEEDLE", tree.Root, "-j", "4"]).GetAwaiter().GetResult());
        Assert.Equal(
            [
                "a.txt",
                "link.txt",
                "link/nested/alias.txt",
                "link/nested/file.txt",
                "linkdir/d.txt",
                "real/nested/alias.txt",
                "real/nested/file.txt",
                "sub/d.txt",
            ],
            Lines(capture.Stdout));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-lL", "NEEDLE", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("link.txt", Lines(capture.Stdout));
    }

    private static string[] Relatives(string root, bool follow)
    {
        var stats = new WalkStats();
        return TreeWalker.Enumerate(root, null, stats, includeHidden: false, followSymlinks: follow)
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Paths(string root, bool follow, int workers)
    {
        var outcome = Searcher.Search(new SearchQuery
        {
            Pattern = "NEEDLE",
            Path = root,
            ForceScan = true,
            FollowSymlinks = follow,
            Report = ReportMode.FilesWithMatches,
            MaxDegreeOfParallelism = workers,
        }).GetAwaiter().GetResult();
        return outcome.Matches.Select(match => match.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static DisposableDir MakeTree()
    {
        var tree = new DisposableDir();
        var root = tree.Root;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "config"), "NEEDLE\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(root, "a.txt"), "NEEDLE\n");
        File.CreateSymbolicLink(Path.Combine(root, "link.txt"), "a.txt");
        File.CreateSymbolicLink(Path.Combine(root, "link.log"), "a.txt");
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), "missing.txt");
        File.CreateSymbolicLink(Path.Combine(root, "loop"), "loop");
        Directory.CreateSymbolicLink(Path.Combine(root, "back"), ".");
        File.CreateSymbolicLink(Path.Combine(root, ".secret"), "a.txt");

        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "d.txt"), "NEEDLE\n");
        Directory.CreateSymbolicLink(Path.Combine(root, "sub", "up"), "..");
        Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), "sub");

        Directory.CreateDirectory(Path.Combine(root, "real", "nested"));
        File.WriteAllText(Path.Combine(root, "real", "nested", "file.txt"), "NEEDLE\n");
        File.CreateSymbolicLink(Path.Combine(root, "real", "nested", "alias.txt"), "file.txt");
        Directory.CreateSymbolicLink(Path.Combine(root, "real", "nested", "up"), "../../link/nested");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), "real");
        return tree;
    }

    private static void DeleteTree(string root)
    {
        Unlink(root);
        Directory.Delete(root, recursive: true);
    }

    private static void Unlink(string dir)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false };
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                File.Delete(info.FullName);
                continue;
            }

            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                Unlink(info.FullName);
            }
        }
    }

    private sealed class DisposableDir : IDisposable
    {
        public DisposableDir() => Root = Directory.CreateTempSubdirectory("ngrep-follow-").FullName;

        public string Root { get; }

        public void Dispose() => DeleteTree(Root);
    }

    private sealed class Capture : IDisposable
    {
        private readonly TextWriter _stdout = Console.Out;
        private readonly TextWriter _stderr = Console.Error;
        private StringWriter _out = new();
        private StringWriter _err = new();

        public Capture()
        {
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public string Stdout => _out.ToString();

        public string Stderr => _err.ToString();

        public void Clear()
        {
            _out = new StringWriter();
            _err = new StringWriter();
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public void Dispose()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }
}

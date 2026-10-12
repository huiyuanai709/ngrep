using System.Text.Json;
using Netgrep.Cli;
using Netgrep.Output;
using Netgrep.Watch;

namespace Netgrep.Tests;

public class CliTests
{
    [Fact]
    public void Search_ndjson_and_exit_codes()
    {
        using var tree = Temp();
        File.WriteAllText(Path.Combine(tree, "App.cs"), "alpha\nbeta netgrep gamma\n");

        using var capture = new Capture();
        var indexed = NetgrepCli.Run(["index", tree, "--format", "json"]);
        Assert.Equal(ExitCodes.Success, indexed);
        using (var indexDoc = JsonDocument.Parse(capture.Stdout))
        {
            Assert.Equal("index", indexDoc.RootElement.GetProperty("command").GetString());
            Assert.Equal(1, indexDoc.RootElement.GetProperty("documents").GetInt32());
            Assert.Equal(1, indexDoc.RootElement.GetProperty("formatVersion").GetInt32());
        }

        capture.Clear();
        var found = NetgrepCli.Run(["search", "netgrep", tree, "--format", "ndjson"]);
        Assert.Equal(ExitCodes.Success, found);
        var line = capture.Stdout.Trim();
        using (var match = JsonDocument.Parse(line))
        {
            Assert.Equal("App.cs", match.RootElement.GetProperty("path").GetString());
            Assert.Equal(2, match.RootElement.GetProperty("line").GetInt32());
            Assert.Equal(6, match.RootElement.GetProperty("column").GetInt32());
            Assert.Equal("netgrep", match.RootElement.GetProperty("text").GetString());
        }

        capture.Clear();
        var missing = NetgrepCli.Run(["search", "not-present", tree, "--format", "ndjson"]);
        Assert.Equal(ExitCodes.NoMatches, missing);
        Assert.Equal("", capture.Stdout.Trim());

        capture.Clear();
        var bad = NetgrepCli.Run(["search", "(", tree, "--regex", "--format", "json"]);
        Assert.Equal(ExitCodes.Error, bad);

        capture.Clear();
        var usage = NetgrepCli.Run(["index"]);
        Assert.Equal(ExitCodes.Error, usage);
        Assert.Contains("Required", capture.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_exits_zero()
    {
        using var capture = new Capture();
        var code = NetgrepCli.Run(["--help"]);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("index", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("search", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("watch", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_tree_hides_ignored_and_binary_hits()
    {
        var sample = FindSample();
        var indexHome = Path.Combine(sample, ".netgrep");
        try
        {
            using var capture = new Capture();
            Assert.Equal(ExitCodes.Success, NetgrepCli.Run(["index", sample]));
            capture.Clear();
            Assert.Equal(ExitCodes.Success, NetgrepCli.Run(["search", "netgrep", sample, "--format", "ndjson"]));
            var paths = capture.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("path").GetString())
                .ToArray();
            Assert.Contains(paths, path => path == "src/Program.cs");
            Assert.Contains(paths, path => path == "src/Nested/keep.tmp");
            Assert.DoesNotContain(paths, path => path != null && path.EndsWith(".log", StringComparison.Ordinal));
            Assert.DoesNotContain(paths, path => path == "src/Nested/scratch.tmp");
            Assert.DoesNotContain(paths, path => path == "assets/icon.bin");

            capture.Clear();
            var hidden = NetgrepCli.Run(["search", "netgrep-log", sample, "--format", "ndjson"]);
            Assert.Equal(ExitCodes.NoMatches, hidden);
        }
        finally
        {
            if (Directory.Exists(indexHome))
            {
                Directory.Delete(indexHome, recursive: true);
            }
        }
    }

    [Fact]
    public void Watch_reindexes_a_new_file()
    {
        using var tree = Temp();
        File.WriteAllText(Path.Combine(tree, "a.txt"), "alpha\n");
        using var cts = new CancellationTokenSource();
        using var capture = new Capture();
        var thread = new Thread(() => WatchCommand.Run(tree, null, OutputFormat.Ndjson, cts.Token))
        {
            IsBackground = true,
        };
        thread.Start();
        try
        {
            var ready = SpinWait.SpinUntil(
                () => capture.Stdout.Contains("\"event\":\"ready\"", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));
            Assert.True(ready, capture.Stdout);
            File.WriteAllText(Path.Combine(tree, "b.txt"), "fresh netgrep\n");
            var reindexed = SpinWait.SpinUntil(
                () => capture.Stdout.Contains("\"event\":\"reindexed\"", StringComparison.Ordinal)
                    || SearcherHits(tree).Contains("b.txt"),
                TimeSpan.FromSeconds(8));
            Assert.True(reindexed, capture.Stdout + capture.Stderr);
            Assert.Contains(SearcherHits(tree), path => path == "b.txt");
        }
        finally
        {
            cts.Cancel();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void Watch_ignores_index_writes()
    {
        var root = Path.GetFullPath("/tmp/netgrep-watch-root");
        var index = Path.Combine(root, ".netgrep", "index");
        Assert.True(WatchCommand.IsSelfInflicted(root, index, Path.Combine(index, "postings.bin")));
        Assert.True(WatchCommand.IsSelfInflicted(root, index, Path.Combine(root, ".netgrep", "index.tmp", "manifest.json")));
        Assert.True(WatchCommand.IsSelfInflicted(root, index, Path.Combine(root, ".git", "HEAD")));
        Assert.False(WatchCommand.IsSelfInflicted(root, index, Path.Combine(root, "src", "App.cs")));
    }

    private static string[] SearcherHits(string root)
    {
        var outcome = Search.Searcher.Search(new Search.SearchQuery { Pattern = "netgrep", Path = root });
        return outcome.Matches.Select(m => m.Path).ToArray();
    }

    private static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var readme = Path.Combine(dir.FullName, "testdata", "sample", "README.md");
            if (File.Exists(readme))
            {
                return Path.Combine(dir.FullName, "testdata", "sample");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("testdata/sample was not found.");
    }

    private static DisposableDir Temp() => new();

    private sealed class DisposableDir : IDisposable
    {
        public DisposableDir() => Path = Directory.CreateTempSubdirectory("netgrep-cli-").FullName;

        public string Path { get; }

        public static implicit operator string(DisposableDir dir) => dir.Path;

        public void Dispose() => Directory.Delete(Path, recursive: true);
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

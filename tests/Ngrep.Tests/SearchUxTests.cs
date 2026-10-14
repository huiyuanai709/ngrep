using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Output;
using Ngrep.Search;
using Ngrep.Text;

namespace Ngrep.Tests;

public class SearchUxTests
{
    [Fact]
    public void Parallel_confirmation_matches_sequential_results()
    {
        using var tree = Temp();
        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(tree.File($"dir/f{i:00}.txt"), $"head line\nnetgrep {i}\ntail line\n");
        }

        Indexing.Indexer.Build(tree.Root);
        var sequential = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        var parallel = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            MaxDegreeOfParallelism = 4,
        }).GetAwaiter().GetResult();
        var scan = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            ForceScan = true,
            MaxDegreeOfParallelism = 4,
        }).GetAwaiter().GetResult();

        Assert.Equal("sequential", sequential.Confirmation);
        Assert.Equal(1, sequential.Workers);
        Assert.Equal("parallel", parallel.Confirmation);
        Assert.Equal(4, parallel.Workers);
        Assert.True(parallel.UsedIndex);
        Assert.True(sequential.UsedTrigramPrefilter);
        Assert.Equal("parallel", scan.Confirmation);
        Assert.False(scan.UsedIndex);
        Assert.False(scan.UsedTrigramPrefilter);
        Assert.Equal(12, sequential.Matches.Count);
        Assert.Equal(sequential.Matches, parallel.Matches);
        Assert.Equal(sequential.Matches.Select(m => (m.Path, m.Line, m.Column, m.Text)).ToArray(),
            scan.Matches.Select(m => (m.Path, m.Line, m.Column, m.Text)).ToArray());
    }

    [Fact]
    public void Scan_workers_agree_including_a_mapped_file()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "nope\nNEEDLE\n");
        File.WriteAllText(tree.File("sub/b.txt"), "NEEDLE9\n");
        File.WriteAllText(tree.File("sub/big.txt"), new string('x', 300 * 1024) + "\nNEEDLE\n");

        static string[] Keys(SearchOutcome outcome) =>
            outcome.Matches
                .Select(m => $"{m.Path}:{m.Line}:{m.Column}:{m.Text}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        void Same(string pattern, bool regex, bool list)
        {
            SearchOutcome Run(int workers) => Searcher.Search(new SearchQuery
            {
                Pattern = pattern,
                Path = tree.Root,
                Regex = regex,
                ForceScan = true,
                NoIgnore = true,
                IncludeHidden = true,
                Report = list ? ReportMode.FilesWithMatches : ReportMode.Matches,
                MaxDegreeOfParallelism = workers,
            }).GetAwaiter().GetResult();

            var one = Run(1);
            var many = Run(4);
            Assert.Equal("sequential", one.Confirmation);
            Assert.Equal("parallel", many.Confirmation);
            Assert.Equal(Keys(one), Keys(many));
            Assert.Equal(one.FilesSearched, many.FilesSearched);
        }

        Same("NEEDLE", regex: false, list: false);
        Same("NEEDLE[0-9]", regex: true, list: false);
        Same("NEEDLE", regex: false, list: true);

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--scan", "--no-ignore", "--hidden", "-E", "NEEDLE[0-9]", tree.Root, "-j", "2"]).GetAwaiter().GetResult());
        Assert.Contains("NEEDLE9", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduler_runs_workers_concurrently_and_honors_cancellation()
    {
        var files = Enumerable.Range(0, 8)
            .Select(i => new ConfirmFile(i.ToString(), i.ToString()))
            .ToArray();
        var release = new ManualResetEventSlim(false);
        var entered = 0;
        await AsyncScheduler.Run(files, 4, CancellationToken.None, (_, _, _) =>
        {
            var n = Interlocked.Increment(ref entered);
            if (n == 4)
            {
                release.Set();
            }

            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("workers did not run concurrently");
            }

            return ValueTask.CompletedTask;
        }, stopOnMatch: false);
        Assert.True(entered >= 4);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = ".",
            CancellationToken = canceled.Token,
            MaxDegreeOfParallelism = 4,
        }));
    }

    [Fact]
    public void Default_command_searches_with_and_without_an_index()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/App.cs"), "alpha\nbeta netgrep gamma\n");
        File.WriteAllText(tree.File("notes.txt"), "nothing\n");

        using var capture = new Capture();
        var scanned = NgrepCli.Run(["netgrep", tree.Root, "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Success, scanned);
        Assert.Contains("no index found", capture.Stderr, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("src/App.cs", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("line").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("before", out _));
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["index", tree.Root, "--format", "json"]).GetAwaiter().GetResult());
        capture.Clear();
        var indexed = NgrepCli.Run(["netgrep", tree.Root, "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Success, indexed);
        Assert.Equal("", capture.Stderr.Trim());
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("src/App.cs", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal("netgrep", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal("beta netgrep gamma", doc.RootElement.GetProperty("lineText").GetString());
        }

        capture.Clear();
        var missing = NgrepCli.Run(["not-present", tree.Root, "-j", "1"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.NoMatches, missing);
        Assert.Equal("", capture.Stdout.Trim());
    }

    [Fact]
    public void Globs_and_types_filter_and_keep_gitignore()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("a.cs"), "netgrep cs\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep txt\n");
        File.WriteAllText(tree.File("sub/c.cs"), "netgrep nested\n");
        File.WriteAllText(tree.File("skip.log"), "netgrep log\n");
        File.WriteAllText(tree.File("widget.foo"), "netgrep foo\n");

        var onlyCs = Paths(tree.Root, "-g", "*.cs");
        Assert.Equal(["a.cs", "sub/c.cs"], onlyCs);

        var noTxt = Paths(tree.Root, "-g", "!*.txt");
        Assert.Equal(["a.cs", "sub/c.cs", "widget.foo"], noTxt);

        var noSub = Paths(tree.Root, "-g", "*.cs", "-g", "!sub/**");
        Assert.Equal(["a.cs"], noSub);

        var typed = Paths(tree.Root, "-t", "cs");
        Assert.Equal(["a.cs", "sub/c.cs"], typed);

        var either = Paths(tree.Root, "-t", "cs", "-t", "txt");
        Assert.Equal(["a.cs", "b.txt", "sub/c.cs"], either);

        var notTxt = Paths(tree.Root, "-T", "txt");
        Assert.DoesNotContain("b.txt", notTxt);
        Assert.Contains("a.cs", notTxt);

        var custom = Paths(tree.Root, "--type-add", "foo:*.foo", "-t", "foo");
        Assert.Equal(["widget.foo"], custom);

        Assert.Empty(Paths(tree.Root, "-g", "*.log"));
        Assert.Equal(["skip.log"], Paths(tree.Root, "--no-ignore", "-g", "*.log"));
        Assert.Equal(["skip.log"], Paths(tree.Root, "--no-ignore-vcs", "-g", "*.log"));

        using var capture = new Capture();
        var unknown = NgrepCli.Run(["netgrep", tree.Root, "-t", "nope"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Error, unknown);
        Assert.Contains("Unknown file type", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        var list = NgrepCli.Run(["--type-list"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Success, list);
        Assert.Contains("cs: *.cs, *.csx", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\nrs: *.rs\n", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\ngo: *.go\n", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\npy: *.py, *.pyi\n", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\njs: ", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\nts: ", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\njava: *.java\n", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\nmd: ", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\njson: *.json\n", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_is_text_and_optional_json_fields()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("App.cs"), "alpha\nbeta netgrep gamma\ndelta\ngap\nepsilon netgrep\nomega\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-A", "1", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(
            """
            App.cs:2:6:beta netgrep gamma
            App.cs-3-delta
            --
            App.cs:5:9:epsilon netgrep
            App.cs-6-omega
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            capture.Stdout.Trim().Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-B", "1", "-C", "0", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        var lines = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("App.cs", first.RootElement.GetProperty("path").GetString());
        Assert.Equal(2, first.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(6, first.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("netgrep", first.RootElement.GetProperty("text").GetString());
        Assert.Equal("beta netgrep gamma", first.RootElement.GetProperty("lineText").GetString());
        var before = first.RootElement.GetProperty("before");
        Assert.Equal(1, before.GetArrayLength());
        Assert.Equal(1, before[0].GetProperty("line").GetInt32());
        Assert.Equal("alpha", before[0].GetProperty("text").GetString());
        Assert.Equal(0, first.RootElement.GetProperty("after").GetArrayLength());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-C", "1", "-j", "1"]).GetAwaiter().GetResult());
        var text = capture.Stdout.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("App.cs-1-alpha\nApp.cs:2:6:beta netgrep gamma\nApp.cs-3-delta", text, StringComparison.Ordinal);
        Assert.Contains("App.cs-4-gap\nApp.cs:5:9:epsilon netgrep\nApp.cs-6-omega", text, StringComparison.Ordinal);
        Assert.DoesNotContain("App.cs-5-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_patterns_word_and_invert()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\nnet\nnet-grep\nthe net.\nalpha\n");

        var word = Searcher.Search(new SearchQuery
        {
            Pattern = "net",
            Path = tree.Root,
            WordRegexp = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        Assert.Equal([2, 3, 4], word.Matches.Select(m => m.Line).ToArray());

        var either = Searcher.Search(new SearchQuery
        {
            Pattern = "alpha",
            AdditionalPatterns = ["netgrep"],
            Path = tree.Root,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        Assert.Equal([1, 5], either.Matches.Select(m => m.Line).ToArray());

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-e", "alpha", "-e", "netgrep", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("a.txt:1:1:netgrep", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:5:1:alpha", capture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt:2:", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", "-e", "alpha", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("a.txt:1:1:netgrep", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:5:1:alpha", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-e", "alpha", tree.Root, "-v", "-j", "1"]).GetAwaiter().GetResult());
        Assert.DoesNotContain("alpha", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:1:1:netgrep", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:2:1:net", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        File.WriteAllText(Path.Combine(tree.Root, "only.txt"), "netgrep\n");
        var none = NgrepCli.Run(["netgrep", Path.Combine(tree.Root, "only.txt"), "-v", "-j", "1"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.NoMatches, none);

        capture.Clear();
        var regex = NgrepCli.Run(["-e", "n.t", "--regex", "-w", tree.Root, "-j", "1", "--format", "ndjson"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Success, regex);
        var paths = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(el => (el.GetProperty("line").GetInt32(), el.GetProperty("text").GetString()))
            .ToArray();
        Assert.Contains(paths, hit => hit.Item1 == 2 && hit.Item2 == "net");
        Assert.DoesNotContain(paths, hit => hit.Item2 == "netgrep");
    }

    [Fact]
    public void Search_help_lists_new_flags_and_subcommands_remain()
    {
        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--help"]).GetAwaiter().GetResult());
        Assert.Contains("index", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("search", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("watch", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["search", "--help"]).GetAwaiter().GetResult());
        Assert.Contains("--glob", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--type-list", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--after-context", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--word-regexp", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--invert-match", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--regexp", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-ignore", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-ignore-vcs", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--files-with-matches", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--files-without-match", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--count", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--only-matching", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--hidden", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--color", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--multiline", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--multiline-dotall", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("rgjson", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--file", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--heading", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-heading", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--stats", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--extended-regexp", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--fixed-strings", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--line-regexp", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--quiet", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--silent", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--line-number", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-messages", capture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--async", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_count_ignores_the_trailing_newline()
    {
        Assert.Equal(0, LineMap.LineCount("", LineMap.Build("")));
        Assert.Equal(1, LineMap.LineCount("a\n", LineMap.Build("a\n")));
        Assert.Equal(2, LineMap.LineCount("a\n\n", LineMap.Build("a\n\n")));
        Assert.Equal(1, LineMap.LineCount("\n", LineMap.Build("\n")));
    }

    private static string[] Paths(string root, params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(["netgrep", ..args, root, "-j", "1"]).GetAwaiter().GetResult();
        Assert.True(code is ExitCodes.Success or ExitCodes.NoMatches, capture.Stderr);
        return capture.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2)[0])
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-ux-").FullName;

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

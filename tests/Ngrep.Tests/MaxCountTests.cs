using Ngrep.Cli;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class MaxCountTests
{
    [Fact]
    public void Max_count_limits_each_file_on_scan_and_index()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nnope\nalpha\n");
        File.WriteAllText(tree.File("b.txt"), "alpha alpha\nalpha\n");

        var scan = Run("-F", "-m", "1", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, scan.Code);
        Assert.Equal("a.txt:1:1:alpha\nb.txt:1:1:alpha alpha\nb.txt:1:7:alpha alpha", scan.Stdout.Trim());

        Indexer.Build(tree.Root);
        var indexed = Run("-F", "--max-count", "1", "alpha", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, indexed.Code);
        Assert.Equal(scan.Stdout, indexed.Stdout);
        Assert.DoesNotContain("scanned content", indexed.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Regex_and_fixed_string_paths_honor_the_same_limit()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("t.txt"), "a\naa\nb\naaa\n");

        var regex = Run("-m", "2", "a+", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, regex.Code);
        Assert.Equal("t.txt:1:1:a\nt.txt:2:1:aa", regex.Stdout.Trim());

        var literal = Run("-F", "-m", "1", "aa", tree.Root, "--scan", "-j", "1");
        Assert.Equal("t.txt:2:1:aa", literal.Stdout.Trim());
    }

    [Fact]
    public void Count_is_capped_and_zero_searches_nothing()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nalpha\nalpha\n");
        File.WriteAllText(tree.File("b.txt"), "nope\n");

        var count = Run("-F", "-c", "-m", "1", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, count.Code);
        Assert.Equal("a.txt:1", count.Stdout.Trim());

        var none = Run("-F", "-m", "0", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, none.Code);
        Assert.Equal("", none.Stdout.Trim());

        var seen = new List<string>();
        Searcher.BeforeConfirm = path => seen.Add(path);
        try
        {
            var outcome = Searcher.Search(new SearchQuery
            {
                Pattern = "alpha",
                Path = tree.Root,
                MaxCount = 0,
                ForceScan = true,
                MaxDegreeOfParallelism = 1,
            }).GetAwaiter().GetResult();
            Assert.Empty(seen);
            Assert.Empty(outcome.Matches);
            Assert.Equal(0, outcome.FilesSearched);
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }

        var missing = Run("-F", "-m", "0", "alpha", Path.Combine(tree.Root, "missing"));
        Assert.Equal(ExitCodes.Error, missing.Code);
    }

    [Fact]
    public void Context_stays_on_the_last_match_and_does_not_add_a_match()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nalpha\ntail\n");

        var run = Run("-F", "-m", "1", "-A", "1", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("a.txt:1:1:alpha\na.txt-2-alpha", run.Stdout.Trim());
        Assert.DoesNotContain("a.txt:2:", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiline_span_counts_once()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nbar\nfoo\nbar\n");

        var run = Run("--multiline", "--multiline-dotall", "-m", "1", "foo.bar", tree.Root, "--scan", "--format", "ndjson", "-j", "1");
        Assert.Equal(ExitCodes.Success, run.Code);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(lines);
        Assert.Contains("\"line\":1", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Invert_word_and_files_without_match_follow_the_limit()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nbeta\nalpha\ngamma\n");
        File.WriteAllText(tree.File("wide.txt"), "alpha\nalphabeta\nalpha\n");
        File.WriteAllText(tree.File("miss.txt"), "nope\n");

        var inverted = Run("-F", "-v", "-m", "1", "alpha", tree.Root, "--scan", "-j", "1", "-g", "a.txt");
        Assert.Equal(ExitCodes.Success, inverted.Code);
        Assert.Equal("a.txt:2:1:beta", inverted.Stdout.Trim());

        var word = Run("-F", "-w", "-m", "1", "alpha", tree.Root, "--scan", "-j", "1", "-g", "wide.txt");
        Assert.Equal("wide.txt:1:1:alpha", word.Stdout.Trim());

        var without = Run("-F", "--files-without-match", "-m", "1", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, without.Code);
        Assert.Equal("miss.txt", without.Stdout.Trim());

        var quietZero = Run("-F", "--files-without-match", "-m", "0", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, quietZero.Code);
        Assert.Equal("", quietZero.Stdout.Trim());

        var listed = Run("-F", "-l", "-m", "2", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal("a.txt\nwide.txt", listed.Stdout.Trim());
    }

    [Fact]
    public void Attached_value_last_flag_and_rejection()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nalpha\nalpha\n");

        var attached = Run("-Fm1", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, attached.Code);
        Assert.Equal("a.txt:1:1:alpha", attached.Stdout.Trim());

        var last = Run("-m", "1", "--max-count", "2", "-F", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal("a.txt:1:1:alpha\na.txt:2:1:alpha", last.Stdout.Trim());

        var negative = Run("-m", "-1", "-F", "alpha", tree.Root);
        Assert.Equal(ExitCodes.Error, negative.Code);

        var missing = Run("--max-count", "-F", "alpha", tree.Root);
        Assert.Equal(ExitCodes.Error, missing.Code);
    }

    [Fact]
    public void Stats_count_the_lines_found_before_the_stop()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha alpha\nnope\nalpha\n");
        File.WriteAllText(tree.File("b.txt"), "alpha\nalpha\n");

        var run = Run("-F", "-m", "1", "--stats", "alpha", tree.Root, "--scan", "-j", "1");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("lines matched: 2", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("matches: 3", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("files matched: 2", run.Stderr, StringComparison.Ordinal);
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

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-max-").FullName;

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

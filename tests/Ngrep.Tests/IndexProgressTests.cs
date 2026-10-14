using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Indexing;

namespace Ngrep.Tests;

public class IndexProgressTests
{
    [Fact]
    public void Json_progress_goes_to_stderr_and_stdout_stays_the_summary()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        File.WriteAllText(tree.File("b.txt"), "beta\n");
        File.WriteAllBytes(tree.File("icon.bin"), [0, 1, 2, 3]);

        var run = Run("index", tree.Root, "--format", "json");
        Assert.Equal(ExitCodes.Success, run.Code);

        using var doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal("index", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("documents").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("skippedBinary").GetInt32());
        Assert.DoesNotContain("added: ", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("updated: ", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("removed: ", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("indexed ", run.Stdout, StringComparison.Ordinal);

        Assert.Contains("added: a.txt", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("added: b.txt", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("added: icon.bin", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unchanged:", run.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "indexed 3/3, added 2, updated 0, unchanged 0, removed 0, skipped 1",
            run.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ndjson_stdout_is_the_summary_object()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");

        var run = Run("index", tree.Root, "--format", "ndjson");
        Assert.Equal(ExitCodes.Success, run.Code);
        using var doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal(1, doc.RootElement.GetProperty("added").GetInt32());
        Assert.DoesNotContain("added: ", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("added: a.txt", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_stdout_summary_does_not_include_progress_lines()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");

        var run = Run("index", tree.Root, "--format", "text");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("added 1", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("added: ", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("indexed ", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("added: a.txt", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_and_remove_name_the_path()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "one\n");
        File.WriteAllText(tree.File("b.txt"), "two\n");
        File.WriteAllText(tree.File("c.txt"), "three\n");
        Assert.Equal(ExitCodes.Success, Run("index", tree.Root, "--quiet").Code);

        File.WriteAllText(tree.File("a.txt"), "changed\n");
        File.Delete(tree.File("b.txt"));

        var run = Run("index", tree.Root, "--format", "json");
        Assert.Equal(ExitCodes.Success, run.Code);
        using var doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("removed").GetInt32());
        Assert.DoesNotContain("updated: ", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("removed: ", run.Stdout, StringComparison.Ordinal);

        Assert.Contains("updated: a.txt", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("removed: b.txt", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("added: ", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("c.txt", run.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "indexed 2/2, added 0, updated 1, unchanged 1, removed 1, skipped 0",
            run.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unchanged_reindex_prints_only_the_summary()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "one\n");
        File.WriteAllText(tree.File("b.txt"), "two\n");
        Assert.Equal(ExitCodes.Success, Run("index", tree.Root, "--quiet").Code);

        var run = Run("index", tree.Root, "--format", "json");
        Assert.Equal(ExitCodes.Success, run.Code);
        using var doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("unchanged").GetInt32());
        Assert.Equal(
            "indexed 2/2, added 0, updated 0, unchanged 2, removed 0, skipped 0",
            run.Stderr.Trim());
    }

    [Theory]
    [InlineData("-q")]
    [InlineData("--quiet")]
    public void Quiet_silences_progress_and_keeps_json_stdout(string flag)
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");

        var run = Run("index", tree.Root, flag, "--format", "json");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("", run.Stderr);
        using var doc = JsonDocument.Parse(run.Stdout);
        Assert.Equal(1, doc.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("documents").GetInt32());
    }

    [Fact]
    public void Summary_is_printed_every_100_files_and_once_at_the_end()
    {
        using var tree = Temp();
        for (var i = 0; i < 101; i++)
        {
            File.WriteAllText(tree.File($"f{i:000}.txt"), "x\n");
        }

        var first = Run("index", tree.Root, "--format", "json");
        Assert.Equal(ExitCodes.Success, first.Code);
        using (var doc = JsonDocument.Parse(first.Stdout))
        {
            Assert.Equal(101, doc.RootElement.GetProperty("added").GetInt32());
        }

        var lines = Lines(first.Stderr);
        Assert.Equal(101, lines.Count(line => line.StartsWith("added: ", StringComparison.Ordinal)));
        Assert.Contains("indexed 100/101, added 100, updated 0, unchanged 0, removed 0, skipped 0", lines);
        Assert.Contains("indexed 101/101, added 101, updated 0, unchanged 0, removed 0, skipped 0", lines);
        Assert.Equal(2, lines.Count(line => line.StartsWith("indexed ", StringComparison.Ordinal)));

        var second = Run("index", tree.Root, "--format", "json");
        var again = Lines(second.Stderr);
        Assert.DoesNotContain(again, line => line.StartsWith("added: ", StringComparison.Ordinal));
        Assert.DoesNotContain(again, line => line.StartsWith("updated: ", StringComparison.Ordinal));
        Assert.DoesNotContain(again, line => line.StartsWith("removed: ", StringComparison.Ordinal));
        Assert.Equal(
            [
                "indexed 100/101, added 0, updated 0, unchanged 100, removed 0, skipped 0",
                "indexed 101/101, added 0, updated 0, unchanged 101, removed 0, skipped 0",
            ],
            again);
    }

    [Fact]
    public void Removals_are_printed_in_path_order()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("m.txt"), "m\n");
        File.WriteAllText(tree.File("a.txt"), "a\n");
        File.WriteAllText(tree.File("z.txt"), "z\n");
        Assert.Equal(ExitCodes.Success, Run("index", tree.Root, "--quiet").Code);
        File.Delete(tree.File("m.txt"));
        File.Delete(tree.File("a.txt"));
        File.Delete(tree.File("z.txt"));
        File.WriteAllText(tree.File("keep.txt"), "k\n");

        var run = Run("index", tree.Root);
        var removed = Lines(run.Stderr).Where(line => line.StartsWith("removed: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["removed: a.txt", "removed: m.txt", "removed: z.txt"], removed);
    }

    [Fact]
    public void Direct_build_does_not_print_progress()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        using var capture = new Capture();
        var built = Indexer.Build(tree.Root);
        Assert.Equal(1, built.Added);
        Assert.Equal("", capture.Stdout);
        Assert.Equal("", capture.Stderr);
    }

    [Fact]
    public void Index_help_documents_quiet()
    {
        var run = Run("index", "--help");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("--quiet", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("progress", run.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static RunResult Run(params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(args);
        return new RunResult(
            code,
            capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal),
            capture.Stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private readonly record struct RunResult(int Code, string Stdout, string Stderr);

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-index-progress-").FullName;

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

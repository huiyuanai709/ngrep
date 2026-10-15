using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Search;

namespace Ngrep.Tests;

public class MultiPathTests
{
    [Fact]
    public void Several_directories_are_searched_and_a_single_path_is_not_prefixed()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/hit.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/hit.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/other.txt"), "nope\n");

        using (Cwd(tree.Root))
        {
            var one = Run("needle", "src", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, one.Code);
            Assert.Equal(["hit.txt"], Paths(one.Stdout));

            var both = Run("needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, both.Code);
            Assert.Equal(["src/hit.txt", "tests/hit.txt"], Paths(both.Stdout));

            var command = Run("search", "needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(both.Stdout, command.Stdout);
        }
    }

    [Fact]
    public void Named_files_keep_the_path_that_was_given()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/b.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src/a.txt", "tests/b.txt", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["src/a.txt", "tests/b.txt"], Paths(run.Stdout));
        }
    }

    [Fact]
    public void Dot_keeps_its_relative_paths_beside_another_root()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("here.txt"), "needle\n");
        File.WriteAllText(tree.File("src/hit.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            // `.` already contains src, so that file is reported again for the second root.
            var run = Run("needle", ".", "src", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["here.txt", "src/hit.txt", "src/hit.txt"], Paths(run.Stdout));
        }
    }

    [Fact]
    public void No_path_searches_the_working_directory()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("only.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["only.txt"], Paths(run.Stdout));
        }
    }

    [Fact]
    public void Ignore_rules_stay_on_the_root_that_owns_them()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/.gitignore"), "*.log\n");
        File.WriteAllText(tree.File("src/keep.txt"), "needle\n");
        File.WriteAllText(tree.File("src/skip.log"), "needle\n");
        File.WriteAllText(tree.File("tests/hit.log"), "needle\n");

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["src/keep.txt", "tests/hit.log"], Paths(run.Stdout));
        }
    }

    [Fact]
    public void Parent_gitignore_applies_to_every_root()
    {
        using var tree = Temp();
        Directory.CreateDirectory(Path.Combine(tree.Root, ".git"));
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("src/keep.txt"), "needle\n");
        File.WriteAllText(tree.File("src/skip.log"), "needle\n");
        File.WriteAllText(tree.File("tests/skip.log"), "needle\n");

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["src/keep.txt"], Paths(run.Stdout));

            var forced = Run("--no-ignore", "needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, forced.Code);
            Assert.Equal(["src/keep.txt", "src/skip.log", "tests/skip.log"], Paths(forced.Stdout));
            Assert.Contains("scanned content", forced.Stderr, StringComparison.Ordinal);
            Assert.Equal(1, CountLines(forced.Stderr, "scanned content"));
        }
    }

    [Fact]
    public void Each_root_uses_its_own_index()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/hit.txt"), "needle-src\n");
        File.WriteAllText(tree.File("tests/hit.txt"), "needle-tests\n");
        Assert.Equal(ExitCodes.Success, Run("index", tree.File("src"), "-q").Code);

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["src/hit.txt", "tests/hit.txt"], Paths(run.Stdout));
            Assert.Equal(1, CountLines(run.Stderr, "no index found"));
        }
    }

    [Fact]
    public void A_parent_index_covers_each_subdirectory()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/hit.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/hit.txt"), "needle\n");
        Assert.Equal(ExitCodes.Success, Run("index", tree.Root, "-q").Code);

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Equal(["src/hit.txt", "tests/hit.txt"], Paths(run.Stdout));
            Assert.DoesNotContain("scanned", run.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void File_reports_count_and_globs_span_roots()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("src/b.cs"), "needle\n");
        File.WriteAllText(tree.File("src/miss.txt"), "nope\n");
        File.WriteAllText(tree.File("tests/c.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/miss.txt"), "nope\n");

        using (Cwd(tree.Root))
        {
            var with = Run("needle", "src", "tests", "-l", "-j", "1");
            Assert.Equal(ExitCodes.Success, with.Code);
            Assert.Equal(["src/a.txt", "src/b.cs", "tests/c.txt"], Lines(with.Stdout));

            var without = Run("--files-without-match", "needle", "src", "tests", "-j", "1");
            Assert.Equal(ExitCodes.Success, without.Code);
            Assert.Equal(["src/miss.txt", "tests/miss.txt"], Lines(without.Stdout));

            var count = Run("needle", "src", "tests", "-c", "-j", "1");
            Assert.Equal(ExitCodes.Success, count.Code);
            Assert.Equal(["src/a.txt:1", "src/b.cs:1", "tests/c.txt:1"], Lines(count.Stdout));

            var glob = Run("needle", "src", "tests", "-g", "*.txt", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, glob.Code);
            Assert.Equal(["src/a.txt", "tests/c.txt"], Paths(glob.Stdout));
        }
    }

    [Fact]
    public void Json_is_one_report_and_stats_add_up()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/b.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            var json = Run("needle", "src", "tests", "--format", "json", "-j", "1");
            Assert.Equal(ExitCodes.Success, json.Code);
            using var doc = JsonDocument.Parse(json.Stdout);
            var paths = doc.RootElement.GetProperty("matches").EnumerateArray()
                .Select(match => match.GetProperty("path").GetString()!)
                .ToArray();
            Assert.Equal(["src/a.txt", "tests/b.txt"], paths);

            var stats = Run("needle", "src", "tests", "--stats", "-j", "1");
            Assert.Equal(ExitCodes.Success, stats.Code);
            Assert.Contains("files searched: 2", stats.Stderr, StringComparison.Ordinal);
            Assert.Contains("files matched: 2", stats.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_missing_path_does_not_hide_the_others()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/b.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            var run = Run("needle", "src", "missing", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Error, run.Code);
            Assert.Equal(["src/a.txt", "tests/b.txt"], Paths(run.Stdout));
            Assert.Contains("Path not found", run.Stderr, StringComparison.Ordinal);

            var quiet = Run("-s", "needle", "src", "missing", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Error, quiet.Code);
            Assert.Equal(["src/a.txt"], Paths(quiet.Stdout));
            Assert.DoesNotContain("Path not found", quiet.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pattern_flags_still_leave_the_remaining_positionals_as_paths()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/b.txt"), "other\n");
        var pats = tree.File("pats.txt");
        File.WriteAllText(pats, "other\n");

        using (Cwd(tree.Root))
        {
            var either = Run("-e", "needle", "-e", "other", "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, either.Code);
            Assert.Equal(["src/a.txt", "tests/b.txt"], Paths(either.Stdout));

            var fromFile = Run("-f", pats, "src", "tests", "--format", "ndjson", "-j", "1");
            Assert.Equal(ExitCodes.Success, fromFile.Code);
            Assert.Equal(["tests/b.txt"], Paths(fromFile.Stdout));
        }
    }

    [Fact]
    public void Quiet_stops_at_the_first_matching_root_unless_stats_are_requested()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/hit.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/hit.txt"), "needle\n");
        var seen = new List<string>();
        Searcher.BeforeConfirm = seen.Add;
        try
        {
            using (Cwd(tree.Root))
            {
                var quiet = Run("-q", "needle", "src", "tests", "-j", "1");
                Assert.Equal(ExitCodes.Success, quiet.Code);
                Assert.Equal("", quiet.Stdout);
                Assert.Contains(seen, path => path == "src/hit.txt");
                Assert.DoesNotContain(seen, path => path.Contains("tests", StringComparison.Ordinal));

                seen.Clear();
                var stats = Run("-q", "needle", "src", "tests", "--stats", "-j", "1");
                Assert.Equal(ExitCodes.Success, stats.Code);
                Assert.Contains("files searched: 2", stats.Stderr, StringComparison.Ordinal);
                Assert.Contains(seen, path => path == "tests/hit.txt");
            }
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }
    }

    [Fact]
    public void Help_documents_multiple_paths()
    {
        var run = Run("search", "--help");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("PATH", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("No path searches", run.Stdout, StringComparison.Ordinal);
    }

    private static string[] Paths(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("path").GetString()!)
            .ToArray();

    private static string[] Lines(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int CountLines(string text, string fragment) =>
        text.Split('\n').Count(line => line.Contains(fragment, StringComparison.Ordinal));

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

    private static WorkingDirectory Cwd(string path) => new(path);

    private sealed class WorkingDirectory : IDisposable
    {
        private readonly string _previous = Directory.GetCurrentDirectory();

        public WorkingDirectory(string path) => Directory.SetCurrentDirectory(path);

        public void Dispose() => Directory.SetCurrentDirectory(_previous);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-paths-").FullName;

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

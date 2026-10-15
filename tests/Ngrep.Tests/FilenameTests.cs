using System.Text;
using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Text;

namespace Ngrep.Tests;

public class FilenameTests
{
    [Fact]
    public void One_file_omits_the_path_until_with_filename()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "alpha\nbeta netgrep gamma\nomega\n");

        var plain = Run("netgrep", path, "-j", "1");
        Assert.Equal(ExitCodes.Success, plain.Code);
        Assert.Equal("2:6:beta netgrep gamma", plain.Stdout.Trim());

        var forced = Run("-H", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt:2:6:beta netgrep gamma", forced.Stdout.Trim());
        Assert.Equal(forced.Stdout, Run("--with-filename", "netgrep", path, "-j", "1").Stdout);

        var hidden = Run("-I", "netgrep", path, "-j", "1");
        Assert.Equal(plain.Stdout, hidden.Stdout);
        Assert.Equal(plain.Stdout, Run("--no-filename", "netgrep", path, "-j", "1").Stdout);

        var context = Run("netgrep", path, "-C", "1", "-N", "--no-column", "-j", "1");
        Assert.Equal("alpha\nbeta netgrep gamma\nomega", context.Stdout.Trim());

        var neither = Run("netgrep", path, "-N", "--no-column", "-j", "1");
        Assert.Equal("beta netgrep gamma", neither.Stdout.Trim());

        var columnOnly = Run("netgrep", path, "-N", "-j", "1");
        Assert.Equal("6:beta netgrep gamma", columnOnly.Stdout.Trim());
    }

    [Fact]
    public void Last_filename_flag_wins_including_a_short_cluster()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "netgrep\n");

        Assert.Equal("a.txt:1:1:netgrep", Run("-I", "-H", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("1:1:netgrep", Run("-H", "-I", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("a.txt:1:1:netgrep", Run("-IH", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("1:1:netgrep", Run("-HI", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal(
            "a.txt:1:1:netgrep",
            Run("--no-filename", "--with-filename", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal(
            "1:1:netgrep",
            Run("--with-filename", "--no-filename", "netgrep", path, "-j", "1").Stdout.Trim());
    }

    [Fact]
    public void A_directory_keeps_the_path_and_no_filename_drops_it()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nbeta netgrep gamma\nomega\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");

        var plain = Run("netgrep", tree.Root, "--no-heading", "-j", "1");
        Assert.Equal(
            "a.txt:2:6:beta netgrep gamma\nb.txt:1:1:netgrep",
            plain.Stdout.Trim());

        var hidden = Run("-I", "netgrep", tree.Root, "--heading", "-C", "1", "-j", "1");
        Assert.Equal(
            "1-alpha\n2:6:beta netgrep gamma\n3-omega\n1:1:netgrep",
            hidden.Stdout.Trim());
        Assert.DoesNotContain("a.txt", hidden.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("b.txt", hidden.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', hidden.Stdout);
    }

    [Fact]
    public void Several_paths_print_prefixes_unless_no_filename()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("src/a.txt"), "needle\n");
        File.WriteAllText(tree.File("tests/b.txt"), "needle\n");

        using (Cwd(tree.Root))
        {
            var both = Run("needle", "src/a.txt", "tests/b.txt", "-j", "1");
            Assert.Equal(ExitCodes.Success, both.Code);
            Assert.Equal(
                "src/a.txt:1:1:needle\ntests/b.txt:1:1:needle",
                both.Stdout.Trim());

            var hidden = Run("-I", "needle", "src/a.txt", "tests/b.txt", "-j", "1");
            Assert.Equal("1:1:needle\n1:1:needle", hidden.Stdout.Trim());

            var oneDirectory = Run("needle", "src", "-j", "1");
            Assert.Equal("a.txt:1:1:needle", oneDirectory.Stdout.Trim());
        }
    }

    [Fact]
    public void Heading_on_one_file_needs_with_filename()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "netgrep\n");

        var heading = Run("--heading", "netgrep", path, "-j", "1");
        Assert.Equal("1:1:netgrep", heading.Stdout.Trim());

        var both = Run("-H", "--heading", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt\n1:1:netgrep", both.Stdout.Trim());

        var prefixed = Run("-H", "--heading", "--no-heading", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt:1:1:netgrep", prefixed.Stdout.Trim());

        var suppressed = Run("-H", "--no-heading", "--heading", "-I", "netgrep", path, "-j", "1");
        Assert.Equal("1:1:netgrep", suppressed.Stdout.Trim());
    }

    [Fact]
    public void Vimgrep_implies_the_path_and_no_filename_drops_it()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "netgrep netgrep\n");

        var locked = Run("--vimgrep", "-N", "--no-column", "--heading", "netgrep", path, "-j", "1");
        Assert.Equal(
            "a.txt:1:1:netgrep netgrep\na.txt:1:9:netgrep netgrep",
            locked.Stdout.Trim());

        var after = Run("--vimgrep", "-I", "netgrep", path, "-j", "1");
        Assert.Equal("1:1:netgrep netgrep\n1:9:netgrep netgrep", after.Stdout.Trim());

        var before = Run("-I", "--vimgrep", "netgrep", path, "-j", "1");
        Assert.Equal(after.Stdout, before.Stdout);

        var restored = Run("-I", "--vimgrep", "-H", "netgrep", path, "-j", "1");
        Assert.Equal(locked.Stdout, restored.Stdout);
    }

    [Fact]
    public void Null_follows_only_a_printed_path()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "alpha\nnetgrep\nomega\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");

        var forced = Run("-H", "-0", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt\02:1:netgrep\n", forced.Stdout);

        var omitted = Run("-0", "netgrep", path, "-C", "1", "-j", "1");
        Assert.Equal("1-alpha\n2:1:netgrep\n3-omega\n", omitted.Stdout);
        Assert.DoesNotContain('\0', omitted.Stdout);

        var directory = Run("-I", "-0", "--heading", "netgrep", tree.Root, "-j", "1");
        Assert.Equal("2:1:netgrep\n1:1:netgrep\n", directory.Stdout);
        Assert.DoesNotContain('\0', directory.Stdout);

        var heading = Run("-H", "--heading", "-0", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt\02:1:netgrep\n", heading.Stdout);

        var count = Run("-c", "-0", "-I", "netgrep", tree.Root, "-j", "1");
        Assert.Equal("1\n1\n", count.Stdout);
        Assert.DoesNotContain('\0', count.Stdout);

        var countPath = Run("-c", "-0", "-H", "netgrep", path, "-j", "1");
        Assert.Equal("a.txt\01\n", countPath.Stdout);
    }

    [Fact]
    public void Count_follows_the_filename_switch_and_lists_do_not()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "nope\n");

        Assert.Equal("1", Run("-c", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("a.txt:1", Run("-c", "-H", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("a.txt:1", Run("-c", "netgrep", tree.Root, "-j", "1").Stdout.Trim());
        Assert.Equal("1", Run("-c", "-I", "netgrep", tree.Root, "-j", "1").Stdout.Trim());

        Assert.Equal("a.txt", Run("-l", "-I", "netgrep", path, "-j", "1").Stdout.Trim());
        Assert.Equal("b.txt", Run("--files-without-match", "-I", "netgrep", tree.Root, "-j", "1").Stdout.Trim());
        Assert.Equal(
            ["a.txt", "b.txt"],
            Run("--files", "-I", tree.Root).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(line => line, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Machine_formats_keep_the_path()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "netgrep\n");

        var ndjson = Run("-I", "netgrep", path, "--format", "ndjson", "-0", "-j", "1");
        Assert.DoesNotContain('\0', ndjson.Stdout);
        using (var doc = JsonDocument.Parse(ndjson.Stdout.Trim()))
        {
            Assert.Equal("a.txt", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("line").GetInt32());
        }

        var json = Run("-H", "netgrep", path, "--format", "json", "-j", "1");
        using (var doc = JsonDocument.Parse(json.Stdout))
        {
            Assert.Equal("a.txt", doc.RootElement.GetProperty("matches")[0].GetProperty("path").GetString());
        }

        var rgjson = Run("--no-filename", "netgrep", path, "--format", "rgjson", "-j", "1");
        Assert.Contains("\"text\":\"a.txt\"", rgjson.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Stdin_beside_a_file_prints_the_stdin_name()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "needle\n");

        using (Cwd(tree.Root))
        using (Feed("needle\n"))
        {
            var both = Run("needle", "a.txt", "-", "-j", "1");
            Assert.Equal(ExitCodes.Success, both.Code);
            Assert.Equal(
                "a.txt:1:1:needle\n(standard input):1:1:needle",
                both.Stdout.Trim());

            var hidden = Run("-I", "needle", "a.txt", "-", "-j", "1");
            Assert.Equal("1:1:needle\n1:1:needle", hidden.Stdout.Trim());
            Assert.DoesNotContain("(standard input)", hidden.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Vimgrep_on_stdin_prints_the_name()
    {
        using (Feed("netgrep\n"))
        {
            var run = Run("--vimgrep", "-N", "--no-column", "netgrep", "-", "-j", "1");
            Assert.Equal("(standard input):1:1:netgrep", run.Stdout.Trim());
        }
    }

    [Fact]
    public void Config_no_filename_is_overridden_by_a_later_with_filename()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "--no-filename\n");

        var previous = Environment.GetEnvironmentVariable("NGREP_CONFIG_PATH");
        Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", config);
        try
        {
            var hidden = Run("netgrep", tree.Root, "-j", "1");
            Assert.Equal("1:1:netgrep\n1:1:netgrep", hidden.Stdout.Trim());

            var forced = Run("-H", "netgrep", tree.Root, "-j", "1");
            Assert.Equal("a.txt:1:1:netgrep\nb.txt:1:1:netgrep", forced.Stdout.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", previous);
        }
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

    private static WorkingDirectory Cwd(string path) => new(path);

    private static FeedScope Feed(string text) => new(text);

    private sealed class WorkingDirectory : IDisposable
    {
        private readonly string _previous = Directory.GetCurrentDirectory();

        public WorkingDirectory(string path) => Directory.SetCurrentDirectory(path);

        public void Dispose() => Directory.SetCurrentDirectory(_previous);
    }

    private sealed class FeedScope : IDisposable
    {
        private readonly MemoryStream _stream;

        public FeedScope(string text)
        {
            _stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            StandardInput.SetOpen(() =>
            {
                _stream.Position = 0;
                return _stream;
            });
            StandardInput.SetRedirected(true);
        }

        public void Dispose()
        {
            StandardInput.SetOpen(null);
            StandardInput.SetRedirected(null);
            _stream.Dispose();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-filename-").FullName;

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

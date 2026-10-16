using System.Text;
using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Indexing;
using Ngrep.Text;

namespace Ngrep.Tests;

public class TextFlagTests
{
    [Fact]
    public void Text_flag_prints_matching_lines_from_binary_files()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("plain.txt"), "plain needle\n");
        File.WriteAllText(tree.File("skip.log"), "ignored NEEDLE\n");
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllBytes(tree.File("icon.bin"), "xx\0NEEDLE\nBEFORE\n"u8.ToArray());
        File.WriteAllBytes(tree.File("later.bin"), "BEFORE\nxx\0AFTER\n"u8.ToArray());

        Assert.Equal(ExitCodes.NoMatches, Lines("NEEDLE", tree.Root, "-j", "1").Code);
        Assert.Equal(ExitCodes.NoMatches, Lines("AFTER", tree.Root, "-j", "1").Code);

        var text = Lines("-a", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, text.Code);
        Assert.Contains("icon.bin", text.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEEDLE", text.Stdout, StringComparison.Ordinal);
        Assert.Contains("xx", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.log", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("plain.txt", text.Stdout, StringComparison.Ordinal);

        var longName = Lines("--text", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(text.Stdout, longName.Stdout);

        var bothNames = Lines("-a", "--text", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, bothNames.Code);
        Assert.Contains("icon.bin", bothNames.Stdout, StringComparison.Ordinal);

        var binary = Lines("--binary", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, binary.Code);
        Assert.Contains("icon.bin", binary.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEEDLE", binary.Stdout, StringComparison.Ordinal);

        var after = Lines("-a", "AFTER", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, after.Code);
        Assert.Contains("later.bin", after.Stdout, StringComparison.Ordinal);
        Assert.Contains("AFTER", after.Stdout, StringComparison.Ordinal);
        Assert.Equal(after.Stdout, Lines("--binary", "AFTER", tree.Root, "-j", "1").Stdout);

        var plain = Lines("-a", "plain", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, plain.Code);
        Assert.Contains("plain.txt", plain.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("icon.bin", plain.Stdout, StringComparison.Ordinal);

        var regex = Lines("-a", "xx.NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, regex.Code);
        Assert.Contains("icon.bin", regex.Stdout, StringComparison.Ordinal);

        var literal = Lines("-aF", "xx.NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, literal.Code);

        var quiet = Lines("-qa", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, quiet.Code);
        Assert.Equal("", quiet.Stdout.Trim());
    }

    [Fact]
    public void Text_flag_scans_when_an_index_omits_binary_files()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "text\n");
        File.WriteAllBytes(tree.File("icon.bin"), "xx\0NEEDLE\n"u8.ToArray());
        Indexer.Build(tree.Root);

        var skipped = Lines("NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, skipped.Code);

        var text = Lines("-a", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, text.Code);
        Assert.Contains("icon.bin", text.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEEDLE", text.Stdout, StringComparison.Ordinal);
        Assert.Contains("scanned content (--text)", text.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--binary", text.Stderr, StringComparison.Ordinal);

        var both = Lines("--binary", "--text", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, both.Code);
        Assert.Contains("scanned content (--binary, --text)", both.Stderr, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(Lines("--text", "NEEDLE", tree.Root, "--format", "ndjson", "-j", "1").Stdout.Trim());
        Assert.Equal("icon.bin", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("NEEDLE", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("xx\0NEEDLE", doc.RootElement.GetProperty("lineText").GetString());
    }

    [Fact]
    public void Text_flag_searches_binary_stdin()
    {
        using var capture = new Capture();
        using (Feed("xx\0NEEDLE\n"))
        {
            var skipped = NgrepCli.Run(["NEEDLE", "-", "--stats", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.NoMatches, skipped);
            Assert.Equal("", capture.Stdout.Trim());
            Assert.Contains("files searched: 0", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            var text = NgrepCli.Run(["-a", "NEEDLE", "-", "--stats", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, text);
            // One stdin stream omits the path unless -H.
            Assert.Equal("1:4:xx\0NEEDLE", capture.Stdout.Trim());
            Assert.Contains("files searched: 1", capture.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("scanned content", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            var forced = NgrepCli.Run(["-aH", "NEEDLE", "-", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, forced);
            Assert.Equal("(standard input):1:4:xx\0NEEDLE", capture.Stdout.Trim());

            capture.Clear();
            var named = NgrepCli.Run(["--text", "NEEDLE", "-", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, named);
        }

        using var match = JsonDocument.Parse(capture.Stdout.Trim());
        Assert.Equal("(standard input)", match.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, match.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(4, match.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("NEEDLE", match.RootElement.GetProperty("text").GetString());
        Assert.Equal("xx\0NEEDLE", match.RootElement.GetProperty("lineText").GetString());

        capture.Clear();
        using var tree = Temp();
        File.WriteAllText(tree.File("disk.txt"), "from-disk\n");
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tree.Root);
        try
        {
            using (Feed("xx\0NEEDLE\n", redirected: true))
            {
                var piped = NgrepCli.Run(["-a", "NEEDLE", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.Success, piped);
                Assert.Equal("1:4:xx\0NEEDLE", capture.Stdout.Trim());
                Assert.DoesNotContain("disk.txt", capture.Stdout, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Config_file_can_set_text()
    {
        using var tree = Temp();
        File.WriteAllBytes(tree.File("icon.bin"), "xx\0NEEDLE\n"u8.ToArray());
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "--text\n");
        var previous = Environment.GetEnvironmentVariable("NGREP_CONFIG_PATH");
        Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", config);
        try
        {
            var hit = Lines("NEEDLE", tree.Root, "-j", "1");
            Assert.Equal(ExitCodes.Success, hit.Code);
            Assert.Contains("icon.bin", hit.Stdout, StringComparison.Ordinal);
            Assert.Contains("NEEDLE", hit.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", previous);
        }
    }

    private static RunResult Lines(params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(args).GetAwaiter().GetResult();
        return new RunResult(code, capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal), capture.Stderr);
    }

    private readonly record struct RunResult(int Code, string Stdout, string Stderr);

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-text-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static FeedScope Feed(string text, bool? redirected = null) => new(text, redirected);

    private sealed class FeedScope : IDisposable
    {
        private readonly MemoryStream _stream;

        public FeedScope(string text, bool? redirected)
        {
            _stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            StandardInput.SetOpen(() =>
            {
                _stream.Position = 0;
                return _stream;
            });
            StandardInput.SetRedirected(redirected);
        }

        public void Dispose()
        {
            StandardInput.SetOpen(null);
            StandardInput.SetRedirected(null);
            _stream.Dispose();
        }
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

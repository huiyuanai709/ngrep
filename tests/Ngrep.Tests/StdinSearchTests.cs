using System.Text;
using System.Text.Json;
using Ngrep.Cli;
using Ngrep.Text;

namespace Ngrep.Tests;

public class StdinSearchTests
{
    [Fact]
    public void Dash_searches_stdin_contents()
    {
        using var capture = new Capture();
        using (Feed("hello ngrep\nworld\n"))
        {
            var code = NgrepCli.Run(["ngrep", "-", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
        }

        using var match = JsonDocument.Parse(capture.Stdout.Trim());
        Assert.Equal("(standard input)", match.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, match.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(7, match.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("ngrep", match.RootElement.GetProperty("text").GetString());
        Assert.Equal("hello ngrep", match.RootElement.GetProperty("lineText").GetString());
        Assert.DoesNotContain("Path not found", capture.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("scanned content", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Dash_matches_when_stdin_is_a_terminal()
    {
        using var capture = new Capture();
        using (Feed("hello ngrep\n", redirected: false))
        {
            var code = NgrepCli.Run(["ngrep", "-"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
        }

        Assert.Equal("(standard input):1:7:hello ngrep", capture.Stdout.Trim());
    }

    [Fact]
    public void No_path_searches_stdin_only_when_redirected()
    {
        using var tree = Temp();
        File.WriteAllText(Path.Combine(tree, "disk.txt"), "from-disk\n");
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tree);
        try
        {
            using var capture = new Capture();
            using (Feed("from-stdin\n", redirected: true))
            {
                var piped = NgrepCli.Run(["from-stdin", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.Success, piped);
                Assert.Contains("(standard input)", capture.Stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("disk.txt", capture.Stdout, StringComparison.Ordinal);

                capture.Clear();
                var diskMiss = NgrepCli.Run(["from-disk", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.NoMatches, diskMiss);
            }

            capture.Clear();
            using (Feed("from-stdin\n", redirected: false))
            {
                var tty = NgrepCli.Run(["from-disk", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.Success, tty);
                Assert.Contains("disk.txt", capture.Stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("(standard input)", capture.Stdout, StringComparison.Ordinal);

                capture.Clear();
                var stdinIgnored = NgrepCli.Run(["from-stdin", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.NoMatches, stdinIgnored);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Explicit_path_ignores_a_redirected_stdin()
    {
        using var tree = Temp();
        File.WriteAllText(Path.Combine(tree, "disk.txt"), "only-on-disk\n");
        using var capture = new Capture();
        using (Feed("only-on-stdin\n", redirected: true))
        {
            var code = NgrepCli.Run(["only-on-disk", tree, "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
        }

        Assert.Contains("disk.txt:1:1:only-on-disk", capture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("(standard input)", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Pattern_file_dash_still_searches_the_directory()
    {
        using var tree = Temp();
        File.WriteAllText(Path.Combine(tree, "disk.txt"), "from-disk\n");
        var previousDir = Directory.GetCurrentDirectory();
        var previousIn = Console.In;
        Directory.SetCurrentDirectory(tree);
        Console.SetIn(new StringReader("from-disk\n"));
        try
        {
            using var capture = new Capture();
            using (Feed("from-stdin\n", redirected: true))
            {
                var code = NgrepCli.Run(["-f", "-", "-l", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.Success, code);
            }

            Assert.Equal("disk.txt", capture.Stdout.Trim());
            Assert.DoesNotContain("(standard input)", capture.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(previousIn);
            Directory.SetCurrentDirectory(previousDir);
        }
    }

    [Fact]
    public void Pattern_file_and_content_dash_are_an_error()
    {
        using var capture = new Capture();
        var both = NgrepCli.Run(["-f", "-", "-"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Error, both);
        Assert.Contains("attempted to read patterns from stdin while also searching stdin", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        var twice = NgrepCli.Run(["-f", "-", "-f", "-"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Error, twice);
        Assert.Contains("stdin has already been consumed", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pattern_file_with_content_from_stdin()
    {
        var pats = Path.Combine(Path.GetTempPath(), "ngrep-stdin-pats-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(pats, "ngrep\n");
        try
        {
            using var capture = new Capture();
            using (Feed("hello ngrep\n"))
            {
                var code = NgrepCli.Run(["-f", pats, "-", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
                Assert.Equal(ExitCodes.Success, code);
            }

            using var match = JsonDocument.Parse(capture.Stdout.Trim());
            Assert.Equal("(standard input)", match.RootElement.GetProperty("path").GetString());
            Assert.Equal("ngrep", match.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(pats);
        }
    }

    [Fact]
    public void Text_reports_use_the_stdin_name()
    {
        using var capture = new Capture();
        using (Feed("keep\nngrep\nngrep\nno\n"))
        {
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "-c", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input):2", capture.Stdout.Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "-l", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input)", capture.Stdout.Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "-o", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input):2:1:ngrep\n(standard input):3:1:ngrep", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "--heading", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input)\n2:1:ngrep\n3:1:ngrep", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "--heading", "--no-heading", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input):2:1:ngrep\n(standard input):3:1:ngrep", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["--files-without-match", "ngrep", "-", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("", capture.Stdout.Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-v", "ngrep", "-", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input):1:1:keep\n(standard input):4:1:no", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-q", "ngrep", "-"]).GetAwaiter().GetResult());
            Assert.Equal("", capture.Stdout);
        }
    }

    [Fact]
    public void Context_regex_and_crlf_follow_file_rules()
    {
        using var capture = new Capture();
        using (Feed("before\nngrep\nafter\n"))
        {
            var code = NgrepCli.Run(["ngrep", "-", "-C", "1", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
            Assert.Equal(
                "(standard input)-1-before\n(standard input):2:1:ngrep\n(standard input)-3-after",
                capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        }

        capture.Clear();
        using (Feed("NGREP\nxxNGREPyy\n"))
        {
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-i", "-w", "ngrep", "-", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("(standard input):1:1:NGREP", capture.Stdout.Trim());
        }

        capture.Clear();
        using (Feed("foo\nbar\n"))
        {
            var code = NgrepCli.Run(["--regex", "-U", "foo\nbar", "-", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
            using var match = JsonDocument.Parse(capture.Stdout.Trim());
            Assert.Equal("(standard input)", match.RootElement.GetProperty("path").GetString());
            Assert.Equal(1, match.RootElement.GetProperty("line").GetInt32());
            Assert.Equal("foo", match.RootElement.GetProperty("lineText").GetString());
        }

        capture.Clear();
        using (Feed("hello ngrep\r\n"))
        {
            var code = NgrepCli.Run(["ngrep", "-", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
            using var match = JsonDocument.Parse(capture.Stdout.Trim());
            Assert.Equal("hello ngrep", match.RootElement.GetProperty("lineText").GetString());
        }
    }

    [Fact]
    public void Json_and_rgjson_name_stdin()
    {
        using var capture = new Capture();
        using (Feed("ngrep\n"))
        {
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
            using var json = JsonDocument.Parse(capture.Stdout);
            Assert.Equal("(standard input)", json.RootElement.GetProperty("matches")[0].GetProperty("path").GetString());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["ngrep", "-", "--format", "rgjson", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Contains("\"text\":\"(standard input)\"", capture.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Globs_and_types_do_not_drop_stdin()
    {
        using var capture = new Capture();
        using (Feed("hello ngrep\n"))
        {
            var code = NgrepCli.Run(["ngrep", "-", "-g", "*.cs", "-t", "cs", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
        }

        Assert.Contains("(standard input):1:7:hello ngrep", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_and_empty_stdin_are_not_errors()
    {
        using var capture = new Capture();
        using (Feed("ngrep\0hidden\n"))
        {
            var binary = NgrepCli.Run(["ngrep", "-", "--stats", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.NoMatches, binary);
            Assert.Equal("", capture.Stdout.Trim());
            Assert.Contains("files searched: 0", capture.Stderr, StringComparison.Ordinal);
        }

        capture.Clear();
        using (Feed(""))
        {
            var empty = NgrepCli.Run(["ngrep", "-"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.NoMatches, empty);
            Assert.Equal("", capture.Stdout.Trim());

            capture.Clear();
            var listed = NgrepCli.Run(["--files-without-match", "absent", "-", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, listed);
            Assert.Equal("(standard input)", capture.Stdout.Trim());
        }
    }

    [Fact]
    public void Unreadable_stdin_exits_2()
    {
        using var capture = new Capture();
        StandardInput.SetOpen(() => new BrokenStream());
        try
        {
            var loud = NgrepCli.Run(["ngrep", "-"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Error, loud);
            Assert.Contains("Unreadable: (standard input)", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            var quiet = NgrepCli.Run(["ngrep", "-", "-s"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Error, quiet);
            Assert.DoesNotContain("Unreadable", capture.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            StandardInput.SetOpen(null);
        }
    }

    [Fact]
    public void Stream_reader_caps_size_and_stops_early()
    {
        var exact = TextFiles.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes("abcd")), null, CancellationToken.None, 4).GetAwaiter().GetResult();
        using (exact)
        {
            Assert.Equal(ReadKind.Ok, exact.Kind);
            Assert.Equal(4, exact.Length);
        }

        var over = TextFiles.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes("abcde")), null, CancellationToken.None, 4).GetAwaiter().GetResult();
        using (over)
        {
            Assert.Equal(ReadKind.TooLarge, over.Kind);
        }

        var binary = TextFiles.ReadStreamAsync(new MemoryStream([1, 0, 2]), null, CancellationToken.None, 8).GetAwaiter().GetResult();
        using (binary)
        {
            Assert.Equal(ReadKind.Binary, binary.Kind);
        }

        var broken = TextFiles.ReadStreamAsync(new BrokenStream(), null, CancellationToken.None, 8).GetAwaiter().GetResult();
        using (broken)
        {
            Assert.Equal(ReadKind.Unreadable, broken.Kind);
        }

        var payload = new byte[1024 * 1024];
        Array.Fill(payload, (byte)'x');
        Encoding.UTF8.GetBytes("ngrep\n").CopyTo(payload, 0);
        var limited = new ChunkedStream(payload, 4096);
        using var capture = new Capture();
        StandardInput.SetOpen(() => limited);
        try
        {
            var code = NgrepCli.Run(["ngrep", "-", "-l", "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, code);
        }
        finally
        {
            StandardInput.SetOpen(null);
        }

        Assert.Equal("(standard input)", capture.Stdout.Trim());
        Assert.True(limited.ReadBytes < payload.Length, $"read {limited.ReadBytes} of {payload.Length}");
    }

    [Fact]
    public void Help_mentions_content_stdin_and_pattern_stdin()
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(["search", "--help"]).GetAwaiter().GetResult();
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("not a terminal", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("patterns from stdin", capture.Stdout, StringComparison.Ordinal);
    }

    private static FeedScope Feed(string text, bool? redirected = null) => new(text, redirected);

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("ngrep-stdin-").FullName;

        public string Path { get; }

        public static implicit operator string(TempDir dir) => dir.Path;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static TempDir Temp() => new();

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

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;

        public ChunkedStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public int ReadBytes { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos >= _data.Length)
            {
                return 0;
            }

            var n = Math.Min(buffer.Length, Math.Min(_chunk, _data.Length - _pos));
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            ReadBytes += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("closed");

        public override int Read(Span<byte> buffer) => throw new IOException("closed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("closed");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

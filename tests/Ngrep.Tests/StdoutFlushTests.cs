using System.Text;
using Ngrep.Output;
using Ngrep.Search;

namespace Ngrep.Tests;

public class StdoutFlushTests
{
    [Fact]
    public void Terminal_policy_autoflushes_a_small_buffer()
    {
        var policy = StdoutPolicy.For(isTerminal: true);

        Assert.True(policy.AutoFlush);
        Assert.Equal(StdoutPolicy.TerminalBufferSize, policy.BufferSize);
        Assert.True(policy.BufferSize < StdoutPolicy.BlockBufferSize);
    }

    [Fact]
    public void Redirected_policy_keeps_the_block_buffer()
    {
        var policy = StdoutPolicy.For(isTerminal: false);

        Assert.False(policy.AutoFlush);
        Assert.Equal(StdoutPolicy.BlockBufferSize, policy.BufferSize);
    }

    [Fact]
    public void Terminal_writer_emits_a_short_line()
    {
        using var stream = new CountingStream();
        using var writer = Stdout.CreateWriter(stream, StdoutPolicy.For(isTerminal: true));

        writer.WriteLine("alpha-unique");

        Assert.Contains("alpha-unique", Encoding.UTF8.GetString(stream.Written), StringComparison.Ordinal);
        var afterFirst = stream.Writes;
        writer.WriteLine("beta-unique");
        Assert.True(stream.Writes > afterFirst);
        AssertFirstLineVisibleBeforeSecond(stream, "alpha-unique", "beta-unique");
    }

    [Fact]
    public void Redirected_writer_holds_a_short_line()
    {
        using var stream = new CountingStream();
        using var writer = Stdout.CreateWriter(stream, StdoutPolicy.For(isTerminal: false));

        writer.WriteLine("alpha-unique");

        Assert.Equal(0, stream.Writes);
        writer.Flush();
        Assert.Contains("alpha-unique", Encoding.UTF8.GetString(stream.Written), StringComparison.Ordinal);
    }

    [Fact]
    public void Redirected_writer_emits_when_the_block_fills()
    {
        var policy = StdoutPolicy.For(isTerminal: false);
        using var stream = new CountingStream();
        using var writer = Stdout.CreateWriter(stream, policy);

        writer.Write(new string('a', policy.BufferSize));
        Assert.Equal(0, stream.Writes);

        writer.Write("z");

        Assert.True(stream.Writes >= 1);
        Assert.True(stream.Written.Length > 0);
        Assert.True(stream.Written.Length < policy.BufferSize + 1);
    }

    [Fact]
    public void Terminal_ndjson_emits_each_match_line()
    {
        WithStdout(isTerminal: true, stream =>
        {
            File(OutputFormat.Ndjson, TextBatch());
            AssertFirstLineVisibleBeforeSecond(stream, "alpha-unique", "beta-unique");
        });
    }

    [Fact]
    public void Redirected_ndjson_keeps_a_short_file_in_one_block()
    {
        WithStdout(isTerminal: false, stream =>
        {
            File(OutputFormat.Ndjson, TextBatch());
            AssertLinesLeaveTogether(stream, "alpha-unique", "beta-unique");
        });
    }

    [Fact]
    public void Terminal_text_emits_each_match_line()
    {
        WithStdout(isTerminal: true, stream =>
        {
            File(OutputFormat.Text, TextBatch());
            AssertFirstLineVisibleBeforeSecond(stream, "alpha-unique", "beta-unique");
        });
    }

    [Fact]
    public void Redirected_text_keeps_a_short_file_in_one_block()
    {
        WithStdout(isTerminal: false, stream =>
        {
            File(OutputFormat.Text, TextBatch());
            AssertLinesLeaveTogether(stream, "alpha-unique", "beta-unique");
        });
    }

    [Fact]
    public void Terminal_text_context_emits_each_line()
    {
        WithStdout(isTerminal: true, stream =>
        {
            File(OutputFormat.Text, ContextBatch());
            AssertFirstLineVisibleBeforeSecond(stream, "alpha-unique", "beta-unique");
            Assert.Contains("gamma-unique", Encoding.UTF8.GetString(stream.Written), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Terminal_raw_rows_emit_each_line()
    {
        WithStdout(isTerminal: true, stream =>
        {
            File(OutputFormat.Text, RawBatch());
            AssertFirstLineVisibleBeforeSecond(stream, "alpha-unique", "beta-unique");
        });
    }

    [Fact]
    public void Redirected_raw_rows_stay_in_the_block_buffer()
    {
        WithStdout(isTerminal: false, stream =>
        {
            File(OutputFormat.Text, RawBatch());
            Assert.Equal(0, stream.Writes);
            Console.Out.Flush();
            AssertLinesLeaveTogether(stream, "alpha-unique", "beta-unique");
        });
    }

    private static void File(OutputFormat format, SearchBatch batch)
    {
        var stream = new MachineOutput.MatchStream(
            format,
            new SearchPresentation(ReportMode.Matches, OnlyMatching: false, Color: false),
            holdAutoHeading: false);
        stream.File(batch);
    }

    private static SearchBatch TextBatch() => new()
    {
        Path = "a.cs",
        Examined = true,
        Matched = true,
        Matches =
        [
            new SearchMatch("a.cs", 1, 1, "alpha-unique", "alpha-unique"),
            new SearchMatch("a.cs", 2, 1, "beta-unique", "beta-unique"),
        ],
    };

    private static SearchBatch ContextBatch() => new()
    {
        Path = "a.cs",
        Examined = true,
        Matched = true,
        Matches =
        [
            new SearchMatch(
                "a.cs",
                2,
                1,
                "beta-unique",
                "beta-unique",
                Before: [new ContextLine(1, "alpha-unique")],
                After: [new ContextLine(3, "gamma-unique")]),
        ],
    };

    private static SearchBatch RawBatch()
    {
        var first = "alpha-unique"u8;
        var second = "beta-unique"u8;
        var body = new byte[first.Length + second.Length];
        first.CopyTo(body);
        second.CopyTo(body.AsSpan(first.Length));
        return new SearchBatch
        {
            Path = "a.cs",
            Examined = true,
            Matched = true,
            Matches = [],
            RawBody = body,
            RawRows =
            [
                new RawLine(1, 1, 0, first.Length),
                new RawLine(2, 1, first.Length, second.Length),
            ],
        };
    }

    private static void WithStdout(bool isTerminal, Action<CountingStream> body)
    {
        var previousOut = Console.Out;
        var previousPolicy = Stdout.Active;
        var stream = new CountingStream();
        var writer = Stdout.CreateWriter(stream, StdoutPolicy.For(isTerminal));
        Stdout.Use(StdoutPolicy.For(isTerminal));
        Console.SetOut(writer);
        try
        {
            body(stream);
        }
        finally
        {
            Console.SetOut(previousOut);
            Stdout.Use(previousPolicy);
            writer.Dispose();
            stream.Dispose();
        }
    }

    private static void AssertFirstLineVisibleBeforeSecond(CountingStream stream, string first, string second)
    {
        var combined = "";
        var firstAlone = false;
        foreach (var payload in stream.Payloads)
        {
            combined += Encoding.UTF8.GetString(payload);
            if (!combined.Contains(first, StringComparison.Ordinal))
            {
                continue;
            }

            firstAlone = !combined.Contains(second, StringComparison.Ordinal);
            break;
        }

        Assert.True(firstAlone);
        Assert.Contains(second, Encoding.UTF8.GetString(stream.Written), StringComparison.Ordinal);
    }

    private static void AssertLinesLeaveTogether(CountingStream stream, string first, string second)
    {
        var combined = "";
        var together = false;
        foreach (var payload in stream.Payloads)
        {
            var before = combined;
            combined += Encoding.UTF8.GetString(payload);
            if (!combined.Contains(first, StringComparison.Ordinal))
            {
                continue;
            }

            together = !before.Contains(first, StringComparison.Ordinal)
                && combined.Contains(second, StringComparison.Ordinal);
            break;
        }

        Assert.True(together);
    }

    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _data = new();

        public int Writes { get; private set; }

        public List<byte[]> Payloads { get; } = [];

        public byte[] Written => _data.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _data.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return;
            }

            Writes++;
            var copy = buffer.ToArray();
            Payloads.Add(copy);
            _data.Write(copy);
        }
    }
}

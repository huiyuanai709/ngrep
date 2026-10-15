using Ngrep.Search;
using Ngrep.Text;

namespace Ngrep.Tests;

/// <summary>
/// Windows kills the process if a thread-pool overlapped completion runs after the
/// read handle has been closed and its callback cleared. These tests lock the
/// portable half of that contract: reads finish, cancellation does not return a
/// success, and no descriptor for the path is still open. The binding-before-handle
/// dispose order is Windows-only and lives in <see cref="TextFiles"/>.
/// </summary>
public class ReadLifetimeTests
{
    [Fact]
    public async Task Mapped_read_round_trips_and_search_finishes_after_a_hit()
    {
        using var tree = new TempDir();
        var payload = new byte[TextFiles.MmapThreshold];
        Array.Fill(payload, (byte)' ');
        var needle = "\t\t<!--百度正/逆地理编码服务地址-->"u8;
        needle.CopyTo(payload.AsSpan(payload.Length - needle.Length - 1));
        payload[^1] = (byte)'\n';
        var wide = tree.File("wide.txt");
        File.WriteAllBytes(wide, payload);
        File.WriteAllText(tree.File("note.txt"), "\t\t<!--百度正/逆地理编码服务地址-->\n");

        using (var read = await TextFiles.ReadAsync(wide, stop: null, CancellationToken.None))
        {
            Assert.Equal(ReadKind.Ok, read.Kind);
            Assert.Equal(payload.Length, read.Length);
            Assert.True(payload.AsSpan().SequenceEqual(read.Span));
        }

        Assert.False(StillOpen(wide));

        var found = await Searcher.Search(new SearchQuery
        {
            Pattern = "百度正/逆地理编码服务地址",
            Path = tree.Root,
            MaxDegreeOfParallelism = 4,
        });
        Assert.Equal(2, found.MatchCount);
        Assert.Contains(found.Matches, match => match.Path == "note.txt" && match.Text.Contains("百度", StringComparison.Ordinal));
        Assert.Contains(found.Matches, match => match.Path == "wide.txt");
        Assert.False(StillOpen(wide));
    }

    [Fact]
    public async Task Parallel_reads_release_every_handle()
    {
        using var tree = new TempDir();
        var files = new List<(string Path, byte[] Bytes, bool Binary)>();
        for (var i = 0; i < 32; i++)
        {
            var length = i % 4 == 0 ? TextFiles.MmapThreshold + 32 : 96 + (i * 17);
            var bytes = new byte[length];
            Array.Fill(bytes, (byte)('a' + (i % 26)));
            bytes[^1] = (byte)'\n';
            var binary = i % 9 == 0;
            if (binary)
            {
                bytes[3] = 0;
            }

            var path = tree.File($"f{i}.dat");
            await File.WriteAllBytesAsync(path, bytes);
            files.Add((path, bytes, binary));
        }

        await Task.WhenAll(files.Select(async file =>
        {
            using var read = await TextFiles.ReadAsync(file.Path, stop: null, CancellationToken.None);
            if (file.Binary)
            {
                Assert.Equal(ReadKind.Binary, read.Kind);
                return;
            }

            Assert.Equal(ReadKind.Ok, read.Kind);
            Assert.True(file.Bytes.AsSpan().SequenceEqual(read.Span));
        }));

        foreach (var file in files)
        {
            Assert.False(StillOpen(file.Path));
        }
    }

    [Fact]
    public async Task Cancel_between_chunks_throws_and_closes_the_handle()
    {
        using var tree = new TempDir();
        var bytes = new byte[96 * 1024];
        Array.Fill(bytes, (byte)'x');
        var path = tree.File("chunked.txt");
        await File.WriteAllBytesAsync(path, bytes);
        var cts = new CancellationTokenSource();
        var chunks = 0;
        ReadStop stop = (_, _) =>
        {
            Interlocked.Increment(ref chunks);
            cts.Cancel();
            return false;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var read = await TextFiles.ReadAsync(path, stop, cts.Token);
        });
        Assert.True(chunks >= 1);
        Assert.False(StillOpen(path));
    }

    [Fact]
    public async Task Already_cancelled_read_throws_and_closes_the_handle()
    {
        using var tree = new TempDir();
        var path = tree.File("small.txt");
        await File.WriteAllTextAsync(path, "hello\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var read = await TextFiles.ReadAsync(path, stop: null, cts.Token);
        });
        Assert.False(StillOpen(path));
    }

    [Fact]
    public async Task Early_stop_returns_the_prefix_and_closes_the_handle()
    {
        using var tree = new TempDir();
        var bytes = new byte[96 * 1024];
        Array.Fill(bytes, (byte)'y');
        "NEEDLE"u8.CopyTo(bytes);
        var path = tree.File("stop.txt");
        await File.WriteAllBytesAsync(path, bytes);
        using (var read = await TextFiles.ReadAsync(path, static (span, _) => span.IndexOf("NEEDLE"u8) >= 0, CancellationToken.None))
        {
            Assert.Equal(ReadKind.Ok, read.Kind);
            Assert.True(read.Length < bytes.Length);
            Assert.Contains((byte)'N', read.Span.ToArray());
        }

        Assert.False(StillOpen(path));
    }

    private static bool StillOpen(string path)
    {
        if (!Directory.Exists("/proc/self/fd"))
        {
            try
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        var full = Path.GetFullPath(path);
        foreach (var fd in Directory.EnumerateFiles("/proc/self/fd"))
        {
            FileSystemInfo? target;
            try
            {
                target = File.ResolveLinkTarget(fd, returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (target != null && string.Equals(target.FullName, full, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-read-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

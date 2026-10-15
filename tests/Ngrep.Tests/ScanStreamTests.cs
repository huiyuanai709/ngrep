using System.Text;
using Ngrep.Cli;
using Ngrep.Ignore;
using Ngrep.Search;

namespace Ngrep.Tests;

[CollectionDefinition("walker-hooks", DisableParallelization = true)]
public sealed class WalkerHooksCollection;

/// <summary>
/// The no-index path used to list and sort the whole tree before reading.
/// These tests lock the streaming and cancel behavior of that walk.
/// </summary>
[Collection("walker-hooks")]
public class ScanStreamTests
{
    [Fact]
    public async Task Scan_emits_a_hit_before_a_later_directory_is_listed()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a-hit.txt"), "needle\n");
        File.WriteAllText(tree.File("z-late/late.txt"), "nope\n");

        var emitted = false;
        var lateBeforeEmit = false;
        TreeWalker.BeforeCollect = full =>
        {
            if (full.EndsWith("z-late", StringComparison.Ordinal) && !emitted)
            {
                lateBeforeEmit = true;
            }
        };
        try
        {
            var outcome = await Searcher.Search(new SearchQuery
            {
                Pattern = "needle",
                Path = tree.Root,
                MaxDegreeOfParallelism = 1,
                Events = new SearchEvents
                {
                    Batch = batch =>
                    {
                        if (batch.Path == "a-hit.txt" && batch.Matches.Count > 0)
                        {
                            emitted = true;
                        }
                    },
                },
            });

            Assert.True(emitted);
            Assert.False(lateBeforeEmit);
            Assert.True(outcome.Streamed);
            Assert.Equal(1, outcome.MatchCount);
            Assert.Empty(outcome.Matches);
        }
        finally
        {
            TreeWalker.BeforeCollect = null;
        }
    }

    [Fact]
    public async Task Parallel_scan_prints_a_sibling_while_an_earlier_directory_is_still_open()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a-bulk/one.txt"), "nope\n");
        File.WriteAllText(tree.File("a-bulk/two.txt"), "nope\n");
        File.WriteAllText(tree.File("z-hit/hit.txt"), "needle\n");

        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 8), Math.Max(io, 8));
        using var hit = new ManualResetEventSlim(false);
        var overlapped = false;
        TreeWalker.BeforeCollect = full =>
        {
            if (!full.EndsWith("a-bulk", StringComparison.Ordinal))
            {
                return;
            }

            if (hit.Wait(TimeSpan.FromSeconds(5)))
            {
                overlapped = true;
            }
        };
        try
        {
            var outcome = await Searcher.Search(new SearchQuery
            {
                Pattern = "needle",
                Path = tree.Root,
                MaxDegreeOfParallelism = 4,
                Events = new SearchEvents
                {
                    Batch = batch =>
                    {
                        if (batch.Matches.Count > 0 && batch.Path.EndsWith("hit.txt", StringComparison.Ordinal))
                        {
                            hit.Set();
                        }
                    },
                },
            });

            Assert.True(overlapped);
            Assert.Equal(1, outcome.MatchCount);
            Assert.True(outcome.Streamed);
        }
        finally
        {
            TreeWalker.BeforeCollect = null;
        }
    }

    [Fact]
    public async Task Cancel_during_a_scan_does_not_read_the_rest_of_the_directory()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a.txt"), "needle\n");
        File.WriteAllText(tree.File("b.txt"), "needle\n");
        File.WriteAllText(tree.File("c.txt"), "needle\n");

        var seen = new List<string>();
        using var cts = new CancellationTokenSource();
        Searcher.BeforeConfirm = path =>
        {
            lock (seen)
            {
                seen.Add(path);
            }

            if (path == "b.txt")
            {
                cts.Cancel();
            }
        };
        try
        {
            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Searcher.Search(new SearchQuery
            {
                Pattern = "needle",
                Path = tree.Root,
                MaxDegreeOfParallelism = 1,
                CancellationToken = cts.Token,
                Events = new SearchEvents { Batch = _ => { } },
            }));
            Assert.NotNull(thrown);
            Assert.Contains("a.txt", seen);
            Assert.Contains("b.txt", seen);
            Assert.DoesNotContain("c.txt", seen);
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }
    }

    [Fact]
    public async Task Cancel_stops_a_parallel_scan_without_reading_every_file()
    {
        using var tree = new TempDir();
        const int count = 80;
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(tree.File($"f{i:00}.txt"), "needle\n");
        }

        var seen = 0;
        using var cts = new CancellationTokenSource();
        Searcher.BeforeConfirm = _ =>
        {
            if (Interlocked.Increment(ref seen) == 1)
            {
                cts.Cancel();
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Searcher.Search(new SearchQuery
            {
                Pattern = "needle",
                Path = tree.Root,
                MaxDegreeOfParallelism = 4,
                CancellationToken = cts.Token,
                Events = new SearchEvents { Batch = _ => { } },
            }));
            Assert.InRange(seen, 1, 16);
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }
    }

    [Fact]
    public async Task Parallel_scan_matches_the_sequential_scan()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("keep.txt"), "needle\n");
        File.WriteAllText(tree.File("sub/hit.txt"), "needle here\n");
        File.WriteAllText(tree.File("sub/miss.txt"), "nope\n");
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("skip.log"), "needle\n");
        File.WriteAllText(tree.File(".secret"), "needle\n");
        File.WriteAllBytes(tree.File("blob.bin"), Encoding.UTF8.GetBytes("needle\0"));

        var sequential = await Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = tree.Root,
            MaxDegreeOfParallelism = 1,
        });
        var streamed = new List<string>();
        var parallel = await Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = tree.Root,
            MaxDegreeOfParallelism = 4,
            Events = new SearchEvents
            {
                Batch = batch =>
                {
                    foreach (var match in batch.Matches)
                    {
                        streamed.Add(match.Path);
                    }
                },
            },
        });

        var expected = sequential.Matches.Select(match => match.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "keep.txt", "sub/hit.txt" }, expected);
        Assert.Equal(expected, streamed.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(sequential.MatchCount, parallel.MatchCount);
        Assert.Equal(sequential.FilesSearched, parallel.FilesSearched);
        Assert.True(parallel.Streamed);
        Assert.False(sequential.Streamed);
    }

    [Theory]
    [InlineData("AppData", true, true)]
    [InlineData("AppData", false, false)]
    [InlineData(".git", false, true)]
    [InlineData(".secret", false, true)]
    [InlineData("Desktop", false, false)]
    [InlineData("desktop.ini", true, true)]
    public void Hidden_entry_covers_dot_names_and_the_hidden_attribute(string name, bool hiddenAttribute, bool hidden)
    {
        Assert.Equal(hidden, TreeWalker.IsHiddenEntry(name, hiddenAttribute));
    }

    [Fact]
    public async Task Hidden_attribute_is_skipped_unless_the_flag_is_set()
    {
        using var tree = new TempDir();
        var secret = tree.File("AppData/secret.txt");
        File.WriteAllText(secret, "needle\n");
        File.WriteAllText(tree.File("visible.txt"), "needle\n");
        var dir = Path.GetDirectoryName(secret)!;
        try
        {
            File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        if ((File.GetAttributes(dir) & FileAttributes.Hidden) == 0)
        {
            // Linux reports Hidden from a leading dot, not from SetAttributes.
            return;
        }

        var skipped = await Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = tree.Root,
            MaxDegreeOfParallelism = 1,
        });
        Assert.DoesNotContain(skipped.Matches, match => match.Path.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(skipped.Matches, match => match.Path == "visible.txt");

        var included = await Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = tree.Root,
            IncludeHidden = true,
            MaxDegreeOfParallelism = 1,
        });
        Assert.Contains(included.Matches, match => match.Path.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Second_ctrl_c_is_the_force_exit_signal()
    {
        var interrupts = 0;
        using var cts = new CancellationTokenSource();
        Assert.True(NgrepCli.RequestSearchCancel(ref interrupts, cts));
        Assert.True(cts.IsCancellationRequested);
        Assert.False(NgrepCli.RequestSearchCancel(ref interrupts, cts));
        Assert.Equal(2, interrupts);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-scan-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

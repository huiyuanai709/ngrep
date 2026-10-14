using Ngrep.Indexing;
using Ngrep.Search;
using Ngrep.Text;

namespace Ngrep.Tests;

public class IndexFlushTests
{
    [Fact]
    public void Added_file_is_searchable_before_later_files_are_processed()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alphaunique\n");
        File.WriteAllText(tree.File("b.txt"), "betaunique\n");

        using var capture = new Capture();
        var ex = Assert.Throws<IndexingStoppedException>(() =>
            Indexer.Build(tree.Root, showProgress: true, onPersisted: (_, path) =>
            {
                if (path != "a.txt")
                {
                    return;
                }

                var indexPath = IndexPaths.DefaultFor(tree.Root);
                Assert.True(File.Exists(Path.Combine(indexPath, "manifest.json")));
                var loaded = IndexStore.TryLoad(indexPath);
                Assert.NotNull(loaded);
                Assert.Equal(["a.txt"], loaded!.Documents.Select(d => d.Path).ToArray());
                Assert.Equal(1, loaded.Manifest.DocumentCount);

                var hit = Searcher.Search(new SearchQuery { Pattern = "alphaunique", Path = tree.Root, MaxDegreeOfParallelism = 1 });
                Assert.True(hit.UsedIndex);
                Assert.Equal("a.txt", Assert.Single(hit.Matches).Path);

                var miss = Searcher.Search(new SearchQuery { Pattern = "betaunique", Path = tree.Root, MaxDegreeOfParallelism = 1 });
                Assert.True(miss.UsedIndex);
                Assert.Empty(miss.Matches);
                throw new IndexingStoppedException();
            }));

        Assert.Equal("stop indexing", ex.Message);
        Assert.Contains("added: a.txt", capture.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("added: b.txt", capture.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("added: ", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Killed_update_keeps_the_new_file_and_the_previous_other_file()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alphaunique\n");
        File.WriteAllText(tree.File("b.txt"), "betaunique\n");
        var first = Indexer.Build(tree.Root);
        var originalId = Assert.Single(IndexStore.TryLoad(first.IndexPath)!.Documents, d => d.Path == "a.txt").Id;

        File.WriteAllText(tree.File("a.txt"), "alphaupdated\n");
        File.WriteAllText(tree.File("c.txt"), "gammaunique\n");

        Assert.Throws<IndexingStoppedException>(() =>
            Indexer.Build(tree.Root, onPersisted: (kind, path) =>
            {
                if (kind == "updated" && path == "a.txt")
                {
                    throw new IndexingStoppedException();
                }
            }));

        var loaded = IndexStore.TryLoad(first.IndexPath);
        Assert.NotNull(loaded);
        var paths = loaded!.Documents.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["a.txt", "b.txt"], paths);
        Assert.DoesNotContain(loaded.Documents, d => d.Path == "c.txt");
        var updated = Assert.Single(loaded.Documents, d => d.Path == "a.txt");
        Assert.Equal(originalId, updated.Id);
        Assert.Equal(TextFiles.Sha256Hex("alphaupdated\n"u8.ToArray()), updated.Sha256);

        var hit = Searcher.Search(new SearchQuery { Pattern = "alphaupdated", Path = tree.Root, MaxDegreeOfParallelism = 1 });
        Assert.Equal("a.txt", Assert.Single(hit.Matches).Path);
        var kept = Searcher.Search(new SearchQuery { Pattern = "betaunique", Path = tree.Root, MaxDegreeOfParallelism = 1 });
        Assert.Equal("b.txt", Assert.Single(kept.Matches).Path);
    }

    [Fact]
    public void Removals_are_committed_one_path_at_a_time_after_the_walk()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        File.WriteAllText(tree.File("b.txt"), "beta\n");
        File.WriteAllText(tree.File("c.txt"), "gamma\n");
        var built = Indexer.Build(tree.Root);
        File.Delete(tree.File("a.txt"));
        File.Delete(tree.File("c.txt"));

        Assert.Throws<IndexingStoppedException>(() =>
            Indexer.Build(tree.Root, onPersisted: (kind, path) =>
            {
                if (kind != "removed")
                {
                    return;
                }

                Assert.Equal("a.txt", path);
                var loaded = IndexStore.TryLoad(built.IndexPath);
                Assert.NotNull(loaded);
                var paths = loaded!.Documents.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                Assert.Equal(["b.txt", "c.txt"], paths);
                throw new IndexingStoppedException();
            }));

        var after = IndexStore.TryLoad(built.IndexPath);
        Assert.Equal(["b.txt", "c.txt"], after!.Documents.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Failed_commit_keeps_the_previous_index_and_omits_the_progress_line()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alphaunique\n");
        var built = Indexer.Build(tree.Root);
        var previous = TextFiles.Sha256Hex("alphaunique\n"u8.ToArray());
        File.WriteAllText(tree.File("a.txt"), "betaunique\n");

        using var capture = new Capture();
        try
        {
            IndexStore.TestHookBeforeCommit = () => throw new IOException("simulated commit failure");
            var ex = Assert.Throws<IOException>(() => Indexer.Build(tree.Root, showProgress: true));
            Assert.Equal("simulated commit failure", ex.Message);
        }
        finally
        {
            IndexStore.TestHookBeforeCommit = null;
        }

        Assert.False(Directory.Exists(built.IndexPath + ".tmp"));
        Assert.DoesNotContain("updated: ", capture.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("added: ", capture.Stderr, StringComparison.Ordinal);
        var loaded = IndexStore.TryLoad(built.IndexPath);
        Assert.NotNull(loaded);
        Assert.Equal(previous, Assert.Single(loaded!.Documents).Sha256);
        var missed = Searcher.Search(new SearchQuery { Pattern = "betaunique", Path = tree.Root, MaxDegreeOfParallelism = 1 });
        Assert.True(missed.UsedIndex);
        Assert.Empty(missed.Matches);

        var again = Indexer.Build(tree.Root);
        Assert.Equal(1, again.Updated);
        var replaced = Searcher.Search(new SearchQuery { Pattern = "betaunique", Path = tree.Root, MaxDegreeOfParallelism = 1 });
        Assert.Equal("a.txt", Assert.Single(replaced.Matches).Path);
    }

    [Fact]
    public void Unchanged_files_do_not_commit_on_their_own()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        File.WriteAllText(tree.File("b.txt"), "beta\n");
        var seen = new List<string>();
        Indexer.Build(tree.Root, onPersisted: (kind, path) => seen.Add(kind + " " + path));
        Assert.Equal(["added a.txt", "added b.txt"], seen);

        seen.Clear();
        Indexer.Build(tree.Root, onPersisted: (kind, path) => seen.Add(kind + " " + path));
        Assert.Empty(seen);
    }

    private static TempDir Temp() => new();

    private sealed class IndexingStoppedException : Exception
    {
        public IndexingStoppedException()
            : base("stop indexing")
        {
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-flush-").FullName;

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

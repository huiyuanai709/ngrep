using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class IndexBuildTests
{
    [Fact]
    public void Extract_matches_unique_invariant_lowercase_windows()
    {
        var text = "NetGrep \u0130ab\nAbCabc";
        var folded = text.ToLowerInvariant();
        var expect = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= folded.Length - 3; i++)
        {
            expect.Add(folded.Substring(i, 3));
        }

        Assert.Equal(expect, Trigrams.Extract(text));
    }

    [Fact]
    public void Nul_inside_the_probe_is_binary_and_past_it_is_text()
    {
        using var tree = Temp();
        var binary = new byte[9000];
        Array.Fill(binary, (byte)'a');
        binary[100] = 0;
        File.WriteAllBytes(tree.File("early.bin"), binary);

        var text = new byte[9000];
        Array.Fill(text, (byte)'b');
        text[8192] = 0;
        File.WriteAllBytes(tree.File("late.txt"), text);
        File.WriteAllText(tree.File("keep.txt"), "visible-token\n");

        var built = Indexer.Build(tree.Root, maxDegreeOfParallelism: 2);
        Assert.Equal(1, built.SkippedBinary);
        Assert.Equal(2, built.Documents);

        var hit = Searcher.Search(new SearchQuery { Pattern = "bbb", Path = tree.Root, MaxDegreeOfParallelism = 1 }).GetAwaiter().GetResult();
        Assert.Contains(hit.Matches, match => match.Path == "late.txt");
        Assert.Empty(Searcher.Search(new SearchQuery { Pattern = "aaa", Path = tree.Root, MaxDegreeOfParallelism = 1 }).GetAwaiter().GetResult().Matches);
    }

    [Fact]
    public void Cold_build_commits_once_and_callback_commits_each_path()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        File.WriteAllText(tree.File("b.txt"), "beta\n");

        var writes = 0;
        try
        {
            IndexStore.TestHookBeforeCommit = () => writes++;
            var built = Indexer.Build(tree.Root, showProgress: true, maxDegreeOfParallelism: 2);
            Assert.Equal(2, built.Added);
            Assert.Equal(1, writes);
        }
        finally
        {
            IndexStore.TestHookBeforeCommit = null;
        }

        writes = 0;
        var seen = new List<string>();
        try
        {
            IndexStore.TestHookBeforeCommit = () => writes++;
            Indexer.Build(tree.Root, onPersisted: (kind, path) => seen.Add(kind + " " + path), maxDegreeOfParallelism: 1);
        }
        finally
        {
            IndexStore.TestHookBeforeCommit = null;
        }

        Assert.Empty(seen);
        Assert.Equal(1, writes);

        File.Delete(tree.File("a.txt"));
        File.Delete(tree.File("b.txt"));
        writes = 0;
        try
        {
            IndexStore.TestHookBeforeCommit = () => writes++;
            var removed = Indexer.Build(tree.Root, maxDegreeOfParallelism: 2);
            Assert.Equal(2, removed.Removed);
            Assert.Equal(0, removed.Documents);
            Assert.Equal(1, writes);
        }
        finally
        {
            IndexStore.TestHookBeforeCommit = null;
        }
    }

    [Fact]
    public void Parallel_and_serial_builds_match()
    {
        using var leftTree = Temp();
        using var rightTree = Temp();
        for (var i = 0; i < 40; i++)
        {
            var body = "file-" + i.ToString("D4") + " token-" + (i % 7) + "\n";
            File.WriteAllText(leftTree.File($"d{i % 5}/f{i:D4}.txt"), body);
            File.WriteAllText(rightTree.File($"d{i % 5}/f{i:D4}.txt"), body);
        }

        File.WriteAllBytes(leftTree.File("d0/blob.bin"), [0, 1, 2, 3, 4]);
        File.WriteAllBytes(rightTree.File("d0/blob.bin"), [0, 1, 2, 3, 4]);

        var left = Indexer.Build(leftTree.Root, maxDegreeOfParallelism: 1);
        var right = Indexer.Build(rightTree.Root, maxDegreeOfParallelism: 4);
        Assert.Equal(left.Added, right.Added);
        Assert.Equal(left.SkippedBinary, right.SkippedBinary);

        var leftIndex = IndexStore.TryLoad(left.IndexPath)!;
        var rightIndex = IndexStore.TryLoad(right.IndexPath)!;
        Assert.Equal(
            leftIndex.Documents.Select(doc => doc.Path + ":" + doc.Id + ":" + doc.Sha256).OrderBy(line => line, StringComparer.Ordinal),
            rightIndex.Documents.Select(doc => doc.Path + ":" + doc.Id + ":" + doc.Sha256).OrderBy(line => line, StringComparer.Ordinal));
        Assert.Equal(leftIndex.Postings.Count, rightIndex.Postings.Count);
        foreach (var pair in leftIndex.Postings)
        {
            Assert.Equal(pair.Value, rightIndex.Postings[pair.Key]);
        }

        var hit = Searcher.Search(new SearchQuery { Pattern = "token-3", Path = rightTree.Root, MaxDegreeOfParallelism = 1 }).GetAwaiter().GetResult();
        Assert.Equal(6, hit.Matches.Count);
    }

    [Fact]
    public void Negative_worker_count_is_rejected()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        var ex = Assert.Throws<ArgumentException>(() => Indexer.Build(tree.Root, maxDegreeOfParallelism: -1));
        Assert.Contains("positive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-build-").FullName;

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

using System.Text;
using System.Text.Json;
using Ngrep.Indexing;
using Ngrep.Output;
using Ngrep.Search;
using Ngrep.Text;

namespace Ngrep.Tests;

public class IndexSearchTests
{
    [Fact]
    public void Line_map_is_one_based_and_counts_utf16_units()
    {
        var text = "alpha\nbeta netgrep gamma\n😀net\n";
        var starts = LineMap.Build(text);
        var netgrep = text.IndexOf("netgrep", StringComparison.Ordinal);
        var (line, column) = LineMap.Map(starts, netgrep);
        Assert.Equal(2, line);
        Assert.Equal(6, column);

        var emoji = text.IndexOf("net", netgrep + 1, StringComparison.Ordinal);
        var mapped = LineMap.Map(starts, emoji);
        Assert.Equal(3, mapped.Line);
        Assert.Equal(3, mapped.Column);
        Assert.Equal("😀net", LineMap.LineText(text, starts, 3));
    }

    [Fact]
    public void Index_and_search_return_path_line_column()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("src/App.cs"), "alpha\nbeta netgrep gamma\n");
        File.WriteAllText(tree.File("notes/readme.txt"), "nothing here\n");

        var built = Indexer.Build(tree.Root);
        Assert.Equal(2, built.Documents);
        Assert.Equal(2, built.Added);
        Assert.True(File.Exists(Path.Combine(built.IndexPath, "postings.bin")));
        Assert.True(File.Exists(Path.Combine(built.IndexPath, "documents.ndjson")));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(built.IndexPath, "manifest.json")));
        Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("invariant-lowercase", manifest.RootElement.GetProperty("caseFolding").GetString());
        Assert.Equal("utf16-code-unit", manifest.RootElement.GetProperty("columnUnit").GetString());

        var loaded = IndexStore.TryLoad(built.IndexPath);
        Assert.NotNull(loaded);
        Assert.Contains(loaded!.Postings.Keys, gram => gram == "net");
        var doc = Assert.Single(loaded.Documents, d => d.Path == "src/App.cs");
        Assert.Equal(new[] { 0, 6, 25 }, doc.LineStarts);

        var outcome = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.True(outcome.UsedIndex);
        var match = Assert.Single(outcome.Matches);
        Assert.Equal("src/App.cs", match.Path);
        Assert.Equal(2, match.Line);
        Assert.Equal(6, match.Column);
        Assert.Equal("netgrep", match.Text);
        Assert.Equal("beta netgrep gamma", match.LineText);
    }

    [Fact]
    public void Trigram_filter_excludes_files_missing_a_gram()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "abcdef");
        File.WriteAllText(tree.File("b.txt"), "abcxyz");
        Indexer.Build(tree.Root);

        var both = Searcher.Search(new SearchQuery { Pattern = "abc", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.Equal(2, both.Matches.Count);

        var one = Searcher.Search(new SearchQuery { Pattern = "xyz", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.Equal("b.txt", Assert.Single(one.Matches).Path);

        var none = Searcher.Search(new SearchQuery { Pattern = "abcxyzdef", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.Empty(none.Matches);
        Assert.True(none.UsedIndex);
    }

    [Fact]
    public void Short_pattern_scans_indexed_files()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "hi");
        Indexer.Build(tree.Root);
        var outcome = Searcher.Search(new SearchQuery { Pattern = "hi", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.True(outcome.UsedIndex);
        Assert.Equal(1, Assert.Single(outcome.Matches).Column);
    }

    [Fact]
    public void Search_load_skips_line_starts_and_unused_postings()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "alpha netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "other words here\n");
        var built = Indexer.Build(tree.Root);
        var full = IndexStore.TryLoad(built.IndexPath);
        Assert.NotNull(full);
        Assert.NotEmpty(full!.Documents[0].LineStarts);
        Assert.True(full.Postings.Count > 3);

        var grams = new HashSet<string>(Trigrams.ForQuery("netgrep"), StringComparer.Ordinal);
        var search = IndexStore.TryLoadForSearch(built.IndexPath, grams);
        Assert.NotNull(search);
        Assert.All(search!.Documents, doc => Assert.Empty(doc.LineStarts));
        Assert.NotEmpty(search.Postings);
        Assert.All(search.Postings.Keys, key => Assert.Contains(key, grams));
        Assert.True(search.Postings.Count < full.Postings.Count);

        var headerOnly = IndexStore.TryLoadForSearch(built.IndexPath, grams: null);
        Assert.NotNull(headerOnly);
        Assert.Empty(headerOnly!.Postings);
        Assert.All(headerOnly.Documents, doc => Assert.Empty(doc.LineStarts));
    }

    [Fact]
    public async Task Indexed_stream_emits_each_file_before_the_next_read()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");
        Indexer.Build(tree.Root);

        var started = new List<string>();
        var emitted = new List<string>();
        Searcher.BeforeConfirm = started.Add;
        try
        {
            var outcome = await Searcher.Search(new SearchQuery
            {
                Pattern = "netgrep",
                Path = tree.Root,
                MaxDegreeOfParallelism = 1,
                Events = new SearchEvents
                {
                    Batch = batch =>
                    {
                        if (batch.Path == "a.txt")
                        {
                            Assert.DoesNotContain("b.txt", started);
                        }

                        emitted.Add(batch.Path);
                    },
                },
            });

            Assert.True(outcome.Streamed);
            Assert.True(outcome.UsedIndex);
            Assert.Empty(outcome.Matches);
            Assert.Equal(2, outcome.MatchCount);
            Assert.Equal(["a.txt", "b.txt"], emitted);
            Assert.Equal(["a.txt", "b.txt"], started);
        }
        finally
        {
            Searcher.BeforeConfirm = null;
        }
    }

    [Fact]
    public void Case_sensitive_verification_uses_folded_index()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "NetGrep");
        Indexer.Build(tree.Root);

        var sensitive = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.Empty(sensitive.Matches);

        var folded = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = tree.Root, IgnoreCase = true }).GetAwaiter().GetResult();
        Assert.Equal("NetGrep", Assert.Single(folded.Matches).Text);

        var exact = Searcher.Search(new SearchQuery { Pattern = "NetGrep", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.Single(exact.Matches);
    }

    [Fact]
    public void Regex_search_reports_match_text()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "netgrep");
        Indexer.Build(tree.Root);
        var outcome = Searcher.Search(new SearchQuery { Pattern = "n.tgrep", Path = tree.Root, Regex = true }).GetAwaiter().GetResult();
        Assert.Equal("netgrep", Assert.Single(outcome.Matches).Text);
    }

    [Fact]
    public void Ignore_binary_and_git_metadata_are_skipped()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("keep.txt"), "visible netgrep");
        File.WriteAllText(tree.File(".git/config"), "hidden netgrep-git");
        File.WriteAllBytes(tree.File("blob.bin"), Encoding.UTF8.GetBytes("hidden netgrep\0"));
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("secret.log"), "hidden netgrep-log");
        File.WriteAllText(tree.File("nested/.gitignore"), "*.tmp\n!keep.tmp\n");
        File.WriteAllText(tree.File("nested/skip.tmp"), "hidden netgrep-tmp");
        File.WriteAllText(tree.File("nested/keep.tmp"), "kept netgrep");

        var built = Indexer.Build(tree.Root);
        Assert.Equal(1, built.SkippedBinary);
        Assert.True(built.SkippedIgnored >= 2);

        var outcome = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = tree.Root }).GetAwaiter().GetResult();
        var paths = outcome.Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "keep.txt", "nested/keep.tmp" }, paths);
        Assert.Empty(Searcher.Search(new SearchQuery { Pattern = "netgrep-log", Path = tree.Root }).GetAwaiter().GetResult().Matches);
        Assert.Empty(Searcher.Search(new SearchQuery { Pattern = "netgrep-git", Path = tree.Root }).GetAwaiter().GetResult().Matches);
    }

    [Fact]
    public void Incremental_update_keeps_unchanged_files_and_drops_deleted_ones()
    {
        using var tree = new TempTree();
        var file = tree.File("src/App.cs");
        File.WriteAllText(file, "alpha\nbeta netgrep gamma\n");
        var first = Indexer.Build(tree.Root);
        Assert.Equal(1, first.Added);

        var second = Indexer.Build(tree.Root);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(1, second.Documents);

        File.WriteAllText(file, "alpha\nbeta netgrep gamma\nextra needle\n");
        var third = Indexer.Build(tree.Root);
        Assert.Equal(1, third.Updated);
        Assert.Contains(Searcher.Search(new SearchQuery { Pattern = "needle", Path = tree.Root }).GetAwaiter().GetResult().Matches, m => m.Line == 3);

        File.Delete(file);
        var fourth = Indexer.Build(tree.Root);
        Assert.Equal(1, fourth.Removed);
        Assert.Equal(0, fourth.Documents);
        Assert.Empty(Searcher.Search(new SearchQuery { Pattern = "needle", Path = tree.Root }).GetAwaiter().GetResult().Matches);
    }

    [Fact]
    public void Missing_index_falls_back_to_content_scan()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "netgrep");
        var outcome = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = tree.Root }).GetAwaiter().GetResult();
        Assert.False(outcome.UsedIndex);
        Assert.NotNull(outcome.Note);
        Assert.Equal(1, Assert.Single(outcome.Matches).Line);
    }

    [Fact]
    public void Subdirectory_search_keeps_index_root_paths()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("src/App.cs"), "netgrep");
        File.WriteAllText(tree.File("other.txt"), "netgrep");
        Indexer.Build(tree.Root);

        var outcome = Searcher.Search(new SearchQuery { Pattern = "netgrep", Path = Path.Combine(tree.Root, "src") }).GetAwaiter().GetResult();
        Assert.True(outcome.UsedIndex);
        Assert.Equal("src/App.cs", Assert.Single(outcome.Matches).Path);
    }

    [Fact]
    public void Corrupt_postings_are_rejected()
    {
        using var tree = new TempTree();
        File.WriteAllText(tree.File("a.txt"), "netgrep");
        var built = Indexer.Build(tree.Root);
        File.WriteAllBytes(Path.Combine(built.IndexPath, "postings.bin"), "XXXX"u8.ToArray());
        var ex = Assert.Throws<InvalidDataException>(() => IndexStore.TryLoad(built.IndexPath));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ndjson_fields_are_stable()
    {
        var json = MachineOutput.MatchJson(new SearchMatch("src/App.cs", 2, 6, "netgrep", "beta netgrep"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("src/App.cs", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(6, doc.RootElement.GetProperty("column").GetInt32());
        Assert.Equal("netgrep", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("beta netgrep", doc.RootElement.GetProperty("lineText").GetString());
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Directory.CreateTempSubdirectory("ngrep-").FullName;
        }

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

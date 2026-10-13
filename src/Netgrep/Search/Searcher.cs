using Netgrep.Ignore;
using Netgrep.Indexing;
using Netgrep.Text;

namespace Netgrep.Search;

internal readonly record struct ContextLine(int Line, string Text);

internal readonly record struct SearchMatch(
    string Path,
    int Line,
    int Column,
    string Text,
    string LineText,
    IReadOnlyList<ContextLine>? Before = null,
    IReadOnlyList<ContextLine>? After = null);

internal sealed class SearchQuery
{
    public required string Pattern { get; init; }

    public IReadOnlyList<string> AdditionalPatterns { get; init; } = [];

    public required string Path { get; init; }

    public string? IndexDirectory { get; init; }

    public bool IgnoreCase { get; init; }

    public bool Regex { get; init; }

    public bool ForceScan { get; init; }

    public bool WordRegexp { get; init; }

    public bool InvertMatch { get; init; }

    public int BeforeContext { get; init; }

    public int AfterContext { get; init; }

    public IReadOnlyList<string> Globs { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> TypeNots { get; init; } = [];

    public IReadOnlyList<string> TypeAdds { get; init; } = [];

    /// <summary>0 uses <c>NETGREP_THREADS</c> or <see cref="Environment.ProcessorCount"/>.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

internal sealed class SearchOutcome
{
    public required IReadOnlyList<SearchMatch> Matches { get; init; }

    public required bool UsedIndex { get; init; }

    public string? Note { get; init; }

    /// <summary><c>parallel</c> when confirmation used more than one worker; otherwise <c>sequential</c>.</summary>
    public string Confirmation { get; init; } = "sequential";

    public int Workers { get; init; } = 1;
}

internal static class Searcher
{
    public static SearchOutcome Search(SearchQuery query)
    {
        query.CancellationToken.ThrowIfCancellationRequested();
        var plan = MatchPlan.Create(query);
        var workers = ResolveWorkers(query);
        var start = Path.GetFullPath(query.Path);
        if (!File.Exists(start) && !Directory.Exists(start))
        {
            throw new DirectoryNotFoundException($"Path not found: {start}");
        }

        if (!query.ForceScan && query.IndexDirectory != null)
        {
            var explicitIndex = IndexStore.TryLoad(query.IndexDirectory)
                ?? throw new InvalidDataException($"Index not found at {Path.GetFullPath(query.IndexDirectory)}.");
            return SearchIndex(explicitIndex, start, query, plan, workers);
        }

        if (!query.ForceScan)
        {
            var discovered = IndexPaths.Discover(start);
            if (discovered != null)
            {
                var index = IndexStore.TryLoad(discovered);
                if (index != null)
                {
                    return SearchIndex(index, start, query, plan, workers);
                }
            }
        }

        var note = query.ForceScan
            ? "content scan (--scan)"
            : "no index found; scanned content";
        var scanned = ContentScan(start, plan, workers, query.CancellationToken);
        return Finish(scanned.Matches, usedIndex: false, note, scanned.Workers);
    }

    private static int ResolveWorkers(SearchQuery query)
    {
        if (query.MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentException("--threads must be a positive integer.");
        }

        if (query.MaxDegreeOfParallelism > 0)
        {
            return query.MaxDegreeOfParallelism;
        }

        var env = Environment.GetEnvironmentVariable("NETGREP_THREADS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (!int.TryParse(env, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new ArgumentException("NETGREP_THREADS must be a positive integer.");
            }

            return parsed;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static SearchOutcome SearchIndex(IndexData index, string start, SearchQuery query, MatchPlan plan, int workers)
    {
        var root = index.Manifest.Root;
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Indexed root does not exist: {root}");
        }

        var prefix = RelativeTo(root, start, out var outside, out var prefixIsFile);
        if (outside)
        {
            var scanned = ContentScan(start, plan, workers, query.CancellationToken);
            return Finish(scanned.Matches, usedIndex: false, "path is outside the indexed root; scanned content", scanned.Workers);
        }

        IEnumerable<DocumentRecord> candidates = index.Documents;
        if (plan.CanUseTrigrams)
        {
            var ids = new HashSet<int>();
            foreach (var pattern in plan.Patterns)
            {
                ids.UnionWith(Intersect(index.Postings, Trigrams.ForQuery(pattern)));
            }

            candidates = index.Documents.Where(document => ids.Contains(document.Id));
        }

        if (prefix.Length > 0)
        {
            candidates = candidates.Where(document => Under(document.Path, prefix, prefixIsFile));
        }

        var files = new List<ConfirmFile>();
        foreach (var document in candidates)
        {
            if (!plan.Filter.Accepts(document.Path))
            {
                continue;
            }

            var full = Path.Combine(root, document.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            files.Add(new ConfirmFile(full, document.Path));
        }

        var degree = files.Count > 1 ? workers : 1;
        var matches = ConfirmScheduler.Run(
            files,
            degree,
            query.CancellationToken,
            (file, local) => TryCollect(file, plan, local, stats: null, gateBinary: false));
        return Finish(matches, usedIndex: true, note: null, degree);
    }

    private static (List<SearchMatch> Matches, int Workers) ContentScan(string start, MatchPlan plan, int workers, CancellationToken cancellation)
    {
        if (File.Exists(start))
        {
            var parent = Path.GetDirectoryName(start) ?? start;
            var relative = Path.GetFileName(start);
            var matches = new List<SearchMatch>();
            if (!IsIgnoredFile(parent, start) && plan.Filter.Accepts(relative))
            {
                TryCollect(new ConfirmFile(start, relative), plan, matches, stats: null, gateBinary: true);
            }

            return (matches, 1);
        }

        var stats = new WalkStats();
        var degree = workers > 1 ? workers : 1;
        var found = ConfirmScheduler.Run(
            Enumerate(start, plan, stats),
            degree,
            cancellation,
            (file, local) => TryCollect(file, plan, local, stats, gateBinary: true));
        return (found, degree);
    }

    private static IEnumerable<ConfirmFile> Enumerate(string start, MatchPlan plan, WalkStats stats)
    {
        foreach (var file in TreeWalker.Enumerate(start, IndexPaths.DefaultFor(start), stats))
        {
            if (!plan.Filter.Accepts(file.RelativePath))
            {
                continue;
            }

            yield return new ConfirmFile(file.FullPath, file.RelativePath);
        }
    }

    private static bool IsIgnoredFile(string walkRoot, string fullPath)
    {
        var stats = new WalkStats();
        return !TreeWalker.Enumerate(walkRoot, null, stats).Any(file =>
            string.Equals(file.FullPath, fullPath, StringComparison.Ordinal));
    }

    private static void TryCollect(ConfirmFile file, MatchPlan plan, List<SearchMatch> matches, WalkStats? stats, bool gateBinary)
    {
        try
        {
            if (gateBinary)
            {
                var info = new FileInfo(file.FullPath);
                if (info.Length > TextFiles.MaxFileBytes)
                {
                    stats?.IncrementSkippedTooLarge();
                    return;
                }

                var bytes = File.ReadAllBytes(file.FullPath);
                if (TextFiles.IsBinary(bytes))
                {
                    stats?.IncrementSkippedBinary();
                    return;
                }

                MatchPlan.Collect(TextFiles.Decode(bytes), file.RelativePath, plan, matches);
                return;
            }

            MatchPlan.Collect(TextFiles.Decode(File.ReadAllBytes(file.FullPath)), file.RelativePath, plan, matches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats?.IncrementSkippedUnreadable();
        }
    }

    private static SearchOutcome Finish(List<SearchMatch> matches, bool usedIndex, string? note, int workers)
    {
        matches.Sort(CompareMatches);
        return new SearchOutcome
        {
            Matches = matches,
            UsedIndex = usedIndex,
            Note = note,
            Confirmation = workers > 1 ? "parallel" : "sequential",
            Workers = workers,
        };
    }

    private static HashSet<int> Intersect(Dictionary<string, List<int>> postings, List<string> grams)
    {
        HashSet<int>? acc = null;
        foreach (var gram in grams.Distinct(StringComparer.Ordinal))
        {
            if (!postings.TryGetValue(gram, out var list) || list.Count == 0)
            {
                return [];
            }

            if (acc == null)
            {
                acc = list.ToHashSet();
                continue;
            }

            acc.IntersectWith(list);
            if (acc.Count == 0)
            {
                return acc;
            }
        }

        return acc ?? [];
    }

    private static string RelativeTo(string root, string start, out bool outside, out bool prefixIsFile)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        start = Path.GetFullPath(start);
        prefixIsFile = File.Exists(start);
        if (string.Equals(root, start.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            outside = false;
            return "";
        }

        var rel = Path.GetRelativePath(root, start).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            outside = true;
            return "";
        }

        outside = false;
        return rel == "." ? "" : rel;
    }

    private static bool Under(string docPath, string prefix, bool prefixIsFile)
    {
        if (prefix.Length == 0)
        {
            return true;
        }

        if (prefixIsFile)
        {
            return docPath.Equals(prefix, StringComparison.Ordinal);
        }

        return docPath.Equals(prefix, StringComparison.Ordinal)
            || docPath.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static int CompareMatches(SearchMatch left, SearchMatch right)
    {
        var path = string.CompareOrdinal(left.Path, right.Path);
        if (path != 0)
        {
            return path;
        }

        var line = left.Line.CompareTo(right.Line);
        if (line != 0)
        {
            return line;
        }

        var column = left.Column.CompareTo(right.Column);
        return column != 0 ? column : left.Text.Length.CompareTo(right.Text.Length);
    }
}

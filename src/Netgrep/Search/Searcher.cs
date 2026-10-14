using System.Collections.Concurrent;
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

internal enum ReportMode
{
    Matches,
    FilesWithMatches,
    FilesWithoutMatch,
    Count,
}

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

    public bool NoIgnore { get; init; }

    public bool NoIgnoreVcs { get; init; }

    public bool IncludeHidden { get; init; }

    public bool Multiline { get; init; }

    public bool MultilineDotAll { get; init; }

    public ReportMode Report { get; init; }

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

    public bool UsedTrigramPrefilter { get; init; }

    public string? Note { get; init; }

    /// <summary><c>parallel</c> when confirmation used more than one worker; otherwise <c>sequential</c>.</summary>
    public string Confirmation { get; init; } = "sequential";

    public int Workers { get; init; } = 1;

    public IReadOnlyList<string> FilesWithoutMatch { get; init; } = [];
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

        if (HiddenPaths.ContainsHardSkip(start))
        {
            return Finish([], usedIndex: false, note: null, workers: 1, usedPrefilter: false, without: null);
        }

        var skipIndex = query.ForceScan || !plan.ApplyGitIgnore;
        if (!skipIndex && query.IndexDirectory != null)
        {
            var explicitIndex = IndexStore.TryLoad(query.IndexDirectory)
                ?? throw new InvalidDataException($"Index not found at {Path.GetFullPath(query.IndexDirectory)}.");
            return SearchIndex(explicitIndex, start, query, plan, workers);
        }

        if (!skipIndex)
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

        var note = ScanNote(query, indexSkipped: skipIndex && !query.ForceScan && IndexWouldApply(start, query));
        var scanned = ContentScan(start, plan, workers, query.CancellationToken);
        return Finish(scanned.Matches, usedIndex: false, note, scanned.Workers, usedPrefilter: false, scanned.Without);
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
            return Finish(scanned.Matches, usedIndex: false, "path is outside the indexed root; scanned content", scanned.Workers, usedPrefilter: false, scanned.Without);
        }

        HashSet<int>? selectedIds = null;
        var usedPrefilter = false;
        if (plan.CanUseTrigrams && plan.Literals!.TrySelect(index.Postings, out var ids))
        {
            usedPrefilter = true;
            selectedIds = ids;
        }

        var files = new List<ConfirmFile>();
        var without = plan.Report == ReportMode.FilesWithoutMatch ? new ConcurrentBag<string>() : null;
        foreach (var document in index.Documents)
        {
            if (prefix.Length > 0 && !Under(document.Path, prefix, prefixIsFile))
            {
                continue;
            }

            if (!plan.IncludeHidden && HiddenPaths.IsHiddenBelow(document.Path, prefix))
            {
                continue;
            }

            if (!plan.Filter.Accepts(document.Path))
            {
                continue;
            }

            var full = Path.Combine(root, document.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            if (selectedIds != null && !selectedIds.Contains(document.Id))
            {
                // A required literal is absent, so the file cannot match.
                if (without != null)
                {
                    without.Add(document.Path);
                }

                continue;
            }

            files.Add(new ConfirmFile(full, document.Path));
        }

        var degree = files.Count > 1 ? workers : 1;
        var matches = ConfirmScheduler.Run(
            files,
            degree,
            query.CancellationToken,
            (file, local) => TryCollect(file, plan, local, stats: null, without));
        return Finish(matches, usedIndex: true, note: null, degree, usedPrefilter, without);
    }

    private static bool IndexWouldApply(string start, SearchQuery query)
    {
        if (query.IndexDirectory != null)
        {
            return IndexStore.TryLoad(query.IndexDirectory) != null;
        }

        var discovered = IndexPaths.Discover(start);
        return discovered != null && IndexStore.TryLoad(discovered) != null;
    }

    private static string ScanNote(SearchQuery query, bool indexSkipped)
    {
        var ignore = query.NoIgnore ? "--no-ignore" : query.NoIgnoreVcs ? "--no-ignore-vcs" : null;
        if (query.ForceScan && ignore != null)
        {
            return $"content scan (--scan, {ignore})";
        }

        if (query.ForceScan)
        {
            return "content scan (--scan)";
        }

        if (indexSkipped && ignore != null)
        {
            return $"scanned content ({ignore})";
        }

        if (ignore != null)
        {
            return $"no index found; scanned content ({ignore})";
        }

        return "no index found; scanned content";
    }

    private static (List<SearchMatch> Matches, int Workers, ConcurrentBag<string>? Without) ContentScan(
        string start,
        MatchPlan plan,
        int workers,
        CancellationToken cancellation)
    {
        var without = plan.Report == ReportMode.FilesWithoutMatch ? new ConcurrentBag<string>() : null;
        if (File.Exists(start))
        {
            var parent = Path.GetDirectoryName(start) ?? start;
            var relative = Path.GetFileName(start);
            var matches = new List<SearchMatch>();
            // includeHidden so a path the user named is not treated as skipped merely for being a dotfile.
            if ((!plan.ApplyGitIgnore || !IsIgnoredFile(parent, start)) && plan.Filter.Accepts(relative))
            {
                TryCollect(new ConfirmFile(start, relative), plan, matches, stats: null, without);
            }

            return (matches, 1, without);
        }

        var stats = new WalkStats();
        var degree = workers > 1 ? workers : 1;
        var found = ConfirmScheduler.Run(
            Enumerate(start, plan, stats),
            degree,
            cancellation,
            (file, local) => TryCollect(file, plan, local, stats, without));
        return (found, degree, without);
    }

    private static IEnumerable<ConfirmFile> Enumerate(string start, MatchPlan plan, WalkStats stats)
    {
        foreach (var file in TreeWalker.Enumerate(start, IndexPaths.DefaultFor(start), stats, plan.ApplyGitIgnore, plan.IncludeHidden))
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
        return !TreeWalker.Enumerate(walkRoot, null, stats, applyGitIgnore: true, includeHidden: true).Any(file =>
            string.Equals(file.FullPath, fullPath, StringComparison.Ordinal));
    }

    private static void TryCollect(
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        WalkStats? stats,
        ConcurrentBag<string>? filesWithoutMatch)
    {
        string text;
        using (var read = TextFiles.Read(file.FullPath))
        {
            switch (read.Kind)
            {
                case ReadKind.TooLarge:
                    stats?.IncrementSkippedTooLarge();
                    return;
                case ReadKind.Binary:
                    stats?.IncrementSkippedBinary();
                    return;
                case ReadKind.Unreadable:
                    stats?.IncrementSkippedUnreadable();
                    return;
            }

            if (!plan.Invert && plan.Literals != null && plan.Literals.Misses(read.Span, plan.IgnoreCase))
            {
                filesWithoutMatch?.Add(file.RelativePath);
                return;
            }

            text = TextFiles.Decode(read.Span);
        }

        if (plan.Report == ReportMode.FilesWithoutMatch)
        {
            if (!MatchPlan.HasMatch(text, plan))
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            return;
        }

        MatchPlan.Collect(text, file.RelativePath, plan, matches);
    }

    private static SearchOutcome Finish(
        List<SearchMatch> matches,
        bool usedIndex,
        string? note,
        int workers,
        bool usedPrefilter,
        ConcurrentBag<string>? without)
    {
        matches.Sort(CompareMatches);
        return new SearchOutcome
        {
            Matches = matches,
            UsedIndex = usedIndex,
            Note = note,
            UsedTrigramPrefilter = usedPrefilter,
            Confirmation = workers > 1 ? "parallel" : "sequential",
            Workers = workers,
            FilesWithoutMatch = NormalizeWithout(without),
        };
    }

    private static string[] NormalizeWithout(ConcurrentBag<string>? without)
    {
        if (without == null || without.IsEmpty)
        {
            return [];
        }

        return without.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
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

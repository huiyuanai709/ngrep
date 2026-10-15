using System.Collections.Concurrent;
using System.Diagnostics;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Text;

namespace Ngrep.Search;

internal readonly record struct ContextLine(int Line, string Text, int Offset = 0);

internal readonly record struct SearchMatch(
    string Path,
    int Line,
    int Column,
    string Text,
    string LineText,
    IReadOnlyList<ContextLine>? Before = null,
    IReadOnlyList<ContextLine>? After = null,
    int Offset = 0);

internal enum ReportMode
{
    Matches,
    FilesWithMatches,
    FilesWithoutMatch,
    Count,
    Quiet,
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

    /// <summary>Whole-line match (<c>-x</c>). The line text, with a trailing CR removed, must match.</summary>
    public bool LineRegexp { get; init; }

    /// <summary>Suppress missing-path and named-unreadable diagnostics (<c>-s</c>). Exit code stays 2.</summary>
    public bool NoMessages { get; init; }

    public bool NoIgnore { get; init; }

    public bool NoIgnoreVcs { get; init; }

    public bool IncludeHidden { get; init; }

    public bool Multiline { get; init; }

    public bool MultilineDotAll { get; init; }

    /// <summary><c>-P</c> / <c>--pcre</c>. Backtracking interpreter instead of the non-backtracking engine.</summary>
    public bool Backtracking { get; init; }

    public ReportMode Report { get; init; }

    /// <summary>When set, <c>-l</c> does not stop at the first hit, so line stats are complete.</summary>
    public bool Stats { get; init; }

    /// <summary>
    /// When false, matches keep path and line numbers and leave <c>text</c> / <c>lineText</c> empty.
    /// Default true. The CLI turns this off for reports that do not print those strings.
    /// </summary>
    public bool CaptureText { get; init; } = true;

    public int BeforeContext { get; init; }

    public int AfterContext { get; init; }

    public IReadOnlyList<string> Globs { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> TypeNots { get; init; } = [];

    public IReadOnlyList<string> TypeAdds { get; init; } = [];

    /// <summary>0 uses <c>NGREP_THREADS</c>, then <c>NETGREP_THREADS</c>, or <see cref="Environment.ProcessorCount"/>.</summary>
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

    public int FilesSearched { get; init; }

    public int FilesMatched { get; init; }

    public int LinesMatched { get; init; }

    public long ElapsedMilliseconds { get; init; }
}

internal sealed class SearchTally
{
    private int _searched;
    private int _matched;

    public int FilesSearched => Volatile.Read(ref _searched);

    public int FilesMatched => Volatile.Read(ref _matched);

    public void File(bool matched)
    {
        Interlocked.Increment(ref _searched);
        if (matched)
        {
            Interlocked.Increment(ref _matched);
        }
    }
}

internal static class Searcher
{
    public static async Task<SearchOutcome> Search(SearchQuery query)
    {
        query.CancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var tally = new SearchTally();
        var plan = MatchPlan.Create(query);
        var workers = ResolveWorkers(query);
        var start = Path.GetFullPath(query.Path);
        if (!File.Exists(start) && !Directory.Exists(start))
        {
            throw new SearchPathException($"Path not found: {start}", query.NoMessages);
        }

        if (HiddenPaths.ContainsHardSkip(start))
        {
            return Finish([], usedIndex: false, note: null, workers: 1, usedPrefilter: false, without: null, tally, clock);
        }

        var skipIndex = query.ForceScan || !plan.ApplyGitIgnore;
        if (!skipIndex && query.IndexDirectory != null)
        {
            var explicitIndex = IndexStore.TryLoad(query.IndexDirectory)
                ?? throw new InvalidDataException($"Index not found at {Path.GetFullPath(query.IndexDirectory)}.");
            return await SearchIndex(explicitIndex, start, query, plan, workers, tally, clock);
        }

        if (!skipIndex)
        {
            var discovered = IndexPaths.Discover(start);
            if (discovered != null)
            {
                var index = IndexStore.TryLoad(discovered);
                if (index != null)
                {
                    return await SearchIndex(index, start, query, plan, workers, tally, clock);
                }
            }
        }

        var note = ScanNote(query, indexSkipped: skipIndex && !query.ForceScan && IndexWouldApply(start, query));
        var scanned = await ContentScan(start, plan, workers, query.CancellationToken, tally);
        return Finish(scanned.Matches, usedIndex: false, note, scanned.Workers, usedPrefilter: false, scanned.Without, tally, clock);
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

        var env = Environment.GetEnvironmentVariable("NGREP_THREADS");
        var name = "NGREP_THREADS";
        if (string.IsNullOrWhiteSpace(env))
        {
            env = Environment.GetEnvironmentVariable("NETGREP_THREADS");
            name = "NETGREP_THREADS";
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            if (!int.TryParse(env, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new ArgumentException($"{name} must be a positive integer.");
            }

            return parsed;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static async Task<SearchOutcome> SearchIndex(IndexData index, string start, SearchQuery query, MatchPlan plan, int workers, SearchTally tally, Stopwatch clock)
    {
        var root = index.Manifest.Root;
        if (!Directory.Exists(root))
        {
            throw new SearchPathException($"Indexed root does not exist: {root}", query.NoMessages);
        }

        var prefix = RelativeTo(root, start, out var outside, out var prefixIsFile);
        if (outside)
        {
            var scanned = await ContentScan(start, plan, workers, query.CancellationToken, tally);
            return Finish(scanned.Matches, usedIndex: false, "path is outside the indexed root; scanned content", scanned.Workers, usedPrefilter: false, scanned.Without, tally, clock);
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
        var stopOnMatch = plan.Report == ReportMode.Quiet && plan.StopAtFirst;
        var matches = await AsyncScheduler.Run(
            files,
            degree,
            query.CancellationToken,
            (file, local, token) => TryCollectAsync(file, plan, local, stats: null, without, tally, token, errorIfUnreadable: prefixIsFile),
            stopOnMatch);

        return Finish(matches, usedIndex: true, note: null, degree, usedPrefilter, without, tally, clock);
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

    private static async Task<(List<SearchMatch> Matches, int Workers, ConcurrentBag<string>? Without)> ContentScan(
        string start,
        MatchPlan plan,
        int workers,
        CancellationToken cancellation,
        SearchTally tally)
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
                await TryCollectAsync(new ConfirmFile(start, relative), plan, matches, stats: null, without, tally, cancellation, errorIfUnreadable: true);
            }

            return (matches, 1, without);
        }

        var stats = new WalkStats();
        var degree = workers > 1 ? workers : 1;
        var stopOnMatch = plan.Report == ReportMode.Quiet && plan.StopAtFirst;
        var found = await AsyncScheduler.Scan(
            start,
            IndexPaths.DefaultFor(start),
            stats,
            plan.ApplyGitIgnore,
            plan.IncludeHidden,
            plan.Filter,
            degree,
            cancellation,
            (file, local, token) => TryCollectAsync(file, plan, local, stats, without, tally, token),
            stopOnMatch);

        return (found, degree, without);
    }

    private static bool IsIgnoredFile(string walkRoot, string fullPath)
    {
        var stats = new WalkStats();
        return !TreeWalker.Enumerate(walkRoot, null, stats, applyGitIgnore: true, includeHidden: true).Any(file =>
            string.Equals(file.FullPath, fullPath, StringComparison.Ordinal));
    }

    private static async ValueTask TryCollectAsync(
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        WalkStats? stats,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        CancellationToken cancellation,
        bool errorIfUnreadable = false)
    {
        var stop = ByteConfirm.ArmFresh(plan);
        try
        {
            using var read = await TextFiles.ReadAsync(file.FullPath, stop, cancellation);
            ByteConfirm.Adopt(stop);
            if (MatchLoaded(read, file, plan, matches, stats, filesWithoutMatch, tally, errorIfUnreadable, out var text))
            {
                return;
            }

            CollectDecoded(text, file, plan, matches, filesWithoutMatch, tally);
        }
        finally
        {
            ByteConfirm.Disarm(stop);
        }
    }

    /// <summary>True when the file is finished. False when <paramref name="text"/> must be matched as a string.</summary>
    private static bool MatchLoaded(
        PooledBytes read,
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        WalkStats? stats,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        bool errorIfUnreadable,
        out string text)
    {
        text = "";
        switch (read.Kind)
        {
            case ReadKind.TooLarge:
                stats?.IncrementSkippedTooLarge();
                return true;
            case ReadKind.Binary:
                stats?.IncrementSkippedBinary();
                return true;
            case ReadKind.Unreadable:
                stats?.IncrementSkippedUnreadable();
                if (errorIfUnreadable)
                {
                    throw new SearchPathException($"Unreadable: {file.FullPath}", plan.NoMessages);
                }

                return true;
        }

        if (ByteConfirm.TryHandle(read.Span, file.RelativePath, plan, matches, out var byteMatched))
        {
            tally.File(byteMatched);
            if (!byteMatched)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            return true;
        }

        // The byte automaton already scans. A literal prefilter would read the file again.
        if (plan.BytePrograms == null && !plan.Invert && plan.Literals != null && plan.Literals.Misses(read.Span, plan.IgnoreCase))
        {
            tally.File(matched: false);
            filesWithoutMatch?.Add(file.RelativePath);
            return true;
        }

        if (MatchPlan.TryCollectAutomaton(read.Span, file.RelativePath, plan, matches, out var automatonMatched))
        {
            tally.File(automatonMatched);
            if (!automatonMatched)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            return true;
        }

        if (MatchPlan.TryCollectBytes(read.Span, file.RelativePath, plan, matches, out var prefiltered))
        {
            tally.File(prefiltered);
            if (!prefiltered)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            return true;
        }

        text = TextFiles.Decode(read.Span);
        return false;
    }

    private static void CollectDecoded(
        string text,
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally)
    {
        if (plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll)
        {
            var hit = MatchPlan.HasMatch(text, plan);
            tally.File(hit);
            if (!hit)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            return;
        }

        var before = matches.Count;
        MatchPlan.Collect(text, file.RelativePath, plan, matches);
        var matched = matches.Count > before;
        tally.File(matched);
        if (plan.Report == ReportMode.FilesWithoutMatch && !matched)
        {
            filesWithoutMatch?.Add(file.RelativePath);
        }
    }

    private static SearchOutcome Finish(
        List<SearchMatch> matches,
        bool usedIndex,
        string? note,
        int workers,
        bool usedPrefilter,
        ConcurrentBag<string>? without,
        SearchTally tally,
        Stopwatch clock)
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
            FilesSearched = tally.FilesSearched,
            FilesMatched = tally.FilesMatched,
            LinesMatched = CountMatchedLines(matches),
            ElapsedMilliseconds = clock.ElapsedMilliseconds,
        };
    }

    private static int CountMatchedLines(List<SearchMatch> matches)
    {
        var count = 0;
        string? path = null;
        var line = 0;
        foreach (var match in matches)
        {
            if (!string.Equals(path, match.Path, StringComparison.Ordinal) || line != match.Line)
            {
                count++;
                path = match.Path;
                line = match.Line;
            }
        }

        return count;
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

/// <summary>
/// The search path is missing, or a file the user named cannot be read.
/// <see cref="Suppress"/> is <c>-s</c>: the message stays off stderr and the exit code is still 2.
/// </summary>
internal sealed class SearchPathException : Exception
{
    public SearchPathException(string message, bool suppress)
        : base(message)
    {
        Suppress = suppress;
    }

    public bool Suppress { get; }
}

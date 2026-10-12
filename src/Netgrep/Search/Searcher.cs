using System.Text.RegularExpressions;
using Netgrep.Ignore;
using Netgrep.Indexing;
using Netgrep.Text;

namespace Netgrep.Search;

internal readonly record struct SearchMatch(string Path, int Line, int Column, string Text, string LineText);

internal sealed class SearchQuery
{
    public required string Pattern { get; init; }

    public required string Path { get; init; }

    public string? IndexDirectory { get; init; }

    public bool IgnoreCase { get; init; }

    public bool Regex { get; init; }

    public bool ForceScan { get; init; }
}

internal sealed class SearchOutcome
{
    public required IReadOnlyList<SearchMatch> Matches { get; init; }

    public required bool UsedIndex { get; init; }

    public string? Note { get; init; }
}

internal static class Searcher
{
    public static SearchOutcome Search(SearchQuery query)
    {
        if (query.Pattern.Length == 0)
        {
            throw new ArgumentException("Pattern is empty.");
        }

        if (query.Regex)
        {
            _ = new Regex(query.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        }

        var start = Path.GetFullPath(query.Path);
        if (!File.Exists(start) && !Directory.Exists(start))
        {
            throw new DirectoryNotFoundException($"Path not found: {start}");
        }

        if (!query.ForceScan && query.IndexDirectory != null)
        {
            var explicitIndex = IndexStore.TryLoad(query.IndexDirectory)
                ?? throw new InvalidDataException($"Index not found at {Path.GetFullPath(query.IndexDirectory)}.");
            return SearchIndex(explicitIndex, start, query);
        }

        if (!query.ForceScan)
        {
            var discovered = IndexPaths.Discover(start);
            if (discovered != null)
            {
                var index = IndexStore.TryLoad(discovered);
                if (index != null)
                {
                    return SearchIndex(index, start, query);
                }
            }
        }

        var note = query.ForceScan
            ? "content scan (--scan)"
            : "no index found; scanned content";
        return new SearchOutcome
        {
            Matches = ContentScan(start, query),
            UsedIndex = false,
            Note = note,
        };
    }

    private static SearchOutcome SearchIndex(IndexData index, string start, SearchQuery query)
    {
        var root = index.Manifest.Root;
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Indexed root does not exist: {root}");
        }

        var prefix = RelativeTo(root, start, out var outside, out var prefixIsFile);
        if (outside)
        {
            return new SearchOutcome
            {
                Matches = ContentScan(start, query),
                UsedIndex = false,
                Note = "path is outside the indexed root; scanned content",
            };
        }

        IEnumerable<DocumentRecord> candidates = index.Documents;
        var grams = query.Regex || query.Pattern.Length < Trigrams.Length
            ? []
            : Trigrams.ForQuery(query.Pattern);
        if (grams.Count > 0)
        {
            var ids = Intersect(index.Postings, grams);
            candidates = index.Documents.Where(d => ids.Contains(d.Id));
        }

        if (prefix.Length > 0)
        {
            candidates = candidates.Where(d => Under(d.Path, prefix, prefixIsFile));
        }

        var matches = new List<SearchMatch>();
        foreach (var document in candidates.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var full = Path.Combine(root, document.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            string text;
            try
            {
                text = TextFiles.Decode(File.ReadAllBytes(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Collect(text, document.Path, query, matches);
        }

        matches.Sort(CompareMatches);
        return new SearchOutcome
        {
            Matches = matches,
            UsedIndex = true,
            Note = null,
        };
    }

    private static List<SearchMatch> ContentScan(string start, SearchQuery query)
    {
        var matches = new List<SearchMatch>();
        var stats = new WalkStats();
        if (File.Exists(start))
        {
            var parent = Path.GetDirectoryName(start) ?? start;
            var relative = Path.GetFileName(start);
            if (!IsIgnoredFile(parent, start))
            {
                TryCollectFile(start, relative, query, matches, stats);
            }
        }
        else
        {
            foreach (var file in TreeWalker.Enumerate(start, IndexPaths.DefaultFor(start), stats))
            {
                TryCollectFile(file.FullPath, file.RelativePath, query, matches, stats);
            }
        }

        matches.Sort(CompareMatches);
        return matches;
    }

    private static bool IsIgnoredFile(string walkRoot, string fullPath)
    {
        var stats = new WalkStats();
        return !TreeWalker.Enumerate(walkRoot, null, stats).Any(f =>
            string.Equals(f.FullPath, fullPath, StringComparison.Ordinal));
    }

    private static void TryCollectFile(string fullPath, string relativePath, SearchQuery query, List<SearchMatch> matches, WalkStats stats)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > TextFiles.MaxFileBytes)
            {
                stats.SkippedTooLarge++;
                return;
            }

            var bytes = File.ReadAllBytes(fullPath);
            if (TextFiles.IsBinary(bytes))
            {
                stats.SkippedBinary++;
                return;
            }

            Collect(TextFiles.Decode(bytes), relativePath, query, matches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stats.SkippedUnreadable++;
        }
    }

    private static void Collect(string text, string relativePath, SearchQuery query, List<SearchMatch> matches)
    {
        var starts = LineMap.Build(text);
        if (!query.Regex)
        {
            var comparison = query.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var from = 0;
            while (from <= text.Length - query.Pattern.Length)
            {
                var idx = text.IndexOf(query.Pattern, from, comparison);
                if (idx < 0)
                {
                    break;
                }

                var (line, column) = LineMap.Map(starts, idx);
                matches.Add(new SearchMatch(
                    relativePath,
                    line,
                    column,
                    text.Substring(idx, query.Pattern.Length),
                    LineMap.LineText(text, starts, line)));
                from = idx + Math.Max(1, query.Pattern.Length);
            }

            return;
        }

        var options = RegexOptions.CultureInvariant;
        if (query.IgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var regex = new Regex(query.Pattern, options, TimeSpan.FromSeconds(5));
        foreach (Match match in regex.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            var (line, column) = LineMap.Map(starts, match.Index);
            matches.Add(new SearchMatch(
                relativePath,
                line,
                column,
                match.Value,
                LineMap.LineText(text, starts, line)));
        }
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
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }
}

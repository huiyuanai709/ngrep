using Ngrep.Ignore;
using Ngrep.Text;

namespace Ngrep.Indexing;

internal static class Indexer
{
    public static IndexResult Build(string root, string? indexDirectory = null)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory not found: {root}");
        }

        var indexPath = string.IsNullOrWhiteSpace(indexDirectory)
            ? IndexPaths.DefaultFor(root)
            : Path.GetFullPath(indexDirectory);

        var existing = IndexStore.TryLoad(indexPath);
        var documents = existing?.Documents.ToDictionary(d => d.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, DocumentRecord>(StringComparer.Ordinal);
        var postings = existing?.Postings ?? new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var createdUtc = existing?.Manifest.CreatedUtc;
        var nextId = documents.Count == 0 ? 1 : documents.Values.Max(d => d.Id) + 1;

        var stats = new WalkStats();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        // Hidden files stay in the index so --hidden can filter at search time.
        var files = TreeWalker.Enumerate(root, indexPath, stats, includeHidden: true)
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stats.IncrementSkippedUnreadable();
                continue;
            }

            if (info.Length > TextFiles.MaxFileBytes)
            {
                stats.IncrementSkippedTooLarge();
                continue;
            }

            documents.TryGetValue(file.RelativePath, out var previous);
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (previous != null && previous.Size == info.Length && previous.MtimeUnixMs == mtime)
            {
                seen.Add(file.RelativePath);
                unchanged++;
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stats.IncrementSkippedUnreadable();
                continue;
            }

            if (TextFiles.IsBinary(bytes))
            {
                stats.IncrementSkippedBinary();
                continue;
            }

            var hash = TextFiles.Sha256Hex(bytes);
            if (previous != null && previous.Sha256 == hash)
            {
                previous.MtimeUnixMs = mtime;
                previous.Size = info.Length;
                seen.Add(file.RelativePath);
                unchanged++;
                continue;
            }

            var text = TextFiles.Decode(bytes);
            var id = previous?.Id ?? nextId++;
            if (previous != null)
            {
                RemoveDoc(postings, previous.Id);
                updated++;
            }
            else
            {
                added++;
            }

            foreach (var gram in Trigrams.Extract(text))
            {
                AddDoc(postings, gram, id);
            }

            documents[file.RelativePath] = new DocumentRecord
            {
                Id = id,
                Path = file.RelativePath,
                Size = info.Length,
                MtimeUnixMs = mtime,
                Sha256 = hash,
                LineStarts = LineMap.Build(text),
            };
            seen.Add(file.RelativePath);
        }

        var removed = 0;
        foreach (var path in documents.Keys.Where(path => !seen.Contains(path)).ToArray())
        {
            RemoveDoc(postings, documents[path].Id);
            documents.Remove(path);
            removed++;
        }

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var kept = documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var manifest = new IndexManifest
        {
            FormatVersion = IndexPaths.FormatVersion,
            Tool = IndexPaths.ToolName,
            Root = root,
            CreatedUtc = string.IsNullOrEmpty(createdUtc) ? now : createdUtc,
            UpdatedUtc = now,
            DocumentCount = kept.Count,
            TrigramCount = postings.Count,
            CaseFolding = "invariant-lowercase",
            ColumnUnit = "utf16-code-unit",
        };

        IndexStore.Write(indexPath, manifest, kept, postings);

        return new IndexResult
        {
            Root = root,
            IndexPath = indexPath,
            Documents = kept.Count,
            Added = added,
            Updated = updated,
            Unchanged = unchanged,
            Removed = removed,
            SkippedIgnored = stats.SkippedIgnored,
            SkippedBinary = stats.SkippedBinary,
            SkippedTooLarge = stats.SkippedTooLarge,
            SkippedUnreadable = stats.SkippedUnreadable,
        };
    }

    private static void RemoveDoc(Dictionary<string, List<int>> postings, int id)
    {
        var empty = new List<string>();
        foreach (var pair in postings)
        {
            pair.Value.Remove(id);
            if (pair.Value.Count == 0)
            {
                empty.Add(pair.Key);
            }
        }

        foreach (var key in empty)
        {
            postings.Remove(key);
        }
    }

    private static void AddDoc(Dictionary<string, List<int>> postings, string gram, int id)
    {
        if (!postings.TryGetValue(gram, out var list))
        {
            postings[gram] = [id];
            return;
        }

        var index = list.BinarySearch(id);
        if (index < 0)
        {
            list.Insert(~index, id);
        }
    }
}

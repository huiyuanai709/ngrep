using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ngrep.Indexing;

internal static class IndexStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Runs after <index>.tmp is complete and before it replaces the live index.
    // A throw leaves the previous index in place. Tests clear this in a finally.
    internal static Action? TestHookBeforeCommit { get; set; }

    public static IndexData? TryLoad(string indexDirectory)
    {
        var dir = ResolveExisting(indexDirectory);
        if (dir == null)
        {
            return null;
        }

        return Read(dir);
    }

    public static void Write(string indexDirectory, IndexManifest manifest, IReadOnlyList<DocumentRecord> documents, Dictionary<string, List<int>> postings)
    {
        var finalDir = Path.GetFullPath(indexDirectory);
        var tempDir = finalDir + ".tmp";
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "manifest.json"), ManifestJson(manifest), Utf8);
        WriteDocuments(Path.Combine(tempDir, "documents.ndjson"), documents);
        using (var stream = CreateWrite(Path.Combine(tempDir, "postings.bin")))
        {
            WritePostings(stream, postings);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            TestHookBeforeCommit?.Invoke();
        }
        catch
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            throw;
        }

        var backup = finalDir + ".bak";
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        if (Directory.Exists(finalDir))
        {
            Directory.Move(finalDir, backup);
        }

        Directory.Move(tempDir, finalDir);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }
    }

    public static string ManifestJson(IndexManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("tool", manifest.Tool);
            writer.WriteString("root", manifest.Root);
            writer.WriteString("createdUtc", manifest.CreatedUtc);
            writer.WriteString("updatedUtc", manifest.UpdatedUtc);
            writer.WriteNumber("documentCount", manifest.DocumentCount);
            writer.WriteNumber("trigramCount", manifest.TrigramCount);
            writer.WriteString("caseFolding", manifest.CaseFolding);
            writer.WriteString("columnUnit", manifest.ColumnUnit);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IndexData Read(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var documentsPath = Path.Combine(directory, "documents.ndjson");
        var postingsPath = Path.Combine(directory, "postings.bin");
        if (!File.Exists(manifestPath) || !File.Exists(documentsPath) || !File.Exists(postingsPath))
        {
            throw new InvalidDataException($"Index at {directory} is incomplete.");
        }

        var manifest = ReadManifest(File.ReadAllText(manifestPath));
        if (manifest.FormatVersion != IndexPaths.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported index format version {manifest.FormatVersion}. This build reads version {IndexPaths.FormatVersion}.");
        }

        var documents = ReadDocuments(documentsPath);
        Dictionary<string, List<int>> postings;
        using (var stream = File.OpenRead(postingsPath))
        {
            postings = ReadPostings(stream);
        }

        return new IndexData
        {
            Manifest = manifest,
            Directory = directory,
            Documents = documents,
            Postings = postings,
        };
    }

    private static string? ResolveExisting(string indexDirectory)
    {
        var full = Path.GetFullPath(indexDirectory);
        if (File.Exists(Path.Combine(full, "manifest.json")))
        {
            return full;
        }

        var bak = full + ".bak";
        if (File.Exists(Path.Combine(bak, "manifest.json")))
        {
            return bak;
        }

        return null;
    }

    private static IndexManifest ReadManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new IndexManifest
            {
                FormatVersion = RequiredInt(root, "formatVersion"),
                Tool = RequiredString(root, "tool"),
                Root = RequiredString(root, "root"),
                CreatedUtc = RequiredString(root, "createdUtc"),
                UpdatedUtc = RequiredString(root, "updatedUtc"),
                DocumentCount = RequiredInt(root, "documentCount"),
                TrigramCount = RequiredInt(root, "trigramCount"),
                CaseFolding = RequiredString(root, "caseFolding"),
                ColumnUnit = RequiredString(root, "columnUnit"),
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("manifest.json is not valid JSON.", ex);
        }
    }

    private const int WriteBufferBytes = 1 << 20;

    private static FileStream CreateWrite(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.SequentialScan);

    private static void WriteDocuments(string path, IReadOnlyList<DocumentRecord> documents)
    {
        var ordered = new DocumentRecord[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            ordered[i] = documents[i];
        }

        Array.Sort(ordered, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        using var stream = CreateWrite(path);
        foreach (var document in ordered)
        {
            document.CachedLine ??= SerializeDocument(document);
            stream.Write(document.CachedLine);
            stream.WriteByte((byte)'\n');
        }
    }

    private static byte[] SerializeDocument(DocumentRecord document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", document.Id);
            writer.WriteString("path", document.Path);
            writer.WriteNumber("size", document.Size);
            writer.WriteNumber("mtimeUnixMs", document.MtimeUnixMs);
            writer.WriteString("sha256", document.Sha256);
            writer.WriteStartArray("lineStarts");
            foreach (var start in document.LineStarts)
            {
                writer.WriteNumberValue(start);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static List<DocumentRecord> ReadDocuments(string path)
    {
        var documents = new List<DocumentRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var starts = new List<int>();
                foreach (var item in root.GetProperty("lineStarts").EnumerateArray())
                {
                    starts.Add(item.GetInt32());
                }

                documents.Add(new DocumentRecord
                {
                    Id = RequiredInt(root, "id"),
                    Path = RequiredString(root, "path"),
                    Size = RequiredLong(root, "size"),
                    MtimeUnixMs = RequiredLong(root, "mtimeUnixMs"),
                    Sha256 = RequiredString(root, "sha256"),
                    LineStarts = starts.Count == 0 ? [0] : starts.ToArray(),
                });
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                throw new InvalidDataException("documents.ndjson contains an invalid record.", ex);
            }
        }

        return documents;
    }

    public static void WritePostings(Stream stream, Dictionary<string, List<int>> postings)
    {
        if (postings.Count == 0)
        {
            using var empty = new BinaryWriter(stream, Utf8, leaveOpen: true);
            empty.Write(Encoding.ASCII.GetBytes("NGTR"));
            empty.Write(IndexPaths.FormatVersion);
            empty.Write(0);
            return;
        }

        var keys = ArrayPool<string>.Shared.Rent(postings.Count);
        try
        {
            var count = 0;
            foreach (var key in postings.Keys)
            {
                keys[count++] = key;
            }

            Array.Sort(keys, 0, count, StringComparer.Ordinal);
            using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("NGTR"));
            writer.Write(IndexPaths.FormatVersion);
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                var key = keys[i];
                var bytes = Encoding.UTF8.GetBytes(key);
                if (bytes.Length > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Trigram is too long to store ({bytes.Length} bytes).");
                }

                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
                var ids = postings[key];
                writer.Write(ids.Count);
                foreach (var id in ids)
                {
                    writer.Write(id);
                }
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(keys);
        }
    }

    public static Dictionary<string, List<int>> ReadPostings(Stream stream)
    {
        using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'N' || magic[1] != (byte)'G' || magic[2] != (byte)'T' || magic[3] != (byte)'R')
        {
            throw new InvalidDataException("postings.bin magic mismatch (expected NGTR).");
        }

        var version = reader.ReadInt32();
        if (version != IndexPaths.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported postings version {version}. This build reads version {IndexPaths.FormatVersion}.");
        }

        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("postings.bin trigram count is invalid.");
        }

        var postings = new Dictionary<string, List<int>>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var len = reader.ReadUInt16();
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len)
            {
                throw new InvalidDataException("postings.bin ended inside a trigram.");
            }

            var gram = Encoding.UTF8.GetString(bytes);
            var n = reader.ReadInt32();
            if (n < 0)
            {
                throw new InvalidDataException("postings.bin posting count is invalid.");
            }

            var ids = new List<int>(n);
            for (var j = 0; j < n; j++)
            {
                ids.Add(reader.ReadInt32());
            }

            postings[gram] = ids;
        }

        return postings;
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"Missing integer field '{name}'.");

    private static long RequiredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : throw new InvalidDataException($"Missing integer field '{name}'.");

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Missing string field '{name}'.");
        }

        return value.GetString() ?? "";
    }
}

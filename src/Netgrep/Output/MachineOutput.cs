using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Netgrep.Indexing;
using Netgrep.Search;

namespace Netgrep.Output;

internal readonly record struct WatchEvent(string Event, IndexResult Result);

internal static class MachineOutput
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteMatches(IReadOnlyList<SearchMatch> matches, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Text:
                foreach (var match in matches)
                {
                    Console.WriteLine($"{match.Path}:{match.Line}:{match.Column}:{match.LineText}");
                }

                break;
            case OutputFormat.Ndjson:
                foreach (var match in matches)
                {
                    Console.WriteLine(MatchJson(match));
                }

                break;
            case OutputFormat.Json:
                Console.WriteLine(MatchArrayJson(matches));
                break;
        }
    }

    public static void WriteIndex(IndexResult result, OutputFormat format)
    {
        if (format == OutputFormat.Text)
        {
            Console.WriteLine($"root {result.Root}");
            Console.WriteLine($"index {result.IndexPath}");
            Console.WriteLine($"documents {result.Documents}");
            Console.WriteLine($"added {result.Added}");
            Console.WriteLine($"updated {result.Updated}");
            Console.WriteLine($"unchanged {result.Unchanged}");
            Console.WriteLine($"removed {result.Removed}");
            Console.WriteLine($"skippedIgnored {result.SkippedIgnored}");
            Console.WriteLine($"skippedBinary {result.SkippedBinary}");
            Console.WriteLine($"skippedTooLarge {result.SkippedTooLarge}");
            Console.WriteLine($"skippedUnreadable {result.SkippedUnreadable}");
            return;
        }

        Console.WriteLine(IndexJson(result));
    }

    public static void WriteWatch(WatchEvent watch, OutputFormat format)
    {
        if (format == OutputFormat.Text)
        {
            if (watch.Event == "ready")
            {
                Console.WriteLine($"watching {watch.Result.Root}");
                Console.WriteLine($"index {watch.Result.IndexPath}");
            }

            Console.WriteLine(
                $"{watch.Event} documents={watch.Result.Documents} added={watch.Result.Added} updated={watch.Result.Updated} unchanged={watch.Result.Unchanged} removed={watch.Result.Removed}");
            return;
        }

        Console.WriteLine(WatchJson(watch));
    }

    public static void Note(string? note)
    {
        if (!string.IsNullOrEmpty(note))
        {
            Console.Error.WriteLine(note);
        }
    }

    public static string MatchJson(SearchMatch match)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            WriteMatch(writer, match);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MatchArrayJson(IReadOnlyList<SearchMatch> matches)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            foreach (var match in matches)
            {
                WriteMatch(writer, match);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMatch(Utf8JsonWriter writer, SearchMatch match)
    {
        writer.WriteStartObject();
        writer.WriteString("path", match.Path);
        writer.WriteNumber("line", match.Line);
        writer.WriteNumber("column", match.Column);
        writer.WriteString("text", match.Text);
        writer.WriteString("lineText", match.LineText);
        writer.WriteEndObject();
    }

    private static string IndexJson(IndexResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("command", "index");
            writer.WriteString("root", result.Root);
            writer.WriteString("indexPath", result.IndexPath);
            writer.WriteNumber("formatVersion", IndexPaths.FormatVersion);
            writer.WriteNumber("documents", result.Documents);
            writer.WriteNumber("added", result.Added);
            writer.WriteNumber("updated", result.Updated);
            writer.WriteNumber("unchanged", result.Unchanged);
            writer.WriteNumber("removed", result.Removed);
            writer.WriteNumber("skippedIgnored", result.SkippedIgnored);
            writer.WriteNumber("skippedBinary", result.SkippedBinary);
            writer.WriteNumber("skippedTooLarge", result.SkippedTooLarge);
            writer.WriteNumber("skippedUnreadable", result.SkippedUnreadable);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string WatchJson(WatchEvent watch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("event", watch.Event);
            writer.WriteString("root", watch.Result.Root);
            writer.WriteString("indexPath", watch.Result.IndexPath);
            writer.WriteNumber("documents", watch.Result.Documents);
            writer.WriteNumber("added", watch.Result.Added);
            writer.WriteNumber("updated", watch.Result.Updated);
            writer.WriteNumber("unchanged", watch.Result.Unchanged);
            writer.WriteNumber("removed", watch.Result.Removed);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Output;

internal readonly record struct WatchEvent(string Event, IndexResult Result);

internal readonly record struct SearchPresentation(ReportMode Report, bool OnlyMatching, bool Color, bool Heading = false);

internal static class MachineOutput
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteMatches(
        IReadOnlyList<SearchMatch> matches,
        OutputFormat format,
        SearchPresentation presentation = default,
        IReadOnlyList<string>? filesWithoutMatch = null,
        long elapsedMilliseconds = 0)
    {
        var color = format == OutputFormat.Text && presentation.Color;
        switch (presentation.Report)
        {
            case ReportMode.Quiet:
                return;
            case ReportMode.Count:
                WriteCounts(matches, format, color, elapsedMilliseconds);
                return;
            case ReportMode.FilesWithMatches:
                WritePaths(DistinctPaths(matches), format, color, asMatches: true, matches, elapsedMilliseconds);
                return;
            case ReportMode.FilesWithoutMatch:
                WritePaths(filesWithoutMatch ?? [], format, color, asMatches: false, matches: null, elapsedMilliseconds);
                return;
        }

        switch (format)
        {
            case OutputFormat.Text:
                WriteText(matches, presentation.OnlyMatching, color, presentation.Heading);
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
            case OutputFormat.RgJson:
                WriteRgJson(matches, elapsedMilliseconds);
                break;
        }
    }

    /// <summary>
    /// Search summary on stderr, after stdout, so text/json/ndjson/rgjson stay intact.
    /// Counts are pattern hits even when the report is <c>--files-without-match</c>.
    /// </summary>
    public static void WriteStats(SearchOutcome outcome)
    {
        Console.Out.Flush();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Console.Error.WriteLine("files searched: " + outcome.FilesSearched.ToString(culture));
        Console.Error.WriteLine("files matched: " + outcome.FilesMatched.ToString(culture));
        Console.Error.WriteLine("lines matched: " + outcome.LinesMatched.ToString(culture));
        Console.Error.WriteLine("matches: " + outcome.Matches.Count.ToString(culture));
        Console.Error.WriteLine("elapsed ms: " + outcome.ElapsedMilliseconds.ToString(culture));
    }

    public static void WriteIndex(IndexResult result, OutputFormat format)
    {
        RejectRgJson(format);
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
        RejectRgJson(format);
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
        if (match.Before != null || match.After != null)
        {
            WriteContext(writer, "before", match.Before);
            WriteContext(writer, "after", match.After);
        }

        writer.WriteEndObject();
    }

    private static void WriteContext(Utf8JsonWriter writer, string name, IReadOnlyList<ContextLine>? lines)
    {
        writer.WriteStartArray(name);
        if (lines != null)
        {
            foreach (var line in lines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", line.Line);
                writer.WriteString("text", line.Text);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static void RejectRgJson(OutputFormat format)
    {
        if (format == OutputFormat.RgJson)
        {
            throw new ArgumentException("--format rgjson is only supported for search.");
        }
    }

    private static void WriteText(IReadOnlyList<SearchMatch> matches, bool onlyMatching, bool color, bool heading)
    {
        string? headingPath = null;
        var headingStarted = false;
        void EnsureHeading(string path)
        {
            if (!heading || string.Equals(headingPath, path, StringComparison.Ordinal))
            {
                return;
            }

            if (headingStarted)
            {
                Console.WriteLine();
            }

            Console.WriteLine(color ? Paint(path, TextColor.Path) : path);
            headingPath = path;
            headingStarted = true;
        }

        if (onlyMatching)
        {
            foreach (var match in matches)
            {
                EnsureHeading(match.Path);
                WriteMatchLine(match, onlyMatching: true, color, includePath: !heading);
            }

            return;
        }

        var matchLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (match.Before == null && match.After == null)
            {
                continue;
            }

            if (!matchLines.TryGetValue(match.Path, out var lines))
            {
                lines = [];
                matchLines[match.Path] = lines;
            }

            lines.Add(match.Line);
        }

        string? path = null;
        HashSet<int>? fileMatches = null;
        var lastLine = 0;
        var printed = false;
        foreach (var match in matches)
        {
            if (match.Before == null && match.After == null)
            {
                EnsureHeading(match.Path);
                WriteMatchLine(match, onlyMatching: false, color, includePath: !heading);
                continue;
            }

            if (!string.Equals(path, match.Path, StringComparison.Ordinal))
            {
                path = match.Path;
                fileMatches = matchLines[match.Path];
                lastLine = 0;
                printed = false;
                EnsureHeading(path);
            }

            var before = match.Before ?? [];
            var upcoming = NextVisible(before, fileMatches, match.Line);
            if (printed && upcoming > lastLine + 1)
            {
                Console.WriteLine("--");
            }

            foreach (var line in before)
            {
                if (line.Line <= lastLine || fileMatches!.Contains(line.Line))
                {
                    continue;
                }

                WriteContextLine(match.Path, line.Line, line.Text, color, includePath: !heading);
                lastLine = line.Line;
                printed = true;
            }

            WriteMatchLine(match, onlyMatching: false, color, includePath: !heading);
            if (match.Line > lastLine)
            {
                lastLine = match.Line;
            }

            printed = true;
            foreach (var line in match.After ?? [])
            {
                if (line.Line <= lastLine || fileMatches!.Contains(line.Line))
                {
                    continue;
                }

                WriteContextLine(match.Path, line.Line, line.Text, color, includePath: !heading);
                lastLine = line.Line;
            }
        }
    }

    private static void WriteMatchLine(SearchMatch match, bool onlyMatching, bool color, bool includePath)
    {
        var body = onlyMatching ? match.Text : match.LineText;
        var line = match.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var column = match.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!color)
        {
            Console.WriteLine(includePath ? $"{match.Path}:{line}:{column}:{body}" : $"{line}:{column}:{body}");
            return;
        }

        if (onlyMatching)
        {
            body = TextColor.Match + body + TextColor.Reset;
        }
        else
        {
            body = Highlight(match.LineText, match.Column, match.Text);
        }

        var numbers = string.Concat(
            Paint(line, TextColor.Line),
            ":",
            Paint(column, TextColor.Column),
            ":",
            body);
        Console.WriteLine(includePath ? string.Concat(Paint(match.Path, TextColor.Path), ":", numbers) : numbers);
    }

    private static void WriteContextLine(string path, int line, string text, bool color, bool includePath)
    {
        var lineText = line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!color)
        {
            Console.WriteLine(includePath ? $"{path}-{lineText}-{text}" : $"{lineText}-{text}");
            return;
        }

        var painted = string.Concat(Paint(lineText, TextColor.Line), "-", text);
        Console.WriteLine(includePath ? string.Concat(Paint(path, TextColor.Path), "-", painted) : painted);
    }

    private static string Highlight(string lineText, int column, string matchText)
    {
        var start = column - 1;
        if (start < 0 || start > lineText.Length)
        {
            return lineText;
        }

        var length = matchText.Length;
        var newline = matchText.IndexOf('\n');
        if (newline >= 0)
        {
            length = newline;
            if (length > 0 && matchText[length - 1] == '\r')
            {
                length--;
            }
        }

        if (start + length > lineText.Length)
        {
            length = lineText.Length - start;
        }

        if (length <= 0 || !lineText.AsSpan(start, length).SequenceEqual(matchText.AsSpan(0, length)))
        {
            return lineText;
        }

        return string.Concat(
            lineText[..start],
            TextColor.Match,
            lineText.Substring(start, length),
            TextColor.Reset,
            lineText[(start + length)..]);
    }

    private static string Paint(string text, string code) => code + text + TextColor.Reset;

    private static IEnumerable<string> DistinctPaths(IReadOnlyList<SearchMatch> matches)
    {
        string? last = null;
        foreach (var match in matches)
        {
            if (string.Equals(last, match.Path, StringComparison.Ordinal))
            {
                continue;
            }

            last = match.Path;
            yield return match.Path;
        }
    }

    private static void WritePaths(
        IEnumerable<string> paths,
        OutputFormat format,
        bool color,
        bool asMatches,
        IReadOnlyList<SearchMatch>? matches,
        long elapsedMilliseconds)
    {
        var list = paths as IReadOnlyList<string> ?? paths.ToArray();
        switch (format)
        {
            case OutputFormat.Text:
                foreach (var path in list)
                {
                    Console.WriteLine(color ? Paint(path, TextColor.Path) : path);
                }

                break;
            case OutputFormat.Ndjson:
                foreach (var path in list)
                {
                    Console.WriteLine(PathJson(path));
                }

                break;
            case OutputFormat.Json:
                Console.WriteLine(PathArrayJson(list));
                break;
            case OutputFormat.RgJson:
                if (asMatches && matches != null)
                {
                    WriteRgJson(matches, elapsedMilliseconds);
                }
                else
                {
                    foreach (var path in list)
                    {
                        WriteRgBegin(path);
                        WriteRgEnd(path, matches: 0, matchedLines: 0);
                    }

                    WriteRgSummary(list.Count, matches: 0, matchedLines: 0, elapsedMilliseconds);
                }

                break;
        }
    }

    private static void WriteCounts(IReadOnlyList<SearchMatch> matches, OutputFormat format, bool color, long elapsedMilliseconds)
    {
        var groups = CountGroups(matches);
        switch (format)
        {
            case OutputFormat.Text:
                foreach (var group in groups)
                {
                    if (!color)
                    {
                        Console.WriteLine($"{group.Path}:{group.Lines}");
                        continue;
                    }

                    Console.WriteLine(string.Concat(
                        Paint(group.Path, TextColor.Path),
                        ":",
                        Paint(group.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture), TextColor.Line)));
                }

                break;
            case OutputFormat.Ndjson:
                foreach (var group in groups)
                {
                    Console.WriteLine(CountJson(group.Path, group.Lines));
                }

                break;
            case OutputFormat.Json:
                Console.WriteLine(CountArrayJson(groups));
                break;
            case OutputFormat.RgJson:
                var matchTotal = 0;
                var lineTotal = 0;
                foreach (var group in groups)
                {
                    WriteRgBegin(group.Path);
                    WriteRgEnd(group.Path, group.Hits, group.Lines);
                    matchTotal += group.Hits;
                    lineTotal += group.Lines;
                }

                WriteRgSummary(groups.Count, matchTotal, lineTotal, elapsedMilliseconds);
                break;
        }
    }

    private readonly record struct CountGroup(string Path, int Lines, int Hits);

    private static List<CountGroup> CountGroups(IReadOnlyList<SearchMatch> matches)
    {
        var groups = new List<CountGroup>();
        string? path = null;
        var lines = new HashSet<int>();
        var hits = 0;
        void Flush()
        {
            if (path != null && lines.Count > 0)
            {
                groups.Add(new CountGroup(path, lines.Count, hits));
            }
        }

        foreach (var match in matches)
        {
            if (!string.Equals(path, match.Path, StringComparison.Ordinal))
            {
                Flush();
                path = match.Path;
                lines = [];
                hits = 0;
            }

            lines.Add(match.Line);
            hits++;
        }

        Flush();
        return groups;
    }

    private static string PathJson(string path)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string PathArrayJson(IReadOnlyList<string> paths)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("paths");
            foreach (var path in paths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CountJson(string path, int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CountArrayJson(IReadOnlyList<CountGroup> groups)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("counts");
            foreach (var group in groups)
            {
                writer.WriteStartObject();
                writer.WriteString("path", group.Path);
                writer.WriteNumber("count", group.Lines);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRgJson(IReadOnlyList<SearchMatch> matches, long elapsedMilliseconds)
    {
        string? path = null;
        var fileMatches = 0;
        var fileLines = new HashSet<int>();
        var totalMatches = 0;
        var totalLines = 0;
        var files = 0;
        var lastLine = 0;
        HashSet<int>? fileMatchLines = null;
        var matchLines = ContextMatchLines(matches);

        void EndFile()
        {
            if (path == null)
            {
                return;
            }

            WriteRgEnd(path, fileMatches, fileLines.Count);
            totalMatches += fileMatches;
            totalLines += fileLines.Count;
            files++;
        }

        foreach (var match in matches)
        {
            if (!string.Equals(path, match.Path, StringComparison.Ordinal))
            {
                EndFile();
                path = match.Path;
                fileMatches = 0;
                fileLines = [];
                lastLine = 0;
                fileMatchLines = matchLines.TryGetValue(path, out var lines) ? lines : null;
                WriteRgBegin(path);
            }

            if (match.Before != null)
            {
                foreach (var line in match.Before)
                {
                    if (line.Line <= lastLine || (fileMatchLines != null && fileMatchLines.Contains(line.Line)))
                    {
                        continue;
                    }

                    WriteRgContext(path!, line);
                    lastLine = line.Line;
                }
            }

            fileMatches++;
            fileLines.Add(match.Line);
            WriteRgMatch(match);
            if (match.Line > lastLine)
            {
                lastLine = match.Line;
            }

            if (match.After == null)
            {
                continue;
            }

            foreach (var line in match.After)
            {
                if (line.Line <= lastLine || (fileMatchLines != null && fileMatchLines.Contains(line.Line)))
                {
                    continue;
                }

                WriteRgContext(path!, line);
                lastLine = line.Line;
            }
        }

        EndFile();
        WriteRgSummary(files, totalMatches, totalLines, elapsedMilliseconds);
    }

    private static Dictionary<string, HashSet<int>> ContextMatchLines(IReadOnlyList<SearchMatch> matches)
    {
        var matchLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (match.Before == null && match.After == null)
            {
                continue;
            }

            if (!matchLines.TryGetValue(match.Path, out var lines))
            {
                lines = [];
                matchLines[match.Path] = lines;
            }

            lines.Add(match.Line);
        }

        return matchLines;
    }

    private static void WriteRgBegin(string path)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "begin");
            writer.WriteStartObject("data");
            WriteRgPath(writer, path);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteRgMatch(SearchMatch match)
    {
        var lineText = match.LineText + "\n";
        var start = Math.Max(0, match.Column - 1);
        var length = match.Text.Length;
        var newline = match.Text.IndexOf('\n');
        if (newline >= 0)
        {
            length = newline;
        }

        if (start + length > match.LineText.Length)
        {
            length = Math.Max(0, match.LineText.Length - start);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "match");
            writer.WriteStartObject("data");
            WriteRgPath(writer, match.Path);
            writer.WriteStartObject("lines");
            writer.WriteString("text", lineText);
            writer.WriteEndObject();
            writer.WriteNumber("line_number", match.Line);
            writer.WriteNumber("absolute_offset", LineOffset(match));
            writer.WriteStartArray("submatches");
            writer.WriteStartObject();
            writer.WriteStartObject("match");
            writer.WriteString("text", match.Text);
            writer.WriteEndObject();
            writer.WriteNumber("start", start);
            writer.WriteNumber("end", start + length);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteRgContext(string path, ContextLine line)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "context");
            writer.WriteStartObject("data");
            WriteRgPath(writer, path);
            writer.WriteStartObject("lines");
            writer.WriteString("text", line.Text + "\n");
            writer.WriteEndObject();
            writer.WriteNumber("line_number", line.Line);
            writer.WriteNumber("absolute_offset", line.Offset);
            writer.WriteStartArray("submatches");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static int LineOffset(SearchMatch match)
    {
        var column = match.Column > 0 ? match.Column - 1 : 0;
        var offset = match.Offset - column;
        return offset < 0 ? 0 : offset;
    }

    private static void WriteRgEnd(string path, int matches, int matchedLines)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "end");
            writer.WriteStartObject("data");
            WriteRgPath(writer, path);
            writer.WriteNull("binary_offset");
            writer.WriteStartObject("stats");
            writer.WriteNumber("matches", matches);
            writer.WriteNumber("matched_lines", matchedLines);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteRgSummary(int files, int matches, int matchedLines, long elapsedMilliseconds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "summary");
            writer.WriteStartObject("data");
            writer.WriteStartObject("stats");
            writer.WriteNumber("searches_with_match", files);
            writer.WriteNumber("matches", matches);
            writer.WriteNumber("matched_lines", matchedLines);
            writer.WriteNumber("elapsed_ms", elapsedMilliseconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteRgPath(Utf8JsonWriter writer, string path)
    {
        writer.WriteStartObject("path");
        writer.WriteString("text", path);
        writer.WriteEndObject();
    }

    private static int NextVisible(IReadOnlyList<ContextLine> before, HashSet<int>? matchLines, int matchLine)
    {
        foreach (var line in before)
        {
            if (matchLines == null || !matchLines.Contains(line.Line))
            {
                return line.Line;
            }
        }

        return matchLine;
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

using System.Text.Json;
using System.Text.RegularExpressions;
using Ngrep.Cli;
using Ngrep.Output;
using Ngrep.Search;

namespace Ngrep.Tests;

public class ParityTests
{
    [Fact]
    public void Files_with_matches_prints_paths_and_keeps_exit_codes()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep netgrep\nnope\n");
        File.WriteAllText(tree.File("b.txt"), "zzz\n");
        File.WriteAllText(tree.File("empty.txt"), "");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-l", "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout))
        {
            var paths = doc.RootElement.GetProperty("paths").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal("a.txt", Assert.Single(paths));
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--files-with-matches", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("a.txt", doc.RootElement.GetProperty("path").GetString());
            Assert.False(doc.RootElement.TryGetProperty("line", out _));
        }

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["missing", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("", capture.Stdout.Trim());
    }

    [Fact]
    public void Files_without_match_lists_misses_including_prefilter_drops()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("hit.txt"), "netgrep\n");
        File.WriteAllText(tree.File("miss.txt"), "zzzzzz\n");
        File.WriteAllText(tree.File("empty.txt"), "");
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File("skip.log"), "netgrep\n");
        Indexing.Indexer.Build(tree.Root);

        var indexed = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            Report = ReportMode.FilesWithoutMatch,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        var parallel = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            Report = ReportMode.FilesWithoutMatch,
            MaxDegreeOfParallelism = 4,
        }).GetAwaiter().GetResult();
        Assert.True(indexed.UsedIndex);
        Assert.True(indexed.UsedTrigramPrefilter);
        Assert.Equal(["empty.txt", "miss.txt"], indexed.FilesWithoutMatch);
        Assert.Equal(indexed.FilesWithoutMatch, parallel.FilesWithoutMatch);
        Assert.Empty(indexed.Matches);

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--files-without-match", "netgrep", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("empty.txt\nmiss.txt", capture.Stdout.Trim().Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["--files-without-match", "netgrep", Path.Combine(tree.Root, "hit.txt"), "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("", capture.Stdout.Trim());
    }

    [Fact]
    public void Invert_changes_which_files_and_lines_count()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("all.txt"), "netgrep\nnetgrep\n");
        File.WriteAllText(tree.File("mixed.txt"), "netgrep\nnope\n");
        File.WriteAllText(tree.File("empty.txt"), "");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-v", "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("mixed.txt", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-v", "--files-without-match", "-j", "1"]).GetAwaiter().GetResult());
        var without = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["all.txt", "empty.txt"], without.OrderBy(path => path, StringComparer.Ordinal).ToArray());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-v", "-c", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("mixed.txt:1", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", Path.Combine(tree.Root, "mixed.txt"), "-c", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("mixed.txt:1", capture.Stdout.Trim());
    }

    [Fact]
    public void Count_is_matching_lines_and_only_matching_prints_the_slice()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep netgrep\nnope\nnetgrep\n");
        File.WriteAllText(tree.File("b.txt"), "nope\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-c", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt:2", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-c", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("a.txt", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-c", "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout))
        {
            var counts = doc.RootElement.GetProperty("counts");
            Assert.Equal(1, counts.GetArrayLength());
            Assert.Equal(2, counts[0].GetProperty("count").GetInt32());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["missing", tree.Root, "-c", "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
        using (var empty = JsonDocument.Parse(capture.Stdout))
        {
            Assert.Equal(0, empty.RootElement.GetProperty("counts").GetArrayLength());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-o", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(
            "a.txt:1:1:netgrep\na.txt:1:9:netgrep\na.txt:3:1:netgrep",
            capture.Stdout.Trim().Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-o", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        var lines = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("netgrep", first.RootElement.GetProperty("text").GetString());
        Assert.Equal("netgrep netgrep", first.RootElement.GetProperty("lineText").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("line").GetInt32());
        Assert.Equal(1, first.RootElement.GetProperty("column").GetInt32());
        Assert.False(capture.Stdout.Contains('\u001b'));
    }

    [Fact]
    public void Report_modes_are_mutually_exclusive()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\n");
        using var capture = new Capture();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["netgrep", tree.Root, "-l", "-c"]).GetAwaiter().GetResult());
        Assert.Contains("mutually exclusive", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["netgrep", tree.Root, "-l", "--files-without-match"]).GetAwaiter().GetResult());
        Assert.Contains("mutually exclusive", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_files_are_skipped_unless_requested_or_named()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("visible.txt"), "netgrep\n");
        File.WriteAllText(tree.File(".secret"), "netgrep\n");
        File.WriteAllText(tree.File(".hidden/note.txt"), "netgrep\n");
        File.WriteAllText(tree.File(".hidden/.inner"), "netgrep\n");
        File.WriteAllText(tree.File(".git/config"), "netgrep\n");
        File.WriteAllText(tree.File(".ngrep/note.txt"), "netgrep\n");
        Indexing.Indexer.Build(tree.Root);

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("visible.txt", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--hidden", "-l", "-j", "1"]).GetAwaiter().GetResult());
        var hidden = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal([".hidden/.inner", ".hidden/note.txt", ".secret", "visible.txt"], hidden.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(hidden, path => path.StartsWith(".git", StringComparison.Ordinal) || path.Contains("/.git", StringComparison.Ordinal));
        Assert.DoesNotContain(hidden, path => path.StartsWith(".ngrep", StringComparison.Ordinal));

        var indexed = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            IncludeHidden = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        Assert.True(indexed.UsedIndex);
        Assert.Contains(indexed.Matches, match => match.Path == ".secret");

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["netgrep", tree.Root, "-g", ".secret", "-l", "-j", "1"]).GetAwaiter().GetResult());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--hidden", "-g", ".secret", "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(".secret", capture.Stdout.Trim());

        capture.Clear();
        var named = Path.Combine(tree.Root, ".secret");
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", named, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains(".secret:1:", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", Path.Combine(tree.Root, ".hidden"), "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(".hidden/note.txt", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--no-ignore", "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("visible.txt", capture.Stdout.Trim());
    }

    [Fact]
    public void Hidden_still_respects_gitignore_until_no_ignore()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File(".gitignore"), ".secret\n");
        File.WriteAllText(tree.File(".secret"), "netgrep\n");
        File.WriteAllText(tree.File("visible.txt"), "other\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["netgrep", tree.Root, "--hidden", "-j", "1"]).GetAwaiter().GetResult());
        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--hidden", "--no-ignore", "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(".secret", capture.Stdout.Trim());
    }

    [Fact]
    public void Color_applies_to_text_only()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "beta netgrep gamma\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--color", "never", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt:1:6:beta netgrep gamma", capture.Stdout.Trim());
        Assert.DoesNotContain("\u001b", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--color=always", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("\u001b[35ma.txt\u001b[0m", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\u001b[32m1\u001b[0m", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("\u001b[34m6\u001b[0m", capture.Stdout, StringComparison.Ordinal);
        Assert.Contains("beta \u001b[1;31mnetgrep\u001b[0m gamma", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--color=always", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        Assert.DoesNotContain("\u001b", capture.Stdout, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("netgrep", doc.RootElement.GetProperty("text").GetString());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--color", "always", "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("\u001b[35ma.txt\u001b[0m", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["netgrep", tree.Root, "--color", "sometimes"]).GetAwaiter().GetResult());
        Assert.Contains("auto, always, or never", capture.Stderr, StringComparison.Ordinal);

        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        var term = Environment.GetEnvironmentVariable("TERM");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Assert.False(TextColor.Enabled(ColorChoice.Auto));
            Assert.True(TextColor.Enabled(ColorChoice.Always));
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("TERM", "dumb");
            Assert.False(TextColor.Enabled(ColorChoice.Auto));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previous);
            Environment.SetEnvironmentVariable("TERM", term);
        }

        Assert.False(TextColor.Enabled(ColorChoice.Never));
    }

    [Fact]
    public void Multiline_regex_crosses_newlines_without_compiled_options()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("span.txt"), "foo\nbar\nnope\n");
        File.WriteAllText(tree.File("lines.txt"), "alpha\nbar\n");

        var plain = MatchPlan.Create(new SearchQuery { Pattern = "foo.bar", Path = tree.Root, Regex = true });
        var multi = MatchPlan.Create(new SearchQuery { Pattern = "foo.bar", Path = tree.Root, Regex = true, Multiline = true });
        var dot = MatchPlan.Create(new SearchQuery { Pattern = "foo.bar", Path = tree.Root, Regex = true, MultilineDotAll = true });
        Assert.All(plain.Regexes!, regex => Assert.False(regex.Options.HasFlag(RegexOptions.Compiled)));
        Assert.All(dot.Regexes!, regex => Assert.False(regex.Options.HasFlag(RegexOptions.Compiled)));
        Assert.True(plain.NonBacktracking);
        Assert.All(plain.Regexes!, regex => Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking)));
        Assert.True(plain.Regexes![0].Options.HasFlag(RegexOptions.Multiline));
        Assert.False(plain.Regexes[0].Options.HasFlag(RegexOptions.Singleline));
        Assert.False(multi.Regexes![0].Options.HasFlag(RegexOptions.Singleline));
        Assert.True(dot.Regexes![0].Options.HasFlag(RegexOptions.Singleline));
        Assert.True(dot.CrossesLines);

        using var capture = new Capture();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["--regex", "foo\nbar", tree.Root, "-j", "1"]).GetAwaiter().GetResult());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--regex", "-U", "foo\nbar", tree.Root, "-o", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("span.txt:1:1:foo\nbar", capture.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["--regex", "-U", "foo.bar", tree.Root, "-j", "1"]).GetAwaiter().GetResult());

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--regex", "--multiline-dotall", "foo.bar", tree.Root, "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("span.txt", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("line").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("column").GetInt32());
            Assert.Equal("foo\nbar", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal("foo", doc.RootElement.GetProperty("lineText").GetString());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--regex", "^bar$", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        var anchored = capture.Stdout.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("lines.txt:2:1:bar", anchored, StringComparison.Ordinal);
        Assert.Contains("span.txt:2:1:bar", anchored, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", anchored, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--regex", "-U", "-v", "foo\nbar", Path.Combine(tree.Root, "span.txt"), "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("span.txt:3:1:nope", capture.Stdout.Trim());

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["--regex", "(?s)foo.bar", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["--regex", "-U", "(?s)foo.bar", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("span.txt", capture.Stdout.Trim());
    }

    [Fact]
    public void Rgjson_is_a_separate_event_stream()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "beta netgrep gamma\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--format", "rgjson", "-j", "1"]).GetAwaiter().GetResult());
        var events = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToArray();
        var types = events.Select(item => item.GetProperty("type").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "begin", "match", "end", "summary" }, types);
        var match = events[1].GetProperty("data");
        Assert.Equal("a.txt", match.GetProperty("path").GetProperty("text").GetString());
        Assert.Equal(1, match.GetProperty("line_number").GetInt32());
        Assert.Equal("beta netgrep gamma\n", match.GetProperty("lines").GetProperty("text").GetString());
        var sub = match.GetProperty("submatches")[0];
        Assert.Equal("netgrep", sub.GetProperty("match").GetProperty("text").GetString());
        Assert.Equal(5, sub.GetProperty("start").GetInt32());
        Assert.Equal(12, sub.GetProperty("end").GetInt32());
        Assert.Equal(0, match.GetProperty("absolute_offset").GetInt32());
        Assert.Equal(1, events[3].GetProperty("data").GetProperty("stats").GetProperty("matched_lines").GetInt32());
        Assert.True(events[3].GetProperty("data").GetProperty("stats").GetProperty("elapsed_ms").GetInt64() >= 0);
        Assert.DoesNotContain("\u001b", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["missing", tree.Root, "--format", "rgjson", "-j", "1"]).GetAwaiter().GetResult());
        using (var summary = JsonDocument.Parse(capture.Stdout.Trim()))
        {
            Assert.Equal("summary", summary.RootElement.GetProperty("type").GetString());
            Assert.Equal(0, summary.RootElement.GetProperty("data").GetProperty("stats").GetProperty("matches").GetInt32());
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout))
        {
            var hit = doc.RootElement.GetProperty("matches")[0];
            Assert.Equal("a.txt", hit.GetProperty("path").GetString());
            Assert.Equal("netgrep", hit.GetProperty("text").GetString());
            Assert.Equal("beta netgrep gamma", hit.GetProperty("lineText").GetString());
            Assert.False(hit.TryGetProperty("type", out _));
        }

        capture.Clear();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["index", tree.Root, "--format", "rgjson"]).GetAwaiter().GetResult());
        Assert.Contains("only supported for search", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pattern_file_keeps_hash_lines_and_empty_lines_match_all()
    {
        Assert.Equal(["# comment", "", "\\#hash", "  spaced", "keep"], PatternFiles.Parse("\uFEFF# comment\n\n\\#hash\n  spaced\nkeep\n"));
        Assert.Empty(PatternFiles.Parse(""));
        Assert.Equal([""], PatternFiles.Parse("\n"));
        Assert.Equal(["netgrep"], PatternFiles.Parse("netgrep"));
        Assert.Equal(["a", "b"], PatternFiles.Parse("a\r\nb\r\n"));

        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\n");
        File.WriteAllText(tree.File("b.txt"), "#hash\n");
        File.WriteAllText(tree.File("c.txt"), "netgrep\n");
        File.WriteAllText(tree.File("d.txt"), "\\#hash\n");
        var pats = Path.Combine(Path.GetTempPath(), "ngrep-pats-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(pats, "#hash\nnetgrep\n");
        try
        {
            using var capture = new Capture();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-e", "alpha", "-f", pats, tree.Root, "-j", "1"]).GetAwaiter().GetResult());
            var lines = capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            Assert.Equal("a.txt:1:1:alpha\nb.txt:1:1:#hash\nc.txt:1:1:netgrep\nd.txt:1:2:\\#hash", lines);

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["alpha", "-f", pats, tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("a.txt\nb.txt\nc.txt\nd.txt", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            File.WriteAllText(pats, "\\#hash\n");
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-f", pats, tree.Root, "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("d.txt:1:1:\\#hash", capture.Stdout.Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-f", pats, "--regex", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("b.txt\nd.txt", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            File.WriteAllText(pats, "\n");
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-f", pats, tree.Root, "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal(
                "a.txt:1:1:alpha\nb.txt:1:1:#hash\nc.txt:1:1:netgrep\nd.txt:1:1:\\#hash",
                capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["-f", pats, tree.Root, "-v", "-l", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("", capture.Stdout.Trim());

            capture.Clear();
            var previousIn = Console.In;
            Console.SetIn(new StringReader("netgrep\n#hash\n"));
            try
            {
                Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-f", "-", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
            }
            finally
            {
                Console.SetIn(previousIn);
            }

            Assert.Equal("b.txt\nc.txt\nd.txt", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

            capture.Clear();
            Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-f", tree.File("missing.txt"), tree.Root]).GetAwaiter().GetResult());
            Assert.Contains("Pattern file not found", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            File.WriteAllText(pats, "");
            Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-f", pats, tree.Root]).GetAwaiter().GetResult());
            Assert.Contains("Pattern file contains no patterns", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            File.WriteAllText(pats, "# only\n");
            Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["-f", pats, tree.Root, "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("", capture.Stdout.Trim());

            capture.Clear();
            File.WriteAllText(pats, "n.t\n");
            Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-f", pats, "--regex", tree.Root, "-l", "-j", "1"]).GetAwaiter().GetResult());
            Assert.Equal("c.txt", capture.Stdout.Trim());
        }
        finally
        {
            File.Delete(pats);
        }
    }

    [Fact]
    public void Headings_drop_the_path_prefix_only_for_text_matches()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\nnext\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt:1:1:netgrep\nb.txt:1:1:netgrep\n", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--heading", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt\n1:1:netgrep\n\nb.txt\n1:1:netgrep\n", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--heading", "--no-heading", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt:1:1:netgrep\nb.txt:1:1:netgrep\n", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", Path.Combine(tree.Root, "a.txt"), "--heading", "-A", "1", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt\n1:1:netgrep\n2-next\n", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--heading", "-o", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt\n1:1:netgrep\n\nb.txt\n1:1:netgrep\n", capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--heading", "--format", "json", "-j", "1"]).GetAwaiter().GetResult());
        using (var doc = JsonDocument.Parse(capture.Stdout))
        {
            var hit = doc.RootElement.GetProperty("matches")[0];
            Assert.Equal("a.txt", hit.GetProperty("path").GetString());
            Assert.Equal("netgrep", hit.GetProperty("text").GetString());
            Assert.Equal("netgrep", hit.GetProperty("lineText").GetString());
            Assert.False(hit.TryGetProperty("absolute_offset", out _));
        }

        Assert.DoesNotContain("files searched", capture.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Stats_go_to_stderr_and_keep_stdout_and_exit_codes()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "zzzzzz\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--stats", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal(
            "a.txt:1:1:netgrep netgrep\na.txt:1:9:netgrep netgrep\n",
            capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("files searched: 2", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("files matched: 1", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("lines matched: 1", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("matches: 2", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("elapsed ms:", capture.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("files searched", capture.Stdout, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-l", "--stats", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("a.txt", capture.Stdout.Trim());
        Assert.Contains("matches: 2", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("lines matched: 1", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.NoMatches, NgrepCli.Run(["missing", tree.Root, "--stats", "--format", "ndjson", "-j", "1"]).GetAwaiter().GetResult());
        Assert.Equal("", capture.Stdout.Trim());
        Assert.Contains("files searched: 2", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("files matched: 0", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("matches: 0", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Rgjson_emits_context_events_when_context_is_requested()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "before\nnetgrep\nafter\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--format", "rgjson", "-C", "1", "-j", "1"]).GetAwaiter().GetResult());
        var events = capture.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString())
            .ToArray();
        Assert.Equal(new[] { "begin", "context", "match", "context", "end", "summary" }, events);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "--format", "ndjson", "-C", "1", "-j", "1"]).GetAwaiter().GetResult());
        using var doc = JsonDocument.Parse(capture.Stdout.Trim());
        Assert.Equal("netgrep", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("before", doc.RootElement.GetProperty("before")[0].GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("absolute_offset", out _));
        Assert.False(doc.RootElement.GetProperty("before")[0].TryGetProperty("offset", out _));
    }

    [Fact]
    public void Parallel_index_search_still_prints_paths_in_order()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\n");
        File.WriteAllText(tree.File("m.txt"), "netgrep\n");
        File.WriteAllText(tree.File("z.txt"), "netgrep\n");
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["index", tree.Root, "-q"]).GetAwaiter().GetResult());

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["netgrep", tree.Root, "-j", "4"]).GetAwaiter().GetResult());
        Assert.Equal(
            "a.txt:1:1:netgrep\nm.txt:1:1:netgrep\nz.txt:1:1:netgrep\n",
            capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Hidden_path_rules_match_the_prefix_model()
    {
        Assert.False(HiddenPaths.IsHidden("src/App.cs"));
        Assert.True(HiddenPaths.IsHidden(".secret"));
        Assert.True(HiddenPaths.IsHidden(".hidden/note.txt"));
        Assert.False(HiddenPaths.IsHiddenBelow(".hidden/note.txt", ".hidden"));
        Assert.True(HiddenPaths.IsHiddenBelow(".hidden/.inner", ".hidden"));
        Assert.False(HiddenPaths.IsHiddenBelow(".secret", ".secret"));
        Assert.True(HiddenPaths.ContainsHardSkip(Path.Combine(Path.GetTempPath(), "repo", ".git", "config")));
        Assert.False(HiddenPaths.ContainsHardSkip(Path.Combine(Path.GetTempPath(), "repo", "src", "App.cs")));
    }

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-parity-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Capture : IDisposable
    {
        private readonly TextWriter _stdout = Console.Out;
        private readonly TextWriter _stderr = Console.Error;
        private StringWriter _out = new();
        private StringWriter _err = new();

        public Capture()
        {
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public string Stdout => _out.ToString();

        public string Stderr => _err.ToString();

        public void Clear()
        {
            _out = new StringWriter();
            _err = new StringWriter();
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public void Dispose()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }
}

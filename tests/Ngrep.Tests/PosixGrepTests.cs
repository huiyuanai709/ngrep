using Ngrep.Cli;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class PosixGrepTests
{
    [Fact]
    public void Extended_regexp_is_dotnet_and_fixed_strings_stay_the_default()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "net\nn.t\n");

        var literal = Run("n.t", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, literal.Code);
        Assert.Equal("a.txt:2:1:n.t", literal.Stdout.Trim());

        var forced = Run("-F", "n.t", tree.Root, "-j", "1");
        Assert.Equal(literal.Stdout, forced.Stdout);

        var extended = Run("-E", "n.t", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, extended.Code);
        Assert.Equal("a.txt:1:1:net\na.txt:2:1:n.t", extended.Stdout.Trim());

        var alias = Run("--extended-regexp", "n.t", tree.Root, "-j", "1");
        Assert.Equal(extended.Stdout, alias.Stdout);

        var regex = Run("--regex", "n.t", tree.Root, "-j", "1");
        Assert.Equal(extended.Stdout, regex.Stdout);
    }

    [Fact]
    public void Extended_and_fixed_are_mutually_exclusive()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "net\n");

        foreach (var args in new[]
        {
            new[] { "-E", "-F", "n.t", tree.Root },
            new[] { "-F", "-E", "n.t", tree.Root },
            new[] { "--regex", "--fixed-strings", "n.t", tree.Root },
            new[] { "--extended-regexp", "-F", "n.t", tree.Root },
            new[] { "-EF", "n.t", tree.Root },
        })
        {
            var run = Run(args);
            Assert.Equal(ExitCodes.Error, run.Code);
            Assert.Contains("mutually exclusive", run.Stderr, StringComparison.Ordinal);
            Assert.Equal("", run.Stdout.Trim());
        }
    }

    [Fact]
    public void Quiet_suppresses_match_output_and_keeps_exit_codes()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nbar\n");
        File.WriteAllText(tree.File("b.txt"), "foo\n");

        var found = Run("-q", "foo", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, found.Code);
        Assert.Equal("", found.Stdout);

        var silent = Run("--silent", "foo", tree.Root, "--format", "json", "-j", "1");
        Assert.Equal(ExitCodes.Success, silent.Code);
        Assert.Equal("", silent.Stdout);

        var missing = Run("-q", "nope", tree.Root, "--format", "json", "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, missing.Code);
        Assert.Equal("", missing.Stdout);

        var json = Run("nope", tree.Root, "--format", "json", "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, json.Code);
        Assert.Contains("\"matches\"", json.Stdout, StringComparison.Ordinal);

        var stats = Run("-q", "foo", tree.Root, "--stats", "-j", "1");
        Assert.Equal(ExitCodes.Success, stats.Code);
        Assert.Equal("", stats.Stdout);
        Assert.Contains("files searched:", stats.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Quiet_is_mutually_exclusive_with_the_other_reports()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\n");
        foreach (var args in new[]
        {
            new[] { "-q", "-c", "foo", tree.Root },
            new[] { "-l", "--quiet", "foo", tree.Root },
            new[] { "-q", "--files-without-match", "foo", tree.Root },
            new[] { "-c", "-l", "-q", "foo", tree.Root },
        })
        {
            var run = Run(args);
            Assert.Equal(ExitCodes.Error, run.Code);
            Assert.Contains("mutually exclusive", run.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Line_number_is_accepted_and_does_not_change_text()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nbar foo\n");
        var plain = Run("foo", tree.Root, "-j", "1");
        var shortFlag = Run("-n", "foo", tree.Root, "-j", "1");
        var longFlag = Run("--line-number", "foo", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, plain.Code);
        Assert.Equal(plain.Stdout, shortFlag.Stdout);
        Assert.Equal(plain.Stdout, longFlag.Stdout);
        Assert.Contains("a.txt:1:1:foo", plain.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:2:5:bar foo", plain.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void No_messages_hides_a_missing_path_and_not_other_errors()
    {
        using var tree = Temp();
        var missing = Path.Combine(tree.Root, "missing-dir");
        var loud = Run("foo", missing, "-j", "1");
        Assert.Equal(ExitCodes.Error, loud.Code);
        Assert.Contains("Path not found", loud.Stderr, StringComparison.Ordinal);
        Assert.Equal("", loud.Stdout.Trim());

        var quiet = Run("-s", "foo", missing, "-j", "1");
        Assert.Equal(ExitCodes.Error, quiet.Code);
        Assert.Equal("", quiet.Stderr);
        Assert.Equal("", quiet.Stdout);

        var both = Run("-q", "--no-messages", "foo", missing);
        Assert.Equal(ExitCodes.Error, both.Code);
        Assert.Equal("", both.Stderr);
        Assert.Equal("", both.Stdout);

        File.WriteAllText(tree.File("a.txt"), "foo\n");
        var badRegex = Run("-s", "-E", "(", tree.Root);
        Assert.Equal(ExitCodes.Error, badRegex.Code);
        Assert.NotEqual("", badRegex.Stderr.Trim());
        Assert.DoesNotContain("Path not found", badRegex.Stderr, StringComparison.Ordinal);

        var badFile = Run("-s", "-f", tree.File("no-patterns.txt"), tree.Root);
        Assert.Equal(ExitCodes.Error, badFile.Code);
        Assert.Contains("Pattern file not found", badFile.Stderr, StringComparison.Ordinal);

        var clash = Run("-s", "-E", "-F", "foo", tree.Root);
        Assert.Equal(ExitCodes.Error, clash.Code);
        Assert.Contains("mutually exclusive", clash.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_unreadable_file_is_an_error_and_a_walk_skip_is_not()
    {
        using var tree = Temp();
        var ok = tree.File("ok.txt");
        var locked = tree.File("locked.txt");
        File.WriteAllText(ok, "foo\n");
        File.WriteAllText(locked, "foo\n");
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);
            var walked = Run("foo", tree.Root, "-j", "1");
            Assert.Contains("ok.txt", walked.Stdout, StringComparison.Ordinal);
            if (!CanRead(locked))
            {
                Assert.Equal(ExitCodes.Success, walked.Code);
                Assert.DoesNotContain("locked.txt", walked.Stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("Unreadable", walked.Stderr, StringComparison.Ordinal);

                var named = Run("foo", locked, "-j", "1");
                Assert.Equal(ExitCodes.Error, named.Code);
                Assert.Contains("Unreadable:", named.Stderr, StringComparison.Ordinal);
                Assert.Equal("", named.Stdout.Trim());

                var hidden = Run("-s", "foo", locked, "-j", "1");
                Assert.Equal(ExitCodes.Error, hidden.Code);
                Assert.Equal("", hidden.Stderr);
                Assert.Equal("", hidden.Stdout);
            }
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Line_regexp_requires_the_whole_line()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nfoobar\nfoo\nfoo \n");
        File.WriteAllText(tree.File("b.txt"), "foobar\n");

        var lines = Run("-x", "foo", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, lines.Code);
        Assert.Equal("a.txt:1:1:foo\na.txt:3:1:foo", lines.Stdout.Trim());

        var only = Run("--line-regexp", "foo", tree.Root, "-o", "-j", "1");
        Assert.Equal(lines.Stdout, only.Stdout);

        var count = Run("-x", "-c", "foo", tree.Root, "-j", "1");
        Assert.Equal("a.txt:2", count.Stdout.Trim());

        var list = Run("-x", "-l", "foo", tree.Root, "-j", "1");
        Assert.Equal("a.txt", list.Stdout.Trim());

        var invert = Run("-x", "-v", "foo", tree.Root, "-j", "1");
        Assert.Equal("a.txt:2:1:foobar\na.txt:4:1:foo \nb.txt:1:1:foobar", invert.Stdout.Trim());

        var quiet = Run("-x", "-q", "foo", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, quiet.Code);
        Assert.Equal("", quiet.Stdout);

        var none = Run("-x", "-q", "foob", Path.Combine(tree.Root, "a.txt"), "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, none.Code);
        Assert.Equal("", none.Stdout);
    }

    [Fact]
    public void Line_regexp_is_case_insensitive_when_asked_and_respects_words()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "Foo\nfoo\nFoo \nfoo!\n!foo\n");
        var folded = Run("-x", "-i", "foo", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:Foo\na.txt:2:1:foo", folded.Stdout.Trim());

        var words = Run("-x", "-w", "foo", tree.Root, "-j", "1");
        Assert.Equal("a.txt:2:1:foo", words.Stdout.Trim());

        var bang = Run("-x", "-w", "foo!", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, bang.Code);
        var bangLine = Run("-x", "foo!", tree.Root, "-j", "1");
        Assert.Equal("a.txt:4:1:foo!", bangLine.Stdout.Trim());
    }

    [Fact]
    public void Line_regexp_regex_matches_the_whole_line_only()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "aaa\nb\n\na\naaa\n");
        File.WriteAllText(tree.File("crlf.txt"), "foo\r\nfoo \r\n");

        var plus = Run("-E", "-x", "a+", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:aaa\na.txt:4:1:a\na.txt:5:1:aaa", plus.Stdout.Trim());

        var either = Run("-E", "-x", "a|aaa", Path.Combine(tree.Root, "a.txt"), "-j", "1");
        Assert.Equal("a.txt:1:1:aaa\na.txt:4:1:a\na.txt:5:1:aaa", either.Stdout.Trim());

        var star = Run("-E", "-x", "a*", Path.Combine(tree.Root, "a.txt"), "-j", "1");
        Assert.Equal("a.txt:1:1:aaa\na.txt:3:1:\na.txt:4:1:a\na.txt:5:1:aaa", star.Stdout.Trim());

        var literalDot = Run("-F", "-x", "a.b", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, literalDot.Code);

        File.WriteAllText(tree.File("dots.txt"), "a.b\naxb\n");
        var fixedDot = Run("-xF", "a.b", tree.Root, "-j", "1");
        Assert.Equal("dots.txt:1:1:a.b", fixedDot.Stdout.Trim());
        var regexDot = Run("-xE", "a.b", tree.Root, "-j", "1");
        Assert.Equal("dots.txt:1:1:a.b\ndots.txt:2:1:axb", regexDot.Stdout.Trim());

        var crlf = Run("-x", "foo", Path.Combine(tree.Root, "crlf.txt"), "-j", "1");
        Assert.Equal("crlf.txt:1:1:foo", crlf.Stdout.Trim());

        File.WriteAllText(tree.File("span.txt"), "foo\nbar\n");
        var across = Run("--regex", "-U", "-x", "foo\nbar", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, across.Code);
    }

    [Fact]
    public void Line_regexp_engine_does_not_add_a_phantom_line()
    {
        var plan = MatchPlan.Create(new SearchQuery
        {
            Pattern = "a*",
            Path = ".",
            Regex = true,
            LineRegexp = true,
        });
        var hits = new List<SearchMatch>();
        MatchPlan.Collect("aaa\n", "a.txt", plan, hits);
        var only = Assert.Single(hits);
        Assert.Equal(1, only.Line);
        Assert.Equal("aaa", only.Text);
        Assert.Equal("aaa", only.LineText);

        hits.Clear();
        MatchPlan.Collect("aaa\nb\n\n", "a.txt", plan, hits);
        Assert.Equal([1, 3], hits.Select(hit => hit.Line).ToArray());

        var alternate = MatchPlan.Create(new SearchQuery
        {
            Pattern = "a|aaa",
            Path = ".",
            Regex = true,
            LineRegexp = true,
        });
        hits.Clear();
        MatchPlan.Collect("aaa\n", "a.txt", alternate, hits);
        Assert.Equal("aaa", Assert.Single(hits).Text);

        var literal = MatchPlan.Create(new SearchQuery
        {
            Pattern = "foo",
            Path = ".",
            LineRegexp = true,
        });
        hits.Clear();
        MatchPlan.Collect("foo\r\nfoo\r\n", "a.txt", literal, hits);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal("foo", hit.Text));
        Assert.All(hits, hit => Assert.Equal("foo", hit.LineText));
    }

    [Fact]
    public void Empty_pattern_with_line_regexp_matches_only_empty_lines()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\n\nbar\n");
        var pats = Path.Combine(Path.GetTempPath(), "netgrep-posix-pats-" + Guid.NewGuid().ToString("n"));
        File.WriteAllText(pats, "\n");
        try
        {
            var all = Run("-f", pats, tree.Root, "-j", "1");
            Assert.Equal("a.txt:1:1:foo\na.txt:2:1:\na.txt:3:1:bar", all.Stdout.Trim());

            var whole = Run("-x", "-f", pats, tree.Root, "-j", "1");
            Assert.Equal("a.txt:2:1:", whole.Stdout.Trim());

            var regex = Run("-E", "-x", "-f", pats, tree.Root, "-j", "1");
            Assert.Equal(whole.Stdout, regex.Stdout);
        }
        finally
        {
            File.Delete(pats);
        }
    }

    [Fact]
    public void Several_patterns_are_ored_as_whole_lines()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nfoobar\nbar\n");
        var run = Run("-x", "-e", "foo", "-e", "bar", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:foo\na.txt:3:1:bar", run.Stdout.Trim());
    }

    [Fact]
    public void Bundled_short_options_match_the_split_form()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "Foo\nFoox\nfoo\npat\npath\n");

        var bundled = Run("-nEix", "f.o", tree.Root, "-j", "1");
        var split = Run("-n", "-E", "-i", "-x", "f.o", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, bundled.Code);
        Assert.Equal(split.Stdout, bundled.Stdout);
        Assert.Equal("a.txt:1:1:Foo\na.txt:3:1:foo", bundled.Stdout.Trim());

        var attached = Run("-nEepat", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, attached.Code);
        Assert.Contains("a.txt:4:1:pat", attached.Stdout, StringComparison.Ordinal);
        Assert.Contains("a.txt:5:1:path", attached.Stdout, StringComparison.Ordinal);

        var quiet = Run("-qefoo", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, quiet.Code);
        Assert.Equal("", quiet.Stdout);

        var found = Run("-qepat", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, found.Code);
        Assert.Equal("", found.Stdout);
    }

    [Fact]
    public void Double_dash_keeps_a_dash_pattern_literal()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "-nE\nfoo\n");
        var run = Run("-j", "1", "--", "-nE", tree.Root);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("a.txt:1:1:-nE", run.Stdout.Trim());
    }

    [Fact]
    public void Line_regexp_still_uses_the_trigram_prefilter()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep\nnetgrep extra\n");
        Indexer.Build(tree.Root);

        var outcome = Searcher.Search(new SearchQuery
        {
            Pattern = "netgrep",
            Path = tree.Root,
            Regex = true,
            LineRegexp = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        Assert.True(outcome.UsedTrigramPrefilter);
        var hit = Assert.Single(outcome.Matches);
        Assert.Equal("a.txt", hit.Path);
        Assert.Equal(1, hit.Line);
        Assert.Equal("netgrep", hit.Text);
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RunResult Run(params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(args).GetAwaiter().GetResult();
        return new RunResult(
            code,
            capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal),
            capture.Stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private readonly record struct RunResult(int Code, string Stdout, string Stderr);

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("netgrep-posix-").FullName;

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
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        public Capture()
        {
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        public string Stdout => _out.ToString();

        public string Stderr => _err.ToString();

        public void Dispose()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }
}

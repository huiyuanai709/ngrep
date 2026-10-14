using System.Text;
using System.Text.RegularExpressions;
using Ngrep.Cli;
using Ngrep.Search;

namespace Ngrep.Tests;

public class RegexEngineTests
{
    public static IEnumerable<object[]> Spans()
    {
        var texts = new[]
        {
            "",
            "a",
            "ab",
            "ba",
            "aaabaa",
            "aaaaaa",
            "xx NEEDLE NEEDLE9 yy",
            "xx foo a bar y bar\nfoo no\nfoo z bar",
            "foo\nbar\n",
            "foo\r\nbar\n",
            "word wording word",
            "token = 1\ntoken=2\nno",
            "abxyzcc",
            "x\nbar\nbar ",
            "a  \nb\t",
            "foo.bar\nfooXbar\n",
            "NEEDLE\nNEEDLE9\n",
            new string('x', 180) + "abc NEEDLE9 foo bar\n" + new string('y', 90) + "ABCxyz",
            new string('c', 7),
            new string('c', 8),
            new string('c', 20),
            new string('x', 57) + new string('c', 7) + "yyyy",
            new string('x', 60) + new string('c', 12) + new string('y', 40),
            string.Concat(Enumerable.Repeat("cxxxxxxx", 40)),
        };
        var patterns = new[]
        {
            "a",
            "a*",
            "a+",
            "a+?",
            "a{2,4}",
            "a{2,4}?",
            "a|ab",
            "ab|a",
            "foo|bar",
            "NEEDLE[0-9]",
            "foo.*bar",
            "foo.*?bar",
            "^bar$",
            "foo$",
            @"\bword\b",
            @"token\s*=\s*[0-9]+",
            "[abc]{2}",
            "[abc]{8}",
            "[abc]{8,12}",
            "[A-Za-z]{3}",
            "n.t",
            @"foo\.bar",
            ".",
            @"\d+",
            "a+",
        };
        foreach (var pattern in patterns.Distinct())
        {
            foreach (var text in texts)
            {
                yield return new object[] { pattern, text, false };
            }
        }

        yield return new object[] { "foo.bar", "foo\nbar", true };
    }

    [Theory]
    [MemberData(nameof(Spans))]
    public void Byte_automaton_matches_nonbacktracking_spans(string pattern, string text, bool dotAll)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.NonBacktracking;
        if (dotAll)
        {
            options |= RegexOptions.Singleline;
        }

        var regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));
        var expected = new List<string>();
        foreach (var match in regex.EnumerateMatches(text))
        {
            if (!dotAll && text.IndexOf('\n', match.Index, match.Length) >= 0)
            {
                continue;
            }

            expected.Add($"{match.Index}:{match.Length}:{text.Substring(match.Index, match.Length)}");
        }

        Assert.True(ByteAutomaton.TryCompile(pattern, new ByteAutomaton.Options(false, dotAll), out var program), pattern);
        var hits = new List<ByteMatch>();
        program.Collect(bytes, dotAll, stopAtFirst: false, hits);
        var actual = hits.Select(hit => $"{hit.Index}:{hit.Length}:{text.Substring(hit.Index, hit.Length)}").ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(60)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(70)]
    public void Class_run_matches_across_a_simd_chunk_boundary(int pad)
    {
        var text = new string('x', pad) + new string('c', 8) + "y";
        Byte_automaton_matches_nonbacktracking_spans("[abc]{8}", text, false);
        Byte_automaton_matches_nonbacktracking_spans("[abc]{2,4}", text, false);
    }

    [Fact]
    public void Default_regex_rejects_backreferences_and_lookaround()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a.txt"), "abcabc\nabc\n");

        using var capture = new Capture();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-E", @"(abc)\1", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("-P", capture.Stderr, StringComparison.Ordinal);
        Assert.Contains("non-backtracking", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-E", "(?=abc)", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("-P", capture.Stderr, StringComparison.Ordinal);

        capture.Clear();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-E", "(?>abc)", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("atomic", capture.Stderr, StringComparison.OrdinalIgnoreCase);

        capture.Clear();
        Assert.Equal(ExitCodes.Success, NgrepCli.Run(["-P", @"(abc)\1", tree.Root, "-j", "1"]).GetAwaiter().GetResult());
        Assert.Contains("a.txt:1:1:abcabc", capture.Stdout, StringComparison.Ordinal);

        var back = MatchPlan.Create(new SearchQuery { Pattern = @"(abc)\1", Path = tree.Root, Regex = true, Backtracking = true });
        Assert.False(back.NonBacktracking);
        Assert.All(back.Regexes!, regex => Assert.False(regex.Options.HasFlag(RegexOptions.NonBacktracking)));
        Assert.All(back.Regexes!, regex => Assert.False(regex.Options.HasFlag(RegexOptions.Compiled)));
        Assert.Null(back.BytePrograms);
    }

    [Fact]
    public void Pcre_and_fixed_strings_are_mutually_exclusive()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("a.txt"), "abc\n");
        using var capture = new Capture();
        Assert.Equal(ExitCodes.Error, NgrepCli.Run(["-P", "-F", "abc", tree.Root]).GetAwaiter().GetResult());
        Assert.Contains("mutually exclusive", capture.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapped_read_finds_a_late_literal_and_an_early_list_hit()
    {
        using var tree = new TempDir();
        var body = new StringBuilder();
        for (var i = 0; i < 8000; i++)
        {
            body.Append(i == 0 ? "EARLY token\n" : "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx token\n");
        }

        body.Append("TAILNEEDLE\n");
        File.WriteAllText(tree.File("wide.txt"), body.ToString());
        Assert.True(new FileInfo(Path.Combine(tree.Root, "wide.txt")).Length >= TextFilesThreshold());

        var listed = Searcher.Search(new SearchQuery
        {
            Pattern = "EARLY",
            Path = tree.Root,
            ForceScan = true,
            NoIgnore = true,
            Report = ReportMode.FilesWithMatches,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        var early = Assert.Single(listed.Matches);
        Assert.Equal(1, early.Line);
        Assert.Equal("EARLY", early.Text);

        var tail = Searcher.Search(new SearchQuery
        {
            Pattern = "TAILNEEDLE",
            Path = tree.Root,
            ForceScan = true,
            NoIgnore = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        var hit = Assert.Single(tail.Matches);
        Assert.Equal("TAILNEEDLE", hit.Text);

        var regex = Searcher.Search(new SearchQuery
        {
            Pattern = "TAILNEEDLE|EARLY",
            Path = tree.Root,
            Regex = true,
            ForceScan = true,
            NoIgnore = true,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();
        Assert.Equal(2, regex.Matches.Count);
        Assert.Contains(regex.Matches, match => match.Text == "EARLY");
        Assert.Contains(regex.Matches, match => match.Text == "TAILNEEDLE");
    }

    private static int TextFilesThreshold() => 256 * 1024;

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-re-").FullName;

        public string Root { get; }

        public string File(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Capture : IDisposable
    {
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();
        private readonly TextWriter _oldOut = Console.Out;
        private readonly TextWriter _oldErr = Console.Error;

        public Capture()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }

        public string Stdout => _stdout.ToString();

        public string Stderr => _stderr.ToString();

        public void Clear()
        {
            _stdout.GetStringBuilder().Clear();
            _stderr.GetStringBuilder().Clear();
        }

        public void Dispose()
        {
            Console.SetOut(_oldOut);
            Console.SetError(_oldErr);
        }
    }
}

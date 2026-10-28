using System.Text;
using Ngrep.Search;

namespace Ngrep.Tests;

public class DfaLineTests
{
    [Theory]
    [InlineData("\\w+Exception")]
    [InlineData("[A-Za-z]+Exception")]
    [InlineData(".*Exception")]
    [InlineData("[a-z]+[0-9]+")]
    [InlineData("^[a-z]+[0-9]+$")]
    [InlineData("[a-f][0-9][a-f]")]
    public void Dfa_lines_match_the_decoded_engine(string pattern)
    {
        const string text = "fooException\nbar Exception baz\nqux123\nabc123def\na1b\nTODO here\nnone\n";
        AssertDfaMatchesDecoded(pattern, text);
    }

    [Theory]
    [InlineData("[a-z]+.*")]
    [InlineData(".*Exception")]
    [InlineData("[a-z]+[0-9]+")]
    public void Dfa_lines_match_the_decoded_engine_with_crlf(string pattern)
    {
        const string text = "fooException\r\nbar Exception baz\r\nqux123\r\n";
        AssertDfaMatchesDecoded(pattern, text);
    }

    [Fact]
    public void Dfa_raw_rows_copy_trimmed_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes("qux123\r\nnone\r\nabc456def\r\n");
        var raw = new RawSink();

        Assert.True(MatchPlan.TryCollectAutomaton(bytes, "a.txt", Plan("[a-z]+[0-9]+"), [], raw, out var matched));
        Assert.True(matched);
        Assert.Equal(["qux123", "abc456def"], Bodies(raw));
        Assert.Equal([1, 3], raw.Rows.Select(row => row.Line).ToArray());
        Assert.Equal([1, 1], raw.Rows.Select(row => row.Column).ToArray());
    }

    [Theory]
    [InlineData("\\A.*Exception")]
    [InlineData(".*Exception\\z")]
    public void Dfa_lines_decline_a_pattern_that_reads_the_whole_file(string pattern)
    {
        var bytes = Encoding.ASCII.GetBytes("fooException");
        Assert.False(MatchPlan.TryCollectAutomaton(bytes, "a.txt", Plan(pattern), [], null, out _));
    }

    [Theory]
    [InlineData("\\w+Exception")]
    [InlineData("[a-z]+[0-9]+")]
    [InlineData("[a-z]+[0-9]+$")]
    public void Dfa_files_without_match_agrees_with_the_decoded_engine(string pattern)
    {
        foreach (var text in new[] { "fooException\nqux123\n", "plain alpha\n" })
        {
            var plan = MatchPlan.Create(new SearchQuery
            {
                Pattern = pattern,
                Path = ".",
                Regex = true,
                Report = ReportMode.FilesWithoutMatch,
                MaxDegreeOfParallelism = 1,
            });

            var bytes = Encoding.ASCII.GetBytes(text);
            Assert.True(MatchPlan.TryCollectAutomaton(bytes, "a.txt", plan, [], null, out var matched));
            Assert.Equal(MatchPlan.HasMatch(text, plan), matched);
        }
    }

    private static void AssertDfaMatchesDecoded(string pattern, string text)
    {
        var plan = Plan(pattern);
        Assert.NotNull(plan.BytePrograms);
        Assert.IsType<DfaProgram>(plan.BytePrograms![0]);

        var bytes = Encoding.ASCII.GetBytes(text);
        var actual = new List<SearchMatch>();
        Assert.True(MatchPlan.TryCollectAutomaton(bytes, "a.txt", plan, actual, null, out var matched));
        Assert.True(matched);

        var expected = new List<SearchMatch>();
        MatchPlan.Collect(text, "a.txt", plan, expected);

        Assert.Equal(Describe(expected), Describe(actual));
    }

    private static string[] Describe(IEnumerable<SearchMatch> matches) =>
        matches.Select(match => $"{match.Line}:{match.Column}:{match.Text}:{match.LineText}").ToArray();

    private static string[] Bodies(RawSink raw) =>
        raw.Rows.Select(row => Encoding.ASCII.GetString(raw.Buffer.WrittenSpan.Slice(row.Start, row.Length))).ToArray();

    private static MatchPlan Plan(string pattern) =>
        MatchPlan.Create(new SearchQuery
        {
            Pattern = pattern,
            Path = ".",
            Regex = true,
            NeedLineText = true,
            NeedMatchText = true,
            MaxDegreeOfParallelism = 1,
        });
}

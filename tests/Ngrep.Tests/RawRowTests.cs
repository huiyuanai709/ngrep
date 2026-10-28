using System.Text;
using Ngrep.Search;

namespace Ngrep.Tests;

public class RawRowTests
{
    [Fact]
    public void Literal_alt_raw_rows_copy_bytes_and_keep_one_row_per_line()
    {
        var bytes = Encoding.ASCII.GetBytes("TODO one\nnone\nFIXME TODO two\n");
        var raw = new RawSink();
        var matches = new List<SearchMatch>();

        Assert.True(MatchPlan.TryCollectAutomaton(bytes, "a.txt", Plan("TODO|FIXME"), matches, raw, out var matched));
        Assert.True(matched);
        Assert.Empty(matches);
        Assert.Equal(["TODO one", "FIXME TODO two"], Bodies(raw));
        Assert.Equal([1, 3], raw.Rows.Select(row => row.Line).ToArray());
        Assert.Equal([1, 1], raw.Rows.Select(row => row.Column).ToArray());
    }

    [Fact]
    public void Literal_alt_raw_rows_fall_back_on_invalid_utf8()
    {
        var bytes = Encoding.ASCII.GetBytes("TODO ")
            .Concat(new byte[] { 0xFF })
            .Concat(Encoding.ASCII.GetBytes(" one\n"))
            .ToArray();

        Assert.False(MatchPlan.TryCollectAutomaton(bytes, "a.txt", Plan("TODO|FIXME"), [], new RawSink(), out _));
    }

    [Fact]
    public void Line_prefilter_raw_rows_copy_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes("fooException\nnone\nBarException\n");
        var raw = new RawSink();
        var matches = new List<SearchMatch>();

        Assert.True(MatchPlan.TryCollectBytes(bytes, "a.txt", Plan("[A-Za-z]+Exception"), matches, raw, out var matched));
        Assert.True(matched);
        Assert.Empty(matches);
        Assert.Equal(["fooException", "BarException"], Bodies(raw));
        Assert.Equal([1, 3], raw.Rows.Select(row => row.Line).ToArray());
    }

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

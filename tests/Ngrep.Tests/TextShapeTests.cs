using System.Text.Json;
using System.Text.RegularExpressions;
using Ngrep.Cli;

namespace Ngrep.Tests;

public class TextShapeTests
{
    [Fact]
    public void Line_numbers_and_columns_can_be_omitted_and_the_last_flag_wins()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nbeta netgrep gamma\nomega\n");

        var plain = Run("netgrep", tree.Root, "-j", "1");
        Assert.Equal("a.txt:2:6:beta netgrep gamma", plain.Stdout.Trim());

        var noLine = Run("netgrep", tree.Root, "-N", "-j", "1");
        Assert.Equal("a.txt:6:beta netgrep gamma", noLine.Stdout.Trim());
        Assert.Equal(noLine.Stdout, Run("netgrep", tree.Root, "--no-line-number", "-j", "1").Stdout);

        var noColumn = Run("netgrep", tree.Root, "--no-column", "-j", "1");
        Assert.Equal("a.txt:2:beta netgrep gamma", noColumn.Stdout.Trim());

        var neither = Run("netgrep", tree.Root, "-N", "--no-column", "-j", "1");
        Assert.Equal("a.txt:beta netgrep gamma", neither.Stdout.Trim());

        var lineBack = Run("-N", "-n", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(plain.Stdout, lineBack.Stdout);
        var lineOff = Run("-nN", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(noLine.Stdout, lineOff.Stdout);

        var columnBack = Run("--no-column", "--column", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(plain.Stdout, columnBack.Stdout);
        var columnOff = Run("--column", "--no-column", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(noColumn.Stdout, columnOff.Stdout);
    }

    [Fact]
    public void Context_drops_the_line_number_and_keeps_the_dash()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nbeta netgrep gamma\nomega\n");

        var both = Run("netgrep", tree.Root, "-C", "1", "-N", "--no-column", "-j", "1");
        Assert.Equal(
            "a.txt-alpha\na.txt:beta netgrep gamma\na.txt-omega",
            both.Stdout.Trim());

        var lineOnly = Run("netgrep", tree.Root, "-C", "1", "--no-column", "-j", "1");
        Assert.Equal(
            "a.txt-1-alpha\na.txt:2:beta netgrep gamma\na.txt-3-omega",
            lineOnly.Stdout.Trim());
    }

    [Fact]
    public void Vimgrep_forces_path_line_column_and_no_heading()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "netgrep netgrep\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");

        var run = Run("--vimgrep", "-N", "--no-column", "--heading", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal(
            "a.txt:1:1:netgrep netgrep\na.txt:1:9:netgrep netgrep\nb.txt:1:1:netgrep",
            run.Stdout.Trim());
        Assert.DoesNotContain("\n\n", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_separates_paths_for_matches_counts_and_lists()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nnetgrep\nomega\n");
        File.WriteAllText(tree.File("b.txt"), "netgrep\n");
        File.WriteAllText(tree.File("miss.txt"), "zzzzzz\n");

        var match = Run("netgrep", tree.Root, "-0", "-j", "1", "--no-heading");
        Assert.Equal("a.txt\02:1:netgrep\nb.txt\01:1:netgrep\n", match.Stdout);

        var noNumbers = Run("-H", "netgrep", Path.Combine(tree.Root, "a.txt"), "-0", "-N", "--no-column", "-C", "1", "-j", "1");
        Assert.Equal("a.txt\0alpha\na.txt\0netgrep\na.txt\0omega\n", noNumbers.Stdout);

        var list = Run("-l0", "netgrep", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, list.Code);
        Assert.DoesNotContain('\n', list.Stdout);
        Assert.EndsWith("\0", list.Stdout);
        Assert.Equal(["a.txt", "b.txt"], SplitNull(list.Stdout));

        var count = Run("netgrep", tree.Root, "-c", "--null", "-j", "1");
        Assert.Equal("a.txt\01\nb.txt\01\n", count.Stdout);

        var without = Run("--files-without-match", "netgrep", tree.Root, "-0", "-j", "1");
        Assert.DoesNotContain('\n', without.Stdout);
        Assert.Equal(["miss.txt"], SplitNull(without.Stdout));

        var files = Run("--files", "-0", tree.Root);
        Assert.Equal(ExitCodes.Success, files.Code);
        Assert.DoesNotContain('\n', files.Stdout);
        Assert.Equal(["a.txt", "b.txt", "miss.txt"], SplitNull(files.Stdout));
        Assert.Equal(files.Stdout, Run("--files", "--null", tree.Root).Stdout);

        var heading = Run("netgrep", tree.Root, "--heading", "-0", "-j", "1");
        Assert.Equal("a.txt\02:1:netgrep\n\nb.txt\01:1:netgrep\n", heading.Stdout);
    }

    [Fact]
    public void Fixed_replace_is_literal_and_does_not_write_the_file()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "foo foo\n");

        var run = Run("-F", "-H", "foo", path, "-r", "$1", "-j", "1");
        Assert.Equal("a.txt:1:1:$1 $1\na.txt:1:5:$1 $1", run.Stdout.Trim());
        Assert.Equal("foo foo\n", File.ReadAllText(path));

        var empty = Run("-F", "-H", "foo", path, "-r", "", "-j", "1");
        Assert.Equal("a.txt:1:1: \na.txt:1:5: ", empty.Stdout.TrimEnd('\r', '\n'));
        Assert.Equal("foo foo\n", File.ReadAllText(path));

        var attached = Run("-F", "-H", "-rX", "foo", path, "-j", "1");
        Assert.Equal("a.txt:1:1:X X\na.txt:1:5:X X", attached.Stdout.Trim());

        var captured = Run("-H", "(f)oo", path, "-r", "[$1]", "-j", "1");
        Assert.Equal("a.txt:1:1:[f] [f]\na.txt:1:5:[f] [f]", captured.Stdout.Trim());
    }

    [Fact]
    public void Regex_replace_expands_groups_and_only_matching_prints_the_replacement()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo-123 bar-9\n");

        var swapped = Run("-E", @"(\w+)-(\d+)", "-r", "$2/$1", tree.Root, "-j", "1");
        Assert.Equal(
            "a.txt:1:1:123/foo 9/bar\na.txt:1:9:123/foo 9/bar",
            swapped.Stdout.Trim());

        var only = Run("-E", @"(\w+)-(\d+)", "-r", "$2", "-o", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:123\na.txt:1:9:9", only.Stdout.Trim());

        var named = Run("-E", @"(?<num>\d+)", "-r", "n=$num", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:5:foo-n=123 bar-n=9\na.txt:1:13:foo-n=123 bar-n=9", named.Stdout.Trim());

        var braced = Run("-E", @"(?<num>\d+)", "-r", "${num}x", tree.Root, "-j", "1");
        Assert.Contains("123x", braced.Stdout, StringComparison.Ordinal);
        Assert.Contains("9x", braced.Stdout, StringComparison.Ordinal);

        var dollar = Run("-E", "foo", "-r", "$$", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:$-123 bar-9", dollar.Stdout.Trim());

        var whole = Run("-E", "foo", "-r", "<$0:$&>", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:<foo:foo>-123 bar-9", whole.Stdout.Trim());

        var missing = Run("-E", "(foo)", "-r", "[$2]", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:[]-123 bar-9", missing.Stdout.Trim());

        var suffix = Run("-E", "(foo)", "-r", "${1}a", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:fooa-123 bar-9", suffix.Stdout.Trim());

        // $1a is the group named 1a, which .NET will not define. It is not group 1 plus "a".
        var namedDigits = Run("-E", "(foo)", "-r", "$1a", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:1:-123 bar-9", namedDigits.Stdout.Trim());
    }

    [Fact]
    public void Replace_keeps_the_last_quantified_capture_on_both_engines()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "xaaay\n");

        var linear = Run("-E", "(a)+", "-r", "[$1]", tree.Root, "-j", "1");
        Assert.Equal("a.txt:1:2:x[a]y", linear.Stdout.Trim());

        var back = Run("-P", "(a)+", "-r", "[$1]", tree.Root, "-j", "1");
        Assert.Equal(linear.Stdout, back.Stdout);

        File.WriteAllText(tree.File("c.txt"), "ac\n");
        var optional = Run("-E", "a(b)?c", "-r", "[$1]", tree.Root, "-j", "1");
        Assert.Equal("c.txt:1:1:[]", optional.Stdout.Trim());

        File.WriteAllText(tree.File("d.txt"), "Foo\n");
        var smart = Run("-S", "(foo)", tree.Root, "-r", "[$1]", "-j", "1");
        Assert.Equal("d.txt:1:1:[Foo]", smart.Stdout.Trim());

        var wholeLine = Run("-E", "-x", "(f)oo", "-r", "$1", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.NoMatches, wholeLine.Code);

        File.WriteAllText(tree.File("b.txt"), "foo\n");
        var anchored = Run("-E", "-x", "(f)oo", "-r", "$1", tree.Root, "-j", "1");
        Assert.Equal("b.txt:1:1:f", anchored.Stdout.Trim());
    }

    [Fact]
    public void NonBacktracking_records_one_capture_where_backtracking_records_each()
    {
        var linear = new Regex("(a)+", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        var back = new Regex("(a)+", RegexOptions.CultureInvariant);
        Assert.Equal("a", linear.Match("aaa").Groups[1].Value);
        Assert.Equal("a", back.Match("aaa").Groups[1].Value);
        Assert.Single(linear.Match("aaa").Groups[1].Captures);
        Assert.Equal(3, back.Match("aaa").Groups[1].Captures.Count);
    }

    [Fact]
    public void Invert_and_machine_formats_ignore_text_shape_flags()
    {
        using var tree = Temp();
        var path = tree.File("a.txt");
        File.WriteAllText(path, "foo\nbar\n");

        var inverted = Run("-H", "foo", path, "-v", "-r", "Z", "-N", "--no-column", "-j", "1");
        Assert.Equal("a.txt:bar", inverted.Stdout.Trim());
        Assert.Equal("foo\nbar\n", File.ReadAllText(path));

        var ndjson = Run("foo", path, "--format", "ndjson", "-r", "Z", "-N", "--no-column", "--vimgrep", "-0", "-j", "1");
        Assert.DoesNotContain('\0', ndjson.Stdout);
        using (var doc = JsonDocument.Parse(ndjson.Stdout.Trim()))
        {
            Assert.Equal("foo", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal("foo", doc.RootElement.GetProperty("lineText").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("line").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("column").GetInt32());
        }

        var json = Run("foo", path, "--format", "json", "-r", "Z", "-N", "-j", "1");
        using (var doc = JsonDocument.Parse(json.Stdout))
        {
            var hit = doc.RootElement.GetProperty("matches")[0];
            Assert.Equal("foo", hit.GetProperty("text").GetString());
            Assert.Equal(1, hit.GetProperty("line").GetInt32());
            Assert.Equal(1, hit.GetProperty("column").GetInt32());
        }

        var rgjson = Run("foo", path, "--format", "rgjson", "-r", "Z", "-0", "-j", "1");
        Assert.DoesNotContain('\0', rgjson.Stdout);
        Assert.Contains("\"text\":\"foo\"", rgjson.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_colors_the_inserted_text_and_leaves_context_alone()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "alpha\nbeta netgrep gamma\nomega\n");

        var colored = Run("netgrep", tree.Root, "-r", "Z", "--color", "always", "-C", "1", "-j", "1");
        const string path = "\u001b[35ma.txt\u001b[0m";
        const string line = "\u001b[32m";
        Assert.Contains($"{path}-{line}1\u001b[0m-alpha", colored.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{path}:{line}2\u001b[0m:\u001b[34m6\u001b[0m:beta \u001b[1;31mZ\u001b[0m gamma", colored.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{path}-{line}3\u001b[0m-omega", colored.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("netgrep", colored.Stdout, StringComparison.Ordinal);
    }

    private static string[] SplitNull(string text) =>
        text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

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
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-shape-").FullName;

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

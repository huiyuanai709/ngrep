using Ngrep.Cli;
using Ngrep.Indexing;

namespace Ngrep.Tests;

public class FilesAndUnrestrictedTests
{
    [Fact]
    public void Files_lists_the_walk_and_honors_filters()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "NEEDLE\n");
        File.WriteAllText(tree.File("b.cs"), "code\n");
        File.WriteAllText(tree.File("sub/c.txt"), "nested\n");
        File.WriteAllText(tree.File("skip.log"), "ignored\n");
        File.WriteAllText(tree.File(".secret"), "hidden-needle\n");
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllText(tree.File(".git/HEAD"), "ref: refs/heads/main\n");
        File.WriteAllBytes(tree.File("icon.bin"), "xx\0NEEDLE\n"u8.ToArray());
        File.CreateSymbolicLink(tree.File("link.txt"), tree.File("a.txt"));

        var plain = Lines("--files", tree.Root);
        Assert.Equal(ExitCodes.Success, plain.Code);
        Assert.Equal("a.txt\nb.cs\nicon.bin\nsub/c.txt", plain.Stdout.Trim());
        Assert.DoesNotContain("scanned", plain.Stderr, StringComparison.Ordinal);

        Assert.Equal(plain.Stdout, Lines("search", "--files", tree.Root).Stdout);
        Assert.Equal(["b.cs"], Paths("--files", "-g", "*.cs", tree.Root));
        Assert.Equal(["b.cs"], Paths("--files", "-t", "cs", tree.Root));
        Assert.Equal([], Paths("--files", "-g", "*.log", tree.Root));
        Assert.Equal(["skip.log"], Paths("--files", "-u", "-g", "*.log", tree.Root));
        Assert.Equal(["skip.log"], Paths("--files", "--no-ignore", "-g", "*.log", tree.Root));

        var unrestricted = Lines("--files", "-uu", tree.Root);
        Assert.Equal(
            ".gitignore\n.secret\na.txt\nb.cs\nicon.bin\nskip.log\nsub/c.txt",
            unrestricted.Stdout.Trim());
        Assert.Equal(unrestricted.Stdout, Lines("--files", "-u", "-u", tree.Root).Stdout);
        Assert.Equal(unrestricted.Stdout, Lines("--files", "--no-ignore", "--hidden", tree.Root).Stdout);
        Assert.Equal(unrestricted.Stdout, Lines("--files", "-uuu", tree.Root).Stdout);
        Assert.Equal(unrestricted.Stdout, Lines("--files", "-u", "--hidden", tree.Root).Stdout);
        Assert.DoesNotContain(".git/", unrestricted.Stdout, StringComparison.Ordinal);

        Assert.Equal([".secret"], Paths("--files", tree.File(".secret")));
        var ignoredFile = Lines("--files", tree.File("skip.log"));
        Assert.Equal(ExitCodes.NoMatches, ignoredFile.Code);
        Assert.Equal("", ignoredFile.Stdout);
        Assert.Equal(["skip.log"], Paths("--files", "-u", tree.File("skip.log")));

        Indexer.Build(tree.Root);
        File.WriteAllText(tree.File("later.txt"), "after index\n");
        var listed = Paths("--files", tree.Root);
        Assert.Contains("later.txt", listed);
        Assert.Contains("a.txt", listed);
        Assert.Contains("icon.bin", listed);
    }

    [Fact]
    public void Files_exit_codes_follow_ripgrep()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "NEEDLE\n");
        Directory.CreateDirectory(tree.File("empty"));

        var quiet = Lines("--files", "-q", tree.Root);
        Assert.Equal(ExitCodes.Success, quiet.Code);
        Assert.Equal("", quiet.Stdout);

        var none = Lines("--files", tree.File("empty"));
        Assert.Equal(ExitCodes.NoMatches, none.Code);
        Assert.Equal("", none.Stdout);
        Assert.Equal(ExitCodes.NoMatches, Lines("--files", "-q", tree.File("empty")).Code);

        var missing = Lines("--files", tree.File("nope"));
        Assert.Equal(ExitCodes.Error, missing.Code);
        Assert.Contains("Path not found", missing.Stderr, StringComparison.Ordinal);
        var suppressed = Lines("--files", "-s", tree.File("nope"));
        Assert.Equal(ExitCodes.Error, suppressed.Code);
        Assert.Equal("", suppressed.Stderr);

        Assert.Equal(ExitCodes.Error, Lines("--files", "-e", "NEEDLE", tree.Root).Code);
        Assert.Equal(ExitCodes.Error, Lines("--files", "-l", tree.Root).Code);
        Assert.Equal(ExitCodes.Error, Lines("--files", "-c", tree.Root).Code);
        Assert.Equal(ExitCodes.Error, Lines("--files", tree.Root, tree.File("a.txt")).Code);
        Assert.Contains("cannot be combined", Lines("--files", "-l", tree.Root).Stderr, StringComparison.Ordinal);

        var formatted = Lines("--files", "--format", "json", tree.Root);
        Assert.Equal("a.txt", formatted.Stdout.Trim());
        Assert.DoesNotContain("{", formatted.Stdout, StringComparison.Ordinal);

        var withMatches = Lines("NEEDLE", tree.Root, "-l", "-j", "1");
        Assert.Equal(ExitCodes.Success, withMatches.Code);
        Assert.Equal("a.txt", withMatches.Stdout.Trim());
    }

    [Fact]
    public void Unrestricted_stacks_and_binary_matches_are_printed()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "-u\n");
        File.WriteAllText(tree.File("skip.log"), "ignored NEEDLE\n-u\n");
        File.WriteAllText(tree.File(".secret"), "hidden-needle\n");
        File.WriteAllText(tree.File(".gitignore"), "*.log\n");
        File.WriteAllBytes(tree.File("icon.bin"), "xx\0NEEDLE\n"u8.ToArray());

        Assert.Equal(ExitCodes.NoMatches, Lines("NEEDLE", tree.Root, "-j", "1").Code);
        var two = Lines("-uu", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, two.Code);
        Assert.Contains("skip.log", two.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("icon.bin", two.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(".secret", two.Stdout, StringComparison.Ordinal);

        var binary = Lines("--binary", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, binary.Code);
        Assert.Contains("icon.bin", binary.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEEDLE", binary.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.log", binary.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(".secret", binary.Stdout, StringComparison.Ordinal);

        var triple = Lines("-uuu", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, triple.Code);
        Assert.Contains("icon.bin", triple.Stdout, StringComparison.Ordinal);
        Assert.Contains("skip.log", triple.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(".secret", triple.Stdout, StringComparison.Ordinal);
        Assert.Equal(SortedPaths(triple.Stdout), SortedPaths(Lines("-u", "-u", "-u", "NEEDLE", tree.Root, "-j", "1").Stdout));
        Assert.Equal(SortedPaths(triple.Stdout), SortedPaths(Lines("--unrestricted", "--unrestricted", "--unrestricted", "NEEDLE", tree.Root, "-j", "1").Stdout));

        Assert.Equal(ExitCodes.NoMatches, Lines("-u", "hidden-needle", tree.Root, "-j", "1").Code);
        Assert.Equal(ExitCodes.Success, Lines("-uu", "hidden-needle", tree.Root, "-j", "1").Code);
        Assert.Equal(ExitCodes.Success, Lines("-u", "--hidden", "hidden-needle", tree.Root, "-j", "1").Code);
        Assert.Equal(ExitCodes.Success, Lines("-u", "ignored", tree.Root, "-j", "1").Code);

        var fourth = Lines("-uuuu", "NEEDLE", tree.Root);
        Assert.Equal(ExitCodes.Error, fourth.Code);
        Assert.Contains("at most 3", fourth.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Error, Lines("-u", "-u", "-u", "-u", "NEEDLE", tree.Root).Code);

        Indexer.Build(tree.Root);
        var indexed = Lines("--binary", "NEEDLE", tree.Root, "-j", "1");
        Assert.Equal(ExitCodes.Success, indexed.Code);
        Assert.Contains("icon.bin", indexed.Stdout, StringComparison.Ordinal);
        Assert.Contains("--binary", indexed.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.NoMatches, Lines("NEEDLE", tree.Root, "-j", "1").Code);

        var literal = Lines("-F", "-j", "1", "--", "-u", tree.Root);
        Assert.Equal(ExitCodes.Success, literal.Code);
        Assert.Contains("a.txt", literal.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.log", literal.Stdout, StringComparison.Ordinal);
    }

    private static string[] SortedPaths(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2)[0])
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string[] Paths(params string[] args) =>
        Lines(args).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static RunResult Lines(params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(args).GetAwaiter().GetResult();
        return new RunResult(code, capture.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal), capture.Stderr);
    }

    private readonly record struct RunResult(int Code, string Stdout, string Stderr);

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-files-").FullName;

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

using Ngrep.Cli;
using Ngrep.Ignore;

namespace Ngrep.Tests;

/// <summary>
/// A named file is checked against its own path and ignore stack. It does not need
/// a listing of the parent tree, which used to make a single-file search cost a walk.
/// </summary>
public class SingleFileSearchTests
{
    [Fact]
    public void Named_file_does_not_list_the_rest_of_the_tree()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File("target.txt"), "needle\n");
        for (var d = 0; d < 20; d++)
        {
            var dir = Path.GetDirectoryName(tree.File($"bulk/d{d}/f.txt"))!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "needle\n");
        }

        var listed = new List<string>();
        TreeWalker.BeforeCollect = listed.Add;
        try
        {
            var run = Run("--scan", "needle", tree.File("target.txt"));
            Assert.Equal(ExitCodes.Success, run.Code);
            Assert.Contains("needle", run.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TreeWalker.BeforeCollect = null;
        }

        // A named file is decided from its own path. It is not found by listing the tree.
        Assert.Empty(listed);
    }

    [Fact]
    public void Named_file_still_honors_ignore_rules()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File(".gitignore"), "secret.txt\n");
        File.WriteAllText(tree.File("secret.txt"), "needle\n");
        File.WriteAllText(tree.File("ok.txt"), "needle\n");

        Assert.Equal(ExitCodes.NoMatches, Run("--scan", "needle", tree.File("secret.txt")).Code);
        Assert.Equal(ExitCodes.Success, Run("--scan", "needle", tree.File("ok.txt")).Code);
    }

    [Fact]
    public void No_ignore_still_searches_a_named_ignored_file()
    {
        using var tree = new TempDir();
        File.WriteAllText(tree.File(".gitignore"), "secret.txt\n");
        File.WriteAllText(tree.File("secret.txt"), "needle\n");

        Assert.Equal(ExitCodes.Success, Run("--scan", "--no-ignore", "needle", tree.File("secret.txt")).Code);
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

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-single-").FullName;

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

using Ngrep.Cli;

namespace Ngrep.Tests;

public class SmartCaseConfigTests
{
    [Fact]
    public void Smart_case_matrix_folds_only_patterns_without_uppercase()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nFoo\nfooBar\nfoo_bar\nFOO_BAR\nbar\nBar\n");

        Assert.Equal("a.txt:1:1:foo", Hit("-x", "foo", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-S", "foo", tree.Root));
        Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-S", "Foo", tree.Root));
        Assert.Equal("a.txt:3:1:fooBar", Hit("-x", "-S", "fooBar", tree.Root));
        Assert.Equal("a.txt:4:1:foo_bar\na.txt:5:1:FOO_BAR", Hit("-x", "-S", "foo_bar", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "--smart-case", "foo", tree.Root));

        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-i", "Foo", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-S", "-i", "Foo", tree.Root));
        Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-i", "-S", "Foo", tree.Root));
        Assert.Equal("a.txt:1:1:foo", Hit("-x", "-i", "--case-sensitive", "foo", tree.Root));
        Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-iS", "Foo", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-Si", "Foo", tree.Root));

        Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-S", "F.o", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-S", "f.o", tree.Root));
        Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-F", "-S", "foo", tree.Root));
        Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-F", "-S", "Foo", tree.Root));

        Assert.Equal(
            "a.txt:1:1:foo\na.txt:2:1:Foo\na.txt:7:1:Bar",
            Hit("-x", "-S", "-e", "foo", "-e", "Bar", tree.Root));
        Assert.Equal(
            "a.txt:1:1:foo\na.txt:2:1:Foo\na.txt:7:1:Bar",
            Hit("-x", "-F", "-S", "-e", "foo", "-e", "Bar", tree.Root));
        Assert.Equal("a.txt", Hit("-l", "-S", "foo", tree.Root));
    }

    [Fact]
    public void Smart_case_uppercase_detection_uses_the_pattern_text()
    {
        Assert.False(CaseFold.HasUppercase("foo"));
        Assert.False(CaseFold.HasUppercase("foo_bar"));
        Assert.False(CaseFold.HasUppercase(""));
        Assert.False(CaseFold.HasUppercase(@"\w"));
        Assert.True(CaseFold.HasUppercase("Foo"));
        Assert.True(CaseFold.HasUppercase("fooBar"));
        Assert.True(CaseFold.HasUppercase(@"\W"));
        Assert.True(CaseFold.Applies(CaseMatch.Smart, "foo"));
        Assert.False(CaseFold.Applies(CaseMatch.Smart, "Foo"));
        Assert.True(CaseFold.Applies(CaseMatch.Insensitive, "Foo"));
        Assert.False(CaseFold.Applies(CaseMatch.Sensitive, "foo"));
    }

    [Fact]
    public void Config_lines_are_one_argument_and_comments_are_skipped()
    {
        Assert.Equal(
            new[] { "--smart-case", "--glob", "*.cs", "--format=ndjson" },
            ConfigFile.Parse(
                """
                # comment

                  # indented comment
                --smart-case
                  --glob
                *.cs

                --format=ndjson
                """));
        Assert.Empty(ConfigFile.Parse("# --smart-case\n\n  \n"));
    }

    [Fact]
    public void Config_is_prepended_and_skipped_for_help_index_and_watch()
    {
        Assert.Equal(new[] { "--smart-case", "foo", "src" }, SearchSyntax.PrependConfig(["foo", "src"], ["--smart-case"]));
        Assert.Equal(
            new[] { "search", "-i", "foo" },
            SearchSyntax.PrependConfig(["search", "foo"], ["-i"]));
        Assert.Equal(
            new[] { "search", "--threads", "4", "foo" },
            SearchSyntax.Rewrite(["--threads", "1", "--threads", "4", "foo"]));
        Assert.True(SearchSyntax.SkipsConfig(["--help"]));
        Assert.True(SearchSyntax.SkipsConfig(["search", "--help"]));
        Assert.True(SearchSyntax.SkipsConfig(["index", "src"]));
        Assert.True(SearchSyntax.SkipsConfig(["watch", "src"]));
        Assert.True(SearchSyntax.SkipsConfig([]));
        Assert.False(SearchSyntax.SkipsConfig(["foo"]));
        Assert.False(SearchSyntax.SkipsConfig(["search", "foo"]));
        Assert.False(SearchSyntax.SkipsConfig(["--", "index"]));
    }

    [Fact]
    public void Config_prepends_smart_case_and_cli_overrides_it()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nFoo\n");
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "# default\n--smart-case\n");

        using (var _ = Env("NGREP_CONFIG_PATH", config))
        {
            Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "foo", tree.Root));
            Assert.Equal("a.txt:2:1:Foo", Hit("-x", "Foo", tree.Root));
            Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "-i", "Foo", tree.Root));
            Assert.Equal("a.txt:1:1:foo", Hit("-x", "--case-sensitive", "foo", tree.Root));
        }

        File.WriteAllText(config, "-i\n");
        using (var _ = Env("NGREP_CONFIG_PATH", config))
        {
            Assert.Equal("a.txt:1:1:foo\na.txt:2:1:Foo", Hit("-x", "Foo", tree.Root));
            Assert.Equal("a.txt:2:1:Foo", Hit("-x", "-S", "Foo", tree.Root));
            Assert.Equal("a.txt:2:1:Foo", Hit("search", "-S", "-x", "Foo", tree.Root));
        }
    }

    [Fact]
    public void Config_glob_is_prepended_and_a_later_cli_glob_wins()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.cs"), "needle\n");
        File.WriteAllText(tree.File("a.txt"), "needle\n");
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "--glob\n*.cs\n");

        using var _ = Env("NGREP_CONFIG_PATH", config);
        Assert.Equal("a.cs:1:1:needle", Hit("needle", tree.Root));
        Expect(ExitCodes.NoMatches, Run("needle", tree.Root, "-g", "!*.cs", "-j", "1"));
    }

    [Fact]
    public void Config_format_is_overridden_by_the_later_cli_value()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "needle\n");
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "--format=json\n");

        using var _ = Env("NGREP_CONFIG_PATH", config);
        var fromConfig = Run("needle", tree.Root, "-j", "1");
        Expect(ExitCodes.Success, fromConfig);
        Assert.Contains("\"matches\"", fromConfig.Stdout, StringComparison.Ordinal);

        var fromCli = Run("needle", tree.Root, "--format", "ndjson", "-j", "1");
        Expect(ExitCodes.Success, fromCli);
        Assert.DoesNotContain("\"matches\"", fromCli.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"path\"", fromCli.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_config_flag_names_the_file_and_ripgrep_config_is_ignored()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "foo\nFoo\n");
        var config = tree.File("ngrep.conf");
        File.WriteAllText(config, "--max-columns=150\n");
        var rg = tree.File("rg.conf");
        File.WriteAllText(rg, "--smart-case\n");

        using (var _ = Env("NGREP_CONFIG_PATH", config))
        {
            var bad = Run("-x", "foo", tree.Root, "-j", "1");
            Expect(ExitCodes.Error, bad);
            Assert.Contains("NGREP_CONFIG_PATH", bad.Stderr, StringComparison.Ordinal);
            Assert.Contains(config, bad.Stderr, StringComparison.Ordinal);
            Assert.Contains("max-columns", bad.Stderr, StringComparison.Ordinal);
            Assert.Equal("", bad.Stdout.Trim());
        }

        File.WriteAllText(config, "--glob *.cs\n");
        using (var _ = Env("NGREP_CONFIG_PATH", config))
        {
            var split = Run("foo", tree.Root, "-j", "1");
            Expect(ExitCodes.Error, split);
            Assert.Contains("NGREP_CONFIG_PATH", split.Stderr, StringComparison.Ordinal);
            Assert.Contains("--glob *.cs", split.Stderr, StringComparison.Ordinal);
        }

        using (var rgEnv = Env("RIPGREP_CONFIG_PATH", rg))
        using (var ours = Env("NGREP_CONFIG_PATH", null))
        {
            _ = rgEnv;
            _ = ours;
            Assert.Equal("a.txt:1:1:foo", Hit("-x", "foo", tree.Root));
            Assert.Equal("a.txt:2:1:Foo", Hit("-x", "Foo", tree.Root));
        }

        var missing = Path.Combine(tree.Root, "missing.conf");
        using (var _ = Env("NGREP_CONFIG_PATH", missing))
        {
            var gone = Run("foo", tree.Root, "-j", "1");
            Expect(ExitCodes.Error, gone);
            Assert.Contains("NGREP_CONFIG_PATH", gone.Stderr, StringComparison.Ordinal);
            Assert.Contains("cannot read", gone.Stderr, StringComparison.Ordinal);

            var help = Run("--help");
            Expect(ExitCodes.Success, help);
            Assert.Contains("search", help.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot read", help.Stderr, StringComparison.Ordinal);

            var indexed = Run("index", tree.Root, "-q");
            Expect(ExitCodes.Success, indexed);
        }

        using (var _ = Env("NGREP_CONFIG_PATH", ""))
        {
            Assert.Equal("a.txt:1:1:foo", Hit("-x", "foo", tree.Root));
        }
    }

    [Fact]
    public void Indexed_search_honors_smart_case()
    {
        using var tree = Temp();
        File.WriteAllText(tree.File("a.txt"), "Foo\nfoo\n");
        Expect(ExitCodes.Success, Run("index", tree.Root, "-q"));
        Assert.Equal("a.txt:1:1:Foo\na.txt:2:1:foo", Hit("-S", "foo", tree.Root));
        Assert.Equal("a.txt:1:1:Foo", Hit("-S", "Foo", tree.Root));
    }

    private static string Hit(params string[] args)
    {
        var run = Run([.. args, "-j", "1"]);
        Expect(ExitCodes.Success, run);
        return run.Stdout.Trim();
    }

    private static void Expect(int code, RunResult run) =>
        Assert.True(run.Code == code, run.Stdout + "\n" + run.Stderr);

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

    private static EnvScope Env(string name, string? value) => new(name, value);

    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private static TempDir Temp() => new();

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Root = Directory.CreateTempSubdirectory("ngrep-smart-").FullName;

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

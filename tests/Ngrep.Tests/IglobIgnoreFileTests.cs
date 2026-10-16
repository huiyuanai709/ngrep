using Ngrep.Cli;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class IglobIgnoreFileTests
{
    [Fact]
    public void Iglob_matches_ordinal_ignore_case_and_agrees_with_regex()
    {
        Assert.False(Matches("*.CS", "a.cs", ignoreCase: false));
        Assert.True(Matches("*.CS", "a.cs", ignoreCase: true));
        Assert.True(Matches("*.CS", "a.CS", ignoreCase: true));
        Assert.True(Matches("[a].txt", "A.txt", ignoreCase: true));
        Assert.False(Matches("[!a].txt", "A.txt", ignoreCase: true));
        Assert.True(Matches("[a-c].txt", "B.txt", ignoreCase: true));
        Assert.False(Matches("[a-c].txt", "D.txt", ignoreCase: true));
        Assert.True(Matches("/Src/*.cs", "src/App.cs", ignoreCase: true));
        Assert.False(Matches("/Src/*.cs", "lib/App.cs", ignoreCase: true));

        foreach (var (rule, path) in new[]
        {
            ("*.CS", "dir/File.cs"),
            ("[ab].txt", "B.txt"),
            ("src/*.cs", "SRC/App.CS"),
            ("foo/**/bar", "Foo/X/Bar"),
        })
        {
            var parsed = IgnoreRule.TryParse(rule, ignoreCase: true);
            Assert.NotNull(parsed);
            Assert.Equal(parsed.MatchesRegex(path, false), parsed.Matches(path, false));
        }
    }

    [Fact]
    public void Glob_order_keeps_case_per_flag_and_last_match_wins()
    {
        var foldIncludeThenSensitiveExclude = GlobFilter.Parse(
        [
            new GlobSpec("*.TXT", true),
            new GlobSpec("!a.txt", false),
        ]);
        Assert.False(foldIncludeThenSensitiveExclude.Accepts("a.txt"));
        Assert.True(foldIncludeThenSensitiveExclude.Accepts("A.TXT"));
        Assert.True(foldIncludeThenSensitiveExclude.Accepts("b.txt"));
        Assert.False(foldIncludeThenSensitiveExclude.Accepts("note.md"));

        var sensitiveExcludeThenFoldInclude = GlobFilter.Parse(
        [
            new GlobSpec("!*.txt", false),
            new GlobSpec("A.TXT", true),
        ]);
        Assert.True(sensitiveExcludeThenFoldInclude.Accepts("a.txt"));
        Assert.True(sensitiveExcludeThenFoldInclude.Accepts("A.TXT"));
        Assert.False(sensitiveExcludeThenFoldInclude.Accepts("b.txt"));
    }

    [Fact]
    public void Ignore_file_source_loses_to_every_higher_source_and_later_files_win()
    {
        var underGlobal = IgnoreStack.Empty
            .Add(IgnoreSource.IgnoreFile, "", "*.log\n")
            .Add(IgnoreSource.Global, "", "!keep.log\n");
        Assert.False(underGlobal.IsIgnored("keep.log", false));
        Assert.True(underGlobal.IsIgnored("a.log", false));

        var gitBeatsWhitelist = IgnoreStack.Empty
            .Add(IgnoreSource.IgnoreFile, "", "!keep.log\n")
            .Add(IgnoreSource.Git, "", "*.log\n");
        Assert.True(gitBeatsWhitelist.IsIgnored("keep.log", false));

        var rgBeats = IgnoreStack.Empty
            .Add(IgnoreSource.IgnoreFile, "", "keep.txt\n")
            .Add(IgnoreSource.RgIgnore, "", "!keep.txt\n");
        Assert.False(rgBeats.IsIgnored("keep.txt", false));

        var laterFile = IgnoreStack.Empty
            .Add(IgnoreSource.IgnoreFile, "", "*.log\n")
            .Add(IgnoreSource.IgnoreFile, "", "!keep.log\n");
        Assert.False(laterFile.IsIgnored("keep.log", false));
        Assert.True(laterFile.IsIgnored("a.log", false));
    }

    [Fact]
    public void Ignore_file_patterns_are_relative_to_the_working_directory()
    {
        var scope = Temp();
        try
        {
            var sub = Path.Combine(scope, "sub");
            Directory.CreateDirectory(sub);
            var atRoot = IgnoreStack.Empty
                .WithWorkingDirectory(scope, scope)
                .Add(IgnoreSource.IgnoreFile, "", "/rooted.log\n*.skip\n");
            Assert.True(atRoot.IsIgnored("rooted.log", false));
            Assert.False(atRoot.IsIgnored("sub/rooted.log", false));
            Assert.True(atRoot.IsIgnored("sub/a.skip", false));

            var atSub = IgnoreStack.Empty
                .WithWorkingDirectory(scope, sub)
                .Add(IgnoreSource.IgnoreFile, "", "/rooted.log\n");
            Assert.True(atSub.IsIgnored("sub/rooted.log", false));
            Assert.False(atSub.IsIgnored("rooted.log", false));

            var elsewhere = Directory.CreateTempSubdirectory("ngrep-cwd-").FullName;
            var outside = IgnoreStack.Empty
                .WithWorkingDirectory(scope, elsewhere)
                .Add(IgnoreSource.IgnoreFile, "", "*.log\n");
            Assert.False(outside.IsIgnored("a.log", false));
            Directory.Delete(elsewhere, recursive: true);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [Fact]
    public void Walker_layers_ignore_files_under_the_gitignore_stack()
    {
        var root = Temp();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".rgignore"), "!kept.txt\n");
            var outside = Directory.CreateTempSubdirectory("ngrep-extra-ignore-").FullName;
            var low = Path.Combine(outside, "low.ignore");
            var high = Path.Combine(outside, "high.ignore");
            File.WriteAllText(low, "*.skip\n!kept.txt\n!keep.log\n");
            File.WriteAllText(high, "extra.txt\n!kept.txt\n");
            Touch(root, "keep.txt", "keep.log", "a.skip", "extra.txt", "kept.txt");

            var layered = IgnorePolicy.All.WithExtra([low, high]);
            Assert.Equal(["keep.txt", "kept.txt"], Walk(root, layered));

            var noVcs = IgnorePolicy.CustomOnly.WithExtra([low]);
            Assert.Equal(["extra.txt", "keep.log", "keep.txt", "kept.txt"], Walk(root, noVcs));

            var none = IgnorePolicy.None.WithExtra([high]);
            Assert.Equal(["a.skip", "keep.log", "keep.txt", "kept.txt"], Walk(root, none));
            Directory.Delete(outside, recursive: true);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cli_iglob_is_case_insensitive_and_interleaves_with_glob()
    {
        var root = Temp();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cs"), "needle\n");
            File.WriteAllText(Path.Combine(root, "B.CS"), "needle\n");
            File.WriteAllText(Path.Combine(root, "note.txt"), "needle\n");

            Assert.Equal(["B.CS"], Paths(root, "-g", "*.CS"));
            Assert.Equal(["B.CS", "a.cs"], Paths(root, "--iglob", "*.CS"));
            Assert.Equal(["note.txt"], Paths(root, "--iglob", "!*.cs"));
            Assert.Equal(["B.CS", "note.txt"], Paths(root, "-g", "!*.cs"));
            Assert.Equal(["note.txt"], Paths(root, "-g", "*", "--iglob", "!*.CS"));
            Assert.Equal(["B.CS"], Paths(root, "--iglob", "!*.CS", "-g", "*.CS"));
            Assert.Equal(["note.txt"], Paths(root, "--iglob=*.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cli_ignore_file_order_cwd_and_no_ignore()
    {
        var root = Temp();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git", "info"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".rgignore"), "!kept.txt\n");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "keep.log"), "needle\n");
            File.WriteAllText(Path.Combine(root, "extra.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "secret.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "sub", "secret.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "kept.txt"), "needle\n");
            var outside = Directory.CreateTempSubdirectory("ngrep-extra-ignore-").FullName;
            var low = Path.Combine(outside, "low.ignore");
            var high = Path.Combine(outside, "high.ignore");
            var rooted = Path.Combine(outside, "rooted.ignore");
            File.WriteAllText(low, "extra.txt\n");
            File.WriteAllText(high, "!extra.txt\nsecret.txt\n");
            File.WriteAllText(rooted, "/secret.txt\n");

            Assert.Equal(["extra.txt", "keep.txt", "kept.txt"], Paths(root, "--ignore-file", low, "--ignore-file", high));
            Assert.Equal(["extra.txt", "keep.txt", "kept.txt", "sub/secret.txt"], Paths(root, "--ignore-file", rooted));
            Assert.Equal(["extra.txt", "keep.log", "keep.txt", "kept.txt"], Paths(root, "--no-ignore-vcs", "--ignore-file", high));
            Assert.Equal(["keep.log", "keep.txt", "kept.txt", "secret.txt", "sub/secret.txt"], Paths(root, "--no-ignore", "--ignore-file", low));
            Assert.Equal(["extra.txt", "keep.log", "keep.txt", "kept.txt"], Paths(root, "-u", "--ignore-file", high));

            using var capture = new Capture();
            var missing = NgrepCli.Run(["needle", "--ignore-file", Path.Combine(root, "missing.ignore"), root, "-j", "1"]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Error, missing);
            Assert.Contains("Ignore file not found", capture.Stderr, StringComparison.Ordinal);

            capture.Clear();
            var files = NgrepCli.Run(["--files", "--ignore-file", high, root]).GetAwaiter().GetResult();
            Assert.Equal(ExitCodes.Success, files);
            Assert.Equal(["extra.txt", "keep.txt", "kept.txt"], Split(capture.Stdout));
            Directory.Delete(outside, recursive: true);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Index_search_applies_ignore_file_without_scanning()
    {
        var root = Temp();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n!keep.log\n");
            File.WriteAllText(Path.Combine(root, "sub", ".gitignore"), "!nested.log\n");
            File.WriteAllText(Path.Combine(root, "plain.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "drop.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "keep.log"), "needle\n");
            File.WriteAllText(Path.Combine(root, "other.log"), "needle\n");
            File.WriteAllText(Path.Combine(root, "sub", "nested.log"), "needle\n");
            File.WriteAllText(Path.Combine(root, "sub", "drop.log"), "needle\n");
            Indexer.Build(root);

            var rules = Path.Combine(root, "extra.ignore");
            File.WriteAllText(rules, "*.log\ndrop.txt\n");
            var filtered = Search(root, rules);
            Assert.True(filtered.UsedIndex);
            Assert.Null(filtered.Note);
            Assert.Equal(["keep.log", "plain.txt", "sub/nested.log"], PathsOf(filtered));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Config_prepends_iglob_and_ignore_file()
    {
        var root = Temp();
        var previous = Directory.GetCurrentDirectory();
        var previousConfig = Environment.GetEnvironmentVariable("NGREP_CONFIG_PATH");
        var outside = Directory.CreateTempSubdirectory("ngrep-config-ignore-").FullName;
        try
        {
            Directory.SetCurrentDirectory(root);
            File.WriteAllText(Path.Combine(root, "a.cs"), "needle\n");
            File.WriteAllText(Path.Combine(root, "B.CS"), "needle\n");
            File.WriteAllText(Path.Combine(root, "skip.txt"), "needle\n");
            var rules = Path.Combine(outside, "rules.ignore");
            File.WriteAllText(rules, "skip.txt\n");
            var config = Path.Combine(outside, "ngrep.conf");
            File.WriteAllText(config, "--iglob\n*.CS\n--ignore-file\n" + rules + "\n");
            Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", config);

            Assert.Equal(["B.CS", "a.cs"], Paths(root));
            Assert.Equal(["B.CS"], Paths(root, "-g", "!*.cs"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NGREP_CONFIG_PATH", previousConfig);
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(outside, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool Matches(string rule, string path, bool ignoreCase)
    {
        var parsed = IgnoreRule.TryParse(rule, ignoreCase);
        Assert.NotNull(parsed);
        var fast = parsed.Matches(path, false);
        Assert.Equal(parsed.MatchesRegex(path, false), fast);
        return fast;
    }

    private static string[] Paths(string root, params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(["needle", ..args, root, "-l", "-j", "1"]).GetAwaiter().GetResult();
        Assert.True(code is ExitCodes.Success or ExitCodes.NoMatches, capture.Stderr);
        return Split(capture.Stdout);
    }

    private static string[] Split(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string[] Walk(string root, IgnorePolicy policy)
    {
        var stats = new WalkStats();
        return TreeWalker.Enumerate(root, null, stats, policy, includeHidden: false)
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static SearchOutcome Search(string root, string ignoreFile) =>
        Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = root,
            IgnoreFiles = [ignoreFile],
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();

    private static string[] PathsOf(SearchOutcome outcome) =>
        outcome.Matches.Select(match => match.Path).Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static void Touch(string dir, params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }
    }

    private static string Temp() => Directory.CreateTempSubdirectory("ngrep-iglob-").FullName;

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

        public void Clear()
        {
            _out.GetStringBuilder().Clear();
            _err.GetStringBuilder().Clear();
        }

        public void Dispose()
        {
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }
    }
}

using Ngrep.Cli;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Search;

namespace Ngrep.Tests;

public class IgnoreSourceTests
{
    [Fact]
    public void Higher_source_overrides_lower_regardless_of_add_order()
    {
        var gitThenIgnore = IgnoreStack.Empty
            .Add(IgnoreSource.Git, "", "*.log\n")
            .Add(IgnoreSource.DotIgnore, "", "!keep.log\n");
        Assert.False(gitThenIgnore.IsIgnored("keep.log", false));
        Assert.True(gitThenIgnore.IsIgnored("a.log", false));

        var ignoreThenGit = IgnoreStack.Empty
            .Add(IgnoreSource.DotIgnore, "", "*.log\n")
            .Add(IgnoreSource.Git, "", "!keep.log\n");
        Assert.True(ignoreThenGit.IsIgnored("keep.log", false));
        Assert.True(ignoreThenGit.IsIgnored("a.log", false));

        var rgOverIgnore = IgnoreStack.Empty
            .Add(IgnoreSource.DotIgnore, "", "!keep.log\n")
            .Add(IgnoreSource.RgIgnore, "", "*.log\n");
        Assert.True(rgOverIgnore.IsIgnored("keep.log", false));

        var ignoreOverRg = IgnoreStack.Empty
            .Add(IgnoreSource.RgIgnore, "", "!keep.log\n")
            .Add(IgnoreSource.DotIgnore, "", "*.log\n");
        Assert.False(ignoreOverRg.IsIgnored("keep.log", false));
    }

    [Fact]
    public void Nested_file_wins_inside_one_source_only()
    {
        var sameSource = IgnoreStack.Empty
            .Add(IgnoreSource.DotIgnore, "", "*.log\n")
            .Add(IgnoreSource.DotIgnore, "sub", "!keep.log\n");
        Assert.False(sameSource.IsIgnored("sub/keep.log", false));
        Assert.True(sameSource.IsIgnored("sub/drop.log", false));
        Assert.True(sameSource.IsIgnored("a.log", false));

        var childGitCannotBeatParentIgnore = IgnoreStack.Empty
            .Add(IgnoreSource.DotIgnore, "", "*.log\n")
            .Add(IgnoreSource.Git, "sub", "!keep.log\n");
        Assert.True(childGitCannotBeatParentIgnore.IsIgnored("sub/keep.log", false));
    }

    [Fact]
    public void Exclude_and_global_sit_under_gitignore()
    {
        var exclude = IgnoreStack.Empty
            .Add(IgnoreSource.Exclude, "", "*.log\n")
            .Add(IgnoreSource.Git, "", "!keep.log\n");
        Assert.False(exclude.IsIgnored("keep.log", false));
        Assert.True(exclude.IsIgnored("a.log", false));

        var global = IgnoreStack.Empty
            .Add(IgnoreSource.Git, "", "*.log\n")
            .Add(IgnoreSource.Global, "", "!keep.log\n");
        Assert.True(global.IsIgnored("keep.log", false));

        var globalWhitelistKeptByGit = IgnoreStack.Empty
            .Add(IgnoreSource.Global, "", "*.txt\n!keep.txt\n");
        Assert.False(globalWhitelistKeptByGit.IsIgnored("keep.txt", false));
        Assert.True(globalWhitelistKeptByGit.IsIgnored("a.txt", false));
    }

    [Fact]
    public void Walker_reads_each_ignore_source()
    {
        var root = Temp();
        try
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git", "info"));
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "git.log\n");
            File.WriteAllText(Path.Combine(repo, ".ignore"), "dot.skip\n");
            File.WriteAllText(Path.Combine(repo, ".rgignore"), "rg.skip\n");
            File.WriteAllText(Path.Combine(repo, ".git", "info", "exclude"), "exclude.log\n");
            var global = Path.Combine(root, "global.exclude");
            File.WriteAllText(global, "global.log\n/rooted.txt\n");
            File.WriteAllText(Path.Combine(repo, ".git", "config"), $"[core]\n\texcludesFile = {global}\n");
            Touch(repo, "keep.txt", "git.log", "dot.skip", "rg.skip", "exclude.log", "global.log", "rooted.txt");
            Directory.CreateDirectory(Path.Combine(repo, "sub"));
            File.WriteAllText(Path.Combine(repo, "sub", "rooted.txt"), "sub");

            Assert.Equal(["keep.txt", "sub/rooted.txt"], Walk(repo));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Walker_precedence_and_nested_ignore_files()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "!keep.log\n");
            File.WriteAllText(Path.Combine(root, "keep.log"), "k");
            File.WriteAllText(Path.Combine(root, "drop.log"), "d");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", ".ignore"), "*.txt\n!keep.txt\n");
            File.WriteAllText(Path.Combine(root, "nested", "keep.txt"), "k");
            File.WriteAllText(Path.Combine(root, "nested", "drop.txt"), "d");
            Directory.CreateDirectory(Path.Combine(root, "blocked"));
            File.WriteAllText(Path.Combine(root, "blocked", ".gitignore"), "!child.log\n");
            File.WriteAllText(Path.Combine(root, "blocked", "child.log"), "c");
            File.WriteAllText(Path.Combine(root, ".rgignore"), "blocked/*.log\n");

            Assert.Equal(["keep.log", "nested/keep.txt"], Walk(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Ancestor_ignore_applies_when_the_walk_starts_below_it()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "git.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "dot.skip\n");
            File.WriteAllText(Path.Combine(root, ".rgignore"), "rg.skip\n");
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            Touch(sub, "keep.txt", "git.log", "dot.skip", "rg.skip");

            Assert.Equal(["keep.txt"], Walk(sub));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Outside_a_checkout_gitignore_and_dot_ignore_still_apply()
    {
        var root = Temp();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "git.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "dot.skip\n");
            Touch(root, "keep.txt", "git.log", "dot.skip");
            Assert.Equal(["keep.txt"], Walk(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Walker_reads_default_global_excludes_when_the_key_is_unset()
    {
        var root = Temp();
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            var xdg = Path.Combine(root, "xdg");
            Directory.CreateDirectory(Path.Combine(xdg, "git"));
            File.WriteAllText(Path.Combine(xdg, "git", "ignore"), "global.log\n");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Touch(repo, "keep.txt", "global.log");

            Assert.Equal(["keep.txt"], Walk(repo));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Gitdir_file_uses_commondir_info_exclude()
    {
        var root = Temp();
        try
        {
            var common = Path.Combine(root, "repo.git");
            var gitDir = Path.Combine(common, "worktrees", "wt");
            Directory.CreateDirectory(Path.Combine(common, "info"));
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(common, "info", "exclude"), "secret.txt\n");
            File.WriteAllText(Path.Combine(gitDir, "commondir"), "../../\n");
            var work = Path.Combine(root, "wt");
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, ".git"), "gitdir: ../repo.git/worktrees/wt\n");
            Touch(work, "keep.txt", "secret.txt");

            Assert.Equal(["keep.txt"], Walk(work));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_ignore_flags_select_different_sources_and_keep_hard_skips()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "config"), "needle\n");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "*.skip\n");
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), Path.Combine(root, "keep.txt"));
            Touch(root, "keep.txt", "a.log", "b.skip");

            Assert.Equal(["keep.txt"], Walk(root));
            Assert.Equal(["a.log", "keep.txt"], Walk(root, IgnorePolicy.CustomOnly));
            Assert.Equal(["a.log", "b.skip", "keep.txt"], Walk(root, IgnorePolicy.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cli_no_ignore_and_no_ignore_vcs_differ()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "config"), "needle\n");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "*.skip\n");
            File.WriteAllText(Path.Combine(root, ".rgignore"), "*.rg\n");
            Touch(root, "keep.txt", "a.log", "b.skip", "c.rg");
            foreach (var name in new[] { "keep.txt", "a.log", "b.skip", "c.rg" })
            {
                File.WriteAllText(Path.Combine(root, name), "needle\n");
            }

            Assert.Equal(["keep.txt"], Paths(root));
            Assert.Equal(["a.log", "keep.txt"], Paths(root, "--no-ignore-vcs"));
            Assert.Equal(["a.log", "b.skip", "c.rg", "keep.txt"], Paths(root, "--no-ignore"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Index_omits_every_source_and_the_flags_scan()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "*.skip\n");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "needle\n");
            File.WriteAllText(Path.Combine(root, "a.log"), "needle\n");
            File.WriteAllText(Path.Combine(root, "b.skip"), "needle\n");
            Indexer.Build(root);

            var indexed = Search(root, noIgnore: false, noIgnoreVcs: false);
            Assert.True(indexed.UsedIndex);
            Assert.Equal(["keep.txt"], PathsOf(indexed));

            var vcs = Search(root, noIgnore: false, noIgnoreVcs: true);
            Assert.False(vcs.UsedIndex);
            Assert.Contains("--no-ignore-vcs", vcs.Note, StringComparison.Ordinal);
            Assert.Equal(["a.log", "keep.txt"], PathsOf(vcs));

            var all = Search(root, noIgnore: true, noIgnoreVcs: false);
            Assert.False(all.UsedIndex);
            Assert.Contains("--no-ignore", all.Note, StringComparison.Ordinal);
            Assert.Equal(["a.log", "b.skip", "keep.txt"], PathsOf(all));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Git_config_resolves_excludes_file_without_starting_git()
    {
        var root = Temp();
        try
        {
            var system = WriteConfig(root, "system", "/from-system");
            var xdg = WriteConfig(root, "xdg", "/from-xdg");
            var global = WriteConfig(root, "global", "/from-global");
            var local = WriteConfig(root, "local", "/from-local");
            var fallback = Path.Combine(root, "fallback");
            var files = new GitConfigFiles(system, xdg, global, local, fallback, Home: null);
            Assert.Equal(Path.GetFullPath("/from-local"), GitConfig.ResolveExcludesPath(files));

            Assert.Equal(
                Path.GetFullPath("/from-global"),
                GitConfig.ResolveExcludesPath(files with { LocalPath = null }));
            Assert.Equal(fallback, GitConfig.ResolveExcludesPath(new GitConfigFiles(null, null, null, null, fallback, null)));

            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(home);
            var tildeTarget = Path.Combine(home, "My Files", "ignore");
            Directory.CreateDirectory(Path.GetDirectoryName(tildeTarget)!);
            var tildeConfig = Path.Combine(root, "tilde");
            File.WriteAllText(tildeConfig, "[Core]\n\tExcludesFile = \"~/My Files/ignore\"\n");
            Assert.Equal(
                tildeTarget,
                GitConfig.ResolveExcludesPath(new GitConfigFiles(null, null, tildeConfig, null, fallback, home)));

            var extra = Path.Combine(root, "extra");
            File.WriteAllText(extra, "[core]\n\texcludesFile = /from-include\n");
            var including = Path.Combine(root, "including");
            File.WriteAllText(including, "[core]\nexcludesFile = /from-main\n[include]\n\tpath = extra\n");
            Assert.Equal(
                Path.GetFullPath("/from-include"),
                GitConfig.ResolveExcludesPath(new GitConfigFiles(null, null, including, null, fallback, null)));

            var continued = Path.Combine(root, "continued");
            File.WriteAllText(continued, "[core]\n\texcludesFile = /tmp/foo\\\nbar\n");
            Assert.Equal(
                Path.GetFullPath("/tmp/foobar"),
                GitConfig.ResolveExcludesPath(new GitConfigFiles(null, null, continued, null, fallback, null)));

            var previous = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(root);
                var relative = Path.Combine(root, "relative");
                File.WriteAllText(relative, "[core]\n\texcludesFile = fallback\n");
                Assert.Equal(
                    fallback,
                    GitConfig.ResolveExcludesPath(new GitConfigFiles(null, null, relative, null, null, null)));
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteConfig(string root, string name, string excludes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, $"[core]\n\texcludesFile = {excludes}\n");
        return path;
    }

    private static void Touch(string dir, params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(dir, name), "x");
        }
    }

    private static string[] Walk(string root, IgnorePolicy? policy = null)
    {
        var stats = new WalkStats();
        return TreeWalker.Enumerate(root, null, stats, policy, includeHidden: false)
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Paths(string root, params string[] args)
    {
        using var capture = new Capture();
        var code = NgrepCli.Run(["needle", ..args, root, "-l", "-j", "1"]).GetAwaiter().GetResult();
        Assert.True(code is ExitCodes.Success or ExitCodes.NoMatches, capture.Stderr);
        return capture.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static SearchOutcome Search(string root, bool noIgnore, bool noIgnoreVcs) =>
        Searcher.Search(new SearchQuery
        {
            Pattern = "needle",
            Path = root,
            NoIgnore = noIgnore,
            NoIgnoreVcs = noIgnoreVcs,
            MaxDegreeOfParallelism = 1,
        }).GetAwaiter().GetResult();

    private static string[] PathsOf(SearchOutcome outcome) =>
        outcome.Matches.Select(match => match.Path).Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static string Temp() => Directory.CreateTempSubdirectory("ngrep-ignore-").FullName;

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

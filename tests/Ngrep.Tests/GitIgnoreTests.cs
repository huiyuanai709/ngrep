using Ngrep.Ignore;

namespace Ngrep.Tests;

public class GitIgnoreTests
{
    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "dir/a.log", false, true)]
    [InlineData("*.log", "dir/a.txt", false, false)]
    [InlineData("src/*.cs", "src/A.cs", false, true)]
    [InlineData("src/*.cs", "src/a/B.cs", false, false)]
    [InlineData("src/*.cs", "A.cs", false, false)]
    [InlineData("/only-root.log", "only-root.log", false, true)]
    [InlineData("/only-root.log", "sub/only-root.log", false, false)]
    [InlineData("?.txt", "a.txt", false, true)]
    [InlineData("?.txt", "ab.txt", false, false)]
    [InlineData("[ab].txt", "a.txt", false, true)]
    [InlineData("[ab].txt", "c.txt", false, false)]
    [InlineData("a/**/c", "a/c", false, true)]
    [InlineData("a/**/c", "a/b/c", false, true)]
    [InlineData("a/**/c", "a/x/y/c", false, true)]
    [InlineData("a/**/c", "c", false, false)]
    [InlineData("foo/**/*.cs", "foo/file.cs", false, true)]
    [InlineData("foo/**/*.cs", "foo/bar/file.cs", false, true)]
    [InlineData("foo/**/*.cs", "foo", true, false)]
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, false)]
    [InlineData("build/", "sub/build", true, true)]
    [InlineData("name\\ with\\ space/", "name with space", true, true)]
    public void Single_rule_matches_git_globs(string rule, string path, bool isDirectory, bool ignored)
    {
        var stack = IgnoreStack.Empty.Add("", rule);
        Assert.Equal(ignored, stack.IsIgnored(path, isDirectory));
    }

    [Fact]
    public void Nested_rules_match_git_check_ignore()
    {
        var stack = IgnoreStack.Empty
            .Add("", """
                # comment

                *.log
                !keep.log
                build/
                src/*.cs
                bin/
                build2/*
                !build2/keep.txt
                foo/
                !foo/
                !foo/a.txt
                """)
            .Add("nested", """
                *.tmp
                !keep.tmp
                """);

        Assert.True(stack.IsIgnored("a.log", false));
        Assert.False(stack.IsIgnored("keep.log", false));
        Assert.False(stack.IsIgnored("nested/keep.log", false));
        Assert.True(stack.IsIgnored("nested/a.log", false));
        Assert.True(stack.IsIgnored("nested/x.tmp", false));
        Assert.False(stack.IsIgnored("nested/keep.tmp", false));
        Assert.True(stack.IsIgnored("nested/skip.tmp", false));
        Assert.True(stack.IsIgnored("build/foo.txt", false));
        Assert.True(stack.IsIgnored("build/keep.txt", false));
        Assert.True(stack.IsIgnored("src/A.cs", false));
        Assert.False(stack.IsIgnored("src/a/B.cs", false));
        Assert.True(stack.IsIgnored("bin/out.txt", false));
        Assert.False(stack.IsIgnored("nested/sub/keep.log", false));
        Assert.False(stack.IsIgnored("root.txt", false));
        Assert.False(stack.IsIgnored("build2", true));
        Assert.False(stack.IsIgnored("build2/keep.txt", false));
        Assert.True(stack.IsIgnored("build2/foo.txt", false));
        Assert.False(stack.IsIgnored("foo", true));
        Assert.False(stack.IsIgnored("foo/a.txt", false));
        Assert.False(stack.IsIgnored("foo/bar", true));
        Assert.False(stack.IsIgnored("foo/bar/b.txt", false));
    }

    [Fact]
    public void Comments_and_blank_lines_are_not_rules()
    {
        var rules = IgnoreRuleSet.Parse("# hello\n\n  \n*.log\n");
        Assert.Equal(1, rules.Count);
    }

    [Theory]
    [InlineData("*.log", "a.log", false)]
    [InlineData("*.log", "dir/a.log", false)]
    [InlineData("*.log", "dir/a.txt", false)]
    [InlineData("src/*.cs", "src/A.cs", false)]
    [InlineData("src/*.cs", "src/a/B.cs", false)]
    [InlineData("/only-root.log", "only-root.log", false)]
    [InlineData("/only-root.log", "sub/only-root.log", false)]
    [InlineData("?.txt", "a.txt", false)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("[ab].txt", "a.txt", false)]
    [InlineData("[ab].txt", "c.txt", false)]
    [InlineData("[!ab].txt", "c.txt", false)]
    [InlineData("[!ab].txt", "a.txt", false)]
    [InlineData("a/**/c", "a/c", false)]
    [InlineData("a/**/c", "a/b/c", false)]
    [InlineData("a/**/c", "a/x/y/c", false)]
    [InlineData("a/**/c", "c", false)]
    [InlineData("foo/**/*.cs", "foo/file.cs", false)]
    [InlineData("foo/**/*.cs", "foo/bar/file.cs", false)]
    [InlineData("foo/**", "foo/a/b", false)]
    [InlineData("foo/**", "foo", false)]
    [InlineData("**/secret.txt", "a/secret.txt", false)]
    [InlineData("**/secret.txt", "secret.txt", false)]
    [InlineData("name\\ with\\ space", "name with space", false)]
    [InlineData("build/", "build", true)]
    [InlineData("build/", "build", false)]
    [InlineData("*", "anything", false)]
    [InlineData("foo*", "foobar", false)]
    [InlineData("foo*", "dir/foobar", false)]
    [InlineData("*mid*", "a/xxmidyy", false)]
    public void Fast_matcher_agrees_with_regex(string rule, string path, bool isDirectory)
    {
        var parsed = IgnoreRule.TryParse(rule);
        Assert.NotNull(parsed);
        Assert.Equal(parsed.MatchesRegex(path, isDirectory), parsed.Matches(path, isDirectory));
    }

    [Fact]
    public void Walker_skips_symlinks_and_ignored_directories()
    {
        var root = Directory.CreateTempSubdirectory("ngrep-walk-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\nsecret/\n");
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.log"), "b");
            Directory.CreateDirectory(Path.Combine(root, "secret"));
            File.WriteAllText(Path.Combine(root, "secret", "c.txt"), "c");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "d.txt"), "d");
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), Path.Combine(root, "a.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), Path.Combine(root, "sub"));

            var stats = new WalkStats();
            var files = TreeWalker.Enumerate(root, null, stats, includeHidden: false)
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["a.txt", "sub/d.txt"], files);
            Assert.Equal(1, stats.SkippedIgnored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

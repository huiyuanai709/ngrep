namespace Ngrep.Ignore;

internal readonly record struct WalkedFile(string FullPath, string RelativePath);

internal readonly struct WalkDir
{
    public string Full { get; init; }

    public string WalkRelative { get; init; }

    public string ScopeRelative { get; init; }

    public IgnoreStack Ignores { get; init; }

    /// <summary>Ripgrep depth. The start path is 0. Its immediate children are 1.</summary>
    public int Depth { get; init; }
}

internal readonly struct WalkOptions
{
    public WalkOptions(IgnorePolicy policy, bool includeHidden, string? indexRelative, int? maxDepth = null)
    {
        Policy = policy;
        IncludeHidden = includeHidden;
        IndexRelative = indexRelative;
        MaxDepth = maxDepth;
    }

    public IgnorePolicy Policy { get; }

    public bool IncludeHidden { get; }

    public string? IndexRelative { get; }

    /// <summary>
    /// Ripgrep <c>-d</c>. Null walks without a cap. 0 is the start path and does not list a directory.
    /// 1 lists that directory and stops. A directory is listed only when its depth is less than the cap.
    /// </summary>
    public int? MaxDepth { get; }
}

internal sealed class WalkStats
{
    private int _skippedIgnored;
    private int _skippedBinary;
    private int _skippedTooLarge;
    private int _skippedUnreadable;

    public int SkippedIgnored => Volatile.Read(ref _skippedIgnored);

    public int SkippedBinary => Volatile.Read(ref _skippedBinary);

    public int SkippedTooLarge => Volatile.Read(ref _skippedTooLarge);

    public int SkippedUnreadable => Volatile.Read(ref _skippedUnreadable);

    public void IncrementSkippedIgnored() => Interlocked.Increment(ref _skippedIgnored);

    public void IncrementSkippedBinary() => Interlocked.Increment(ref _skippedBinary);

    public void IncrementSkippedTooLarge() => Interlocked.Increment(ref _skippedTooLarge);

    public void IncrementSkippedUnreadable() => Interlocked.Increment(ref _skippedUnreadable);
}

internal static class TreeWalker
{
    /// <summary>Test hook. Invoked with the directory full path before its entries are listed.</summary>
    internal static Action<string>? BeforeCollect { get; set; }

    /// <summary>
    /// A dot name, or a Windows/macOS hidden attribute. <paramref name="includeHidden"/> (--hidden)
    /// is applied by the caller.
    /// </summary>
    internal static bool IsHiddenEntry(string name, bool hiddenAttribute)
    {
        if (name.Length > 0 && name[0] == '.')
        {
            return true;
        }

        return hiddenAttribute;
    }

    /// <summary>
    /// True when a content scan of <paramref name="walkRoot"/> would not yield <paramref name="fullPath"/>.
    /// Hidden names are included, so a path the user named is not rejected only for a leading dot.
    /// Symlinks are not yielded, so a named symlink is treated as not searched.
    /// </summary>
    public static bool IsIgnoredFile(string walkRoot, string fullPath, IgnorePolicy? policy = null)
    {
        var stats = new WalkStats();
        foreach (var file in Enumerate(walkRoot, indexDirectory: null, stats, policy, includeHidden: true))
        {
            if (string.Equals(file.FullPath, fullPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Depth of <paramref name="walkRelative"/> from the start path. <c>""</c> is 0, <c>a</c> is 1, <c>a/b</c> is 2.
    /// </summary>
    internal static int DepthOf(string walkRelative)
    {
        if (walkRelative.Length == 0)
        {
            return 0;
        }

        var depth = 1;
        for (var i = 0; i < walkRelative.Length; i++)
        {
            if (walkRelative[i] == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    public static IEnumerable<WalkedFile> Enumerate(
        string walkRoot,
        string? indexDirectory,
        WalkStats stats,
        IgnorePolicy? policy = null,
        bool includeHidden = false,
        int? maxDepth = null)
    {
        var chosen = policy ?? IgnorePolicy.All;
        var root = CreateRoot(walkRoot, chosen);
        var options = new WalkOptions(chosen, includeHidden, IndexRelative(root.Full, indexDirectory), maxDepth);
        var stack = new Stack<WalkDir>();
        stack.Push(root);
        var files = new List<WalkedFile>(32);
        var children = new List<WalkDir>(8);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            Collect(dir, options, stats, files, children);
            foreach (var file in files)
            {
                yield return file;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    public static WalkOptions CreateOptions(string walkRootFull, string? indexDirectory, IgnorePolicy policy, bool includeHidden, int? maxDepth = null) =>
        new(policy, includeHidden, IndexRelative(walkRootFull, indexDirectory), maxDepth);

    public static WalkDir CreateRoot(string walkRoot, IgnorePolicy policy)
    {
        walkRoot = Path.GetFullPath(walkRoot);
        var scopeRoot = FindGitRoot(walkRoot) ?? walkRoot;
        var scopeRelative = Relative(scopeRoot, walkRoot);
        var ignores = policy.Vcs || policy.Custom
            ? LoadAncestors(scopeRoot, walkRoot, policy)
            : IgnoreStack.Empty;
        return new WalkDir
        {
            Full = walkRoot,
            WalkRelative = "",
            ScopeRelative = scopeRelative,
            Ignores = ignores,
            Depth = 0,
        };
    }

    public static void Collect(WalkDir dir, WalkOptions options, WalkStats stats, List<WalkedFile> files, List<WalkDir> children, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        files.Clear();
        children.Clear();
        // A directory at the cap is not listed. Its children would be deeper than the cap.
        // Depth 0 therefore does not read the start directory. Ignore rules of a directory
        // we do not list are left unread; ancestors were already loaded on the root.
        if (options.MaxDepth is int limit && dir.Depth >= limit)
        {
            return;
        }

        BeforeCollect?.Invoke(dir.Full);
        cancellation.ThrowIfCancellationRequested();
        var sink = new ListSink();
        if (!DirectoryEntries.TryList(dir.Full, ref sink, cancellation))
        {
            stats.IncrementSkippedUnreadable();
            return;
        }

        var entries = sink.Entries;
        if (entries == null)
        {
            return;
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        var ignores = dir.Ignores;
        if (options.Policy.Vcs || options.Policy.Custom)
        {
            ignores = AddListedIgnoreFiles(ignores, dir, entries, options.Policy, stats);
        }

        foreach (var entry in entries)
        {
            if (entry.Kind == DirentKind.Symlink)
            {
                continue;
            }

            var name = entry.Name;
            var isDir = entry.Kind == DirentKind.Directory;
            if (entry.Kind == DirentKind.Other)
            {
                continue;
            }

            var childWalk = JoinRelative(dir.WalkRelative, name);
            if (isDir && (name is ".git" or ".ngrep" || IsIndexPath(childWalk, options.IndexRelative)))
            {
                continue;
            }

            // Dot names and the Windows/macOS hidden attribute are skipped unless the
            // caller asked for them. Ignore files were read above; the files themselves
            // are not hits unless hidden names are included.
            if (!options.IncludeHidden && IsHiddenEntry(name, entry.Hidden))
            {
                continue;
            }

            var scopeRelative = JoinRelative(dir.ScopeRelative, name);
            if (ignores.IsIgnored(scopeRelative, isDir, checkParents: false))
            {
                if (!isDir)
                {
                    stats.IncrementSkippedIgnored();
                }

                continue;
            }

            var full = Path.Join(dir.Full, name);
            if (isDir)
            {
                var childDepth = DepthOf(childWalk);
                if (options.MaxDepth is int childLimit && childDepth >= childLimit)
                {
                    continue;
                }

                children.Add(new WalkDir
                {
                    Full = full,
                    WalkRelative = childWalk,
                    ScopeRelative = scopeRelative,
                    Ignores = ignores,
                    Depth = childDepth,
                });
                continue;
            }

            files.Add(new WalkedFile(full, childWalk));
        }
    }

    public static string? FindGitRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string JoinRelative(string parent, string name)
    {
        if (parent.Length == 0)
        {
            return name;
        }

        return string.Concat(parent, "/", name);
    }

    private static IgnoreStack AddListedIgnoreFiles(
        IgnoreStack ignores,
        WalkDir dir,
        List<(string Name, DirentKind Kind, bool Hidden)> entries,
        IgnorePolicy policy,
        WalkStats stats)
    {
        var git = false;
        var dot = false;
        var rg = false;
        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i].Name)
            {
                case ".gitignore":
                    git = true;
                    break;
                case ".ignore":
                    dot = true;
                    break;
                case ".rgignore":
                    rg = true;
                    break;
            }
        }

        if (policy.Vcs && git)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".gitignore"), IgnoreSource.Git, dir.ScopeRelative, stats);
        }

        if (policy.Custom && dot)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".ignore"), IgnoreSource.DotIgnore, dir.ScopeRelative, stats);
        }

        if (policy.Custom && rg)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".rgignore"), IgnoreSource.RgIgnore, dir.ScopeRelative, stats);
        }

        return ignores;
    }

    private static IgnoreStack LoadAncestors(string scopeRoot, string walkRoot, IgnorePolicy policy)
    {
        var stack = IgnoreStack.Empty;
        if (policy.Vcs)
        {
            stack = LoadVcsRoot(stack, scopeRoot);
        }

        var walkFull = Path.GetFullPath(walkRoot);
        foreach (var dir in Chain(scopeRoot, walkRoot))
        {
            if (string.Equals(dir, walkFull, StringComparison.Ordinal))
            {
                continue;
            }

            var rel = Relative(scopeRoot, dir);
            if (policy.Vcs)
            {
                stack = TryAddIfExists(stack, Path.Combine(dir, ".gitignore"), IgnoreSource.Git, rel);
            }

            if (policy.Custom)
            {
                stack = TryAddIfExists(stack, Path.Combine(dir, ".ignore"), IgnoreSource.DotIgnore, rel);
                stack = TryAddIfExists(stack, Path.Combine(dir, ".rgignore"), IgnoreSource.RgIgnore, rel);
            }
        }

        return stack;
    }

    /// <summary>
    /// Global excludes and <c>info/exclude</c> apply only when <paramref name="scopeRoot"/> has a
    /// <c>.git</c> file or directory. Patterns are relative to that git root.
    /// </summary>
    private static IgnoreStack LoadVcsRoot(IgnoreStack stack, string scopeRoot)
    {
        var common = GitConfig.ResolveCommonGitDir(scopeRoot);
        if (common == null)
        {
            return stack;
        }

        var excludes = GitConfig.FindExcludesPath(common);
        if (!string.IsNullOrEmpty(excludes))
        {
            stack = TryAddIfExists(stack, excludes, IgnoreSource.Global, "");
        }

        return TryAddIfExists(stack, Path.Combine(common, "info", "exclude"), IgnoreSource.Exclude, "");
    }

    private static IgnoreStack TryAddIfExists(IgnoreStack stack, string path, IgnoreSource source, string baseRelative)
    {
        if (!File.Exists(path))
        {
            return stack;
        }

        return TryAdd(stack, path, source, baseRelative, stats: null);
    }

    private static IgnoreStack TryAdd(IgnoreStack stack, string path, IgnoreSource source, string baseRelative, WalkStats? stats)
    {
        try
        {
            return stack.Add(source, baseRelative, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stats?.IncrementSkippedUnreadable();
            return stack;
        }
    }

    private static IEnumerable<string> Chain(string scopeRoot, string walkRoot)
    {
        var chain = new List<string>();
        var current = Path.GetFullPath(walkRoot);
        var top = Path.GetFullPath(scopeRoot);
        while (true)
        {
            chain.Add(current);
            if (string.Equals(current, top, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    private static string Relative(string scopeRoot, string walkRoot)
    {
        var rel = Path.GetRelativePath(scopeRoot, walkRoot).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static string? IndexRelative(string walkRoot, string? indexDirectory)
    {
        if (string.IsNullOrEmpty(indexDirectory))
        {
            return ".ngrep/index";
        }

        var full = Path.GetFullPath(indexDirectory);
        var rel = Path.GetRelativePath(walkRoot, full).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return null;
        }

        return rel == "." ? "" : rel;
    }

    private static bool IsIndexPath(string walkRelative, string? indexRelative)
    {
        if (string.IsNullOrEmpty(indexRelative))
        {
            return false;
        }

        return walkRelative.Equals(indexRelative, StringComparison.Ordinal)
            || walkRelative.StartsWith(indexRelative + "/", StringComparison.Ordinal);
    }

    private struct ListSink : IDirentSink
    {
        public List<(string Name, DirentKind Kind, bool Hidden)> Entries;

        public void OnEntry(string name, DirentKind kind, bool hiddenAttribute)
        {
            Entries ??= new List<(string, DirentKind, bool)>(32);
            Entries.Add((name, kind, hiddenAttribute));
        }
    }
}

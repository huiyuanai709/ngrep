namespace Ngrep.Ignore;

internal readonly record struct WalkedFile(string FullPath, string RelativePath);

internal readonly struct WalkDir
{
    public string Full { get; init; }

    public string WalkRelative { get; init; }

    public string ScopeRelative { get; init; }

    public IgnoreStack Ignores { get; init; }
}

internal readonly struct WalkOptions
{
    public WalkOptions(bool applyGitIgnore, bool includeHidden, string? indexRelative)
    {
        ApplyGitIgnore = applyGitIgnore;
        IncludeHidden = includeHidden;
        IndexRelative = indexRelative;
    }

    public bool ApplyGitIgnore { get; }

    public bool IncludeHidden { get; }

    public string? IndexRelative { get; }
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
    public static bool IsIgnoredFile(string walkRoot, string fullPath)
    {
        var stats = new WalkStats();
        foreach (var file in Enumerate(walkRoot, indexDirectory: null, stats, applyGitIgnore: true, includeHidden: true))
        {
            if (string.Equals(file.FullPath, fullPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static IEnumerable<WalkedFile> Enumerate(
        string walkRoot,
        string? indexDirectory,
        WalkStats stats,
        bool applyGitIgnore = true,
        bool includeHidden = false)
    {
        var root = CreateRoot(walkRoot, applyGitIgnore);
        var options = new WalkOptions(applyGitIgnore, includeHidden, IndexRelative(root.Full, indexDirectory));
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

    public static WalkOptions CreateOptions(string walkRootFull, string? indexDirectory, bool applyGitIgnore, bool includeHidden) =>
        new(applyGitIgnore, includeHidden, IndexRelative(walkRootFull, indexDirectory));

    public static WalkDir CreateRoot(string walkRoot, bool applyGitIgnore)
    {
        walkRoot = Path.GetFullPath(walkRoot);
        var scopeRoot = FindGitRoot(walkRoot) ?? walkRoot;
        var scopeRelative = Relative(scopeRoot, walkRoot);
        var ignores = applyGitIgnore ? LoadAncestors(scopeRoot, walkRoot) : IgnoreStack.Empty;
        return new WalkDir
        {
            Full = walkRoot,
            WalkRelative = "",
            ScopeRelative = scopeRelative,
            Ignores = ignores,
        };
    }

    public static void Collect(WalkDir dir, WalkOptions options, WalkStats stats, List<WalkedFile> files, List<WalkDir> children, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeCollect?.Invoke(dir.Full);
        cancellation.ThrowIfCancellationRequested();
        files.Clear();
        children.Clear();
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
        if (options.ApplyGitIgnore)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(entries[i].Name, ".gitignore", StringComparison.Ordinal))
                {
                    continue;
                }

                var gitignore = Path.Join(dir.Full, ".gitignore");
                try
                {
                    ignores = ignores.Add(dir.ScopeRelative, File.ReadAllText(gitignore));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stats.IncrementSkippedUnreadable();
                }

                break;
            }
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
            // caller asked for them. .gitignore is still read above; the file itself is
            // not a hit unless hidden names are included.
            if (!options.IncludeHidden && IsHiddenEntry(name, entry.Hidden))
            {
                continue;
            }

            var scopeRelative = JoinRelative(dir.ScopeRelative, name);
            if (options.ApplyGitIgnore && ignores.IsIgnored(scopeRelative, isDir, checkParents: false))
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
                children.Add(new WalkDir
                {
                    Full = full,
                    WalkRelative = childWalk,
                    ScopeRelative = scopeRelative,
                    Ignores = ignores,
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

    private static IgnoreStack LoadAncestors(string scopeRoot, string walkRoot)
    {
        var stack = IgnoreStack.Empty;
        foreach (var dir in Chain(scopeRoot, walkRoot))
        {
            if (string.Equals(dir, Path.GetFullPath(walkRoot), StringComparison.Ordinal))
            {
                continue;
            }

            var gitignore = Path.Combine(dir, ".gitignore");
            if (!File.Exists(gitignore))
            {
                continue;
            }

            var rel = Path.GetRelativePath(scopeRoot, dir).Replace('\\', '/');
            if (rel == ".")
            {
                rel = "";
            }

            stack = stack.Add(rel, File.ReadAllText(gitignore));
        }

        return stack;
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

namespace Netgrep.Ignore;

internal readonly record struct WalkedFile(string FullPath, string RelativePath);

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
    public static IEnumerable<WalkedFile> Enumerate(string walkRoot, string? indexDirectory, WalkStats stats)
    {
        walkRoot = Path.GetFullPath(walkRoot);
        var scopeRoot = FindGitRoot(walkRoot) ?? walkRoot;
        var indexRelative = IndexRelative(walkRoot, indexDirectory);
        var stackFrames = new Stack<(string Full, string WalkRelative, IgnoreStack Ignores)>();
        stackFrames.Push((walkRoot, "", LoadChain(scopeRoot, walkRoot)));

        while (stackFrames.Count > 0)
        {
            var (full, walkRelative, ignores) = stackFrames.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(full)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stats.IncrementSkippedUnreadable();
                continue;
            }

            var directories = new List<(string Full, string WalkRelative, string ScopeRelative)>();
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stats.IncrementSkippedUnreadable();
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var isDir = (attributes & FileAttributes.Directory) != 0;
                var childWalk = walkRelative.Length == 0 ? name : walkRelative + "/" + name;
                var scopeRelative = ScopeRelative(scopeRoot, entry);

                if (isDir && (name is ".git" or ".netgrep" || IsIndexPath(childWalk, indexRelative)))
                {
                    continue;
                }

                if (isDir)
                {
                    if (ignores.IsIgnored(scopeRelative, true))
                    {
                        continue;
                    }

                    directories.Add((entry, childWalk, scopeRelative));
                    continue;
                }

                if (ignores.IsIgnored(scopeRelative, false))
                {
                    stats.IncrementSkippedIgnored();
                    continue;
                }

                yield return new WalkedFile(entry, childWalk);
            }

            for (var i = directories.Count - 1; i >= 0; i--)
            {
                var dir = directories[i];
                var childIgnores = ignores;
                var gitignore = Path.Combine(dir.Full, ".gitignore");
                if (File.Exists(gitignore))
                {
                    childIgnores = ignores.Add(dir.ScopeRelative, File.ReadAllText(gitignore));
                }

                stackFrames.Push((dir.Full, dir.WalkRelative, childIgnores));
            }
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

    private static IgnoreStack LoadChain(string scopeRoot, string walkRoot)
    {
        var stack = IgnoreStack.Empty;
        foreach (var dir in Chain(scopeRoot, walkRoot))
        {
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

    private static string ScopeRelative(string scopeRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(scopeRoot, fullPath).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static string? IndexRelative(string walkRoot, string? indexDirectory)
    {
        if (string.IsNullOrEmpty(indexDirectory))
        {
            return ".netgrep/index";
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
}

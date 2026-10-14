using System.Collections.Concurrent;
using Ngrep.Ignore;

namespace Ngrep.Search;

internal readonly record struct ConfirmFile(string FullPath, string RelativePath);

/// <summary>
/// Confirms candidates on a fixed set of workers. A directory walk lists a folder on one
/// worker, searches most of its files there, and pushes child directories so other workers
/// can list them. <paramref name="stopOnMatch"/> is <c>-q</c> without <c>--stats</c>: the
/// first hit stops later files, not only the rest of the file that matched.
/// </summary>
internal static class ConfirmScheduler
{
    private readonly struct Work
    {
        public Work(WalkDir directory)
        {
            Directory = directory;
            Files = null;
        }

        public Work(ConfirmFile[] files)
        {
            Directory = default;
            Files = files;
        }

        public WalkDir Directory { get; }

        public ConfirmFile[]? Files { get; }

        public bool IsDirectory => Files == null;
    }

    public static List<SearchMatch> Run(
        IReadOnlyList<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Action<ConfirmFile, List<SearchMatch>> collect,
        bool stopOnMatch)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workers <= 1 || files.Count <= 1)
        {
            var matches = new List<SearchMatch>();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var before = matches.Count;
                collect(file, matches);
                if (stopOnMatch && matches.Count > before)
                {
                    break;
                }
            }

            return matches;
        }

        var next = 0;
        var halt = new int[1];
        var batchSize = Math.Max(1, Math.Min(32, files.Count / workers));
        return Finish(workers, cancellation, halt, slot =>
        {
            var local = new List<SearchMatch>();
            while (Volatile.Read(ref halt[0]) == 0 && !cancellation.IsCancellationRequested)
            {
                var start = Interlocked.Add(ref next, batchSize) - batchSize;
                if (start >= files.Count)
                {
                    break;
                }

                var end = Math.Min(files.Count, start + batchSize);
                for (var n = start; n < end; n++)
                {
                    if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    var before = local.Count;
                    collect(files[n], local);
                    if (stopOnMatch && local.Count > before)
                    {
                        Volatile.Write(ref halt[0], 1);
                        break;
                    }
                }
            }

            return local;
        });
    }

    public static List<SearchMatch> Scan(
        string walkRoot,
        string? indexDirectory,
        WalkStats stats,
        bool applyGitIgnore,
        bool includeHidden,
        PathFilter filter,
        int workers,
        CancellationToken cancellation,
        Action<ConfirmFile, List<SearchMatch>> collect,
        bool stopOnMatch)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = TreeWalker.CreateRoot(walkRoot, applyGitIgnore);
        var options = TreeWalker.CreateOptions(root.Full, indexDirectory, applyGitIgnore, includeHidden);
        if (workers <= 1)
        {
            return ScanSequential(root, options, stats, filter, cancellation, collect, stopOnMatch);
        }

        var stack = new ConcurrentStack<Work>();
        stack.Push(new Work(root));
        var pending = 1;
        var halt = new int[1];
        return Finish(workers, cancellation, halt, _ =>
        {
            var local = new List<SearchMatch>();
            var listed = new List<WalkedFile>(32);
            var children = new List<WalkDir>(8);
            var batch = new List<ConfirmFile>(32);
            while (true)
            {
                if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (!stack.TryPop(out var work))
                {
                    if (Volatile.Read(ref pending) == 0 || Volatile.Read(ref halt[0]) != 0)
                    {
                        break;
                    }

                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    if (!work.IsDirectory)
                    {
                        SearchBatch(work.Files!, local, collect, cancellation, halt, stopOnMatch);
                        continue;
                    }

                    TreeWalker.Collect(work.Directory, options, stats, listed, children);
                    batch.Clear();
                    foreach (var file in listed)
                    {
                        if (filter.Accepts(file.RelativePath))
                        {
                            batch.Add(new ConfirmFile(file.FullPath, file.RelativePath));
                        }
                    }

                    var inline = Math.Min(batch.Count, 64);
                    for (var n = 0; n < inline; n++)
                    {
                        if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        var before = local.Count;
                        collect(batch[n], local);
                        if (stopOnMatch && local.Count > before)
                        {
                            Volatile.Write(ref halt[0], 1);
                            break;
                        }
                    }

                    if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                    {
                        continue;
                    }

                    var extras = new List<Work>(children.Count + 1);
                    for (var c = children.Count - 1; c >= 0; c--)
                    {
                        extras.Add(new Work(children[c]));
                    }

                    if (batch.Count > inline)
                    {
                        var tail = new ConfirmFile[batch.Count - inline];
                        for (var n = 0; n < tail.Length; n++)
                        {
                            tail[n] = batch[inline + n];
                        }

                        extras.Add(new Work(tail));
                    }

                    if (extras.Count > 0)
                    {
                        Interlocked.Add(ref pending, extras.Count);
                        foreach (var extra in extras)
                        {
                            stack.Push(extra);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }

            return local;
        });
    }

    private static List<SearchMatch> ScanSequential(
        WalkDir root,
        WalkOptions options,
        WalkStats stats,
        PathFilter filter,
        CancellationToken cancellation,
        Action<ConfirmFile, List<SearchMatch>> collect,
        bool stopOnMatch)
    {
        var matches = new List<SearchMatch>();
        var stack = new Stack<WalkDir>();
        stack.Push(root);
        var files = new List<WalkedFile>(32);
        var children = new List<WalkDir>(8);
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            TreeWalker.Collect(dir, options, stats, files, children);
            foreach (var file in files)
            {
                if (!filter.Accepts(file.RelativePath))
                {
                    continue;
                }

                cancellation.ThrowIfCancellationRequested();
                var before = matches.Count;
                collect(new ConfirmFile(file.FullPath, file.RelativePath), matches);
                if (stopOnMatch && matches.Count > before)
                {
                    return matches;
                }
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        return matches;
    }

    private static void SearchBatch(
        ConfirmFile[] files,
        List<SearchMatch> local,
        Action<ConfirmFile, List<SearchMatch>> collect,
        CancellationToken cancellation,
        int[] halt,
        bool stopOnMatch)
    {
        foreach (var file in files)
        {
            if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
            {
                return;
            }

            var before = local.Count;
            collect(file, local);
            if (stopOnMatch && local.Count > before)
            {
                Volatile.Write(ref halt[0], 1);
                return;
            }
        }
    }

    private static List<SearchMatch> Finish(
        int workers,
        CancellationToken cancellation,
        int[] halt,
        Func<int, List<SearchMatch>> body)
    {
        var locals = new List<SearchMatch>[workers];
        var threads = new Thread[workers];
        Exception? failure = null;
        for (var i = 0; i < workers; i++)
        {
            var slot = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    locals[slot] = body(slot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    Volatile.Write(ref halt[0], 1);
                }
            })
            {
                IsBackground = true,
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (failure != null)
        {
            throw failure;
        }

        if (cancellation.IsCancellationRequested && Volatile.Read(ref halt[0]) == 0)
        {
            throw new OperationCanceledException(cancellation);
        }

        var merged = new List<SearchMatch>();
        foreach (var local in locals)
        {
            if (local != null)
            {
                merged.AddRange(local);
            }
        }

        return merged;
    }
}

using System.Threading.Channels;
using Ngrep.Ignore;

namespace Ngrep.Search;

/// <summary>
/// Same walk and confirm contract as <see cref="ConfirmScheduler"/>, with task workers.
/// Directory listing stays synchronous. Each worker reads with RandomAccess.ReadAsync
/// and then runs the synchronous matcher. <c>--async</c> is off unless the caller asks for it.
/// </summary>
internal static class AsyncScheduler
{
    public static async Task<List<SearchMatch>> Run(
        IReadOnlyList<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask> collect,
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
                await collect(file, matches, cancellation);
                if (stopOnMatch && matches.Count > before)
                {
                    break;
                }
            }

            return matches;
        }

        return await FanOut(files, workers, cancellation, collect, stopOnMatch);
    }

    public static async Task<List<SearchMatch>> Scan(
        string walkRoot,
        string? indexDirectory,
        WalkStats stats,
        bool applyGitIgnore,
        bool includeHidden,
        PathFilter filter,
        int workers,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask> collect,
        bool stopOnMatch)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = TreeWalker.CreateRoot(walkRoot, applyGitIgnore);
        var options = TreeWalker.CreateOptions(root.Full, indexDirectory, applyGitIgnore, includeHidden);
        if (workers <= 1)
        {
            return await ScanSequential(root, options, stats, filter, cancellation, collect, stopOnMatch);
        }

        var halt = new int[1];
        return await FanOut(
            Enumerate(root, options, stats, filter, cancellation, halt),
            workers,
            cancellation,
            collect,
            stopOnMatch,
            halt);
    }

    private static async Task<List<SearchMatch>> ScanSequential(
        WalkDir root,
        WalkOptions options,
        WalkStats stats,
        PathFilter filter,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask> collect,
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
                await collect(new ConfirmFile(file.FullPath, file.RelativePath), matches, cancellation);
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

    private static IEnumerable<ConfirmFile> Enumerate(
        WalkDir root,
        WalkOptions options,
        WalkStats stats,
        PathFilter filter,
        CancellationToken cancellation,
        int[] halt)
    {
        var stack = new Stack<WalkDir>();
        stack.Push(root);
        var files = new List<WalkedFile>(32);
        var children = new List<WalkDir>(8);
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Volatile.Read(ref halt[0]) != 0)
            {
                yield break;
            }

            var dir = stack.Pop();
            TreeWalker.Collect(dir, options, stats, files, children);
            foreach (var file in files)
            {
                if (filter.Accepts(file.RelativePath))
                {
                    yield return new ConfirmFile(file.FullPath, file.RelativePath);
                }
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    private static async Task<List<SearchMatch>> FanOut(
        IEnumerable<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask> collect,
        bool stopOnMatch,
        int[]? halt = null)
    {
        halt ??= new int[1];
        var channel = Channel.CreateUnbounded<ConfirmFile>(new UnboundedChannelOptions { SingleWriter = true });
        Exception? failure = null;
        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var file in files)
                {
                    if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    channel.Writer.TryWrite(file);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                Volatile.Write(ref halt[0], 1);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var locals = new List<SearchMatch>[workers];
        var consumers = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            var slot = i;
            consumers[i] = Task.Run(async () =>
            {
                var local = new List<SearchMatch>();
                try
                {
                    await foreach (var file in channel.Reader.ReadAllAsync(cancellation))
                    {
                        if (Volatile.Read(ref halt[0]) != 0 || cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        var before = local.Count;
                        await collect(file, local, cancellation);
                        if (stopOnMatch && local.Count > before)
                        {
                            Volatile.Write(ref halt[0], 1);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    Volatile.Write(ref halt[0], 1);
                }

                locals[slot] = local;
            }, CancellationToken.None);
        }

        await Task.WhenAll(consumers);
        await producer;
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

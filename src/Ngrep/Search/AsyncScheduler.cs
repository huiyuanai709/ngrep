using System.Threading.Channels;
using Ngrep.Ignore;

namespace Ngrep.Search;

internal readonly record struct ConfirmFile(string FullPath, string RelativePath);

internal readonly record struct ConfirmResult(bool Examined, bool Matched)
{
    public static ConfirmResult Skip => new(false, false);
}

internal readonly record struct EmitJob(bool Confirm, ConfirmFile File);

/// <summary>
/// Walks and confirms on task workers. Directory listing stays on the producer task.
/// Each worker reads with <see cref="RandomAccess.ReadAsync"/> and then runs the
/// synchronous SIMD and byte-automaton matcher. This is the only search scheduler.
/// </summary>
internal static class AsyncScheduler
{
    public static async Task<List<SearchMatch>> Run(
        IReadOnlyList<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask<ConfirmResult>> collect,
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
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask<ConfirmResult>> collect,
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
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask<ConfirmResult>> collect,
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
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask<ConfirmResult>> collect,
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

    /// <summary>
    /// Confirms jobs in parallel and invokes <paramref name="onFile"/> in job order.
    /// A job with <see cref="EmitJob.Confirm"/> false is a prefilter miss and is emitted
    /// without a read. Printing overlaps later confirms.
    /// </summary>
    public static async Task EmitOrdered(
        IReadOnlyList<EmitJob> jobs,
        int workers,
        CancellationToken cancellation,
        Func<ConfirmFile, List<SearchMatch>, CancellationToken, ValueTask<ConfirmResult>> collect,
        Action<EmitJob, List<SearchMatch>, ConfirmResult> onFile,
        bool stopOnMatch)
    {
        cancellation.ThrowIfCancellationRequested();
        if (jobs.Count == 0)
        {
            return;
        }

        if (workers <= 1 || CountConfirm(jobs) <= 1)
        {
            foreach (var job in jobs)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!job.Confirm)
                {
                    onFile(job, [], ConfirmResult.Skip);
                    continue;
                }

                var local = new List<SearchMatch>();
                var result = await collect(job.File, local, cancellation);
                onFile(job, local, result);
                if (stopOnMatch && result.Matched)
                {
                    break;
                }
            }

            return;
        }

        var halt = 0;
        var done = new bool[jobs.Count];
        var results = new ConfirmResult[jobs.Count];
        var batches = new List<SearchMatch>?[jobs.Count];
        var next = 0;
        Exception? failure = null;
        var gate = new object();
        var pending = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void Drain()
        {
            while (next < jobs.Count)
            {
                if (!jobs[next].Confirm)
                {
                    pending.Writer.TryWrite(next);
                    next++;
                    continue;
                }

                if (!done[next])
                {
                    break;
                }

                pending.Writer.TryWrite(next);
                if (stopOnMatch && results[next].Matched)
                {
                    Volatile.Write(ref halt, 1);
                    next = jobs.Count;
                    break;
                }

                next++;
            }

            if (next >= jobs.Count)
            {
                pending.Writer.TryComplete();
            }
        }

        lock (gate)
        {
            Drain();
        }

        var confirm = new List<int>();
        for (var i = 0; i < jobs.Count; i++)
        {
            if (jobs[i].Confirm)
            {
                confirm.Add(i);
            }
        }

        var work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleWriter = true });
        foreach (var index in confirm)
        {
            work.Writer.TryWrite(index);
        }

        work.Writer.TryComplete();
        var consumers = new Task[Math.Min(workers, Math.Max(1, confirm.Count))];
        for (var i = 0; i < consumers.Length; i++)
        {
            consumers[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var index in work.Reader.ReadAllAsync(cancellation))
                    {
                        if (Volatile.Read(ref halt) != 0 || cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        var local = new List<SearchMatch>();
                        var result = await collect(jobs[index].File, local, cancellation);
                        lock (gate)
                        {
                            if (Volatile.Read(ref halt) != 0 && next > index)
                            {
                                continue;
                            }

                            batches[index] = local;
                            results[index] = result;
                            done[index] = true;
                            Drain();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    Volatile.Write(ref halt, 1);
                }
            }, CancellationToken.None);
        }

        var emitted = Task.Run(async () =>
        {
            try
            {
                await foreach (var index in pending.Reader.ReadAllAsync(CancellationToken.None))
                {
                    var job = jobs[index];
                    if (!job.Confirm)
                    {
                        onFile(job, [], ConfirmResult.Skip);
                        continue;
                    }

                    onFile(job, batches[index] ?? [], results[index]);
                }
            }
            catch (ChannelClosedException)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                Volatile.Write(ref halt, 1);
            }
        }, CancellationToken.None);

        await Task.WhenAll(consumers);
        lock (gate)
        {
            if (failure == null && Volatile.Read(ref halt) == 0)
            {
                Drain();
            }

            pending.Writer.TryComplete(failure);
        }

        await emitted;
        if (failure != null)
        {
            throw failure;
        }

        if (cancellation.IsCancellationRequested && Volatile.Read(ref halt) == 0)
        {
            throw new OperationCanceledException(cancellation);
        }
    }

    private static int CountConfirm(IReadOnlyList<EmitJob> jobs)
    {
        var count = 0;
        foreach (var job in jobs)
        {
            if (job.Confirm)
            {
                count++;
            }
        }

        return count;
    }
}

using System.Collections.Concurrent;

namespace Ngrep.Search;

internal readonly record struct ConfirmFile(string FullPath, string RelativePath);

/// <summary>
/// Confirms candidates with a bounded worker pool. The producer enumerates files (the gitignore
/// walk stays single-threaded). Workers read and search. Cancellation unblocks both sides.
/// </summary>
internal static class ConfirmScheduler
{
    public static List<SearchMatch> Run(
        IEnumerable<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Action<ConfirmFile, List<SearchMatch>> collect)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workers <= 1)
        {
            var matches = new List<SearchMatch>();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                collect(file, matches);
            }

            return matches;
        }

        return RunParallel(files, workers, cancellation, collect);
    }

    private static List<SearchMatch> RunParallel(
        IEnumerable<ConfirmFile> files,
        int workers,
        CancellationToken cancellation,
        Action<ConfirmFile, List<SearchMatch>> collect)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var queue = new BlockingCollection<ConfirmFile>(Math.Max(8, workers * 8));
        Exception? failure = null;
        var locals = new List<SearchMatch>[workers];

        var producer = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var file in files)
                {
                    queue.Add(file, linked.Token);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                TryCancel(linked);
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            var slot = i;
            locals[slot] = [];
            tasks[i] = Task.Factory.StartNew(() =>
            {
                var local = locals[slot];
                try
                {
                    foreach (var file in queue.GetConsumingEnumerable(linked.Token))
                    {
                        try
                        {
                            collect(file, local);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                            TryCancel(linked);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // The caller canceled, or a sibling failed and unblocked the queue.
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException)
        {
            // Workers catch their own failures. This is a backstop.
        }

        try
        {
            producer.Wait();
        }
        catch (AggregateException)
        {
        }

        if (cancellation.IsCancellationRequested || failure is OperationCanceledException)
        {
            throw new OperationCanceledException(cancellation);
        }

        if (failure != null)
        {
            throw failure;
        }

        var merged = new List<SearchMatch>();
        foreach (var local in locals)
        {
            merged.AddRange(local);
        }

        return merged;
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

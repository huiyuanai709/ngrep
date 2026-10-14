namespace Ngrep;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        // Async file reads park work on the pool. Keep at least two workers per
        // CPU so a read completion is not waiting on thread-pool injection.
        ThreadPool.GetMinThreads(out var workers, out var io);
        var min = Math.Max(workers, Environment.ProcessorCount * 2);
        ThreadPool.SetMinThreads(min, Math.Max(io, Environment.ProcessorCount));
        return Cli.NgrepCli.Run(args);
    }
}

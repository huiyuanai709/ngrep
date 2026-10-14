using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class ThreadPoolWarmup
{
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        var min = Math.Max(workers, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(min, Math.Max(io, Environment.ProcessorCount));
    }
}

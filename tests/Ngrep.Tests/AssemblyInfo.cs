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

        // Walker tests that create .git must not inherit this machine's excludes.
        var root = Path.Combine(Path.GetTempPath(), "ngrep-git-config-isolation");
        Directory.CreateDirectory(Path.Combine(root, "xdg"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "no-global-gitconfig"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "xdg"));
    }
}

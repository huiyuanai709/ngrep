using System.Diagnostics;
using System.Text;
using Ngrep.Ignore;
using Ngrep.Search;
using Ngrep.Text;

// Cold-scan microbench. Two corpora:
//   small files — open/walk cost of a literal miss
//   large source-like files — confirm, regex prefilter, and -l early stop
// Prints the same rg commands when rg is on PATH.
var files = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 2500;
var smallBytes = 32 * 1024;
var largeCount = 8;
var largeBytes = 8 * 1024 * 1024;
var root = Directory.CreateTempSubdirectory("ngrep-bench-").FullName;
var largeRoot = Path.Combine(root, "large");
try
{
    WriteSmall(root, files, smallBytes);
    WriteLarge(largeRoot, largeCount, largeBytes);

    ScannerThroughput();
    Warm(root, "unlikely-token-zz");
    var miss = Time(root, "unlikely-token-zz", regex: false);
    var confirm = Time(root, "NEEDLE", regex: false);
    var regex = Time(root, "NEEDLE[0-9]", regex: true);
    var listed = Time(largeRoot, "EARLY", regex: false, filesWithMatches: true);
    var largeMiss = Time(largeRoot, "unlikely-token-zz", regex: false);
    var largeConfirm = Time(largeRoot, "NEEDLE", regex: false);
    var largeRegex = Time(largeRoot, "NEEDLE[0-9]", regex: true);
    var classMiss = Time(largeRoot, "[abc]{8}", regex: true);

    Console.WriteLine($"small_files={files} bytes_each={smallBytes} total_mib={files * (long)smallBytes / (1024 * 1024)}");
    Print("literal_miss", miss);
    Print("literal_confirm", confirm);
    Print("regex_prefilter", regex);
    Console.WriteLine($"large_files={largeCount} bytes_each={largeBytes}");
    Print("large_miss", largeMiss);
    Print("large_confirm", largeConfirm);
    Print("large_regex", largeRegex);
    Print("regex_class_miss", classMiss);
    Print("files_with_matches", listed);

    var tiny = Path.Combine(root, "tiny");
    var tinyFiles = WriteTiny(tiny, dirs: 40, sub: 20, perDir: 15);
    WarmTiny(tiny, "unlikely-token-zz");
    var tinyMiss = TimeTiny(tiny, "unlikely-token-zz", ignore: true, quiet: false, list: false);
    var tinyNoIgnore = TimeTiny(tiny, "unlikely-token-zz", ignore: false, quiet: false, list: false);
    var tinyQuiet = TimeTiny(tiny, "NEEDLE", ignore: true, quiet: true, list: false);
    var tinyList = TimeTiny(tiny, "token", ignore: true, quiet: false, list: true);
    var walkMs = TimeWalk(tiny, gitignore: true);
    Console.WriteLine($"tiny_files={tinyFiles}");
    Print("tiny_ignore_miss", tinyMiss);
    Print("tiny_noignore_miss", tinyNoIgnore);
    Print("tiny_quiet_hit", tinyQuiet);
    Print("tiny_list_hit", tinyList);
    Console.WriteLine($"tiny_walk_ms={walkMs:F1}");

    var rg = WhichRg();
    if (rg != null)
    {
        var rgMiss = TimeRg(rg, root, ["-F", "--no-ignore", "-uu", "unlikely-token-zz", root]);
        var rgConfirm = TimeRg(rg, root, ["-F", "--no-ignore", "-uu", "NEEDLE", root]);
        var rgRegex = TimeRg(rg, root, ["--no-ignore", "-uu", "NEEDLE[0-9]", root]);
        var rgList = TimeRg(rg, largeRoot, ["-F", "-l", "--no-ignore", "-uu", "EARLY", largeRoot]);
        var rgTiny = TimeRg(rg, tiny, ["-F", "--no-heading", "unlikely-token-zz", tiny]);
        var rgTinyU = TimeRg(rg, tiny, ["-F", "--no-ignore", "-uu", "--no-heading", "unlikely-token-zz", tiny]);
        var rgQuiet = TimeRg(rg, tiny, ["-F", "-q", "NEEDLE", tiny]);
        var rgFiles = TimeRg(rg, tiny, ["-F", "-l", "token", tiny]);
        var rgLargeMiss = TimeRg(rg, largeRoot, ["-F", "--no-ignore", "-uu", "unlikely-token-zz", largeRoot]);
        var rgLargeRegex = TimeRg(rg, largeRoot, ["--no-ignore", "-uu", "NEEDLE[0-9]", largeRoot]);
        var rgClass = TimeRg(rg, largeRoot, ["--no-ignore", "-uu", "[abc]{8}", largeRoot]);
        Console.WriteLine($"rg={rg}");
        Console.WriteLine($"rg_tiny_ignore_miss_ms={rgTiny:F1}");
        Console.WriteLine($"rg_tiny_noignore_miss_ms={rgTinyU:F1}");
        Console.WriteLine($"rg_tiny_quiet_hit_ms={rgQuiet:F1}");
        Console.WriteLine($"rg_tiny_list_hit_ms={rgFiles:F1}");
        Console.WriteLine($"rg_literal_miss_ms={rgMiss:F1}");
        Console.WriteLine($"rg_literal_confirm_ms={rgConfirm:F1}");
        Console.WriteLine($"rg_regex_ms={rgRegex:F1}");
        Console.WriteLine($"rg_files_with_matches_ms={rgList:F1}");
        Console.WriteLine($"rg_large_miss_ms={rgLargeMiss:F1}");
        Console.WriteLine($"rg_large_regex_ms={rgLargeRegex:F1}");
        Console.WriteLine($"rg_regex_class_miss_ms={rgClass:F1}");
    }

    Console.WriteLine("hyperfine example (after `dotnet publish -c Release -r linux-x64`):");
    Console.WriteLine($"  hyperfine -w 1 -r 3 'rg -F --no-ignore -uu unlikely-token-zz {root}' '<publish>/ngrep --scan unlikely-token-zz {root}'");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Print(string name, (double Ms, int Files, int Matches) sample) =>
    Console.WriteLine($"{name}_ms={sample.Ms:F1} files={sample.Files} matches={sample.Matches}");

static void WriteSmall(string root, int files, int bytesEach)
{
    var line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx const token = 1;\n"u8;
    var body = new byte[bytesEach];
    for (var offset = 0; offset + line.Length <= body.Length; offset += line.Length)
    {
        line.CopyTo(body.AsSpan(offset));
    }

    for (var i = 0; i < files; i++)
    {
        var dir = Path.Combine(root, "d" + (i % 20));
        Directory.CreateDirectory(dir);
        var suffix = i == files / 2 ? "NEEDLE9\n"u8 : "NEEDLE\n"u8;
        suffix.CopyTo(body.AsSpan(body.Length - suffix.Length));
        File.WriteAllBytes(Path.Combine(dir, $"f{i}.ts"), body);
    }
}

static void ScannerThroughput()
{
    var data = new byte[32 << 20];
    var line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx const token = 1;\n"u8;
    for (var offset = 0; offset + line.Length <= data.Length; offset += line.Length)
    {
        line.CopyTo(data.AsSpan(offset));
    }

    var needle = "unlikely-token-zz"u8.ToArray();
    var scanner = SimdLiteralScanner.Create([needle], ignoreCase: false);
    scanner.IndexOfAny(data);
    const int iters = 8;
    var clock = Stopwatch.StartNew();
    var found = 0;
    for (var i = 0; i < iters; i++)
    {
        found += scanner.IndexOfAny(data);
    }

    clock.Stop();
    var bytes = (double)data.Length * iters;
    Console.WriteLine($"isa={SimdLiteralScanner.Active} scanner_miss_gbps={bytes / clock.Elapsed.TotalSeconds / 1e9:F2} found={found}");

    clock.Restart();
    for (var i = 0; i < iters; i++)
    {
        found += data.AsSpan().IndexOf(needle);
    }

    clock.Stop();
    Console.WriteLine($"bcl_indexof_gbps={bytes / clock.Elapsed.TotalSeconds / 1e9:F2} found={found}");
}

static void WriteLarge(string root, int count, int bytesEach)
{
    Directory.CreateDirectory(root);
    var line = Encoding.ASCII.GetBytes(new string('x', 48) + " const token = 1;\n");
    for (var i = 0; i < count; i++)
    {
        using var stream = File.Create(Path.Combine(root, $"big{i}.ts"));
        var needle = i == 0 ? "EARLY\n" : "xxxxxx\n";
        stream.Write(Encoding.ASCII.GetBytes(needle));
        var left = bytesEach - needle.Length - 8;
        while (left >= line.Length)
        {
            stream.Write(line);
            left -= line.Length;
        }

        stream.Write(Encoding.ASCII.GetBytes("NEEDLE\n"));
    }
}

static void Warm(string root, string pattern) =>
    Searcher.Search(Query(root, pattern, false, false));

static (double Ms, int Files, int Matches) Time(string root, string pattern, bool regex, bool filesWithMatches = false)
{
    var query = Query(root, pattern, regex, filesWithMatches);
    Searcher.Search(query);
    var clock = Stopwatch.StartNew();
    var outcome = Searcher.Search(query);
    clock.Stop();
    return (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
}

static SearchQuery Query(string root, string pattern, bool regex, bool filesWithMatches) => new()
{
    Pattern = pattern,
    Path = root,
    Regex = regex,
    ForceScan = true,
    NoIgnore = true,
    IncludeHidden = true,
    Report = filesWithMatches ? ReportMode.FilesWithMatches : ReportMode.Matches,
    MaxDegreeOfParallelism = Environment.ProcessorCount,
};

static string? WhichRg()
{
    foreach (var candidate in new[] { "/exec-daemon/rg", "rg" })
    {
        if (candidate.Contains('/') && File.Exists(candidate))
        {
            return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path == null)
        {
            continue;
        }

        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, "rg");
            if (File.Exists(full))
            {
                return full;
            }
        }
    }

    return null;
}

static int WriteTiny(string root, int dirs, int sub, int perDir)
{
    Directory.CreateDirectory(Path.Combine(root, ".git"));
    File.WriteAllText(Path.Combine(root, ".gitignore"), """
        *.log
        *.tmp
        *.o
        bin/
        obj/
        node_modules/
        dist/
        generated/
        *.user
        [Dd]ebug/
        **/secret.txt

        """);
    var body = "using System;\nnamespace N;\nclass C { int token = 1; }\n"u8.ToArray();
    var n = 0;
    for (var d = 0; d < dirs; d++)
    {
        for (var s = 0; s < sub; s++)
        {
            var dir = Path.Combine(root, "src", "d" + d, "s" + s);
            Directory.CreateDirectory(dir);
            for (var f = 0; f < perDir; f++)
            {
                var path = Path.Combine(dir, "f" + f + ".cs");
                if (d == 0 && s == 0 && f == 0)
                {
                    var withNeedle = new byte[body.Length + 7];
                    body.CopyTo(withNeedle, 0);
                    "NEEDLE\n"u8.CopyTo(withNeedle.AsSpan(body.Length));
                    File.WriteAllBytes(path, withNeedle);
                }
                else
                {
                    File.WriteAllBytes(path, body);
                }

                n++;
            }

            File.WriteAllText(Path.Combine(dir, "skip.log"), "noise\n");
            File.WriteAllText(Path.Combine(dir, "scratch.tmp"), "noise\n");
        }
    }

    var junk = Path.Combine(root, "src", "node_modules", "pkg");
    Directory.CreateDirectory(junk);
    for (var i = 0; i < 100; i++)
    {
        File.WriteAllText(Path.Combine(junk, "f" + i + ".js"), "module.exports = 1;\n");
    }

    return n;
}

static void WarmTiny(string root, string pattern) =>
    Searcher.Search(TinyQuery(root, pattern, ignore: true, quiet: false, list: false));

static (double Ms, int Files, int Matches) TimeTiny(string root, string pattern, bool ignore, bool quiet, bool list)
{
    var clock = Stopwatch.StartNew();
    var outcome = Searcher.Search(TinyQuery(root, pattern, ignore, quiet, list));
    clock.Stop();
    return (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
}

static double TimeWalk(string root, bool gitignore)
{
    var stats = new WalkStats();
    var clock = Stopwatch.StartNew();
    var n = 0;
    foreach (var _ in TreeWalker.Enumerate(root, null, stats, gitignore, includeHidden: false))
    {
        n++;
    }

    clock.Stop();
    Console.WriteLine($"tiny_walk_files={n} ignored={stats.SkippedIgnored}");
    return clock.Elapsed.TotalMilliseconds;
}

static SearchQuery TinyQuery(string root, string pattern, bool ignore, bool quiet, bool list) => new()
{
    Pattern = pattern,
    Path = root,
    ForceScan = true,
    NoIgnore = !ignore,
    IncludeHidden = false,
    Report = quiet ? ReportMode.Quiet : list ? ReportMode.FilesWithMatches : ReportMode.Matches,
    MaxDegreeOfParallelism = Environment.ProcessorCount,
};

static double TimeRg(string rg, string workDir, string[] args)
{
    var info = new ProcessStartInfo
    {
        FileName = rg,
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in args)
    {
        info.ArgumentList.Add(arg);
    }

    var clock = Stopwatch.StartNew();
    using var process = Process.Start(info)!;
    process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();
    clock.Stop();
    return clock.Elapsed.TotalMilliseconds;
}

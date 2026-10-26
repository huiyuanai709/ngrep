using System.Diagnostics;
using System.Text;
using Ngrep.Ignore;
using Ngrep.Search;
using Ngrep.Text;

// Warm-scan microbench. Corpora stay separate so every tool searches the same tree:
//   root — small files plus large/ (literal miss, confirm, regex)
//   root/large — large files only
//   a sibling tiny tree — walk/ignore, created after the paired timings
// In-process rows are Searcher only. Process rows warm once, then time once.
// Optional args[1] is an ngrep binary (Native AOT) timed as a subprocess.
var files = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 2500;
var ngrepBin = args.Length > 1 && File.Exists(args[1]) ? Path.GetFullPath(args[1]) : null;
var smallBytes = 32 * 1024;
var largeCount = 8;
var largeBytes = 8 * 1024 * 1024;
var root = Directory.CreateTempSubdirectory("ngrep-bench-").FullName;
var largeRoot = Path.Combine(root, "large");
var tiny = Directory.CreateTempSubdirectory("ngrep-bench-tiny-").FullName;
// One 31 MiB file, the largest the reader accepts: a first-line hit confirms over
// the whole file, and a stopped read of it shows the map-then-scan path.
var oneBig = Directory.CreateTempSubdirectory("ngrep-bench-onebig-").FullName;
try
{
    WriteSmall(root, files, smallBytes);
    WriteLarge(largeRoot, largeCount, largeBytes);
    WriteLarge(oneBig, 1, 31 * 1024 * 1024);

    ScannerThroughput();
    var miss = await Time(root, "unlikely-token-zz", regex: false);
    var confirm = await Time(root, "NEEDLE", regex: false);
    var regex = await Time(root, "NEEDLE[0-9]", regex: true);
    var listed = await Time(largeRoot, "EARLY", regex: false, filesWithMatches: true);
    var largeMiss = await Time(largeRoot, "unlikely-token-zz", regex: false);
    var largeConfirm = await Time(largeRoot, "NEEDLE", regex: false);
    var largeRegex = await Time(largeRoot, "NEEDLE[0-9]", regex: true);
    var classMiss = await Time(largeRoot, "[abc]{8}", regex: true);
    var singleFile = await TimeSingleFile(Path.Combine(root, $"d{(files - 1) % 20}", $"f{files - 1}.ts"), "NEEDLE");
    var largeListMiss = await Time(oneBig, "unlikely-token-zz", regex: false, filesWithMatches: true, workers: 1, runs: 5);
    var largeEarlyConfirm = await Time(oneBig, "EARLY", regex: false, workers: 1, runs: 5);

    Console.WriteLine("measure=in-process paired=yes");
    Console.WriteLine("new_rows=best_of_5");
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
    Print("single_file_scan", singleFile);
    Print("large_list_miss", largeListMiss);
    Print("large_early_confirm", largeEarlyConfirm);

    var rg = Which("rg", "/exec-daemon/rg");
    var grep = Which("grep");
    Console.WriteLine("measure=process+startup paired=yes");
    Console.WriteLine($"rg={rg ?? "missing"}");
    Console.WriteLine($"grep={grep ?? "missing"}");
    if (grep != null)
    {
        Console.WriteLine($"grep_version={FirstLine(grep, ["--version"])}");
    }

    if (rg != null)
    {
        Console.WriteLine($"rg_version={FirstLine(rg, ["--version"])}");
    }

    Console.WriteLine($"ngrep_bin={ngrepBin ?? "missing"}");
    var cases = PairedCases(root, largeRoot);
    foreach (var sample in cases)
    {
        if (ngrepBin != null)
        {
            TimeTool("ngrep", ngrepBin, sample.Name, sample.Ngrep);
        }

        if (rg != null)
        {
            TimeTool("rg", rg, sample.Name, sample.Rg);
        }

        if (grep != null)
        {
            TimeTool("grep", grep, sample.Name, sample.Grep);
        }
    }

    var tinyFiles = WriteTiny(tiny, dirs: 40, sub: 20, perDir: 15);
    var tinySingle = await TimeSingleFile(Path.Combine(tiny, "src", "d39", "s19", "f14.cs"), "token");
    var tinyMiss = await TimeTiny(tiny, "unlikely-token-zz", ignore: true, quiet: false, list: false);
    var tinyNoIgnore = await TimeTiny(tiny, "unlikely-token-zz", ignore: false, quiet: false, list: false);
    var tinyQuiet = await TimeTiny(tiny, "NEEDLE", ignore: true, quiet: true, list: false);
    var tinyList = await TimeTiny(tiny, "token", ignore: true, quiet: false, list: true);
    var walkMs = TimeWalk(tiny, gitignore: true);
    Console.WriteLine($"tiny_files={tinyFiles} tiny_root={tiny}");
    Print("tiny_single_file_scan", tinySingle);
    Print("tiny_ignore_miss", tinyMiss);
    Print("tiny_noignore_miss", tinyNoIgnore);
    Print("tiny_quiet_hit", tinyQuiet);
    Print("tiny_list_hit", tinyList);
    Console.WriteLine($"tiny_walk_ms={walkMs:F1}");
    if (rg != null)
    {
        TimeTool("rg", rg, "tiny_ignore_miss", ["-F", "--no-heading", "unlikely-token-zz", tiny]);
        TimeTool("rg", rg, "tiny_noignore_miss", ["-F", "--no-ignore", "-uu", "--no-heading", "unlikely-token-zz", tiny]);
        TimeTool("rg", rg, "tiny_quiet_hit", ["-F", "-q", "NEEDLE", tiny]);
        TimeTool("rg", rg, "tiny_list_hit", ["-F", "-l", "token", tiny]);
    }

    Console.WriteLine("hyperfine example (after `dotnet publish -c Release -r linux-x64`):");
    Console.WriteLine($"  hyperfine -w 1 -r 3 'rg -F --no-ignore -uu unlikely-token-zz {root}' '<publish>/ngrep --scan --no-ignore --hidden unlikely-token-zz {root}'");
}
finally
{
    Directory.Delete(root, recursive: true);
    Directory.Delete(tiny, recursive: true);
    Directory.Delete(oneBig, recursive: true);
}

static void Print(string name, (double Ms, int Files, int Matches) sample) =>
    Console.WriteLine($"{name}_ms={sample.Ms:F1} files={sample.Files} matches={sample.Matches}");

static void WriteSmall(string root, int files, int bytesEach)
{
    var line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx const token = 1;\n"u8;
    var body = new byte[bytesEach];
    var filled = 0;
    while (filled + line.Length <= body.Length)
    {
        line.CopyTo(body.AsSpan(filled));
        filled += line.Length;
    }

    // A zero tail is a NUL. ngrep only probes the first 8 KiB, so it would still
    // search the file, while rg skips it as binary. Fill the remainder with text.
    if (filled < body.Length)
    {
        body.AsSpan(filled).Fill((byte)'x');
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

    if (Ngrep.Search.ByteAutomaton.TryCompile("[abc]{8}", new Ngrep.Search.ByteAutomaton.Options(false, false), out var classProgram))
    {
        var hits = new List<Ngrep.Search.ByteMatch>();
        classProgram.Collect(data, false, false, hits);
        clock.Restart();
        for (var i = 0; i < iters; i++)
        {
            hits.Clear();
            classProgram.Collect(data, false, false, hits);
        }

        clock.Stop();
        Console.WriteLine($"class_run_miss_gbps={bytes / clock.Elapsed.TotalSeconds / 1e9:F2} hits={hits.Count} program={classProgram.GetType().Name}");
    }

    if (Ngrep.Search.ByteAutomaton.TryCompile("[A-Z]{8}", new Ngrep.Search.ByteAutomaton.Options(false, false), out var wideProgram))
    {
        var hits = new List<Ngrep.Search.ByteMatch>();
        wideProgram.Collect(data, false, false, hits);
        clock.Restart();
        for (var i = 0; i < iters; i++)
        {
            hits.Clear();
            wideProgram.Collect(data, false, false, hits);
        }

        clock.Stop();
        Console.WriteLine($"wide_class_gbps={bytes / clock.Elapsed.TotalSeconds / 1e9:F2} hits={hits.Count} program={wideProgram.GetType().Name}");
    }
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

static async Task<(double Ms, int Files, int Matches)> Time(string root, string pattern, bool regex, bool filesWithMatches = false, int workers = 0, int runs = 1)
{
    var query = Query(root, pattern, regex, filesWithMatches, workers);
    await Searcher.Search(query);
    var best = (Ms: double.MaxValue, Files: 0, Matches: 0);
    for (var run = 0; run < runs; run++)
    {
        var clock = Stopwatch.StartNew();
        var outcome = await Searcher.Search(query);
        clock.Stop();
        if (clock.Elapsed.TotalMilliseconds < best.Ms)
        {
            best = (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
        }
    }

    return best;
}

static SearchQuery Query(string root, string pattern, bool regex, bool filesWithMatches, int workers) => new()
{
    Pattern = pattern,
    Path = root,
    Regex = regex,
    ForceScan = true,
    NoIgnore = true,
    IncludeHidden = true,
    Report = filesWithMatches ? ReportMode.FilesWithMatches : ReportMode.Matches,
    MaxDegreeOfParallelism = workers > 0 ? workers : Environment.ProcessorCount,
};

// A named file keeps the ignore policy on, which is the path that used to enumerate
// the whole parent tree to decide whether the one file is ignored.
static async Task<(double Ms, int Files, int Matches)> TimeSingleFile(string file, string pattern)
{
    var query = new SearchQuery
    {
        Pattern = pattern,
        Path = file,
        ForceScan = true,
        Report = ReportMode.FilesWithMatches,
        MaxDegreeOfParallelism = 1,
    };
    await Searcher.Search(query);
    var best = (Ms: double.MaxValue, Files: 0, Matches: 0);
    for (var run = 0; run < 5; run++)
    {
        var clock = Stopwatch.StartNew();
        var outcome = await Searcher.Search(query);
        clock.Stop();
        if (clock.Elapsed.TotalMilliseconds < best.Ms)
        {
            best = (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
        }
    }

    return best;
}

static string? Which(string name, params string[] preferred)
{
    foreach (var candidate in preferred)
    {
        if (candidate.Contains('/') && File.Exists(candidate))
        {
            return candidate;
        }
    }

    var path = Environment.GetEnvironmentVariable("PATH");
    if (path == null)
    {
        return null;
    }

    foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
    {
        var full = Path.Combine(dir, name);
        if (File.Exists(full))
        {
            return full;
        }
    }

    return null;
}

static (string Name, string[] Rg, string[] Grep, string[] Ngrep)[] PairedCases(string root, string largeRoot) =>
[
    ("literal_miss",
        ["-F", "--no-ignore", "-uu", "unlikely-token-zz", root],
        ["-F", "-r", "unlikely-token-zz", root],
        ["--scan", "--no-ignore", "--hidden", "unlikely-token-zz", root]),
    ("literal_confirm",
        ["-F", "--no-ignore", "-uu", "NEEDLE", root],
        ["-F", "-r", "NEEDLE", root],
        ["--scan", "--no-ignore", "--hidden", "NEEDLE", root]),
    ("regex_prefilter",
        ["--no-ignore", "-uu", "NEEDLE[0-9]", root],
        ["-E", "-r", "NEEDLE[0-9]", root],
        ["-E", "--scan", "--no-ignore", "--hidden", "NEEDLE[0-9]", root]),
    ("large_miss",
        ["-F", "--no-ignore", "-uu", "unlikely-token-zz", largeRoot],
        ["-F", "-r", "unlikely-token-zz", largeRoot],
        ["--scan", "--no-ignore", "--hidden", "unlikely-token-zz", largeRoot]),
    ("large_regex",
        ["--no-ignore", "-uu", "NEEDLE[0-9]", largeRoot],
        ["-E", "-r", "NEEDLE[0-9]", largeRoot],
        ["-E", "--scan", "--no-ignore", "--hidden", "NEEDLE[0-9]", largeRoot]),
    ("regex_class_miss",
        ["--no-ignore", "-uu", "[abc]{8}", largeRoot],
        ["-E", "-r", "[abc]{8}", largeRoot],
        ["-E", "--scan", "--no-ignore", "--hidden", "[abc]{8}", largeRoot]),
    ("files_with_matches",
        ["-F", "-l", "--no-ignore", "-uu", "EARLY", largeRoot],
        ["-F", "-l", "-r", "EARLY", largeRoot],
        ["--scan", "--no-ignore", "--hidden", "-l", "EARLY", largeRoot]),
];

static void TimeTool(string name, string bin, string sample, string[] argv)
{
    var ms = TimeProcess(bin, argv);
    Console.WriteLine($"{name}_{sample}_ms={ms:F1}");
    Console.WriteLine($"argv_{name}_{sample}={bin} {string.Join(' ', argv)}");
}

static string FirstLine(string bin, string[] argv)
{
    var info = StartInfo(bin, argv);
    using var process = Process.Start(info)!;
    var line = process.StandardOutput.ReadLine() ?? "";
    process.StandardError.ReadToEnd();
    process.WaitForExit();
    return line.Trim();
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

static async Task<(double Ms, int Files, int Matches)> TimeTiny(string root, string pattern, bool ignore, bool quiet, bool list)
{
    var query = TinyQuery(root, pattern, ignore, quiet, list);
    await Searcher.Search(query);
    var clock = Stopwatch.StartNew();
    var outcome = await Searcher.Search(query);
    clock.Stop();
    return (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
}

static double TimeWalk(string root, bool gitignore)
{
    var stats = new WalkStats();
    var clock = Stopwatch.StartNew();
    var n = 0;
    foreach (var _ in TreeWalker.Enumerate(root, null, stats, gitignore ? IgnorePolicy.All : IgnorePolicy.None, includeHidden: false))
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

static double TimeProcess(string bin, string[] argv)
{
    RunProcess(bin, argv);
    var clock = Stopwatch.StartNew();
    RunProcess(bin, argv);
    clock.Stop();
    return clock.Elapsed.TotalMilliseconds;
}

static void RunProcess(string bin, string[] argv)
{
    var info = StartInfo(bin, argv);
    using var process = Process.Start(info)!;
    process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();
}

static ProcessStartInfo StartInfo(string bin, string[] argv)
{
    var info = new ProcessStartInfo
    {
        FileName = bin,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in argv)
    {
        info.ArgumentList.Add(arg);
    }

    return info;
}

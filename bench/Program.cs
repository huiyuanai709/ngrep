using System.Diagnostics;
using Ngrep.Search;

// Cold-scan microbench. Builds a throwaway tree, times an ASCII literal miss
// (the common scan), a literal hit, and a regex that has a literal prefilter.
// When `rg` is on PATH it prints the same commands for hyperfine.
var root = Directory.CreateTempSubdirectory("ngrep-bench-").FullName;
var files = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 400;
var bytesEach = 32 * 1024;
try
{
    for (var i = 0; i < files; i++)
    {
        var dir = Path.Combine(root, "d" + (i % 20));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"f{i}.ts");
        var text = new string('x', bytesEach - 16) + (i == files / 2 ? "NEEDLE_HIT\n" : "xxxxxxxxxx\n");
        File.WriteAllText(path, text);
    }

    Warm(root);
    var miss = Time(root, "unlikely-token-zz", regex: false);
    var hit = Time(root, "NEEDLE_HIT", regex: false);
    var regex = Time(root, "NEEDLE_.*HIT", regex: true);
    var listed = Time(root, "NEEDLE_HIT", regex: false, filesWithMatches: true);
    Console.WriteLine($"files={files} bytes_each={bytesEach} total_mib={files * (long)bytesEach / (1024 * 1024)}");
    Console.WriteLine($"literal_miss_ms={miss.Ms:F1} files_searched={miss.Files}");
    Console.WriteLine($"literal_hit_ms={hit.Ms:F1} matches={hit.Matches}");
    Console.WriteLine($"regex_prefilter_ms={regex.Ms:F1} matches={regex.Matches}");
    Console.WriteLine($"files_with_matches_ms={listed.Ms:F1} matches={listed.Matches}");
    Console.WriteLine("hyperfine example (after `dotnet publish -c Release -r linux-x64`):");
    Console.WriteLine($"  hyperfine -w 1 -r 3 'rg -F --no-ignore --glob \"!**/.git/**\" unlikely-token-zz {root}' '<publish>/ngrep --scan unlikely-token-zz {root}'");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Warm(string root) =>
    Searcher.Search(Query(root, "unlikely-token-zz", false, false));

static (double Ms, int Files, int Matches) Time(string root, string pattern, bool regex, bool filesWithMatches = false)
{
    var clock = Stopwatch.StartNew();
    var outcome = Searcher.Search(Query(root, pattern, regex, filesWithMatches));
    clock.Stop();
    return (clock.Elapsed.TotalMilliseconds, outcome.FilesSearched, outcome.Matches.Count);
}

static SearchQuery Query(string root, string pattern, bool regex, bool filesWithMatches) => new()
{
    Pattern = pattern,
    Path = root,
    Regex = regex,
    ForceScan = true,
    Report = filesWithMatches ? ReportMode.FilesWithMatches : ReportMode.Matches,
    MaxDegreeOfParallelism = Environment.ProcessorCount,
};

using System.CommandLine;
using System.Text;
using Ngrep.Indexing;
using Ngrep.Output;
using Ngrep.Search;
using Ngrep.Watch;

namespace Ngrep.Cli;

internal static class NgrepCli
{
    public static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var root = Build();
            var parsed = root.Parse(SearchSyntax.Rewrite(args));
            if (parsed.Errors.Count > 0)
            {
                foreach (var error in parsed.Errors)
                {
                    Console.Error.WriteLine(error.Message);
                }

                return ExitCodes.Error;
            }

            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }
    }

    private static RootCommand Build()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json, ndjson, or rgjson. rgjson is a search-only event stream.",
            DefaultValueFactory = _ => "text",
            Recursive = true,
        };
        format.AcceptOnlyFromAmong("text", "json", "ndjson", "rgjson");

        var index = new Option<string>("--index")
        {
            Description = "Index directory. Defaults to <root>/.ngrep/index. Search walks parents to find .ngrep/index when omitted.",
            Recursive = true,
        };

        var root = new RootCommand(
            "Search a tree. `ngrep <pattern> [path]` is the same as `ngrep search`. " +
            "Uses an index when one is found; otherwise scans file contents.");
        root.Options.Add(format);
        root.Options.Add(index);

        var indexPath = new Argument<string>("path")
        {
            Description = "Directory to index.",
        };
        var indexQuiet = new Option<bool>("--quiet", "-q")
        {
            Description = "Silence index progress on stderr. The stdout summary is unchanged. Search -q is a different flag and suppresses match output.",
        };
        var indexCommand = new Command("index", "Walk a tree and build or update the inverted index.");
        indexCommand.Arguments.Add(indexPath);
        indexCommand.Options.Add(indexQuiet);
        indexCommand.SetAction(result =>
        {
            var path = result.GetRequiredValue(indexPath);
            var built = Indexer.Build(path, result.GetValue(index), showProgress: !result.GetValue(indexQuiet));
            MachineOutput.WriteIndex(built, OutputFormats.Parse(result.GetValue(format)));
            return ExitCodes.Success;
        });

        var positionals = new Argument<string[]>("pattern")
        {
            Description = "Search pattern and optional path: <pattern> [path]. With -e or -f, every -e value is a pattern and every -f value is a pattern file (OR). A positional written before the first -e or -f is also a pattern; any later positional is the path.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var ignoreCase = new Option<bool>("--ignore-case", "-i")
        {
            Description = "Case-insensitive match (ordinal, culture-invariant).",
        };
        var regex = new Option<bool>("--regex", "-E", "--extended-regexp")
        {
            Description = "Treat patterns as .NET regular expressions, not POSIX BRE or ERE. -E and --extended-regexp are aliases. The default is fixed strings. Required literals are trigram-filtered when extraction is safe.",
        };
        var fixedStrings = new Option<bool>("--fixed-strings", "-F")
        {
            Description = "Treat patterns as literal text. This is the default. Mutually exclusive with -E/--regex and -P/--pcre (exit 2).",
        };
        var pcre = new Option<bool>("--pcre", "-P")
        {
            Description = "Backtracking .NET regex interpreter. Supports backreferences, lookaround, and atomic groups. Implies --regex. The default -E engine is non-backtracking and linear. Mutually exclusive with -F.",
        };
        var scan = new Option<bool>("--scan")
        {
            Description = "Ignore any on-disk index and scan file contents.",
        };
        var glob = new Option<string[]>("-g", "--glob")
        {
            Description = "Include or exclude files. Repeatable. A leading ! excludes (like ripgrep). The last matching glob wins. Globs filter after .gitignore and do not re-include ignored files.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var type = new Option<string[]>("-t", "--type")
        {
            Description = "Only files of this type. Repeatable; types are OR-ed. See --type-list.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var typeNot = new Option<string[]>("-T", "--type-not")
        {
            Description = "Exclude files of this type. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var typeList = new Option<bool>("--type-list")
        {
            Description = "List built-in file types and exit 0.",
        };
        var typeAdd = new Option<string[]>("--type-add")
        {
            Description = "Add globs to a type: name:*.ext[,*.ext] or name:include:*.ext. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var after = new Option<int>("-A", "--after-context")
        {
            Description = "Lines of context to print after each match.",
        };
        var before = new Option<int>("-B", "--before-context")
        {
            Description = "Lines of context to print before each match.",
        };
        var context = new Option<int>("-C", "--context")
        {
            Description = "Lines of context before and after each match. -A and -B override that side.",
        };
        var regexp = new Option<string[]>("-e", "--regexp")
        {
            Description = "Pattern to search for. Repeatable. Matches if any pattern matches (OR).",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var patternFile = new Option<string[]>("-f", "--file")
        {
            Description = "Read patterns from a file, one per line. An empty line matches every line. A line starting with # is a pattern, not a comment. '-' reads stdin. Repeatable, OR-ed with -e and a positional pattern.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var word = new Option<bool>("-w", "--word-regexp")
        {
            Description = "Only match when both ends are word boundaries (Unicode letter/digit or underscore).",
        };
        var lineRegexp = new Option<bool>("--line-regexp", "-x")
        {
            Description = "Match only when the entire line matches. A literal must equal the line. A regex is tested against each line, with a trailing CR removed, anchored at both ends. .NET regex, not POSIX ERE.",
        };
        var invert = new Option<bool>("-v", "--invert-match")
        {
            Description = "Print lines that do not match. Does not trigram-filter; reads candidates in parallel and stops each matching line at the first hit.",
        };
        var noIgnore = new Option<bool>("--no-ignore")
        {
            Description = "Do not respect .gitignore. Globs still filter. .git and .ngrep stay skipped. Scans, because the index omits ignored files.",
        };
        var noIgnoreVcs = new Option<bool>("--no-ignore-vcs")
        {
            Description = "Do not respect VCS ignore files. ngrep only reads .gitignore, so this matches --no-ignore. Scans instead of using the index.",
        };
        var threads = new Option<int>("-j", "--threads")
        {
            Description = "Workers for file reads. Default is the processor count, or NGREP_THREADS. NETGREP_THREADS is used only when NGREP_THREADS is unset.",
        };
        var filesWith = new Option<bool>("--files-with-matches", "-l")
        {
            Description = "Print only the paths of files with at least one match.",
        };
        var filesWithout = new Option<bool>("--files-without-match")
        {
            Description = "Print only the paths of searched files with no matches. With -v, a match is a line that failed the pattern.",
        };
        var count = new Option<bool>("--count", "-c")
        {
            Description = "Print the number of matching lines per file (path:count). With -v, count lines that do not match. A line with several hits counts once. Zero counts are omitted.",
        };
        var quiet = new Option<bool>("--quiet", "-q", "--silent")
        {
            Description = "Print no match report on stdout. Exit 0 if any line matches, 1 if none, and 2 on error. Mutually exclusive with -c, -l, and --files-without-match. Does not hide the stderr scan note.",
        };
        var lineNumber = new Option<bool>("--line-number", "-n")
        {
            Description = "Accepted for grep compatibility. Text output always includes the 1-based line number (path:line:column:text, or line:column:text under a heading).",
        };
        var noMessages = new Option<bool>("--no-messages", "-s")
        {
            Description = "Suppress stderr for a missing search path and for an explicitly named unreadable file. The exit code is still 2. Does not hide usage errors, bad regexes, or missing pattern files. Files skipped while walking a directory stay silent.",
        };
        var onlyMatching = new Option<bool>("--only-matching", "-o")
        {
            Description = "Text output prints the matched text instead of the whole line. JSON and NDJSON keep path, line, column, text, and lineText; text is already the match.",
        };
        var hidden = new Option<bool>("--hidden")
        {
            Description = "Search hidden files and directories (a name starting with '.'). .git, .ngrep, the index directory, and symlinks stay skipped.",
        };
        var color = new Option<string>("--color")
        {
            Description = "When to color text output: auto, always, or never. auto colors a terminal unless NO_COLOR is set or TERM=dumb. JSON, NDJSON, and rgjson are never colored.",
            DefaultValueFactory = _ => "auto",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var multiline = new Option<bool>("--multiline", "-U")
        {
            Description = "Let a regex match span lines. ^ and $ are line anchors either way. '.' does not match a newline unless --multiline-dotall. The default engine is non-backtracking. -P selects the backtracking interpreter. No RegexOptions.Compiled.",
        };
        var multilineDotAll = new Option<bool>("--multiline-dotall")
        {
            Description = "Let '.' match a newline. Implies --multiline.",
        };
        var heading = new Option<bool>("--heading")
        {
            Description = "Print a path heading before each file's text matches, and omit the path from match lines. Forces headings for one file and for a pipe.",
        };
        var noHeading = new Option<bool>("--no-heading")
        {
            Description = "Keep path:line:column:text on every text match line. Overrides --heading. Default when stdout is not a terminal, TERM=dumb, or only one file was searched.",
        };
        var stats = new Option<bool>("--stats")
        {
            Description = "After the search, print files searched, files matched, lines matched, matches, and elapsed milliseconds on stderr. Stdout is unchanged. Collects every hit so -l line counts are complete.",
        };

        var searchCommand = new Command("search", "Query the index, or scan contents when no index covers the path.");
        searchCommand.Arguments.Add(positionals);
        searchCommand.Options.Add(ignoreCase);
        searchCommand.Options.Add(regex);
        searchCommand.Options.Add(pcre);
        searchCommand.Options.Add(fixedStrings);
        searchCommand.Options.Add(scan);
        searchCommand.Options.Add(glob);
        searchCommand.Options.Add(type);
        searchCommand.Options.Add(typeNot);
        searchCommand.Options.Add(typeList);
        searchCommand.Options.Add(typeAdd);
        searchCommand.Options.Add(after);
        searchCommand.Options.Add(before);
        searchCommand.Options.Add(context);
        searchCommand.Options.Add(regexp);
        searchCommand.Options.Add(patternFile);
        searchCommand.Options.Add(word);
        searchCommand.Options.Add(lineRegexp);
        searchCommand.Options.Add(invert);
        searchCommand.Options.Add(noIgnore);
        searchCommand.Options.Add(noIgnoreVcs);
        searchCommand.Options.Add(threads);
        searchCommand.Options.Add(filesWith);
        searchCommand.Options.Add(filesWithout);
        searchCommand.Options.Add(count);
        searchCommand.Options.Add(quiet);
        searchCommand.Options.Add(lineNumber);
        searchCommand.Options.Add(noMessages);
        searchCommand.Options.Add(onlyMatching);
        searchCommand.Options.Add(hidden);
        searchCommand.Options.Add(color);
        searchCommand.Options.Add(multiline);
        searchCommand.Options.Add(multilineDotAll);
        searchCommand.Options.Add(heading);
        searchCommand.Options.Add(noHeading);
        searchCommand.Options.Add(stats);
        searchCommand.SetAction(async (result, cancellationToken) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void Cancel(object? _, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
            }

            var hooked = false;
            try
            {
                Console.CancelKeyPress += Cancel;
                hooked = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }

            try
            {
                return await ExecuteSearch(
                    result,
                    format,
                    index,
                    ignoreCase,
                    regex,
                    pcre,
                    scan,
                    glob,
                    type,
                    typeNot,
                    typeList,
                    typeAdd,
                    after,
                    before,
                    context,
                    word,
                    invert,
                    noIgnore,
                    noIgnoreVcs,
                    threads,
                    filesWith,
                    filesWithout,
                    count,
                    quiet,
                    lineRegexp,
                    fixedStrings,
                    noMessages,
                    onlyMatching,
                    hidden,
                    color,
                    multiline,
                    multilineDotAll,
                    heading,
                    noHeading,
                    stats,
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("search canceled");
                return ExitCodes.Error;
            }
            finally
            {
                if (hooked)
                {
                    Console.CancelKeyPress -= Cancel;
                }
            }
        });

        var watchPath = new Argument<string>("path")
        {
            Description = "Directory to watch. Changes trigger an incremental reindex.",
        };
        var watchCommand = new Command("watch", "Watch a tree and incrementally refresh the index.");
        watchCommand.Arguments.Add(watchPath);
        watchCommand.SetAction(result =>
        {
            using var cts = new CancellationTokenSource();
            void Cancel(object? _, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
            }

            Console.CancelKeyPress += Cancel;
            try
            {
                return WatchCommand.Run(
                    result.GetRequiredValue(watchPath),
                    result.GetValue(index),
                    OutputFormats.Parse(result.GetValue(format)),
                    cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= Cancel;
            }
        });

        root.Subcommands.Add(indexCommand);
        root.Subcommands.Add(searchCommand);
        root.Subcommands.Add(watchCommand);
        return root;
    }

    private static async Task<int> ExecuteSearch(
        System.CommandLine.ParseResult result,
        Option<string> format,
        Option<string> index,
        Option<bool> ignoreCase,
        Option<bool> regex,
        Option<bool> pcre,
        Option<bool> scan,
        Option<string[]> glob,
        Option<string[]> type,
        Option<string[]> typeNot,
        Option<bool> typeList,
        Option<string[]> typeAdd,
        Option<int> after,
        Option<int> before,
        Option<int> context,
        Option<bool> word,
        Option<bool> invert,
        Option<bool> noIgnore,
        Option<bool> noIgnoreVcs,
        Option<int> threads,
        Option<bool> filesWith,
        Option<bool> filesWithout,
        Option<bool> count,
        Option<bool> quiet,
        Option<bool> lineRegexp,
        Option<bool> fixedStrings,
        Option<bool> noMessages,
        Option<bool> onlyMatching,
        Option<bool> hidden,
        Option<string> color,
        Option<bool> multiline,
        Option<bool> multilineDotAll,
        Option<bool> heading,
        Option<bool> noHeading,
        Option<bool> stats,
        CancellationToken cancellationToken)
    {
        var showTypes = result.GetValue(typeList);
        var adds = Values(result, typeAdd);
        if (showTypes)
        {
            foreach (var line in FileTypes.Create(adds).ListLines())
            {
                Console.WriteLine(line);
            }

            return ExitCodes.Success;
        }

        var bound = SearchSyntax.Bind(result, typeList: false);
        if (result.GetValue(fixedStrings) && (result.GetValue(regex) || result.GetValue(pcre)))
        {
            throw new ArgumentException("-E/--regex, -P/--pcre, and -F/--fixed-strings are mutually exclusive.");
        }

        var patterns = LoadPatterns(bound);
        var report = BindReport(result, filesWith, filesWithout, count, quiet);
        var beforeCount = ContextCount(result, before, context);
        var afterCount = ContextCount(result, after, context);
        if (beforeCount < 0 || afterCount < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        var parsedFormat = OutputFormats.Parse(result.GetValue(format));
        var colorOn = parsedFormat == OutputFormat.Text && TextColor.Enabled(TextColor.Parse(result.GetValue(color)));
        var wantStats = result.GetValue(stats);
        SearchOutcome outcome;
        try
        {
            outcome = await Searcher.Search(new SearchQuery
            {
                Pattern = patterns[0],
                AdditionalPatterns = patterns.Count > 1 ? patterns[1..] : [],
                Path = bound.Path,
                IndexDirectory = result.GetValue(index),
                IgnoreCase = result.GetValue(ignoreCase),
                Regex = result.GetValue(regex) || result.GetValue(pcre),
                Backtracking = result.GetValue(pcre),
                ForceScan = result.GetValue(scan),
                WordRegexp = result.GetValue(word),
                InvertMatch = result.GetValue(invert),
                LineRegexp = result.GetValue(lineRegexp),
                NoMessages = result.GetValue(noMessages),
                NoIgnore = result.GetValue(noIgnore),
                NoIgnoreVcs = result.GetValue(noIgnoreVcs),
                IncludeHidden = result.GetValue(hidden),
                Multiline = result.GetValue(multiline),
                MultilineDotAll = result.GetValue(multilineDotAll),
                Stats = wantStats,
                Report = report,
                BeforeContext = beforeCount,
                AfterContext = afterCount,
                Globs = Values(result, glob),
                Types = Values(result, type),
                TypeNots = Values(result, typeNot),
                TypeAdds = adds,
                MaxDegreeOfParallelism = ThreadCount(result, threads),
                CancellationToken = cancellationToken,
            });
        }
        catch (SearchPathException ex)
        {
            if (!ex.Suppress)
            {
                Console.Error.WriteLine(ex.Message);
            }

            return ExitCodes.Error;
        }
        var forceHeading = result.GetValue(heading);
        var suppressHeading = result.GetValue(noHeading);
        var useHeading = parsedFormat == OutputFormat.Text
            && report == ReportMode.Matches
            && !suppressHeading
            && (forceHeading || (TextColor.IsTerminal() && outcome.FilesSearched > 1));
        MachineOutput.Note(outcome.Note);
        MachineOutput.WriteMatches(
            outcome.Matches,
            parsedFormat,
            new SearchPresentation(report, result.GetValue(onlyMatching), colorOn, useHeading),
            outcome.FilesWithoutMatch,
            outcome.ElapsedMilliseconds);
        if (wantStats)
        {
            MachineOutput.WriteStats(outcome);
        }

        var produced = report == ReportMode.FilesWithoutMatch
            ? outcome.FilesWithoutMatch.Count > 0
            : outcome.Matches.Count > 0;
        return produced ? ExitCodes.Success : ExitCodes.NoMatches;
    }

    private static List<string> LoadPatterns(BoundSearch bound)
    {
        var patterns = new List<string>(bound.Patterns);
        foreach (var file in bound.PatternFiles)
        {
            patterns.AddRange(PatternFiles.Read(file));
        }

        if (patterns.Count == 0)
        {
            throw new ArgumentException("Pattern file contains no patterns.");
        }

        return patterns;
    }

    private static ReportMode BindReport(
        System.CommandLine.ParseResult result,
        Option<bool> filesWith,
        Option<bool> filesWithout,
        Option<bool> count,
        Option<bool> quiet)
    {
        var with = result.GetValue(filesWith);
        var without = result.GetValue(filesWithout);
        var counts = result.GetValue(count);
        var silent = result.GetValue(quiet);
        var selected = (with ? 1 : 0) + (without ? 1 : 0) + (counts ? 1 : 0) + (silent ? 1 : 0);
        if (selected > 1)
        {
            throw new ArgumentException("-l/--files-with-matches, --files-without-match, -c/--count, and -q/--quiet/--silent are mutually exclusive.");
        }

        if (with)
        {
            return ReportMode.FilesWithMatches;
        }

        if (without)
        {
            return ReportMode.FilesWithoutMatch;
        }

        if (counts)
        {
            return ReportMode.Count;
        }

        if (silent)
        {
            return ReportMode.Quiet;
        }

        return ReportMode.Matches;
    }

    private static string[] Values(System.CommandLine.ParseResult result, Option<string[]> option) =>
        result.GetValue(option) ?? [];

    private static int ContextCount(System.CommandLine.ParseResult result, Option<int> side, Option<int> both)
    {
        if (IsSet(result, side))
        {
            return result.GetValue(side);
        }

        if (IsSet(result, both))
        {
            return result.GetValue(both);
        }

        return 0;
    }

    private static int ThreadCount(System.CommandLine.ParseResult result, Option<int> threads)
    {
        if (!IsSet(result, threads))
        {
            return 0;
        }

        var value = result.GetValue(threads);
        if (value < 1)
        {
            throw new ArgumentException("--threads must be a positive integer.");
        }

        return value;
    }

    private static bool IsSet(System.CommandLine.ParseResult result, Option option)
    {
        var parsed = result.GetResult(option);
        return parsed != null && parsed.Tokens.Count > 0;
    }
}

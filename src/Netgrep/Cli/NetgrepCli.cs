using System.CommandLine;
using System.Text;
using Netgrep.Indexing;
using Netgrep.Output;
using Netgrep.Search;
using Netgrep.Watch;

namespace Netgrep.Cli;

internal static class NetgrepCli
{
    public static int Run(string[] args)
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

            return parsed.Invoke(new InvocationConfiguration
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
            Description = "Output format: text, json, or ndjson.",
            DefaultValueFactory = _ => "text",
            Recursive = true,
        };
        format.AcceptOnlyFromAmong("text", "json", "ndjson");

        var index = new Option<string>("--index")
        {
            Description = "Index directory. Defaults to <root>/.netgrep/index. Search walks parents to find .netgrep/index when omitted.",
            Recursive = true,
        };

        var root = new RootCommand(
            "Search a tree. `netgrep <pattern> [path]` is the same as `netgrep search`. " +
            "Uses an index when one is found; otherwise scans file contents.");
        root.Options.Add(format);
        root.Options.Add(index);

        var indexPath = new Argument<string>("path")
        {
            Description = "Directory to index.",
        };
        var indexCommand = new Command("index", "Walk a tree and build or update the inverted index.");
        indexCommand.Arguments.Add(indexPath);
        indexCommand.SetAction(result =>
        {
            var path = result.GetRequiredValue(indexPath);
            var built = Indexer.Build(path, result.GetValue(index));
            MachineOutput.WriteIndex(built, OutputFormats.Parse(result.GetValue(format)));
            return ExitCodes.Success;
        });

        var positionals = new Argument<string[]>("pattern")
        {
            Description = "Search pattern and optional path: <pattern> [path]. With -e, every -e value is a pattern (OR). A positional written before the first -e is also a pattern; any later positional is the path.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var ignoreCase = new Option<bool>("--ignore-case", "-i")
        {
            Description = "Case-insensitive match (ordinal, culture-invariant).",
        };
        var regex = new Option<bool>("--regex")
        {
            Description = "Treat patterns as regular expressions. Regex search reads candidate files and does not trigram-filter.",
        };
        var scan = new Option<bool>("--scan")
        {
            Description = "Ignore any on-disk index and scan file contents.",
        };
        var glob = new Option<string[]>("-g", "--glob")
        {
            Description = "Include or exclude files. Repeatable. A leading ! excludes (like ripgrep). The last matching glob wins. Globs do not re-include gitignored files.",
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
        var word = new Option<bool>("-w", "--word-regexp")
        {
            Description = "Only match when both ends are word boundaries (Unicode letter/digit or underscore).",
        };
        var invert = new Option<bool>("-v", "--invert-match")
        {
            Description = "Print lines that do not match. Reads every candidate file (no trigram filter).",
        };
        var threads = new Option<int>("-j", "--threads")
        {
            Description = "Workers for file reads. Default is the processor count, or NETGREP_THREADS.",
        };

        var searchCommand = new Command("search", "Query the index, or scan contents when no index covers the path.");
        searchCommand.Arguments.Add(positionals);
        searchCommand.Options.Add(ignoreCase);
        searchCommand.Options.Add(regex);
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
        searchCommand.Options.Add(word);
        searchCommand.Options.Add(invert);
        searchCommand.Options.Add(threads);
        searchCommand.SetAction(result =>
        {
            using var cts = new CancellationTokenSource();
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
                return ExecuteSearch(
                    result,
                    format,
                    index,
                    ignoreCase,
                    regex,
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
                    threads,
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

    private static int ExecuteSearch(
        System.CommandLine.ParseResult result,
        Option<string> format,
        Option<string> index,
        Option<bool> ignoreCase,
        Option<bool> regex,
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
        Option<int> threads,
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
        var beforeCount = ContextCount(result, before, context);
        var afterCount = ContextCount(result, after, context);
        if (beforeCount < 0 || afterCount < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        var outcome = Searcher.Search(new SearchQuery
        {
            Pattern = bound.Patterns[0],
            AdditionalPatterns = bound.Patterns.Length > 1 ? bound.Patterns[1..] : [],
            Path = bound.Path,
            IndexDirectory = result.GetValue(index),
            IgnoreCase = result.GetValue(ignoreCase),
            Regex = result.GetValue(regex),
            ForceScan = result.GetValue(scan),
            WordRegexp = result.GetValue(word),
            InvertMatch = result.GetValue(invert),
            BeforeContext = beforeCount,
            AfterContext = afterCount,
            Globs = Values(result, glob),
            Types = Values(result, type),
            TypeNots = Values(result, typeNot),
            TypeAdds = adds,
            MaxDegreeOfParallelism = ThreadCount(result, threads),
            CancellationToken = cancellationToken,
        });
        MachineOutput.Note(outcome.Note);
        MachineOutput.WriteMatches(outcome.Matches, OutputFormats.Parse(result.GetValue(format)));
        return outcome.Matches.Count > 0 ? ExitCodes.Success : ExitCodes.NoMatches;
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

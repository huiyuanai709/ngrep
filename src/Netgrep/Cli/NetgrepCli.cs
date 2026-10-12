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
            var parsed = root.Parse(args);
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

        var root = new RootCommand("netgrep searches source trees with a trigram inverted index.");
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

        var pattern = new Argument<string>("pattern")
        {
            Description = "Literal text to find. Use --regex for a regular expression.",
        };
        var searchPath = new Argument<string>("path")
        {
            Description = "File or directory to search. Defaults to the current directory.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => ".",
        };
        var ignoreCase = new Option<bool>("--ignore-case", "-i")
        {
            Description = "Case-insensitive match (ordinal, culture-invariant).",
        };
        var regex = new Option<bool>("--regex")
        {
            Description = "Treat pattern as a regular expression. Regex search scans candidate file contents and does not trigram-filter in v1.",
        };
        var scan = new Option<bool>("--scan")
        {
            Description = "Ignore any on-disk index and scan file contents.",
        };
        var searchCommand = new Command("search", "Query the index, or scan contents when no index covers the path.");
        searchCommand.Arguments.Add(pattern);
        searchCommand.Arguments.Add(searchPath);
        searchCommand.Options.Add(ignoreCase);
        searchCommand.Options.Add(regex);
        searchCommand.Options.Add(scan);
        searchCommand.SetAction(result =>
        {
            var outcome = Searcher.Search(new SearchQuery
            {
                Pattern = result.GetRequiredValue(pattern),
                Path = result.GetValue(searchPath) ?? ".",
                IndexDirectory = result.GetValue(index),
                IgnoreCase = result.GetValue(ignoreCase),
                Regex = result.GetValue(regex),
                ForceScan = result.GetValue(scan),
            });
            MachineOutput.Note(outcome.Note);
            MachineOutput.WriteMatches(outcome.Matches, OutputFormats.Parse(result.GetValue(format)));
            return outcome.Matches.Count > 0 ? ExitCodes.Success : ExitCodes.NoMatches;
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
}

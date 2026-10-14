using System.CommandLine;
using System.CommandLine.Parsing;

namespace Netgrep.Cli;

internal readonly record struct BoundSearch(string[] Patterns, string Path);

/// <summary>
/// <c>netgrep &lt;pattern&gt; [path]</c> is rewritten to <c>search</c> unless the first positional
/// token is <c>index</c>, <c>search</c>, or <c>watch</c>. <c>--</c> forces the search form so a
/// pattern named <c>index</c> is not taken as a command.
/// </summary>
internal static class SearchSyntax
{
    private static readonly HashSet<string> Subcommands = new(StringComparer.Ordinal)
    {
        "index",
        "search",
        "watch",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--format",
        "--index",
        "-g",
        "--glob",
        "-t",
        "--type",
        "-T",
        "--type-not",
        "--type-add",
        "-A",
        "--after-context",
        "-B",
        "--before-context",
        "-C",
        "--context",
        "-e",
        "--regexp",
        "-j",
        "--threads",
        "--color",
    };

    public static string[] Rewrite(string[] args)
    {
        if (args.Length == 0 || HasSubcommand(args))
        {
            return args;
        }

        var rewritten = new string[args.Length + 1];
        rewritten[0] = "search";
        args.CopyTo(rewritten, 1);
        return rewritten;
    }

    public static BoundSearch Bind(ParseResult result, bool typeList)
    {
        var patterns = new List<string>();
        string? path = null;
        var sawRegexp = false;
        var tookPositionalPattern = false;
        string? pending = null;

        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.Option)
            {
                pending = IsValueOption(token.Value) ? token.Value : null;
                continue;
            }

            if (token.Type != TokenType.Argument)
            {
                continue;
            }

            if (pending != null)
            {
                if (IsRegexpOption(pending))
                {
                    if (token.Value.Length == 0)
                    {
                        throw new ArgumentException("Pattern is empty.");
                    }

                    patterns.Add(token.Value);
                    sawRegexp = true;
                }

                pending = null;
                continue;
            }

            if (!sawRegexp && !tookPositionalPattern)
            {
                if (token.Value.Length == 0)
                {
                    throw new ArgumentException("Pattern is empty.");
                }

                patterns.Add(token.Value);
                tookPositionalPattern = true;
                continue;
            }

            if (path != null)
            {
                throw new ArgumentException("Too many arguments. Usage: netgrep <pattern> [path]");
            }

            path = token.Value;
        }

        if (patterns.Count == 0)
        {
            if (typeList)
            {
                return new BoundSearch([], ".");
            }

            throw new ArgumentException("Pattern is required. Usage: netgrep <pattern> [path]");
        }

        return new BoundSearch(patterns.ToArray(), path ?? ".");
    }

    private static bool HasSubcommand(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?" or "--version")
            {
                return true;
            }

            if (arg == "--")
            {
                return false;
            }

            if (arg.StartsWith('-'))
            {
                if (arg.Contains('='))
                {
                    continue;
                }

                if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && ValueOptions.Contains(arg[..2]))
                {
                    continue;
                }

                if (ValueOptions.Contains(arg))
                {
                    i++;
                }

                continue;
            }

            return Subcommands.Contains(arg);
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--type-list")
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValueOption(string token)
    {
        if (ValueOptions.Contains(token))
        {
            return true;
        }

        if (token.Length > 2 && token[0] == '-' && token[1] != '-' && ValueOptions.Contains(token[..2]))
        {
            return true;
        }

        return false;
    }

    private static bool IsRegexpOption(string token) => token is "-e" or "--regexp";
}

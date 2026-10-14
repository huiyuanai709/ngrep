using System.CommandLine;
using System.CommandLine.Parsing;

namespace Netgrep.Cli;

internal readonly record struct BoundSearch(string[] Patterns, string[] PatternFiles, string Path);

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
        "-f",
        "--file",
    };

    public static string[] Rewrite(string[] args)
    {
        args = ExpandShortBundles(args);
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
        var files = new List<string>();
        string? path = null;
        var sawPatternSource = false;
        var tookPositionalPattern = false;
        string? pending = null;

        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.Option)
            {
                var name = OptionName(token.Value);
                var inline = InlineValue(token.Value);
                if (IsPatternSource(name))
                {
                    sawPatternSource = true;
                }

                if (inline != null)
                {
                    TakePatternSource(name, inline, patterns, files);
                    pending = null;
                    continue;
                }

                pending = IsValueOption(name) ? name : null;
                continue;
            }

            if (token.Type != TokenType.Argument)
            {
                continue;
            }

            if (pending != null)
            {
                TakePatternSource(pending, token.Value, patterns, files);
                pending = null;
                continue;
            }

            if (!sawPatternSource && !tookPositionalPattern)
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

        if (patterns.Count == 0 && files.Count == 0)
        {
            if (typeList)
            {
                return new BoundSearch([], [], ".");
            }

            throw new ArgumentException("Pattern is required. Usage: netgrep <pattern> [path]");
        }

        return new BoundSearch(patterns.ToArray(), files.ToArray(), path ?? ".");
    }

    private static void TakePatternSource(string name, string value, List<string> patterns, List<string> files)
    {
        if (IsRegexpOption(name))
        {
            if (value.Length == 0)
            {
                throw new ArgumentException("Pattern is empty.");
            }

            patterns.Add(value);
            return;
        }

        if (IsFileOption(name))
        {
            if (value.Length == 0)
            {
                throw new ArgumentException("Pattern file path is empty.");
            }

            files.Add(value);
        }
    }

    private static bool HasSubcommand(string[] args)
    {
        var sawPatternSource = false;
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
                var name = OptionName(arg);
                if (IsPatternSource(name))
                {
                    sawPatternSource = true;
                }

                if (arg.Contains('='))
                {
                    continue;
                }

                if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && ValueOptions.Contains(arg[..2]))
                {
                    continue;
                }

                if (ValueOptions.Contains(arg) || ValueOptions.Contains(name))
                {
                    i++;
                }

                continue;
            }

            if (sawPatternSource)
            {
                return false;
            }

            return Subcommands.Contains(arg);
        }

        // `-e` / `-f` with no path still searches `.`, including when every positional was a pattern source.
        if (sawPatternSource)
        {
            return false;
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

    private static string OptionName(string token)
    {
        var eq = token.IndexOf('=');
        if (eq >= 0)
        {
            token = token[..eq];
        }

        if (token.Length > 2 && token[0] == '-' && token[1] != '-' && ValueOptions.Contains(token[..2]))
        {
            return token[..2];
        }

        return token;
    }

    private static string? InlineValue(string token)
    {
        var eq = token.IndexOf('=');
        return eq >= 0 ? token[(eq + 1)..] : null;
    }

    private static bool IsPatternSource(string name) => IsRegexpOption(name) || IsFileOption(name);

    private static bool IsRegexpOption(string token) => token is "-e" or "--regexp";

    private static bool IsFileOption(string token) => token is "-f" or "--file";

    /// <summary>
    /// Boolean shorts may share one token (<c>-nEi</c>). A value short ends the cluster and
    /// keeps the rest of the token (<c>-nEepat</c> is <c>-n -E -epat</c>). A token that starts
    /// with a value short is left as an attached value (<c>-epat</c>, <c>-A2</c>).
    /// </summary>
    private static string[] ExpandShortBundles(string[] args)
    {
        var expanded = new List<string>(args.Length);
        var passthrough = false;
        foreach (var arg in args)
        {
            if (passthrough)
            {
                expanded.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                passthrough = true;
                expanded.Add(arg);
                continue;
            }

            if (!TryExpandShortBundle(arg, expanded))
            {
                expanded.Add(arg);
            }
        }

        return expanded.ToArray();
    }

    private const string BoolShorts = "iEFqnsxvwlcoU";

    private const string ValueShorts = "efgtTABCj";

    private static bool TryExpandShortBundle(string arg, List<string> destination)
    {
        if (arg.Length <= 2 || arg[0] != '-' || arg[1] == '-' || arg.Contains('='))
        {
            return false;
        }

        if (ValueShorts.Contains(arg[1]))
        {
            return false;
        }

        var parts = new List<string>();
        for (var i = 1; i < arg.Length; i++)
        {
            var ch = arg[i];
            if (BoolShorts.Contains(ch))
            {
                parts.Add("-" + ch);
                continue;
            }

            if (ValueShorts.Contains(ch))
            {
                var rest = arg[(i + 1)..];
                // "=" keeps a value that starts with '-' attached to the option.
                parts.Add(rest.Length == 0 ? "-" + ch : "-" + ch + "=" + rest);
                destination.AddRange(parts);
                return true;
            }

            return false;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        destination.AddRange(parts);
        return true;
    }
}

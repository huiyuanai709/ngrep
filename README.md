# ngrep

ngrep is a small search CLI in the spirit of ripgrep: it walks a tree, honors `.gitignore`, `.ignore`, `.rgignore`, and git's exclude files, and builds a trigram inverted index so later searches do not read every file. Output is stable text, JSON, or NDJSON for tools that want path, line, and column.

The index format is [docs/index-format.md](docs/index-format.md). Ignore rules, command behavior, and library choices are in [DESIGN.md](DESIGN.md).

## Requirements

.NET SDK **11** (`net11.0`). This repo was built with `11.0.100-rc.1` (see `global.json`). A stable 11.0 SDK also works; `rollForward` is `latestFeature`.

## Build and run

```bash
dotnet build
dotnet test
dotnet run --project src/Ngrep -- index testdata/sample
dotnet run --project src/Ngrep -- ngrep testdata/sample
dotnet run --project src/Ngrep -- search ngrep testdata/sample --format ndjson
echo 'hello ngrep' | dotnet run --project src/Ngrep -- ngrep
dotnet run --project src/Ngrep -- ngrep -
```

`testdata/sample` is a tiny tree with a nested `.gitignore`, a binary file, and a log that should not match.

## Commands

```text
ngrep <pattern> [PATH...] [search flags]
ngrep search <pattern> [PATH...] [search flags]
ngrep --files [path] [filters]
ngrep index <path> [--index <dir>] [--format text|json|ndjson] [--quiet|-q]
ngrep watch <path> [--index <dir>] [--format text|json|ndjson]
```

`ngrep <pattern> [PATH...]` is the same command as `ngrep search`. The first positional word is a subcommand only when it is `index`, `search`, or `watch`. To search for the literal `index`, run `ngrep search index` or `ngrep -- index`.

Each `PATH` is a root, searched in the order you name them. A path inside another path is searched again. No path searches `.`. `.gitignore` is loaded the same way as a one-path search of that root (from the git root through that directory), and `.ngrep/index` is discovered from that root. `--index` still names one index directory and is applied to each root; a root outside that index is scanned. `-f -` still reads patterns from stdin. A path named `-` searches stdin, not a file named `-` (use `./-`). With no path, a terminal searches `.` and a pipe searches stdin.

With one path, printed paths stay relative to the index root or to the directory that was scanned. With two or more paths, each hit is prefixed with the path you named, so `ngrep needle src tests` prints `src/App.cs` and `tests/App.cs`. `.` is not prefixed. `-l`, `--files-without-match`, and `-c` use those same paths. A missing path prints `Path not found` on stderr (unless `-s`) and the exit code is 2; the other paths are still searched. The same scan note is printed once when several roots scan for the same reason.

Search flags:

```text
[--index <dir>] [--format text|json|ndjson|rgjson]
[--ignore-case|-i] [-S|--smart-case] [--case-sensitive] [-E|--extended-regexp|--regex] [-F|--fixed-strings] [--scan]
[-e|--regexp <pattern>]... [-f|--file <path>]... [-w|--word-regexp] [-x|--line-regexp] [-v|--invert-match]
[-l|--files-with-matches] [--files-without-match] [-c|--count] [-q|--quiet|--silent] [-o|--only-matching]
[--files] [-n|--line-number] [-N|--no-line-number] [--column] [--no-column] [--vimgrep] [-0|--null]
[-r|--replace <text>] [-s|--no-messages]
[-H|--with-filename] [-I|--no-filename] [--heading] [--no-heading] [--stats]
[-g|--glob <glob>]... [--iglob <glob>]... [--ignore-file <path>]...
[-t|--type <type>]... [-T|--type-not <type>]...
[--type-add <spec>]... [--type-list]
[-A|--after-context <n>] [-B|--before-context <n>] [-C|--context <n>] [-m|--max-count <num>]
[-U|--multiline] [--multiline-dotall] [-P|--pcre] [--hidden] [--binary] [-a|--text] [-L|--follow]
[-u|--unrestricted] [--color auto|always|never]
[-j|--threads <n>] [-d|--max-depth <n>]
```

`index` writes `<path>/.ngrep/index` unless `--index` is set. Running it again updates changed files and drops deleted ones (size + mtime, then SHA-256). A second pass does not read bytes when size and mtime still match. File reads use the same async pipeline as search (`NGREP_THREADS`, else `NETGREP_THREADS`, else the processor count). `-q` skips the per-path stderr lines.

While that walk runs, progress is printed on stderr in English. A new document is `added: path`, a replaced document is `updated: path`, and a document that is no longer in the tree is `removed: path`. Each of those lines is printed only after that document has been committed. Unchanged files, ignored files, and skipped files (binary, too large, or unreadable) do not get their own line. Every 100 files examined, and once after removals, a summary is printed: `indexed 120/500, added 3, updated 1, unchanged 116, removed 0, skipped 0`. The `indexed` counts are files the walker examined, not ignored files. `skipped` in that line is binary, too large, and unreadable. Stdout stays the final summary for `--format text`, `json`, or `ndjson`. `-q` / `--quiet` on `index` turns the stderr progress off and leaves that summary in place. It is a different flag from search `-q` / `--quiet` / `--silent`, which suppresses match output. `watch` does not print this progress.

Changed files are trigrammed and merged into the in-memory index, then written in chunks to `<index>.tmp`. A chunk is 4096 added, updated, or removed paths, or 32 MiB of raw bytes from those adds and updates, and one last commit refreshes `updatedUtc`. That directory replaces `<index>` only when `manifest.json`, `documents.ndjson`, and `postings.bin` are complete. A failed write leaves the previous chunk in place. A process killed mid-build keeps the last successful chunk, not a per-file prefix and not an unbounded buffer of every file's text. The live index stays in memory; 100,000 tiny files peaked near 270 MB RSS. Parallel reads stay within `workers × 4` files. `added:` / `updated:` / `removed:` lines are printed only after the chunk that contains them has been committed. Files whose size and mtime match are not read. A matching hash refreshes size and mtime in the next chunk. The every-100 `indexed` summary still prints during the walk.

Search looks upward from each path (default `.` when none is given) for `.ngrep/index`. Several paths are searched one after another, each with that lookup. When an index covers the path, ngrep builds a required-literal prefilter and intersects trigram postings before reading candidates. A literal, or a regex piece, can feed that filter when every alternative has a required substring of 3+ characters (`foo`, `foo.*bar`, `foo|bar`, `(ab){2}`, `n.grep`). The file is still read to confirm the real match, so a prefilter hit is not a result. Trigrams cannot help when an alternative is shorter than 3 (`ab`, `a|ngrep`, `foo*` which must allow `fo`), when the pattern can match with no required text (`.`, `a*`), when the syntax is not extracted (lookaround, backreferences), or when `-v` is set. At the indexed root those queries read every indexed file. A path strictly inside that root is scanned instead (`pattern is not covered by trigrams; scanned content`), so a file the snapshot does not list is still opened. A trigram-safe query whose path has no indexed document scans too (`path is not in the index; scanned content`). An ASCII required piece still rejects a file from its bytes before the text is decoded, including pieces shorter than 3. With no index, `--scan`, `--no-ignore`, `--no-ignore-vcs`, or a path outside the indexed root, ngrep scans contents and prints a one-line note on stderr. You do not pass `--scan` just because the tree has never been indexed. The index does not contain ignored files, so the ignore flags always scan.

Patterns are .NET regular expressions unless `-F` / `--fixed-strings` is set. That includes patterns read from a file. `-E` / `--extended-regexp` / `--regex` selects that default explicitly and does not change the engine. The language is not POSIX basic (BRE) or extended (ERE). It is also not ripgrep's `-E`, which sets the input encoding. ngrep has no encoding flag; a later one would be `--encoding`. The default engine is non-backtracking (`RegexOptions.NonBacktracking`): matching is linear in the length of the file. Backreferences (`\1`), lookaround (`(?=...)`, `(?<=...)`), and atomic groups (`(?>...)`) are rejected with a message that names `-P` / `--pcre`. `-P` selects the backtracking interpreter. `-F` treats every pattern as literal text and is mutually exclusive with `-E` and `-P` (exit 2). There is no BRE engine: `ngrep 'a+'` searches for one or more `a`, `ngrep 'a\+'` searches for a literal `a+`, and `ngrep -F 'a\+'` searches for the four characters `a\+`. POSIX `grep 'a\+'` searches for one or more `a`.

`-i` / `--ignore-case` compares with ordinal ignore-case. `-S` / `--smart-case` does that only for a pattern whose text contains no uppercase letter (`char.IsUpper`); any uppercase letter keeps that pattern case-sensitive. Each pattern is decided on its own, so `-S -e foo -e Bar` folds `foo` and not `Bar`. An uppercase letter inside a regex escape counts (`\W` is case-sensitive under `-S`; pass `-i` to fold it). `--case-sensitive` forces a case-sensitive search, which is already the default. The last of `-i` / `--ignore-case`, `-S` / `--smart-case`, and `--case-sensitive` wins, including a flag prepended from `NGREP_CONFIG_PATH`. `-s` remains `--no-messages`, not ripgrep's `-s`.

Patterns are OR. `-e` / `--regexp` may be repeated. A positional pattern written before the first `-e` or `-f` is included; once either flag is present, every remaining positional is a path (`ngrep -e foo -e bar src tests`, `ngrep -f pats.txt src`). `-w` keeps a match only when both ends are word boundaries (a Unicode letter, digit, or `_` on one side and a non-word character or the end of the text on the other). `-x` / `--line-regexp` keeps a match only when the entire line matches. The line is the same text ngrep prints: a trailing CR before LF is not part of the line. A literal must equal that text. A regex is run against that text anchored at both ends (`\A(?:pattern)\z`), so `a|aaa` matches a line `aaa`. A pattern that contains a newline does not match a single line, including with `-U`. `-v` prints each line that matches none of the patterns; `text` and `lineText` are that line and `column` is 1.

`-f` / `--file` reads more patterns, one per line, and OR-s them after positional and `-e` patterns. Repeat the flag for several files. `-` reads patterns from stdin, not the haystack. The newline is not part of the pattern, and a trailing newline does not add an extra pattern. An empty line is an empty pattern and matches every line (one hit per line, column 1, `text` empty, `lineText` the line). With `-x`, that empty pattern matches only empty lines. A line whose first character is `#` is a normal pattern, including the `#`. Nothing in the file is a comment, and `\#` is not rewritten. The default regex treats `\#` as an escape (`\#` matches `#`). `-F` keeps the backslash literal. `-E` / `--regex` does the same as the default. A missing or unreadable path exits 2 (`Pattern file not found`). A zero-length file that yields no lines, and no `-e` or positional pattern, exits 2 (`Pattern file contains no patterns`). `#` starts a comment only in the config file (`NGREP_CONFIG_PATH`), not in a pattern file. `RIPGREP_CONFIG_PATH` is not read.

### Searching stdin

File contents can come from stdin. That is separate from `-f -`, which is the pattern list.

```bash
echo 'hello ngrep' | ngrep ngrep
ngrep ngrep -
ngrep -f pats.txt -
echo foo | ngrep -f - src
```

- `ngrep <pattern> -` reads the haystack from stdin, including when stdin is a terminal (it waits until EOF).
- `ngrep <pattern>` with no path reads stdin when stdin is not a terminal (a pipe or a redirect). When stdin is a terminal, that command still searches the working directory.
- `ngrep <pattern> .` or any other path searches that path and leaves stdin unread. Use this when a pipe is attached and you still want the tree. Same rule as ripgrep. Several paths are searched in order; a `-` among them is stdin.
- The path `-` is stdin, not a file named `-`. Search that file with `./-`.

`-f -` / `--file -` consumes stdin for patterns. With no path, ngrep then searches the working directory, because the stream is already the pattern list. `ngrep -f pats.txt -` and `cat file | ngrep -f pats.txt` read patterns from the file and contents from stdin. `ngrep -f - -` exits 2 (`attempted to read patterns from stdin while also searching stdin`): one stream cannot be both. A second `-f -` exits 2 (`error reading -f/--file from stdin: stdin has already been consumed`).

The path label is `(standard input)`, in the same place ngrep prints a file path. A stdin-only search omits it unless `-H` or `--vimgrep`:

```text
1:7:hello ngrep
```

`-H` prints `(standard input):1:7:hello ngrep`. `-H --heading` prints that name once and `line:column:text` under it. `--heading` alone does not add the name. `--no-heading` keeps the prefixed line when the path is shown, and wins when both heading flags are set. JSON, NDJSON, and rgjson set `path` to `(standard input)` either way. `-l` prints `(standard input)`. `-c` prints `2` for that input and `(standard input):2` with `-H`. Line numbers and columns stay on. ripgrep's label is `<stdin>`.

A NUL in the first 8 KiB skips the stream the way a binary file is skipped (no match, exit 1, no error). More than 32 MiB is the same too-large skip. `-a` / `--text` and `--binary` search the stream and print matching lines. An unreadable stream exits 2 (`Unreadable: (standard input)`); `-s` hides that line and still exits 2. Empty stdin is an empty file: exit 1, and `--files-without-match` prints `(standard input)`.

Stdin does not use the index, so there is no `no index found` note. Globs, types, and ignore rules do not apply, because there is no path to filter. `--scan` and `--index` do not change that. `--stats` counts the stream as one file when its bytes were examined.

`-g` / `--glob` is repeatable. A glob that starts with `!` excludes, same as ripgrep. If any glob does not start with `!`, a file must be selected by an include. When several globs match one path, the last one wins. `--iglob` is the same filter with ordinal case-insensitive matching (`--iglob '*.CS'` selects `a.cs`). `-g` and `--iglob` share one list, in the order you wrote them, and each flag keeps its own case. Globs use gitignore syntax (`*`, `?`, `**`, `[abc]`) and run after ignore files. They do not bring an ignored file back. `ngrep foo -g '*.log'` still skips an ignored log. `--no-ignore` turns every automatic ignore file off (`.gitignore`, `.ignore`, `.rgignore`, `info/exclude`, and the global excludes file) so those files are walked, and `-g` then filters that larger set (`--no-ignore -g '*.log'` prints the log). `--no-ignore-vcs` turns off only the git sources (`.gitignore`, `info/exclude`, and the global file) and still honors `.ignore` and `.rgignore`. `.git`, `.ngrep`, the index directory, and symlinks stay skipped either way.

`--ignore-file PATH` adds one gitignore-format file. Repeat it for several files. Patterns are matched relative to the current working directory, not relative to the file and not relative to the search path. A pattern with no `/` matches in any directory under that working directory. A leading `/` anchors at the working directory, so `--ignore-file` with `/secret.txt` skips `secret.txt` there and not `sub/secret.txt`. A path outside the working directory is not affected. Precedence, highest first: `.rgignore`, `.ignore`, `.gitignore`, `info/exclude`, the global excludes file, then `--ignore-file`. A whitelist in `.gitignore` keeps a file that `--ignore-file` would skip. A whitelist in `--ignore-file` does not bring back a file a higher source ignored. Later `--ignore-file` flags beat earlier ones. `--no-ignore`, `--no-ignore-vcs`, and `-u` do not turn these files off. A missing path exits 2. The index still applies: `--ignore-file` only removes files the index already has.

`-t` selects built-in types (OR if repeated). `-T` excludes types. `--type-list` prints the set and exits 0. `--type-add name:*.ext[,*.ext]` adds globs (a bare extension means `*.ext`; `name:include:*.ext` is accepted). Type names are case-insensitive. Types compose with globs and with `.gitignore` the same way: ignored files stay ignored.

`-A`, `-B`, and `-C` add context in text output. `-C` sets both sides; `-A` and `-B` replace that side when they are also set. Context lines use `path-line-text` when the path and the line number are printed, `line-text` when the path is omitted, and `path-text` when `-N` is set and the path is shown. Match lines stay `path:line:column:lineText` unless `-N`, `--no-column`, `-I`, or a single file or stdin drops a field. A `--` line separates groups that do not touch. Overlapping context is not printed twice, and a context line that is itself a match is printed only as a match. `-r` rewrites match lines only; context lines stay as read.

`-m NUM` / `--max-count NUM` stops a file after NUM matching lines and continues with the next file. Several hits on one line count once toward NUM, and that line is still printed for every hit it holds unless plain text merged it into one row (see `--vimgrep` below). A `-U` match that spans lines counts once, at its starting line. `0` is legal and searches nothing: no file bytes are read, stdout is empty, and the exit code is 1 after the path is checked (a missing path is still 2). `-c` reports at most NUM, because the count is the lines found before the stop. `-l` and `-q` still stop at the first hit; they can be combined with `-m`. After-context of the last reported match is printed. A later line that also matches stays context and is not another match, so a file never reports more than NUM matching lines. Ripgrep can print more than NUM when a contextual line itself matches. `--files-without-match -m 0` prints nothing here, because those files were not searched; ripgrep's summary printer can still list them with a zero count. Adjacent multiline matches are not rolled into one limit unit. The fixed-string reader returns once those lines and their after-context are buffered. A regex may finish a buffer it has already read, then drop lines past the limit. The last `-m` / `--max-count` wins.

`-l` / `--files-with-matches` prints one path per file that has a hit, and stops reading that file after the first hit. `--files-without-match` prints paths that were searched and had none. `-c` / `--count` prints `path:count` when the path is shown, and `count` when it is not (one file, stdin, or `-I`). The count is matching lines (a line with two hits counts once). Files with a zero count are omitted. `-q` / `--quiet` / `--silent` prints no match report on stdout (text, JSON, NDJSON, and rgjson). Exit 0 means some line matched, 1 means none, 2 means an error. These four flags are mutually exclusive (exit 2). POSIX allows only one of `-c`, `-l`, and `-q`; `--files-without-match` joins that set. Text for `-l` is paths. Text for `-c` is `path:count` or `count`. `--format json` is `{"paths":[...]}` or `{"counts":[{"path","count"}]}`. `--format ndjson` is one `{"path"}` or `{"path","count"}` object per line. That is not a match object: normal search JSON is unchanged. `-q` does not hide the stderr scan note. It stops the walk at the first matching file, and it stops reading that file at the first hit, unless `--stats` is set.

`-n` / `--line-number` prints the 1-based line. That is the default, including when stdout is not a terminal. `-N` / `--no-line-number` omits it. `--column` prints the 1-based column. That is also the default, which is the opposite of ripgrep (ripgrep prints a column only with `--column` or `--vimgrep`). `--no-column` omits it. The last of `-n` and `-N` wins, and the last of `--column` and `--no-column` wins. `--column` does not force line numbers, so `-N --column` is `path:column:text` when the path is shown, and `column:text` when it is not. With neither number, a match is `path:text`, or just the text when the path is omitted or printed as a heading. Context never prints a column. These flags change text output only. `--format json`, `ndjson`, and `rgjson` keep `line` and `column`.

`-H` / `--with-filename` prints the path on text matches. `-I` / `--no-filename` leaves it off. The last flag wins, including a short cluster (`-HI` is `-I`). One file or stdin omits the path unless `-H` or `--vimgrep`: a match is `line:column:text`, context is `line-text`, and `-c` is `count`. A directory prints the path even when it holds one file. Several paths do too. `-I` removes that path from matches, context, and `-c`. `-l`, `--files-without-match`, and `--files` still print paths, because the path is the result. JSON, NDJSON, and rgjson ignore `-H` and `-I` and keep `path`.

`--vimgrep` forces `path:line:column:text`, one row per hit, and disables headings. It overrides `-N`, `--no-column`, and `--heading`, and it implies `-H` so one file and stdin still print the path. `-I` drops that path and leaves `line:column:text`. Plain text prints one row per matching line, like ripgrep and grep, with the first hit's column; `--vimgrep` and `-o` print one row per hit instead, so a line with two hits is printed twice, each time with that hit's column and the same line text.

`-0` / `--null` follows each printed path with a NUL byte. A match line is `path` + NUL + `line:column:text` (whichever of those fields are on) and a newline. A line with no path has no NUL. A context line uses NUL instead of the dash after the path (`path` + NUL + `line-text`). `-l`, `--files-without-match`, and `--files` print `path` + NUL and no newline. `-c` prints `path` + NUL + `count` + newline when the path is shown, and `count` + newline when it is not. A heading is `path` + NUL instead of a newline, then the match lines. `--files` ignores `--format` and still honors `-0`. json, ndjson, and rgjson ignore `-0`.

`-r` / `--replace` substitutes matches in the printed line. It does not modify files, and it is not a recursion switch: search is always recursive. The default regex, `-E`, and `-P` expand captures. `-F` inserts the replacement literally, so `$1` stays the two characters `$1`. Capture syntax: `$$` is `$`, `$0` / `$&` / `${0}` is the whole match, `$N` and `${N}` are that group index (`$01` is group 1), and `$name` / `${name}` is a .NET group name (`(?<name>...)`). The name is the longest run of letters, digits, and underscores, so `$1a` is the group named `1a`, not group 1 plus `a`. Use `${1}a` for that. A missing or unmatched group expands to empty. .NET group names must start with a letter or `_`, and `(?P<name>...)` is not valid. `` $` ``, `$'`, `$+`, and `$_` are not substitutions. Every match on the line is replaced, and each output row for that line shows the fully substituted text with the original column. `-o` prints only that match's replacement. `-v` is unchanged, because those lines are not pattern matches. A span that starts inside an earlier replacement is skipped. A cross-line match is substituted on its starting printed line. json, ndjson, and rgjson ignore `-r` and still report the original `text` and `lineText`.

The default `-E` engine is non-backtracking. Simple captures, named groups, and `Regex` replacement all work on that engine. A quantified group such as `(a)+` substitutes `Group.Value`, the last iteration, which is the same text the backtracking engine uses for `$1`. Non-backtracking keeps only that last capture in `Group.Captures` (one entry); `-P` keeps every iteration. Backreferences, lookaround, and atomic groups are still rejected on `-E`; use `-P` for those patterns. If a recorded span cannot be re-matched at print time, `$0` and `$&` are the matched text and every other group is empty.

`-s` / `--no-messages` suppresses stderr for a missing search path and for a file you named that cannot be read. The exit code is still 2. It does not hide usage errors, a bad regex, a missing `-f` file, or the scan note. A file that cannot be read while walking a directory is skipped with no message and does not change the exit code, with or without `-s`.

`-v` changes what a hit is. `-l -v` lists files that contain at least one non-matching line. `--files-without-match -v` lists files whose every line matches (an empty file has no lines, so it is listed). `-c -v` counts non-matching lines. Exit 0 means the selected report was non-empty; exit 1 means it was empty.

`-o` / `--only-matching` changes text output to `path:line:column:text`, the matched slice, one line per hit. JSON and NDJSON stay `path`, `line`, `column`, `text`, `lineText`. `text` was already the slice. Context flags still fill `before` / `after` in JSON; text `-o` does not print context lines.

`-d` / `--max-depth` (`--maxdepth` is the same flag) caps directory depth the way ripgrep does. Depth 0 is the path you named. Depth 1 is an immediate child of that path. Depth 2 is a child of those, and so on. `-d 0` on a directory lists nothing, because that directory is the start path and its files sit at depth 1. `-d 0` on a file still searches that file. `-d 1` reads only the files directly in the directory. `-d 2` also reads the files in its child directories. Omitting the flag walks the whole tree. `.gitignore`, `--hidden`, and `--no-ignore` / `--no-ignore-vcs` still decide what is skipped; the cap only stops descent. A directory that the cap does not list does not contribute a `.gitignore`. Ancestor ignore files still apply. An indexed search keeps the index and drops document paths deeper than the cap, so those files are not opened.

Hidden files and directories are skipped by default. A name that starts with `.` is hidden. On Windows and macOS, the hidden file attribute is hidden too, so a content scan of a home directory does not walk `AppData`. `--hidden` searches both. Naming a file, or a directory, still searches that path: `ngrep pat .secret` reads `.secret`, and `ngrep pat .github` reads non-hidden children of `.github`. Hard skips stay skipped with or without `--hidden`, `--no-ignore`, `--binary`, `-a` / `--text`, or an explicit path: a directory named `.git` or `.ngrep`, and the index directory. Symlinks are skipped too, unless `-L` / `--follow` is set. `-g` does not bring a skipped hidden path back (same rule as gitignore). A gitignore negation does not un-hide a dotfile; pass `--hidden`. `--no-ignore` does not imply `--hidden`. The index still stores hidden names and hidden-attribute files so `--hidden` can use it. An indexed search still decides hidden from the path string (a dot name), because the index does not store the attribute. A content scan skips both in the walk unless the flag is set.

`-L` / `--follow` follows symbolic links to files and directories. Without it, every symlink is skipped, including when `--hidden` or `--no-ignore` is set. The printed path is the link; the bytes are the target. A directory link is walked under that link path. The walk records the canonical path of each ancestor and does not enter a target already on that chain, so a cycle stops. A link to a sibling directory is still walked. A broken link is skipped. `index` and `watch` do not follow links, and the index has no symlink targets, so `-L` scans (`scanned content (--follow)` when an index would otherwise have been used).

`-u` / `--unrestricted` stacks like ripgrep, and it may be repeated. `-u` is `--no-ignore` (every ignore file, including `.ignore` and `.rgignore`). `-uu` is `--no-ignore --hidden`. `-uuu` is `--no-ignore --hidden --binary`. Separate `-u` flags stack the same way (`-u -u` is `-uu`). A fourth `-u` exits 2. `.git`, `.ngrep`, and the index directory stay skipped at every level, including `-uuu`. Symlinks stay skipped unless `-L` / `--follow`. Ripgrep searches `.git` once `-uu` un-hides it. `--no-ignore-vcs` is narrower than `-u`: it still reads `.ignore` and `.rgignore`.

`-a` / `--text` searches a file that has a NUL byte in the first 8 KiB as if it were text, the same way ripgrep's `-a` does, and prints the matching line. Stdin is included. Without `-a` or `--binary`, that file is skipped and is not a match. `--binary` does the same search and also prints the line. Ripgrep's `--binary` (what `-uuu` turns on there) keeps searching but prints `binary file matches` instead of the line. ngrep prints the line for both flags. Files larger than 32 MiB stay skipped. The index does not store binary files, so either flag scans. The stderr note names `--text`, `--binary`, or both. Neither flag implies `--no-ignore` or `--hidden`. `-uuu` still adds `--binary`, not `-a`.

`--files` lists the files a search would walk, one relative path per line, and does not search. No pattern: `ngrep --files`, `ngrep --files src`, `ngrep --files -g '*.cs'`, `ngrep --files -t cs`. The positional is the path. `-e` and `-f` exit 2. Ignore, hidden, glob, and type filters are the same ones search uses, including `-u` / `-uu`. It does not read file contents, so a binary file and a file over 32 MiB are still listed (the same set `-uu` and `-uuu` list). It does not consult the index, so a file added after `index` is listed. `--format` does not change the lines. `-0` / `--null` prints `path` + NUL and no newline. `-q` prints nothing and still exits 0 when any path would have been listed, or 1 when none would. A missing path is exit 2 (`-s` hides that line). `--files` cannot be combined with `-l`, `--files-without-match`, or `-c`. Hard skips stay unlisted.

`--color` is `auto` (default), `always`, or `never`. `auto` colors text when stdout is a terminal, `NO_COLOR` is unset, and `TERM` is not `dumb`. `--color` alone means `auto`. JSON, NDJSON, and rgjson are never colored. Text uses magenta paths, green line numbers, blue columns, and bold red matches. There is no `--colors` spec.

`-U` / `--multiline` lets a **regex** include a newline. `--multiline-dotall` implies `-U` and lets `.` match a newline (`RegexOptions.Singleline`). Either way `^` and `$` are line anchors (`RegexOptions.Multiline`), including when `-U` is off, so `^bar$` matches a line rather than only the whole file. Without `-U`, a match that contains a newline is dropped, including a pattern that turns dotall on itself such as `(?s)foo.bar`. User patterns never use `RegexOptions.Compiled`. A non-regex pattern is an exact substring, so a newline in that pattern already matches and `-U` does not change it. `-v` treats every line overlapped by a cross-line match as a matching line.

Text headings follow ripgrep when the path is shown and more than one file is searched. On a terminal (`TERM` is not `dumb`, and stdout is not redirected), a search that reads more than one file prints the path on its own line, then `line:column:text` (and context as `line-text`) without repeating the path. A blank line separates files. A one-file directory, a pipe of a directory search, or `TERM=dumb` keeps `path:line:column:text` when the path is shown. `--heading` forces the heading form for those searches, including one file or stdin when `-H` is also set. `--heading` alone does not add a path to a single file or to stdin. `--no-heading` forces the prefixed form and wins when both flags are set. `-I` suppresses the heading, because there is no path to print. JSON, NDJSON, and rgjson never print headings. `-l`, `--files-without-match`, and `-c` do not use headings. `NO_COLOR` turns color off and does not turn headings off. A heading is colored with the same magenta as a path.

`--stats` prints a summary on stderr after stdout is flushed:

```text
files searched: 2
files matched: 1
lines matched: 1
matches: 1
elapsed ms: 3
```

Stdout formats are unchanged. “Files searched” counts files whose contents were examined, including an ASCII literal miss. Binary files, files over the size limit, unreadable files, and trigram-prefilter drops are not counted. “Files matched”, “lines matched”, and “matches” describe pattern hits (a line with two hits counts once as a line and twice as matches). With `--files-without-match`, the summary is still those hits, not the paths printed on stdout. `--stats` also finishes each file, so `-l` does not stop at the first hit and the line counts stay complete. Exit codes still follow the selected report, not the summary.

`--format rgjson` is a separate search-only event stream (`begin`, `match`, `context`, `end`, `summary`). It does not replace `--format json` or `ndjson`. `lines.text` is the line plus a newline. `submatches[].start` / `end` are 0-based UTF-16 offsets into that line, not file byte offsets. `absolute_offset` is the 0-based UTF-16 index of the start of that line in the decoded text, not a file byte offset. `summary.data.stats.elapsed_ms` is wall time for the search. When `-A`, `-B`, or `-C` is set, surrounding lines are `type: context` events (deduped the same way as text context) and are not added to `--format json` match objects. A cross-line match keeps the full slice in `submatches[].match.text`; `end` stops at the first line. `index` and `watch` reject `rgjson` (exit 2).

`-j` / `--threads` bounds confirmation workers (default: processor count, or `NGREP_THREADS`). `NETGREP_THREADS` is read only when `NGREP_THREADS` is unset. `1` forces a single worker.

### Config file

`NGREP_CONFIG_PATH` is a path to a search config file, the same idea as ripgrep's `RIPGREP_CONFIG_PATH`. ngrep does not read `RIPGREP_CONFIG_PATH`. A ripgrep config usually contains flags ngrep does not implement, and reading it would either error or drop those flags.

Each line is one argument. Leading and trailing whitespace is removed. Empty lines are ignored. A line whose first non-whitespace character is `#` is a comment. There is no shell splitting and no quote removal: `--glob=*.cs` is one argument, and `--glob` plus `*.cs` is two lines. A flag line that contains a space exits 2 and names the file and line (`--glob *.cs` is not split). Arguments are prepended to the ones you typed. On `ngrep search ...` they are inserted after `search`. On `ngrep <pattern>` they are placed before the pattern, and the implicit `search` command stays in front. A flag you pass later therefore wins. The last of `-i`, `-S`, and `--case-sensitive` wins. The last `--format`, `--index`, `--color`, `-A`, `-B`, `-C`, `-m` / `--max-count`, and `-j` / `--threads` wins. Repeatable flags such as `-g`, `--iglob`, `--ignore-file`, and `-e` accumulate, and the last matching glob still wins.

`index`, `watch`, `--help`, `-h`, `-?`, `--version`, `--no-config`, and `ngrep` with no arguments do not read the file, so a bad config cannot hide help. `--no-config` suppresses the file entirely, the same way as ripgrep: the flag is recognized on the raw command line before `NGREP_CONFIG_PATH` is opened, so nothing in the file is applied. It may appear anywhere before `--`, including after the pattern (`ngrep foo --no-config`). A token after `--` is not the flag. The operand of another option is not the flag either (`-e --no-config` searches for that pattern and still reads the file). A `--no-config` line inside the config file does not skip that file. An empty `NGREP_CONFIG_PATH` is the same as unset. A missing, unreadable, or non-UTF-8 file exits 2 and the message starts with `NGREP_CONFIG_PATH`. An unknown flag exits 2 and the message names `NGREP_CONFIG_PATH` and the flag. Other booleans in the file stay on; ngrep does not add a `--no-` switch for every flag.

```text
# Fold patterns that have no uppercase letter.
--smart-case

# One argument per line.
--glob
*.cs
```

`watch` indexes once, then reindexes about 300 ms after a change. It is a thin `FileSystemWatcher` loop: like `index`, it does not follow symlinks (search follows them only with `-L`), it ignores its own index writes, and a `.gitignore` edit is just another incremental rebuild. Ctrl+C exits 0.

### Exit codes

| Code | When |
| --- | --- |
| 0 | Index or watch succeeded, search found at least one match (including `-q`), `--files` listed at least one path (including `-q` when a path would have been listed), or `--type-list` / `--help` ran. |
| 1 | Search finished and found no matches, or `--files` listed nothing. |
| 2 | Bad usage, missing path, invalid regex or glob, unknown type, a fourth `-u`, bad config file, canceled search, unreadable index, or another error. |

The first Ctrl+C cancels a search and exits 2 after `search canceled` on stderr. Listing stops, and reads already in flight stop at the next chunk (the cancel token is not passed to `RandomAccess.ReadAsync`, so Windows does not `CancelIoEx` a handle that is about to be disposed). A second Ctrl+C exits immediately with code 2.

Errors are plain text on stderr. Stdout is only the selected format, including when it is empty. `-q` leaves stdout empty. `-s` drops the missing-path and named-unreadable lines and still exits 2.

### POSIX grep flags

Classic `grep` without `-E` or `-F` uses POSIX basic regular expressions. ngrep does not. The default is .NET regular expressions (non-backtracking), the same engine as `-E` / `--extended-regexp` / `--regex`. Those flags are aliases of the default. `-F` / `--fixed-strings` selects literal text. `-E` is not ripgrep's `--encoding`. Encoding is not a flag today; a later flag would be `--encoding`.

Boolean short options can share one token (`-nEi`, `-xF`, `-q`). A value option ends the cluster. The rest of the token is its operand (`-nEepat` is `-n -E -e pat`). A token that starts with a value option is left unchanged (`-epat`, `-A2`, `-j1`), which is how those attached values already worked. Tokens after `--` are not flags. A pattern whose name is `index`, `search`, or `watch` still needs `ngrep search` or `ngrep --`.

| POSIX | ngrep | Status |
| --- | --- | --- |
| `-E` | `-E`, `--extended-regexp`, `--regex` | .NET regex, the default. Not POSIX ERE and not ripgrep's encoding flag |
| `-F` | `-F`, `--fixed-strings` | Literal text. Exit 2 together with `-E` or `-P` |
| `-c` | `-c`, `--count` | Matching lines per file. Zero counts omitted |
| `-m` | `-m`, `--max-count` | Stop each file after NUM matching lines. `0` searches nothing |
| `-l` | `-l`, `--files-with-matches` | Paths with a hit |
| `-q` | `-q`, `--quiet`, `--silent` | No stdout match report. Exit 0 / 1 / 2 |
| `-i` | `-i`, `--ignore-case` | Ordinal, culture-invariant |
| `-n` | `-n`, `--line-number` | On by default. `-N` / `--no-line-number` turns them off |
| `-s` | `-s`, `--no-messages` | Hides missing-path and named-unreadable errors. Exit code unchanged |
| `-v` | `-v`, `--invert-match` | Lines that do not match |
| `-x` | `-x`, `--line-regexp` | The whole line must match |
| `-e` | `-e`, `--regexp` | Repeatable pattern, OR |
| `-f` | `-f`, `--file` | Pattern file, one pattern per line, OR |
| `-H` | `-H`, `--with-filename` | Print the path. Default for a directory or several paths |
| `-h` | `-I`, `--no-filename` | POSIX `-h` is help here. Ripgrep's `-I` omits the path. Default for one file or stdin |

Dialect gaps that remain:

- No POSIX BRE. The default is .NET regex. `a+` is one or more `a`. `a\+` is a literal plus. `-F` makes metacharacters literal, so `a\+` is four characters.
- The default, and `-E`, is the .NET regex language, not POSIX ERE. The engine is non-backtracking and linear. It does not support backreferences, lookaround, or atomic groups; `-P` / `--pcre` is the backtracking interpreter for those. POSIX collating elements and equivalence classes are absent. Escapes such as `\s` follow .NET. `^` and `$` are line anchors (`RegexOptions.Multiline`). `.` does not match a newline unless `--multiline-dotall`. User patterns are not compiled (`RegexOptions.Compiled` is not AOT-safe). The backtracking engine times out after five seconds. Non-backtracking does not backtrack, so that timeout is a bound on a linear scan. `-E` is not ripgrep's encoding flag.
- Line numbers and columns stay on when stdout is not a terminal. Ripgrep turns line numbers off for a pipe and hides the column unless `--column` is set. `--column` here does not imply `-n`.
- There is no non-recursive directory mode. `-r` is replace-in-output, not recursion. POSIX `-h` is help here; `--no-filename` is `-I`, as in ripgrep.
- `-q` does not suppress the stderr scan note (`no index found; scanned content` and the same family). Without `--stats` it stops the walk at the first matching file. `--stats` still finishes the walk so the counts stay complete.
- `-s` does not cover unreadable files discovered while walking a directory. Those stay skipped, with no diagnostic and no exit 2. `-s` also does not hide usage errors, bad regexes, or missing pattern files.
- An unknown character in a short cluster is not expanded (`-nZ` is an error). Options after `--` are literals.
- Search walks the tree unless `-d` / `--max-depth` caps it. `-r` / `--replace` is print-only and does not change that. `-d 1` reads only the files directly in the directory you named.
- Stdin contents follow ripgrep: `ngrep pat -`, or no path when stdin is not a terminal. `-f -` is still the pattern list, and combining it with a path of `-` exits 2. The printed name is `(standard input)`, omitted unless `-H`, `--vimgrep`, or another path is searched with it. Line numbers stay on.
- `-c`, `-l`, `-q`, and `--files-without-match` cannot be combined. POSIX only defines the first three.

### Match fields

Text (human):

```text
src/Program.cs:7:35:        System.Console.WriteLine("ngrep sample");
```

NDJSON, one object per line, and each object inside `search --format json` → `matches`:

```json
{"path":"src/Program.cs","line":7,"column":35,"text":"ngrep","lineText":"        System.Console.WriteLine(\"ngrep sample\");"}
```

`line` and `column` are 1-based. `column` counts UTF-16 code units, which is the same as a Unicode code point for BMP text. `text` is the matched slice (`lineText` when `-v` is set). `lineText` is the full line without its newline. Paths use `/` and are relative to the index root (or to the scanned directory when there is no index).

When `-A`, `-B`, or `-C` is set, each match object also has `before` and `after` arrays of `{ "line", "text" }`. Those fields are omitted when no context was requested, so existing readers keep seeing only `path`, `line`, `column`, `text`, and `lineText`. Context in JSON is per match and may overlap; text output merges it.

`index --format json` is a single object with `command`, `root`, `indexPath`, `formatVersion`, `documents`, `added`, `updated`, `unchanged`, `removed`, `skippedIgnored`, `skippedBinary`, `skippedTooLarge`, `skippedUnreadable`.

`watch --format json` and `watch --format ndjson` are the same stream: one object per event, with `event` of `ready`, `reindexed`, or `stopped`.

## Index location

Default: `<root>/.ngrep/index` (`manifest.json`, `documents.ndjson`, `postings.bin`). The walker does not index that directory. Override with `--index`. A directory named `.netgrep` from an older build is not read; run `index` again to write `.ngrep`.

## Ignore rules

- Nested `.gitignore`, `.ignore`, and `.rgignore` files apply, from the git root down to the file. Outside a git checkout, only those files under the path you passed are used. `.gitignore` is still read there. `info/exclude` and the global excludes file (`core.excludesFile`, or `~/.config/git/ignore` when that key is unset) apply only inside a checkout, matched from the git root. Highest precedence first: `.rgignore`, `.ignore`, `.gitignore`, `info/exclude`, global, then any `--ignore-file`. `--no-ignore-vcs` skips the three git sources and still reads `--ignore-file`. `--no-ignore` skips all five automatic sources and still reads `--ignore-file`.
- `.git`, `.ngrep`, and the index directory are always skipped. Symlinks are skipped unless `-L` / `--follow`.
- Binary files (NUL in the first 8 KiB) are skipped unless `--binary`, `-a` / `--text`, or `-uuu`. Files larger than 32 MiB are always skipped. `--files` lists both, because it does not read contents.
- A parent directory that is ignored stays ignored; `!keep.txt` cannot resurrect a file inside `build/` if `build/` itself is ignored. `build/*` plus `!build/keep.txt` can. Details and the glob dialect are in [DESIGN.md](DESIGN.md).

`testdata/sample/skip-me.log`, `src/Nested/scratch.tmp`, and `generated/out.txt` are force-added so the fixture is in git. ngrep still ignores them.

## Native AOT

PublishAot is on in `src/Ngrep/Ngrep.csproj`, along with invariant globalization and reflection-free JSON.

```bash
dotnet publish src/Ngrep/Ngrep.csproj -c Release -r linux-x64
dotnet publish src/Ngrep/Ngrep.csproj -c Release -r linux-x64 /p:IlcInstructionSet=x86-64-v3
dotnet publish src/Ngrep/Ngrep.csproj -c Release -r linux-x64 /p:IlcInstructionSet=x86-64-v4
```

`IlcInstructionSet` is a CPU requirement, not a runtime light-up. Native AOT folds `Avx2.IsSupported` and `Avx512BW.IsSupported` for the set you pick, so the binary uses that set or it does not start. Leave the property unset for the default, which is x86-64-v2: the literal scan stays on SSE2 (`pcmpeqb`) and the binary runs on a CPU without AVX2 or AVX-512. `x86-64-v3` requires AVX2 (and the rest of v3: AVX, BMI1, BMI2, F16C, FMA, LZCNT, MOVBE) and emits AVX2 with no AVX-512. `x86-64-v4` requires AVX-512F, AVX-512BW, AVX-512CD, AVX-512DQ, and AVX-512VL. That binary will not run on a CPU without AVX-512. The project file does not set `IlcInstructionSet`, so a plain `dotnet publish` stays on the v2 baseline. Do not pass `native`; that target is not used here.

That publish uses Satori GC (`PublishWithSatoriGC` 11.0.0-satori.38196422592.1). Satori applies only to the self-contained Native AOT publish. `dotnet test` and `dotnet run` stay on the stock JIT GC. Pass `/p:UseSatoriGC=false` to publish with the stock GC instead. `ServerGarbageCollection` stays false.

The native binary is `src/Ngrep/bin/Release/net11.0/linux-x64/publish/ngrep` unless `-o` is set. `dotnet build` / `dotnet run` stay on the JIT, which is the right loop for development. The same `/p:IlcInstructionSet` values apply to `win-x64` and `osx-x64`. `osx-arm64` is arm64; do not pass an x86-64 instruction set for it.

To cut a release, set `<Version>` in `src/Ngrep/Ngrep.csproj` and push a tag `vX.Y.Z` with that same number (`v0.2.0` for version `0.2.0`). [`.github/workflows/release.yml`](.github/workflows/release.yml) builds Native AOT binaries and uploads one baseline plus, on x64, an AVX2 asset and an AVX-512 asset. Only the two newest releases are kept. After each publish the workflow deletes every older release and its tag. The workflow can also be run by hand with a version; it creates the tag when the tag is missing.

| Asset | CPU |
| --- | --- |
| `ngrep-linux-x64`, `ngrep-win-x64.exe`, `ngrep-osx-x64` | x86-64-v2. No AVX2 or AVX-512 required. `ngrep-osx-x64` is uploaded only when the macOS runner can cross-compile x64. |
| `ngrep-linux-x64-avx2`, `ngrep-win-x64-avx2.exe`, `ngrep-osx-x64-avx2` | Requires AVX2 (`x86-64-v3`). Will not run without it. |
| `ngrep-linux-x64-avx512`, `ngrep-win-x64-avx512.exe`, `ngrep-osx-x64-avx512` | Requires AVX-512 (`x86-64-v4`). Will not run without it. |
| `ngrep-osx-arm64` | Apple silicon. No AVX2 or AVX-512 asset is built. |

`dotnet publish -c Release -r linux-x64` completed on this machine with the .NET 11 RC SDK and no trimmer warnings. The result is a stripped ELF (~4.5 MB) that is dynamically linked against the system libc, not a fully static musl binary. `index` and `search --format ndjson` were run from that binary.

Search reads candidate files on task workers with `RandomAccess.ReadAsync` (`FileOptions.Asynchronous` and a sequential scan hint). Bytes come from `ArrayPool` and are returned before line matching. Files of at least 256 KiB are memory-mapped once their length is known. There is no separate synchronous read path. When an ASCII required literal is known, those bytes are scanned before UTF-8 decode: 64 at a time if AVX-512BW is supported, 32 with AVX2, 16 with SSE2 or SSSE3, otherwise scalar. One literal compares the first byte, the rarest interior byte, and the last byte, then exact-compares. Several literals of length at least 2 use a Teddy fingerprint (one bit per pattern, two or three leading bytes) and exact-compare the hits. ASCII `A-Z` is folded for `--ignore-case` and for a `--smart-case` pattern that has no uppercase letter. A pure ASCII file is confirmed on those bytes, so a miss never decodes and a hit only allocates the matching lines. Hits on the same line share that one string, so a minified line that contains the needle many times is not copied once per hit. `-l` stops reading once the matching line has been seen. A non-ASCII file is still decoded. Files of at least 256 KiB are memory-mapped instead of copied, so a warm scan reads the page cache. A regex that is not a case-sensitive literal is confirmed by a linear engine: on ASCII text, an in-tree byte automaton (literal SIMD, a class run, a literal prefix plus disjoint classes, or a Pike VM over a Thompson NFA) confirms the bytes and only candidate lines are decoded. When every alternative has a required ASCII literal, that literal scan narrows the candidate lines first and each candidate line is confirmed with the .NET engine; a Pike VM then runs only when the file cannot be narrowed, such as a non-ASCII file, because the SIMD literal scan is much cheaper than the VM. A Pike VM is otherwise preceded by a fully determinized byte automaton used only to reject a miss, which turns a pattern such as `[a-z]+[0-9]+` from a thread list per byte into one table lookup per byte. The VM still produces the spans. Patterns the automaton does not accept use `RegexOptions.NonBacktracking`. `-P` / `--pcre` keeps the backtracking interpreter. User patterns never use `RegexOptions.Compiled`. After a decode, line starts are collected with the same dispatch, scanning UTF-16 for `\n`. A content scan walks and reads together: several tasks list sibling directories, and hits are printed as each file is confirmed instead of after the whole tree is collected and sorted. `-j 1` stays a single depth-first walk. Ignore state is an immutable stack, so those listers do not share a mutable cursor. Index search still prints in path order. A channel hands files to the async read workers. `-v` uses the same pool, skips the literal reject (a missing literal is a hit), and leaves a line at the first hit.

Still short of ripgrep:

- The default regex engine is non-backtracking. ASCII patterns with a literal or a simple class run are confirmed on the bytes, and a literal alternation uses the Teddy scan. A pattern that compiles to a Pike VM narrows to candidate lines with its required literal first, because the VM is much slower than the scan; a VM left with no required literal and no anchor is preceded by a determinized byte automaton that settles a miss in one table lookup per byte, and the VM only produces the spans. An anchored pattern still runs the VM with no such filter, so `^[a-z]+[0-9]+$` stays the slow shape. `RegexOptions.NonBacktracking` is linear but slower than ripgrep's lazy DFA on a dense character class with no literal. `-P` is the backtracking interpreter and can be slower still. Backreferences and lookaround are not in the default language. Teddy covers multi-literal fingerprints; it is not a DFA and it does not fold non-ASCII case. `RegexOptions.Compiled` is not used. `dotnet test` and `dotnet run` dispatch at runtime (`Avx512BW`, then `Avx2`, then SSE2 / SSSE3, then scalar). Default `dotnet publish -c Release -r linux-x64` does not. Native AOT's baseline is x86-64-v2, and it does not keep AVX2 or AVX-512 as optional light-up, so those `IsSupported` checks fold off and the native binary keeps the SSE2 / SSSE3 scan (no `%ymm` or `%zmm`). That binary does not fault on a CPU without AVX-512. `/p:IlcInstructionSet=x86-64-v3` emits AVX2, still no AVX-512, and requires AVX2. `/p:IlcInstructionSet=x86-64-v4` emits AVX-512 and will not run without it. The project leaves the property unset. The release workflow publishes the v3 and v4 binaries as separate assets. `dotnet run -c Release --project bench/Ngrep.Bench.csproj` times a warmed scan against `rg` when `rg` is on `PATH`.
- Trigrams are three characters. A short alternative, a pattern that can match without a literal, lookaround, and backreferences still read every candidate. `-v` does too: a file that lacks the pattern is a hit, so postings cannot drop it. `--files-without-match` records those drops as paths without reading them when the prefilter is safe.
- `-g` and `--iglob` do not re-include an ignored path or a hidden path. Ripgrep's `-g` overrides ignore rules. `--no-ignore` opts out of every automatic ignore file and still reads `--ignore-file`. `--no-ignore-vcs` opts out of `.gitignore`, `info/exclude`, and the global excludes file, and still reads `.ignore`, `.rgignore`, and `--ignore-file`. There is no `--no-ignore-files`, `--glob-case-insensitive`, or `--ignore-file-case-insensitive`. `--hidden` is separate. An ignore whitelist does not un-hide a dotfile. A content scan treats a Windows or macOS hidden attribute like a dot name. An indexed search still uses the dot-name rule only.
- `-a` / `--text` and `-uuu` / `--binary` print a NUL-file match as text. Ripgrep's `--binary` prints `binary file matches` and does not print the line. `.git` and `.ngrep` stay skipped at `-uuu`. `-u` is `--no-ignore`, so it skips `.ignore` and `.rgignore` as well as `.gitignore`, and it still reads `--ignore-file`.
- An empty `-f` pattern matches each line once. Ripgrep's empty regex also reports a zero-width match at every offset (`-o` prints one empty match per byte). `NGREP_CONFIG_PATH` is one argument per line and accepts only ngrep's flags. `RIPGREP_CONFIG_PATH` is not read. `--smart-case` treats any uppercase letter in the pattern text as case-sensitive, including a regex escape such as `\W` (ripgrep ignores non-literal uppercase).
- Headings are path plus `line:column:text`. The column stays on unless `--no-column`. Ripgrep’s default match line has no column unless `--column` or `--vimgrep` is set. Auto headings require a terminal, more than one file whose contents were read, and a path that is being shown. `--vimgrep` forces `path:line:column:text` and no heading. `-I` drops that path. `-0` / `--null` ends a printed path with NUL, including `--files`, `-l`, and `--files-without-match`.
- `--stats` is five lines on stderr (files searched, files matched, lines matched, matches, elapsed ms). It is not ripgrep’s byte counts or elapsed breakdown, and it does not count prefilter drops as searched.
- `--format rgjson` adds context events, a UTF-16 `absolute_offset` (line start, not a byte offset), and `elapsed_ms`. It is still not ripgrep’s JSON: no byte offsets, no `elapsed` duration object, no binary detection stats. `-r` does not rewrite rgjson; `text` stays the original match.
- `-c` prints `count` for one file or stdin, and `path:count` when the path is shown. `-H` forces the path. There is no `--include-zero` and no `--colors` customization.

AOT notes:

- System.CommandLine 2.0 handlers do not use reflection-based binding.
- Gitignore globs use the interpreter. User regexes use `RegexOptions.NonBacktracking` by default, which does not emit IL and is AOT-safe. `-P` uses the interpreter. `RegexOptions.Compiled` is not used, because the pattern is data and Compiled is not AOT-safe.
- `FileSystemWatcher` is the supported watch API. The loop does not raise inotify limits or coalesce anything finer than a 300 ms flag.
- Index JSON is `Utf8JsonWriter` / `JsonDocument`, not reflection `JsonSerializer`.
- Invariant globalization means culture-specific case folding is not available. The index folds with invariant lowercase, and `--ignore-case` compares with ordinal ignore-case.
- Publish needs a native toolchain (`clang` and the usual linker). That toolchain was already present here.

# ngrep

ngrep is a small search CLI in the spirit of ripgrep: it walks a tree, honors `.gitignore`, and builds a trigram inverted index so later searches do not read every file. Output is stable text, JSON, or NDJSON for tools that want path, line, and column.

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
```

`testdata/sample` is a tiny tree with a nested `.gitignore`, a binary file, and a log that should not match.

## Commands

```text
ngrep <pattern> [path] [search flags]
ngrep search <pattern> [path] [search flags]
ngrep index <path> [--index <dir>] [--format text|json|ndjson] [--quiet|-q]
ngrep watch <path> [--index <dir>] [--format text|json|ndjson]
```

`ngrep <pattern> [path]` is the same command as `ngrep search`. The first positional word is a subcommand only when it is `index`, `search`, or `watch`. To search for the literal `index`, run `ngrep search index` or `ngrep -- index`.

Search flags:

```text
[--index <dir>] [--format text|json|ndjson|rgjson]
[--ignore-case|-i] [-E|--extended-regexp|--regex] [-F|--fixed-strings] [--scan]
[-e|--regexp <pattern>]... [-f|--file <path>]... [-w|--word-regexp] [-x|--line-regexp] [-v|--invert-match]
[-l|--files-with-matches] [--files-without-match] [-c|--count] [-q|--quiet|--silent] [-o|--only-matching]
[-n|--line-number] [-s|--no-messages]
[--heading] [--no-heading] [--stats]
[-g|--glob <glob>]... [-t|--type <type>]... [-T|--type-not <type>]...
[--type-add <spec>]... [--type-list]
[-A|--after-context <n>] [-B|--before-context <n>] [-C|--context <n>]
[-U|--multiline] [--multiline-dotall] [-P|--pcre] [--hidden]
[--color auto|always|never]
[-j|--threads <n>]
```

`index` writes `<path>/.ngrep/index` unless `--index` is set. Running it again updates changed files and drops deleted ones (size + mtime, then SHA-256). A second pass does not read bytes when size and mtime still match. File reads use the same async pipeline as search (`NGREP_THREADS`, else `NETGREP_THREADS`, else the processor count). `-q` skips the per-path stderr lines.

While that walk runs, progress is printed on stderr in English. A new document is `added: path`, a replaced document is `updated: path`, and a document that is no longer in the tree is `removed: path`. Each of those lines is printed only after that document has been committed. Unchanged files, ignored files, and skipped files (binary, too large, or unreadable) do not get their own line. Every 100 files examined, and once after removals, a summary is printed: `indexed 120/500, added 3, updated 1, unchanged 116, removed 0, skipped 0`. The `indexed` counts are files the walker examined, not ignored files. `skipped` in that line is binary, too large, and unreadable. Stdout stays the final summary for `--format text`, `json`, or `ndjson`. `-q` / `--quiet` on `index` turns the stderr progress off and leaves that summary in place. It is a different flag from search `-q` / `--quiet` / `--silent`, which suppresses match output. `watch` does not print this progress.

Changed files are trigrammed in memory. The index is then written once, to `<index>.tmp`, and that directory replaces `<index>` only when `manifest.json`, `documents.ndjson`, and `postings.bin` are complete. A failed write leaves the previous index in place. A process killed before that write keeps the previous index (or no index, on a first build). Removals are applied in path order in that same snapshot. `added:` / `updated:` / `removed:` lines are printed only after the write succeeds. Files whose size and mtime match are not read. A matching hash refreshes size and mtime in that same final snapshot. The every-100 `indexed` summary still prints during the walk.

Search looks upward from `[path]` (default `.`) for `.ngrep/index`. When an index covers the path, ngrep builds a required-literal prefilter and intersects trigram postings before reading candidates. A literal, or a regex piece, can feed that filter when every alternative has a required substring of 3+ characters (`foo`, `foo.*bar`, `foo|bar`, `(ab){2}`, `n.grep`). The file is still read to confirm the real match, so a prefilter hit is not a result. Trigrams cannot help when an alternative is shorter than 3 (`ab`, `a|ngrep`, `foo*` which must allow `fo`), when the pattern can match with no required text (`.`, `a*`), when the syntax is not extracted (lookaround, backreferences), or when `-v` is set. Those queries read every indexed file. An ASCII required piece still rejects a file from its bytes before the text is decoded, including pieces shorter than 3. With no index, `--scan`, `--no-ignore`, `--no-ignore-vcs`, or a path outside the indexed root, ngrep scans contents and prints a one-line note on stderr. You do not pass `--scan` just because the tree has never been indexed. The index does not contain gitignored files, so the ignore flags always scan.

Patterns are fixed strings unless `-E` / `--extended-regexp` / `--regex` is set. That flag turns every pattern into a .NET regular expression, including patterns read from a file. It is not POSIX basic (BRE) or extended (ERE) syntax. The default engine is non-backtracking (`RegexOptions.NonBacktracking`): matching is linear in the length of the file. Backreferences (`\1`), lookaround (`(?=...)`, `(?<=...)`), and atomic groups (`(?>...)`) are rejected with a message that names `-P` / `--pcre`. `-P` selects the backtracking interpreter and implies `--regex`. `-F` / `--fixed-strings` selects the fixed-string default explicitly and is mutually exclusive with `-E` and `-P` (exit 2). There is no BRE engine: `ngrep 'a\+'` searches for the four characters `a\+`, while POSIX `grep 'a\+'` searches for one or more `a`.

Patterns are OR. `-e` / `--regexp` may be repeated. A positional pattern written before the first `-e` or `-f` is included; once either flag is present, the remaining positional argument is the path (`ngrep -e foo -e bar src`, `ngrep -f pats.txt src`). `-w` keeps a match only when both ends are word boundaries (a Unicode letter, digit, or `_` on one side and a non-word character or the end of the text on the other). `-x` / `--line-regexp` keeps a match only when the entire line matches. The line is the same text ngrep prints: a trailing CR before LF is not part of the line. A literal must equal that text. A regex is run against that text anchored at both ends (`\A(?:pattern)\z`), so `a|aaa` matches a line `aaa`. A pattern that contains a newline does not match a single line, including with `-U`. `-v` prints each line that matches none of the patterns; `text` and `lineText` are that line and `column` is 1.

`-f` / `--file` reads more patterns, one per line, and OR-s them after positional and `-e` patterns. Repeat the flag for several files. `-` reads stdin. The newline is not part of the pattern, and a trailing newline does not add an extra pattern. An empty line is an empty pattern and matches every line (one hit per line, column 1, `text` empty, `lineText` the line). With `-x`, that empty pattern matches only empty lines. A line whose first character is `#` is a normal pattern, including the `#`. Nothing in the file is a comment, and `\#` is not rewritten: without `--regex` the backslash is literal, and with `--regex` it is a regex escape (`\#` matches `#`). A missing or unreadable path exits 2 (`Pattern file not found`). A zero-length file that yields no lines, and no `-e` or positional pattern, exits 2 (`Pattern file contains no patterns`). `#` comments belong to ripgrep's config file (`RIPGREP_CONFIG_PATH`), which ngrep does not read.

`-g` is repeatable. A glob that starts with `!` excludes, same as ripgrep. If any glob does not start with `!`, a file must be selected by an include. When several globs match one path, the last one wins. Globs use gitignore syntax (`*`, `?`, `**`, `[abc]`) and run after `.gitignore`. They do not bring a gitignored file back. `ngrep foo -g '*.log'` still skips a gitignored log. `--no-ignore` and `--no-ignore-vcs` turn `.gitignore` off so those files are walked, and `-g` then filters that larger set (`--no-ignore -g '*.log'` prints the log). ngrep does not read `.ignore` or global git excludes, so the two flags do the same thing. `.git`, `.ngrep`, the index directory, and symlinks stay skipped either way.

`-t` selects built-in types (OR if repeated). `-T` excludes types. `--type-list` prints the set and exits 0. `--type-add name:*.ext[,*.ext]` adds globs (a bare extension means `*.ext`; `name:include:*.ext` is accepted). Type names are case-insensitive. Types compose with globs and with `.gitignore` the same way: ignored files stay ignored.

`-A`, `-B`, and `-C` add context in text output. `-C` sets both sides; `-A` and `-B` replace that side when they are also set. Context lines use `path-line-text`. Match lines stay `path:line:column:lineText`. A `--` line separates groups that do not touch. Overlapping context is not printed twice, and a context line that is itself a match is printed only as a match.

`-l` / `--files-with-matches` prints one path per file that has a hit, and stops reading that file after the first hit. `--files-without-match` prints paths that were searched and had none. `-c` / `--count` prints `path:count`, where the count is matching lines (a line with two hits counts once). Files with a zero count are omitted. `-q` / `--quiet` / `--silent` prints no match report on stdout (text, JSON, NDJSON, and rgjson). Exit 0 means some line matched, 1 means none, 2 means an error. These four flags are mutually exclusive (exit 2). POSIX allows only one of `-c`, `-l`, and `-q`; `--files-without-match` joins that set. Text for `-l` and `-c` is paths or `path:count`. `--format json` is `{"paths":[...]}` or `{"counts":[{"path","count"}]}`. `--format ndjson` is one `{"path"}` or `{"path","count"}` object per line. That is not a match object: normal search JSON is unchanged. `-q` does not hide the stderr scan note. It stops the walk at the first matching file, and it stops reading that file at the first hit, unless `--stats` is set.

`-n` / `--line-number` is accepted and does not change output. Text matches already include the 1-based line (`path:line:column:text`, or `line:column:text` under a heading). There is no flag to hide the line number or the column.

`-s` / `--no-messages` suppresses stderr for a missing search path and for a file you named that cannot be read. The exit code is still 2. It does not hide usage errors, a bad regex, a missing `-f` file, or the scan note. A file that cannot be read while walking a directory is skipped with no message and does not change the exit code, with or without `-s`.

`-v` changes what a hit is. `-l -v` lists files that contain at least one non-matching line. `--files-without-match -v` lists files whose every line matches (an empty file has no lines, so it is listed). `-c -v` counts non-matching lines. Exit 0 means the selected report was non-empty; exit 1 means it was empty.

`-o` / `--only-matching` changes text output to `path:line:column:text`, the matched slice, one line per hit. JSON and NDJSON stay `path`, `line`, `column`, `text`, `lineText`. `text` was already the slice. Context flags still fill `before` / `after` in JSON; text `-o` does not print context lines.

Hidden files and directories (any path component whose name starts with `.`) are skipped by default. `--hidden` searches them. Naming a file, or a directory, still searches that path: `ngrep pat .secret` reads `.secret`, and `ngrep pat .github` reads non-hidden children of `.github`. Hard skips stay skipped with or without `--hidden`, `--no-ignore`, or an explicit path: a directory named `.git` or `.ngrep`, the index directory, and symlinks. `-g` does not bring a skipped hidden path back (same rule as gitignore). A gitignore negation does not un-hide a dotfile; pass `--hidden`. `--no-ignore` does not imply `--hidden`. The index still stores hidden files so `--hidden` can use it. A content scan skips them in the walk unless the flag is set.

`--color` is `auto` (default), `always`, or `never`. `auto` colors text when stdout is a terminal, `NO_COLOR` is unset, and `TERM` is not `dumb`. `--color` alone means `auto`. JSON, NDJSON, and rgjson are never colored. Text uses magenta paths, green line numbers, blue columns, and bold red matches. There is no `--colors` spec.

`-U` / `--multiline` lets a **regex** include a newline. `--multiline-dotall` implies `-U` and lets `.` match a newline (`RegexOptions.Singleline`). Either way `^` and `$` are line anchors (`RegexOptions.Multiline`), including when `-U` is off, so `^bar$` matches a line rather than only the whole file. Without `-U`, a match that contains a newline is dropped, including a pattern that turns dotall on itself such as `(?s)foo.bar`. User patterns never use `RegexOptions.Compiled`. A non-regex pattern is an exact substring, so a newline in that pattern already matches and `-U` does not change it. `-v` treats every line overlapped by a cross-line match as a matching line.

Text headings follow ripgrep when more than one file is searched. On a terminal (`TERM` is not `dumb`, and stdout is not redirected), a search that reads more than one file prints the path on its own line, then `line:column:text` (and context as `line-text`) without repeating the path. A blank line separates files. A single file, a one-file directory, a pipe, or `TERM=dumb` keeps `path:line:column:text`. `--heading` forces the heading form even then. `--no-heading` forces the prefixed form and wins when both flags are set. JSON, NDJSON, and rgjson never print headings. `-l`, `--files-without-match`, and `-c` do not use headings. `NO_COLOR` turns color off and does not turn headings off. A heading is colored with the same magenta as a path.

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

`watch` indexes once, then reindexes about 300 ms after a change. It is a thin `FileSystemWatcher` loop: it does not follow symlinks, it ignores its own index writes, and a `.gitignore` edit is just another incremental rebuild. Ctrl+C exits 0.

### Exit codes

| Code | When |
| --- | --- |
| 0 | Index or watch succeeded, search found at least one match (including `-q`), or `--type-list` / `--help` ran. |
| 1 | Search finished and found no matches. |
| 2 | Bad usage, missing path, invalid regex or glob, unknown type, canceled search, unreadable index, or another error. |

Errors are plain text on stderr. Stdout is only the selected format, including when it is empty. `-q` leaves stdout empty. `-s` drops the missing-path and named-unreadable lines and still exits 2.

### POSIX grep flags

Classic `grep` without `-E` or `-F` uses POSIX basic regular expressions. ngrep does not. The default is fixed strings (GNU `grep -F`). `-E` selects .NET regular expressions. `-E` is not ripgrep's `--encoding`.

Boolean short options can share one token (`-nEi`, `-xF`, `-q`). A value option ends the cluster. The rest of the token is its operand (`-nEepat` is `-n -E -e pat`). A token that starts with a value option is left unchanged (`-epat`, `-A2`, `-j1`), which is how those attached values already worked. Tokens after `--` are not flags. A pattern whose name is `index`, `search`, or `watch` still needs `ngrep search` or `ngrep --`.

| POSIX | ngrep | Status |
| --- | --- | --- |
| `-E` | `-E`, `--extended-regexp`, `--regex` | .NET regex, not POSIX ERE |
| `-F` | `-F`, `--fixed-strings` | Fixed strings. This is already the default. Exit 2 together with `-E` |
| `-c` | `-c`, `--count` | Matching lines per file. Zero counts omitted |
| `-l` | `-l`, `--files-with-matches` | Paths with a hit |
| `-q` | `-q`, `--quiet`, `--silent` | No stdout match report. Exit 0 / 1 / 2 |
| `-i` | `-i`, `--ignore-case` | Ordinal, culture-invariant |
| `-n` | `-n`, `--line-number` | Accepted. Line numbers are always printed |
| `-s` | `-s`, `--no-messages` | Hides missing-path and named-unreadable errors. Exit code unchanged |
| `-v` | `-v`, `--invert-match` | Lines that do not match |
| `-x` | `-x`, `--line-regexp` | The whole line must match |
| `-e` | `-e`, `--regexp` | Repeatable pattern, OR |
| `-f` | `-f`, `--file` | Pattern file, one pattern per line, OR |

Dialect gaps that remain:

- No POSIX BRE. Without `-E`, metacharacters are literal. `a\+` is four characters, not "one or more `a`".
- `-E` is the .NET regex language, not POSIX ERE. The default engine is non-backtracking and linear. It does not support backreferences, lookaround, or atomic groups; `-P` / `--pcre` is the backtracking interpreter for those, and it implies `--regex`. POSIX collating elements and equivalence classes are absent. Escapes such as `\s` follow .NET. `^` and `$` are line anchors (`RegexOptions.Multiline`). `.` does not match a newline unless `--multiline-dotall`. User patterns are not compiled (`RegexOptions.Compiled` is not AOT-safe). The backtracking engine times out after five seconds. Non-backtracking does not backtrack, so that timeout is a bound on a linear scan.
- `-n` cannot turn line numbers off. The column is always printed. There is no `-H` / `-h` filename switch and no `--no-line-number`.
- `-q` does not suppress the stderr scan note (`no index found; scanned content` and the same family). Without `--stats` it stops the walk at the first matching file. `--stats` still finishes the walk so the counts stay complete.
- `-s` does not cover unreadable files discovered while walking a directory. Those stay skipped, with no diagnostic and no exit 2. `-s` also does not hide usage errors, bad regexes, or missing pattern files.
- An unknown character in a short cluster is not expanded (`-nZ` is an error). Options after `--` are literals.
- Search is always recursive. There is no `-r` and no non-recursive directory mode.
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

- Nested `.gitignore` files apply, from the git root down to the file. Outside a git checkout, only `.gitignore` files under the path you passed are used.
- `.git`, `.ngrep`, the index directory, and symlinks are always skipped.
- Binary files (NUL in the first 8 KiB) and files larger than 32 MiB are skipped.
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

Search reads candidate files on task workers with `RandomAccess.ReadAsync` (`FileOptions.Asynchronous` and a sequential scan hint). Bytes come from `ArrayPool` and are returned before line matching. Files of at least 256 KiB are memory-mapped once their length is known. There is no separate synchronous read path. When an ASCII required literal is known, those bytes are scanned before UTF-8 decode: 64 at a time if AVX-512BW is supported, 32 with AVX2, 16 with SSE2 or SSSE3, otherwise scalar. One literal compares the first byte, the rarest interior byte, and the last byte, then exact-compares. Several literals of length at least 2 use a Teddy fingerprint (one bit per pattern, two or three leading bytes) and exact-compare the hits. ASCII `A-Z` is folded only for `--ignore-case`. A pure ASCII file is confirmed on those bytes, so a miss never decodes and a hit only allocates the matching lines. Hits on the same line share that one string, so a minified line that contains the needle many times is not copied once per hit. `-l` stops reading once the matching line has been seen. A non-ASCII file is still decoded. Files of at least 256 KiB are memory-mapped instead of copied, so a warm scan reads the page cache. A regex that is not a case-sensitive literal is confirmed by a linear engine: on ASCII text, an in-tree byte automaton (literal SIMD, a class run, a literal prefix plus disjoint classes, or a Pike VM over a Thompson NFA) runs on the bytes and only candidate lines are decoded; patterns that automaton does not accept, and any non-ASCII file, use `RegexOptions.NonBacktracking`. `-P` / `--pcre` keeps the backtracking interpreter. User patterns never use `RegexOptions.Compiled`. After a decode, line starts are collected with the same dispatch, scanning UTF-16 for `\n`. The gitignore walk stays on one task (ignore state is a stack); a channel hands files to the async read workers. `-v` uses the same pool, skips the literal reject (a missing literal is a hit), and leaves a line at the first hit.

Still short of ripgrep:

- The default regex engine is non-backtracking. ASCII patterns with a literal or a simple class run are confirmed on the bytes; everything else in the supported language goes to `RegexOptions.NonBacktracking`, which is linear but slower than ripgrep's lazy DFA on a dense character class with no literal. `-P` is the backtracking interpreter and can be slower still. Backreferences and lookaround are not in the default language. Teddy covers multi-literal fingerprints; it is not a DFA and it does not fold non-ASCII case. `RegexOptions.Compiled` is not used. `dotnet test` and `dotnet run` dispatch at runtime (`Avx512BW`, then `Avx2`, then SSE2 / SSSE3, then scalar). Default `dotnet publish -c Release -r linux-x64` does not. Native AOT's baseline is x86-64-v2, and it does not keep AVX2 or AVX-512 as optional light-up, so those `IsSupported` checks fold off and the native binary keeps the SSE2 / SSSE3 scan (no `%ymm` or `%zmm`). That binary does not fault on a CPU without AVX-512. `/p:IlcInstructionSet=x86-64-v3` emits AVX2, still no AVX-512, and requires AVX2. `/p:IlcInstructionSet=x86-64-v4` emits AVX-512 and will not run without it. The project leaves the property unset. The release workflow publishes the v3 and v4 binaries as separate assets. `dotnet run -c Release --project bench/Ngrep.Bench.csproj` times a warmed scan against `rg` when `rg` is on `PATH`.
- Trigrams are three characters. A short alternative, a pattern that can match without a literal, lookaround, and backreferences still read every candidate. `-v` does too: a file that lacks the pattern is a hit, so postings cannot drop it. `--files-without-match` records those drops as paths without reading them when the prefilter is safe.
- `-g` does not re-include a gitignored path or a hidden path. `--no-ignore` / `--no-ignore-vcs` opt out of `.gitignore` only. `--hidden` is separate. A gitignore whitelist does not un-hide a dotfile. Hidden means a name starting with `.`, not the Windows hidden attribute.
- An empty `-f` pattern matches each line once. Ripgrep's empty regex also reports a zero-width match at every offset (`-o` prints one empty match per byte). There is no `RIPGREP_CONFIG_PATH`.
- Headings are path plus `line:column:text`. Ripgrep’s default match line has no column unless `--column` is set. Auto headings require a terminal and more than one file whose contents were read. There is no `--vimgrep`, `--with-filename`, or `--null`.
- `--stats` is five lines on stderr (files searched, files matched, lines matched, matches, elapsed ms). It is not ripgrep’s byte counts or elapsed breakdown, and it does not count prefilter drops as searched.
- `--format rgjson` adds context events, a UTF-16 `absolute_offset` (line start, not a byte offset), and `elapsed_ms`. It is still not ripgrep’s JSON: no byte offsets, no `elapsed` duration object, no binary detection stats, no replacement text.
- `-c` always prints `path:count`, including for a single file. There is no `--include-zero` and no `--colors` customization.

AOT notes:

- System.CommandLine 2.0 handlers do not use reflection-based binding.
- Gitignore globs use the interpreter. User regexes use `RegexOptions.NonBacktracking` by default, which does not emit IL and is AOT-safe. `-P` uses the interpreter. `RegexOptions.Compiled` is not used, because the pattern is data and Compiled is not AOT-safe.
- `FileSystemWatcher` is the supported watch API. The loop does not raise inotify limits or coalesce anything finer than a 300 ms flag.
- Index JSON is `Utf8JsonWriter` / `JsonDocument`, not reflection `JsonSerializer`.
- Invariant globalization means culture-specific case folding is not available. The index folds with invariant lowercase, and `--ignore-case` compares with ordinal ignore-case.
- Publish needs a native toolchain (`clang` and the usual linker). That toolchain was already present here.

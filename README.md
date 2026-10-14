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
ngrep index <path> [--index <dir>] [--format text|json|ndjson]
ngrep watch <path> [--index <dir>] [--format text|json|ndjson]
```

`ngrep <pattern> [path]` is the same command as `ngrep search`. The first positional word is a subcommand only when it is `index`, `search`, or `watch`. To search for the literal `index`, run `ngrep search index` or `ngrep -- index`.

Search flags:

```text
[--index <dir>] [--format text|json|ndjson|rgjson]
[--ignore-case|-i] [--regex] [--scan]
[-e|--regexp <pattern>]... [-f|--file <path>]... [-w|--word-regexp] [-v|--invert-match]
[-l|--files-with-matches] [--files-without-match] [-c|--count] [-o|--only-matching]
[--heading] [--no-heading] [--stats]
[-g|--glob <glob>]... [-t|--type <type>]... [-T|--type-not <type>]...
[--type-add <spec>]... [--type-list]
[-A|--after-context <n>] [-B|--before-context <n>] [-C|--context <n>]
[-U|--multiline] [--multiline-dotall] [--hidden]
[--color auto|always|never]
[-j|--threads <n>]
```

`index` writes `<path>/.ngrep/index` unless `--index` is set. Running it again updates changed files and drops deleted ones (size + mtime, then SHA-256).

Search looks upward from `[path]` (default `.`) for `.ngrep/index`. When an index covers the path, ngrep builds a required-literal prefilter and intersects trigram postings before reading candidates. A literal, or a regex piece, can feed that filter when every alternative has a required substring of 3+ characters (`foo`, `foo.*bar`, `foo|bar`, `(ab){2}`, `n.grep`). The file is still read to confirm the real match, so a prefilter hit is not a result. Trigrams cannot help when an alternative is shorter than 3 (`ab`, `a|ngrep`, `foo*` which must allow `fo`), when the pattern can match with no required text (`.`, `a*`), when the syntax is not extracted (lookaround, backreferences), or when `-v` is set. Those queries read every indexed file. An ASCII required piece still rejects a file from its bytes before the text is decoded, including pieces shorter than 3. With no index, `--scan`, `--no-ignore`, `--no-ignore-vcs`, or a path outside the indexed root, ngrep scans contents and prints a one-line note on stderr. You do not pass `--scan` just because the tree has never been indexed. The index does not contain gitignored files, so the ignore flags always scan.

Patterns are OR. `-e` / `--regexp` may be repeated. A positional pattern written before the first `-e` or `-f` is included; once either flag is present, the remaining positional argument is the path (`ngrep -e foo -e bar src`, `ngrep -f pats.txt src`). `--regex` applies to every pattern, including patterns read from a file. `-w` keeps a match only when both ends are word boundaries (a Unicode letter, digit, or `_` on one side and a non-word character or the end of the text on the other). `-v` prints each line that matches none of the patterns; `text` and `lineText` are that line and `column` is 1.

`-f` / `--file` reads more patterns, one per line, and OR-s them after positional and `-e` patterns. Repeat the flag for several files. `-` reads stdin. The newline is not part of the pattern, and a trailing newline does not add an extra pattern. An empty line is an empty pattern and matches every line (one hit per line, column 1, `text` empty, `lineText` the line). A line whose first character is `#` is a normal pattern, including the `#`. Nothing in the file is a comment, and `\#` is not rewritten: without `--regex` the backslash is literal, and with `--regex` it is a regex escape (`\#` matches `#`). A missing or unreadable path exits 2 (`Pattern file not found`). A zero-length file that yields no lines, and no `-e` or positional pattern, exits 2 (`Pattern file contains no patterns`). `#` comments belong to ripgrep's config file (`RIPGREP_CONFIG_PATH`), which ngrep does not read.

`-g` is repeatable. A glob that starts with `!` excludes, same as ripgrep. If any glob does not start with `!`, a file must be selected by an include. When several globs match one path, the last one wins. Globs use gitignore syntax (`*`, `?`, `**`, `[abc]`) and run after `.gitignore`. They do not bring a gitignored file back. `ngrep foo -g '*.log'` still skips a gitignored log. `--no-ignore` and `--no-ignore-vcs` turn `.gitignore` off so those files are walked, and `-g` then filters that larger set (`--no-ignore -g '*.log'` prints the log). ngrep does not read `.ignore` or global git excludes, so the two flags do the same thing. `.git`, `.ngrep`, the index directory, and symlinks stay skipped either way.

`-t` selects built-in types (OR if repeated). `-T` excludes types. `--type-list` prints the set and exits 0. `--type-add name:*.ext[,*.ext]` adds globs (a bare extension means `*.ext`; `name:include:*.ext` is accepted). Type names are case-insensitive. Types compose with globs and with `.gitignore` the same way: ignored files stay ignored.

`-A`, `-B`, and `-C` add context in text output. `-C` sets both sides; `-A` and `-B` replace that side when they are also set. Context lines use `path-line-text`. Match lines stay `path:line:column:lineText`. A `--` line separates groups that do not touch. Overlapping context is not printed twice, and a context line that is itself a match is printed only as a match.

`-l` / `--files-with-matches` prints one path per file that has a hit, and stops reading that file after the first hit. `--files-without-match` prints paths that were searched and had none. `-c` / `--count` prints `path:count`, where the count is matching lines (a line with two hits counts once). Files with a zero count are omitted. These three flags are mutually exclusive (exit 2). Text is paths or `path:count`. `--format json` is `{"paths":[...]}` or `{"counts":[{"path","count"}]}`. `--format ndjson` is one `{"path"}` or `{"path","count"}` object per line. That is not a match object: normal search JSON is unchanged.

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
| 0 | Index or watch succeeded, search found at least one match, or `--type-list` / `--help` ran. |
| 1 | Search finished and found no matches. |
| 2 | Bad usage, missing path, invalid regex or glob, unknown type, canceled search, unreadable index, or another error. |

Errors are plain text on stderr. Stdout is only the selected format, including when it is empty.

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
```

That publish uses Satori GC (`PublishWithSatoriGC` 11.0.0-satori.38196422592.1). Satori applies only to the self-contained Native AOT publish. `dotnet test` and `dotnet run` stay on the stock JIT GC. Pass `/p:UseSatoriGC=false` to publish with the stock GC instead. `ServerGarbageCollection` stays false.

The native binary is `src/Ngrep/bin/Release/net11.0/linux-x64/publish/ngrep`. `dotnet build` / `dotnet run` stay on the JIT, which is the right loop for development.

To cut a release, set `<Version>` in `src/Ngrep/Ngrep.csproj` and push a tag `vX.Y.Z` with that same number (`v0.2.0` for version `0.2.0`). [`.github/workflows/release.yml`](.github/workflows/release.yml) builds Native AOT binaries and uploads `ngrep-linux-x64`, `ngrep-win-x64.exe`, `ngrep-osx-arm64`, and `ngrep-osx-x64` (when the macOS runner has an x64 toolchain) to the GitHub Release for that tag. Only the two newest releases are kept. After each publish the workflow deletes every older release and its tag. The workflow can also be run by hand with a version; it creates the tag when the tag is missing.

`dotnet publish -c Release -r linux-x64` completed on this machine with the .NET 11 RC SDK and no trimmer warnings. The result is a stripped ELF (~4.5 MB) that is dynamically linked against the system libc, not a fully static musl binary. `index` and `search --format ndjson` were run from that binary.

Confirmation reads candidate files on a pool of workers. Bytes come from `ArrayPool`, with a sequential read hint, and are returned before the line scan. When an ASCII required literal is known, those bytes are scanned before UTF-8 decode: 64 at a time if AVX-512BW is supported, 32 with AVX2, 16 with SSE2 or Vector128, otherwise scalar. One literal compares the first and last bytes in the register and then exact-compares the span. Several literals compare the register against each distinct first byte, then exact-compare. ASCII `A-Z` is folded only for `--ignore-case`. A SIMD hit is a candidate. The file is still decoded and confirmed with `IndexOf`, the regex interpreter, `-w`, and `-v`. A miss is not decoded. User patterns never use `RegexOptions.Compiled`. After a decode, line starts are collected with the same dispatch, scanning UTF-16 for `\n`. The gitignore walk stays on one thread (ignore state is a stack); a bounded queue overlaps that walk with content reads. `-v` uses the same pool, skips the literal reject (a missing literal is a hit), and leaves a line at the first hit.

Still short of ripgrep:

- User regexes run on the interpreter. `RegexOptions.Compiled` is not used, because the pattern is data and Native AOT cannot compile it. There is no regex DFA. The SIMD layer is a literal prefilter, not ripgrep's Teddy: it does not pack every pattern byte into one fingerprint, and a file that contains the literal is still decoded and matched in full. Rejecting a file that lacks the literal is the case that gets faster. `dotnet test` and `dotnet run` dispatch at runtime (`Avx512BW`, then `Avx2`, then SSE2 / Vector128, then scalar). Default `dotnet publish -c Release -r linux-x64` does not. Native AOT's baseline is x86-64-v2, and it does not keep AVX2 or AVX-512 as optional light-up, so those `IsSupported` checks fold off and the native binary keeps the SSE2 scan (`pcmpeqb`, no `%ymm` or `%zmm`). That binary does not fault on a CPU without AVX-512. `/p:IlcInstructionSet=x86-64-v3` emits AVX2 and still no AVX-512. `/p:IlcInstructionSet=x86-64-v4` emits AVX-512 and will not run without it, so the project does not set that.
- Trigrams are three characters. A short alternative, a pattern that can match without a literal, lookaround, and backreferences still read every candidate. `-v` does too: a file that lacks the pattern is a hit, so postings cannot drop it. `--files-without-match` records those drops as paths without reading them when the prefilter is safe.
- `-g` does not re-include a gitignored path or a hidden path. `--no-ignore` / `--no-ignore-vcs` opt out of `.gitignore` only. `--hidden` is separate. A gitignore whitelist does not un-hide a dotfile. Hidden means a name starting with `.`, not the Windows hidden attribute.
- An empty `-f` pattern matches each line once. Ripgrep's empty regex also reports a zero-width match at every offset (`-o` prints one empty match per byte). There is no `RIPGREP_CONFIG_PATH`.
- Headings are path plus `line:column:text`. Ripgrep’s default match line has no column unless `--column` is set. Auto headings require a terminal and more than one file whose contents were read. There is no `--vimgrep`, `--with-filename`, or `--null`.
- `--stats` is five lines on stderr (files searched, files matched, lines matched, matches, elapsed ms). It is not ripgrep’s byte counts or elapsed breakdown, and it does not count prefilter drops as searched.
- `--format rgjson` adds context events, a UTF-16 `absolute_offset` (line start, not a byte offset), and `elapsed_ms`. It is still not ripgrep’s JSON: no byte offsets, no `elapsed` duration object, no binary detection stats, no replacement text.
- `-c` always prints `path:count`, including for a single file. There is no `--include-zero` and no `--colors` customization.

AOT notes:

- System.CommandLine 2.0 handlers do not use reflection-based binding.
- Gitignore and user regexes use the interpreter, not `RegexOptions.Compiled`, because the patterns are data and Compiled is not AOT-safe. Regex search is correct under AOT and slower than a source-generated pattern.
- `FileSystemWatcher` is the supported watch API. The loop does not raise inotify limits or coalesce anything finer than a 300 ms flag.
- Index JSON is `Utf8JsonWriter` / `JsonDocument`, not reflection `JsonSerializer`.
- Invariant globalization means culture-specific case folding is not available. The index folds with invariant lowercase, and `--ignore-case` compares with ordinal ignore-case.
- Publish needs a native toolchain (`clang` and the usual linker). That toolchain was already present here.

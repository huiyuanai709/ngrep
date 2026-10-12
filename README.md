# netgrep

netgrep is a small search CLI in the spirit of ripgrep: it walks a tree, honors `.gitignore`, and builds a trigram inverted index so later searches do not read every file. Output is stable text, JSON, or NDJSON for tools that want path, line, and column.

The index format is [docs/index-format.md](docs/index-format.md). Ignore rules, command behavior, and library choices are in [DESIGN.md](DESIGN.md).

## Requirements

.NET SDK **11** (`net11.0`). This repo was built with `11.0.100-rc.1` (see `global.json`). A stable 11.0 SDK also works; `rollForward` is `latestFeature`.

## Build and run

```bash
dotnet build
dotnet test
dotnet run --project src/Netgrep -- index testdata/sample
dotnet run --project src/Netgrep -- netgrep testdata/sample
dotnet run --project src/Netgrep -- search netgrep testdata/sample --format ndjson
```

`testdata/sample` is a tiny tree with a nested `.gitignore`, a binary file, and a log that should not match.

## Commands

```text
netgrep <pattern> [path] [search flags]
netgrep search <pattern> [path] [search flags]
netgrep index <path> [--index <dir>] [--format text|json|ndjson]
netgrep watch <path> [--index <dir>] [--format text|json|ndjson]
```

`netgrep <pattern> [path]` is the same command as `netgrep search`. The first positional word is a subcommand only when it is `index`, `search`, or `watch`. To search for the literal `index`, run `netgrep search index` or `netgrep -- index`.

Search flags:

```text
[--index <dir>] [--format text|json|ndjson]
[--ignore-case|-i] [--regex] [--scan]
[-e|--regexp <pattern>]... [-w|--word-regexp] [-v|--invert-match]
[-g|--glob <glob>]... [-t|--type <type>]... [-T|--type-not <type>]...
[--type-add <spec>]... [--type-list]
[-A|--after-context <n>] [-B|--before-context <n>] [-C|--context <n>]
[-j|--threads <n>]
```

`index` writes `<path>/.netgrep/index` unless `--index` is set. Running it again updates changed files and drops deleted ones (size + mtime, then SHA-256).

Search looks upward from `[path]` (default `.`) for `.netgrep/index`. When an index covers the path, a literal pattern of 3+ characters is filtered with the trigram postings, then confirmed by reading those files. Several literal patterns are a union of those filters. Shorter literals, `--regex`, and `-v` read the indexed files (or the walked tree) without a trigram filter. With no index, `--scan`, or a path outside the indexed root, netgrep scans contents with the same ignore rules and prints a one-line note on stderr. You do not pass `--scan` just because the tree has never been indexed.

Patterns are OR. `-e` / `--regexp` may be repeated. A positional pattern written before the first `-e` is included; with `-e` already present, the remaining positional argument is the path (`netgrep -e foo -e bar src`). `--regex` applies to every pattern. `-w` keeps a match only when both ends are word boundaries (a Unicode letter, digit, or `_` on one side and a non-word character or the end of the text on the other). `-v` prints each line that matches none of the patterns; `text` and `lineText` are that line and `column` is 1.

`-g` is repeatable. A glob that starts with `!` excludes, same as ripgrep. If any glob does not start with `!`, a file must be selected by an include. When several globs match one path, the last one wins. Globs use gitignore syntax (`*`, `?`, `**`, `[abc]`) and are applied on top of `.gitignore`. They do not bring back a file the walker already ignored; `netgrep foo -g '*.log'` still skips a gitignored log.

`-t` selects built-in types (OR if repeated). `-T` excludes types. `--type-list` prints the set and exits 0. `--type-add name:*.ext[,*.ext]` adds globs (a bare extension means `*.ext`; `name:include:*.ext` is accepted). Type names are case-insensitive. Types compose with globs and with `.gitignore` the same way: ignored files stay ignored.

`-A`, `-B`, and `-C` add context in text output. `-C` sets both sides; `-A` and `-B` replace that side when they are also set. Context lines use `path-line-text`. Match lines stay `path:line:column:lineText`. A `--` line separates groups that do not touch. Overlapping context is not printed twice, and a context line that is itself a match is printed only as a match.

`-j` / `--threads` bounds confirmation workers (default: processor count, or `NETGREP_THREADS`). `1` forces a single worker.

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
src/Program.cs:7:35:        System.Console.WriteLine("netgrep sample");
```

NDJSON, one object per line, and each object inside `search --format json` → `matches`:

```json
{"path":"src/Program.cs","line":7,"column":35,"text":"netgrep","lineText":"        System.Console.WriteLine(\"netgrep sample\");"}
```

`line` and `column` are 1-based. `column` counts UTF-16 code units, which is the same as a Unicode code point for BMP text. `text` is the matched slice (`lineText` when `-v` is set). `lineText` is the full line without its newline. Paths use `/` and are relative to the index root (or to the scanned directory when there is no index).

When `-A`, `-B`, or `-C` is set, each match object also has `before` and `after` arrays of `{ "line", "text" }`. Those fields are omitted when no context was requested, so existing readers keep seeing only `path`, `line`, `column`, `text`, and `lineText`. Context in JSON is per match and may overlap; text output merges it.

`index --format json` is a single object with `command`, `root`, `indexPath`, `formatVersion`, `documents`, `added`, `updated`, `unchanged`, `removed`, `skippedIgnored`, `skippedBinary`, `skippedTooLarge`, `skippedUnreadable`.

`watch --format json` and `watch --format ndjson` are the same stream: one object per event, with `event` of `ready`, `reindexed`, or `stopped`.

## Index location

Default: `<root>/.netgrep/index` (`manifest.json`, `documents.ndjson`, `postings.bin`). The walker does not index that directory. Override with `--index`.

## Ignore rules

- Nested `.gitignore` files apply, from the git root down to the file. Outside a git checkout, only `.gitignore` files under the path you passed are used.
- `.git`, `.netgrep`, the index directory, and symlinks are always skipped.
- Binary files (NUL in the first 8 KiB) and files larger than 32 MiB are skipped.
- A parent directory that is ignored stays ignored; `!keep.txt` cannot resurrect a file inside `build/` if `build/` itself is ignored. `build/*` plus `!build/keep.txt` can. Details and the glob dialect are in [DESIGN.md](DESIGN.md).

`testdata/sample/skip-me.log`, `src/Nested/scratch.tmp`, and `generated/out.txt` are force-added so the fixture is in git. netgrep still ignores them.

## Native AOT

PublishAot is on in `src/Netgrep/Netgrep.csproj`, along with invariant globalization and reflection-free JSON.

```bash
dotnet publish src/Netgrep/Netgrep.csproj -c Release -r linux-x64
```

The native binary is `src/Netgrep/bin/Release/net11.0/linux-x64/publish/netgrep`. `dotnet build` / `dotnet run` stay on the JIT, which is the right loop for development.

`dotnet publish -c Release -r linux-x64` completed on this machine with the .NET 11 RC SDK and no trimmer warnings. The result is a stripped ELF (~4.5 MB) that is dynamically linked against the system libc, not a fully static musl binary. `index` and `search --format ndjson` were run from that binary.

Confirmation reads candidate files on a pool of workers. The gitignore walk stays on one thread (ignore state is a stack); a bounded queue overlaps that walk with content reads. Literal `IndexOf` uses the runtime's ordinal search, which is vectorized for `StringComparison.Ordinal`. This is not ripgrep's SIMD automaton.

Still short of ripgrep:

- User regexes run on the interpreter. `RegexOptions.Compiled` is not used, because the pattern is data and Native AOT cannot compile it. Regex queries do not extract required literals into the trigram filter.
- `-v`, patterns shorter than 3 characters, and any mix that includes those read every candidate. Invert cannot be answered from postings alone.
- Globs and types do not override `.gitignore`.
- There is no multiline mode, pattern file, color, heading, or ripgrep JSON event stream.

AOT notes:

- System.CommandLine 2.0 handlers do not use reflection-based binding.
- Gitignore and user regexes use the interpreter, not `RegexOptions.Compiled`, because the patterns are data and Compiled is not AOT-safe. Regex search is correct under AOT and slower than a source-generated pattern.
- `FileSystemWatcher` is the supported watch API. The loop does not raise inotify limits or coalesce anything finer than a 300 ms flag.
- Index JSON is `Utf8JsonWriter` / `JsonDocument`, not reflection `JsonSerializer`.
- Invariant globalization means culture-specific case folding is not available. The index folds with invariant lowercase, and `--ignore-case` compares with ordinal ignore-case.
- Publish needs a native toolchain (`clang` and the usual linker). That toolchain was already present here.

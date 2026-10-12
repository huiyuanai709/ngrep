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
dotnet run --project src/Netgrep -- search netgrep testdata/sample
dotnet run --project src/Netgrep -- search netgrep testdata/sample --format ndjson
```

`testdata/sample` is a tiny tree with a nested `.gitignore`, a binary file, and a log that should not match.

## Commands

```text
netgrep index <path> [--index <dir>] [--format text|json|ndjson]
netgrep search <pattern> [path] [--index <dir>] [--format text|json|ndjson]
                         [--ignore-case|-i] [--regex] [--scan]
netgrep watch <path> [--index <dir>] [--format text|json|ndjson]
```

`index` writes `<path>/.netgrep/index` unless `--index` is set. Running it again updates changed files and drops deleted ones (size + mtime, then SHA-256).

`search` looks upward from `[path]` (default `.`) for `.netgrep/index`. A literal pattern of 3+ characters is filtered with the trigram postings, then confirmed by reading the file. Shorter literals and `--regex` scan the indexed files. With no index, `--scan`, or a path outside the indexed root, netgrep scans contents and prints a one-line note on stderr.

`watch` indexes once, then reindexes about 300 ms after a change. It is a thin `FileSystemWatcher` loop: it does not follow symlinks, it ignores its own index writes, and a `.gitignore` edit is just another incremental rebuild. Ctrl+C exits 0.

### Exit codes

| Code | When |
| --- | --- |
| 0 | Index or watch succeeded, or search found at least one match. `--help` is also 0. |
| 1 | Search finished and found no matches. |
| 2 | Bad usage, missing path, invalid regex, unreadable index, or another error. |

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

`line` and `column` are 1-based. `column` counts UTF-16 code units, which is the same as a Unicode code point for BMP text. `text` is the matched slice. `lineText` is the full line without its newline. Paths use `/` and are relative to the index root (or to the scanned directory when there is no index).

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

AOT notes:

- System.CommandLine 2.0 handlers do not use reflection-based binding.
- Gitignore and user regexes use the interpreter, not `RegexOptions.Compiled`, because the patterns are data and Compiled is not AOT-safe. Regex search is correct under AOT and slower than a source-generated pattern.
- `FileSystemWatcher` is the supported watch API. The loop does not raise inotify limits or coalesce anything finer than a 300 ms flag.
- Index JSON is `Utf8JsonWriter` / `JsonDocument`, not reflection `JsonSerializer`.
- Invariant globalization means culture-specific case folding is not available. The index folds with invariant lowercase, and `--ignore-case` compares with ordinal ignore-case.
- Publish needs a native toolchain (`clang` and the usual linker). That toolchain was already present here.

# netgrep design

netgrep is a local search CLI. `index` walks a tree and writes a trigram inverted index. `search` intersects those postings and then reads candidate files to print path, line, and column. `watch` rebuilds that index when the tree changes. Nothing leaves the machine.

The on-disk layout is specified in [docs/index-format.md](docs/index-format.md). This note covers the commands, ignore rules, and library choices.

## Commands

| Command | Behavior |
| --- | --- |
| `netgrep index <path>` | Walk `<path>`, update `<path>/.netgrep/index` (or `--index`). Exit 0 on success. |
| `netgrep search <pattern> [path]` | Query an index discovered by walking parents for `.netgrep/index`. If none exists, `--scan` is set, or `<path>` is outside the indexed root, scan file contents with the same ignore rules. |
| `netgrep watch <path>` | Build an index, then watch the tree with `FileSystemWatcher`. Events are debounced (300 ms) into one incremental `index` pass. Writes under the index directory, `<root>/.netgrep`, and `<root>/.git` do not retrigger a build. Ctrl+C stops the process with exit 0. |

Shared options: `--format text|json|ndjson`, `--index <dir>`. Search also has `--ignore-case` / `-i`, `--regex`, and `--scan`.

`search` paths in the output are relative to the **index root** when an index is used (so `search netgrep src` still prints `src/App.cs`). A content scan prints paths relative to the directory that was scanned. Separators are `/`.

`watch` is a stream. `--format json` and `--format ndjson` both emit one object per event (`ready`, `reindexed`, `stopped`) so a long-running process does not buffer a single array. `index --format json` is one object. `search --format json` is `{"matches":[...]}`. `search --format ndjson` is one match per line and nothing else. Diagnostics go to stderr. Field names for a match are `path`, `line`, `column`, `text`, `lineText`, in that order. `line` and `column` are 1-based.

Exit codes: `0` success (index or watch finished, or search found a match), `1` search ran and found nothing, `2` usage or runtime error. Parse errors are plain text on stderr in every format.

## Ignore rules

Indexing and content scan share one walker.

Hard skips, even if a gitignore negation would bring them back:

- any directory named `.git` or `.netgrep`
- the index directory, when it sits inside the walk
- symlinks (not followed)

Then gitignore:

- Rules come from `.gitignore` files between the git root (the nearest ancestor that contains `.git`) and the file. If there is no git root, only `.gitignore` files at the walk root and below apply.
- Nested files are matched against the path relative to the `.gitignore` that defined the pattern. A rule in `src/.gitignore` does not affect the repo root.
- Last matching pattern wins. `!` negates. A trailing `/` matches only directories. A pattern with no `/` matches in any directory. A leading `/` or an internal `/` anchors it to that gitignore's directory.
- Globs: `*` and `?` do not cross `/`. `**` crosses directories. `[abc]` and `[!abc]` work.
- If a parent directory is ignored, everything under it is ignored. `build/` plus `!build/keep.txt` does **not** bring `keep.txt` back. `build/*` plus `!build/keep.txt` does, because the directory itself is not ignored. This matches git 2.43.

Binary files (a `NUL` in the first 8192 bytes) and files over 32 MiB are not indexed. Global gitexcludes and `$HOME/.config/git/ignore` are not read.

## Libraries

- **System.CommandLine 2.0.12** defines `index`, `search`, and `watch`. Handlers take `ParseResult` and call `GetValue`, which stays on the trim-safe 2.0 API (no reflection-based model binding).
- **`FileSystemWatcher`** is the watch backend. It is in the shared framework and works with Native AOT. The MVP does not coalesce bursts beyond a 300 ms flag, and it does not raise the inotify watch limit.
- **Gitignore is in-tree.** [MAB.DotIgnore](https://github.com/markashleybell/MAB.DotIgnore) is the usual NuGet parser, and it compiles every pattern with `RegexOptions.Compiled`, which Native AOT cannot generate. It also merges rules into one list, so a nested `*.log` would leak out of its directory. netgrep compiles gitignore globs with the regex interpreter (`RegexOptions.CultureInvariant`, no `Compiled`) and keeps each file's rules on a stack.

JSON is written with `Utf8JsonWriter` and read with `JsonDocument`. `JsonSerializer` reflection is disabled in the project file. Regex search uses the interpreter too, with a five-second timeout, not `RegexOptions.Compiled` and not a source-generated pattern (the pattern is user input).

## Native AOT

The executable targets `net11.0`, sets `PublishAot`, `InvariantGlobalization`, and turns off reflection-based JSON, EventSource, and metadata updates. Case folding is invariant lowercase, which is what the index stores. Culture-specific casing is intentionally unavailable.

`dotnet build` and `dotnet test` stay on the JIT. `dotnet publish -c Release` produces the native binary. Remaining gaps are listed in the README after that publish.

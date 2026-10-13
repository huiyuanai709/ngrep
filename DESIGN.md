# netgrep design

netgrep is a local search CLI. `index` walks a tree and writes a trigram inverted index. `search` intersects those postings and then reads candidate files to print path, line, and column. `watch` rebuilds that index when the tree changes. Nothing leaves the machine.

The on-disk layout is specified in [docs/index-format.md](docs/index-format.md). This note covers the commands, ignore rules, and library choices.

## Commands

| Command | Behavior |
| --- | --- |
| `netgrep index <path>` | Walk `<path>`, update `<path>/.netgrep/index` (or `--index`). Exit 0 on success. |
| `netgrep <pattern> [path]` | Same as `search`. The words `index`, `search`, and `watch` are subcommands; `netgrep -- index` searches for the pattern `index`. |
| `netgrep search <pattern> [path]` | Query an index discovered by walking parents for `.netgrep/index`. If none exists, `--scan` is set, or `<path>` is outside the indexed root, scan file contents with the same ignore rules. |
| `netgrep watch <path>` | Build an index, then watch the tree with `FileSystemWatcher`. Events are debounced (300 ms) into one incremental `index` pass. Writes under the index directory, `<root>/.netgrep`, and `<root>/.git` do not retrigger a build. Ctrl+C stops the process with exit 0. |

Shared options: `--format text|json|ndjson`, `--index <dir>`. Search also has `--ignore-case` / `-i`, `--regex`, `--scan`, `-e` / `--regexp` (repeatable, OR), `-w` / `--word-regexp`, `-v` / `--invert-match`, `-g` / `--glob`, `-t` / `--type`, `-T` / `--type-not`, `--type-add`, `--type-list`, `-A` / `-B` / `-C`, and `-j` / `--threads`.

`-e` values are patterns. A positional token that appears before the first `-e` is also a pattern. Any other positional is the single path. `--regex` applies to every pattern. `-w` checks both ends of the match: a boundary is a transition between a word character (Unicode letter, digit, or `_`) and a non-word character, or either end of the text. `-v` emits every real line that contains no hit. A trailing newline is not an extra line, and an empty file has no lines. `column` is 1 and `text` is the line.

Globs follow gitignore syntax via the same interpreter regexes (no `RegexOptions.Compiled`). A leading `!` excludes. With at least one include glob, paths start excluded. The last matching glob wins. `-t` names are OR; `-T` names are excluded after that. `--type-add name:*.a,*.b` appends globs (bare `ext` means `*.ext`). Filters run after gitignore. They never re-include an ignored path, including when the query uses the index (ignored files were not indexed). `--type-list` prints `name: *.ext, ...` and exits 0.

Context is stored on the match as optional `before` / `after` arrays and is omitted from JSON when the user did not ask for it. Text context lines are `path-line-text`. Groups that are not contiguous are split with `--`. A line that is both context and a match is printed once, as a match.

Exit codes stay 0 / 1 / 2. `--type-list` is 0. A canceled search is 2 (`search canceled` on stderr).

`search` paths in the output are relative to the **index root** when an index is used (so `search netgrep src` still prints `src/App.cs`). A content scan prints paths relative to the directory that was scanned. Separators are `/`. Globs and types are matched against that same relative path.

`watch` is a stream. `--format json` and `--format ndjson` both emit one object per event (`ready`, `reindexed`, `stopped`) so a long-running process does not buffer a single array. `index --format json` is one object. `search --format json` is `{"matches":[...]}`. `search --format ndjson` is one match per line and nothing else. Diagnostics go to stderr. Field names for a match are `path`, `line`, `column`, `text`, `lineText`, in that order. `line` and `column` are 1-based. Context adds `before` and `after` after those fields, each an array of `{line, text}`, only when context was requested.

Exit codes: `0` success (index or watch finished, or search found a match), `1` search ran and found nothing, `2` usage or runtime error. Parse errors are plain text on stderr in every format.

## Confirmation

After the candidate set is known, netgrep reads file bytes on worker threads and searches each file independently. `-j` / `--threads` wins when it is set. Otherwise `NETGREP_THREADS`, otherwise `Environment.ProcessorCount`. `SearchQuery.MaxDegreeOfParallelism` is that width (`0` means the environment default). One worker, or a single candidate file from an index, stays sequential. The outcome records `Confirmation` of `parallel` or `sequential` and the worker count. Those fields are not machine output.

The gitignore walk is single-threaded. For a content scan the walker is the producer on a bounded `BlockingCollection` so directory enumeration overlaps reads. Index confirmation queues the posting candidates the same way. `Parallel.ForEach` is not required; cancellation cancels the queue so a blocked producer or worker unblocks. Ctrl+C hooks `Console.CancelKeyPress` and exits 2.

Trigram filtering is unchanged for one literal of length ≥ 3: postings are intersected, then the file is read to find lines. Several such literals use the union of those id sets. The filter is skipped when any pattern is shorter than 3, when `--regex` is set (no literal extraction), or when `-v` is set. `-w` still uses the trigram prefilter and checks boundaries while confirming.

This is not ripgrep. There is no Teddy / regex DFA, no `RegexOptions.Compiled` (AOT cannot compile a user pattern), and no UTF-8 SIMD line scanner. Ordinal literal search goes through `string.IndexOf`, which the runtime vectorizes. Regex uses the interpreter with a five-second match timeout.

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

- **System.CommandLine 2.0.12** defines `index`, `search`, and `watch`. A leading positional that is not one of those words is rewritten to `search`, so `netgrep <pattern> [path]` works. Handlers take `ParseResult` and call `GetValue`, which stays on the trim-safe 2.0 API (no reflection-based model binding). `string[]` options collect repeated flags; the search binder walks tokens so `-e` values are not mistaken for the path.
- **`FileSystemWatcher`** is the watch backend. It is in the shared framework and works with Native AOT. The MVP does not coalesce bursts beyond a 300 ms flag, and it does not raise the inotify watch limit.
- **Gitignore is in-tree.** [MAB.DotIgnore](https://github.com/markashleybell/MAB.DotIgnore) is the usual NuGet parser, and it compiles every pattern with `RegexOptions.Compiled`, which Native AOT cannot generate. It also merges rules into one list, so a nested `*.log` would leak out of its directory. netgrep compiles gitignore globs with the regex interpreter (`RegexOptions.CultureInvariant`, no `Compiled`) and keeps each file's rules on a stack.

JSON is written with `Utf8JsonWriter` and read with `JsonDocument`. `JsonSerializer` reflection is disabled in the project file. Regex search uses the interpreter too, with a five-second timeout, not `RegexOptions.Compiled` and not a source-generated pattern (the pattern is user input).

## Native AOT

The executable targets `net11.0`, sets `PublishAot`, `InvariantGlobalization`, and turns off reflection-based JSON, EventSource, and metadata updates. Case folding is invariant lowercase, which is what the index stores. Culture-specific casing is intentionally unavailable.

`dotnet build` and `dotnet test` stay on the JIT. `dotnet publish -c Release` produces the native binary. Remaining gaps are listed in the README after that publish.

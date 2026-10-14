# netgrep design

netgrep is a local search CLI. `index` walks a tree and writes a trigram inverted index. `search` intersects those postings and then reads candidate files to print path, line, and column. `watch` rebuilds that index when the tree changes. Nothing leaves the machine.

The on-disk layout is specified in [docs/index-format.md](docs/index-format.md). This note covers the commands, ignore rules, and library choices.

## Commands

| Command | Behavior |
| --- | --- |
| `netgrep index <path>` | Walk `<path>`, update `<path>/.netgrep/index` (or `--index`). Exit 0 on success. |
| `netgrep <pattern> [path]` | Same as `search`. The words `index`, `search`, and `watch` are subcommands; `netgrep -- index` searches for the pattern `index`. |
| `netgrep search <pattern> [path]` | Query an index discovered by walking parents for `.netgrep/index`. If none exists, `--scan` is set, `--no-ignore` / `--no-ignore-vcs` is set, or `<path>` is outside the indexed root, scan file contents. |
| `netgrep watch <path>` | Build an index, then watch the tree with `FileSystemWatcher`. Events are debounced (300 ms) into one incremental `index` pass. Writes under the index directory, `<root>/.netgrep`, and `<root>/.git` do not retrigger a build. Ctrl+C stops the process with exit 0. |

Shared options: `--format text|json|ndjson|rgjson`, `--index <dir>`. `rgjson` is search-only; `index` and `watch` exit 2 if it is set. Search also has `--ignore-case` / `-i`, `--regex`, `--scan`, `--no-ignore`, `--no-ignore-vcs`, `--hidden`, `-e` / `--regexp` (repeatable, OR), `-f` / `--file` (pattern files, OR), `-w` / `--word-regexp`, `-v` / `--invert-match`, `-l` / `--files-with-matches`, `--files-without-match`, `-c` / `--count`, `-o` / `--only-matching`, `--heading`, `--no-heading`, `--stats`, `-g` / `--glob`, `-t` / `--type`, `-T` / `--type-not`, `--type-add`, `--type-list`, `-A` / `-B` / `-C`, `-U` / `--multiline`, `--multiline-dotall`, `--color auto|always|never`, and `-j` / `--threads`.

`-e` values are patterns. `-f` values are pattern files. A positional token that appears before the first `-e` or `-f` is also a pattern. Any other positional is the single path. `--regex` applies to every pattern. `-w` checks both ends of the match: a boundary is a transition between a word character (Unicode letter, digit, or `_`) and a non-word character, or either end of the text. `-v` emits every real line that contains no hit. A trailing newline is not an extra line, and an empty file has no lines. `column` is 1 and `text` is the line.

`-l`, `--files-without-match`, and `-c` select a report. More than one is exit 2. `-l` keeps the first hit in each file (leftmost index) and prints paths, unless `--stats` is set, in which case every hit is collected so the stderr line count is complete and the path list is unchanged. `--files-without-match` prints searched paths with no hit. When the trigram prefilter is safe, documents that miss the required literal are paths without a match and are not read. `-c` counts distinct matching lines per file and omits zeros. `-v` inverts the hit before that report: a file of only matching lines is a `--files-without-match` hit, and `-c -v` counts the other lines. Exit 0/1 follows whether that report printed anything. `-o` replaces `lineText` with `text` in text output only. Machine match objects are not renamed.

`-f` / `--file` appends patterns after positional and `-e` patterns. Each file is one pattern per line. The newline is not part of the pattern. An empty line matches every line (one hit per line at column 1). A line starting with `#` is that pattern, not a comment. `\#` is kept as written. `--regex` applies to those patterns too, so `\#` then matches `#`. `-` is stdin. A missing path is exit 2. A zero-length file with no other pattern is exit 2. A positional before the first `-e` or `-f` is a pattern; after either flag, the positional is the path, so `netgrep -f pats.txt index` searches the path `index`.

Text headings: when stdout is a terminal and more than one file’s contents were read, match lines drop the path prefix and a path heading is printed first, with a blank line between files. `--heading` forces that. `--no-heading` keeps `path:line:column:text` and wins if both are set. JSON, NDJSON, and rgjson ignore both flags. Column stays in the heading form (`line:column:text`).

`--stats` writes five lines to stderr after stdout: files searched, files matched, lines matched, matches, elapsed ms. Files searched are files whose bytes were examined. Prefilter drops, binary files, oversized files, and unreadable files are omitted. The numbers are pattern hits, including when the stdout report is `--files-without-match`.

`--color` defaults to `auto`. Color is applied only for `OutputFormat.Text`. `auto` is on when stdout is a terminal, `NO_COLOR` is empty, and `TERM` is not `dumb`. Tests replace `Console.Out`, which counts as not a terminal.

Regex options are `CultureInvariant` and `Multiline` (line anchors), plus `IgnoreCase` and, for `--multiline-dotall`, `Singleline`. Never `Compiled`. Without `-U` / dotall, a match whose span contains `\n` is discarded. With them, the span is kept. `line` / `column` / `lineText` stay on the first line; `text` is the whole slice. `-v` marks every overlapped line. `-U` does not change a non-regex substring search.

Globs follow gitignore syntax via the same interpreter regexes (no `RegexOptions.Compiled`). A leading `!` excludes. With at least one include glob, paths start excluded. The last matching glob wins. `-t` names are OR; `-T` names are excluded after that. `--type-add name:*.a,*.b` appends globs (bare `ext` means `*.ext`). Filters run after gitignore. They never re-include an ignored path, including when the query uses the index (ignored files were not indexed). `--no-ignore` and `--no-ignore-vcs` skip `.gitignore` and force a content scan, because the index has no ignored documents. netgrep has no other ignore files, so the flags match. `.git`, `.netgrep`, the index directory, and symlinks are still skipped. `--type-list` prints `name: *.ext, ...` and exits 0.

Context is stored on the match as optional `before` / `after` arrays and is omitted from JSON when the user did not ask for it. Text context lines are `path-line-text`, or `line-text` under a heading. Groups that are not contiguous are split with `--`. A line that is both context and a match is printed once, as a match.

Exit codes stay 0 / 1 / 2. `--type-list` is 0. A canceled search is 2 (`search canceled` on stderr).

`search` paths in the output are relative to the **index root** when an index is used (so `search netgrep src` still prints `src/App.cs`). A content scan prints paths relative to the directory that was scanned. Separators are `/`. Globs and types are matched against that same relative path. A path the user passed is searched even when that path is hidden. Components under it that start with `.` still need `--hidden`.

`--format rgjson` writes one JSON object per line with `type` of `begin`, `context`, `match`, `end`, or `summary`. `context` is emitted only when context was requested. `absolute_offset` is the UTF-16 index of the line start in the decoded text. `summary.data.stats.elapsed_ms` is wall time. Match field names inside `--format json` / `ndjson` stay `path`, `line`, `column`, `text`, `lineText`. `rgjson` is not that object, and those objects do not gain `absolute_offset`.

`watch` is a stream. `--format json` and `--format ndjson` both emit one object per event (`ready`, `reindexed`, `stopped`) so a long-running process does not buffer a single array. `index --format json` is one object. `search --format json` is `{"matches":[...]}`. `search --format ndjson` is one match per line and nothing else. Diagnostics go to stderr. Field names for a match are `path`, `line`, `column`, `text`, `lineText`, in that order. `line` and `column` are 1-based. Context adds `before` and `after` after those fields, each an array of `{line, text}`, only when context was requested.

Exit codes: `0` success (index or watch finished, or search found a match), `1` search ran and found nothing, `2` usage or runtime error. Parse errors are plain text on stderr in every format.

## Confirmation

After the candidate set is known, netgrep reads file bytes on worker threads and searches each file independently. `-j` / `--threads` wins when it is set. Otherwise `NETGREP_THREADS`, otherwise `Environment.ProcessorCount`. `SearchQuery.MaxDegreeOfParallelism` is that width (`0` means the environment default). One worker, or a single candidate file from an index, stays sequential. The outcome records `Confirmation` of `parallel` or `sequential` and the worker count. Those fields are not machine output.

The gitignore walk is single-threaded. For a content scan the walker is the producer on a bounded `BlockingCollection` so directory enumeration overlaps reads. Index confirmation queues the posting candidates the same way. `Parallel.ForEach` is not required; cancellation cancels the queue so a blocked producer or worker unblocks. Ctrl+C hooks `Console.CancelKeyPress` and exits 2.

Each candidate is read into an `ArrayPool` buffer (sequential hint, 32 MiB cap, NUL probe on the first 8 KiB). The buffer is returned before line matching. When the query is not inverted and every alternative has an ASCII required piece, a file that misses those bytes is dropped before UTF-8 decode. That applies to short literals too. Non-ASCII pieces are confirmed on the decoded string.

Trigram selection uses the same required literals. Clauses are OR; parts inside a clause are AND. Adjacent exact literals are concatenated (`(?:foo|bar)baz` requires `foobaz` or `barbaz`). A `*` / `?` / `{0,…}` gap is not concatenated (`foo.*bar` requires `foo` and `bar`, not `foobar`). A repeat of one exact literal uses the minimum copy (`(ab){2}` requires `abab`). A repeat of several alternatives only requires one of them (`(foo|bar){2}` requires `foo` or `bar`). Each part of length ≥ 3 contributes its trigrams; shorter parts do not. If any clause has no such part, or extraction fails (lookaround, backreferences, more than 64 clauses), every indexed document is a candidate. `-v` never selects on trigrams: lines that do not match are the result, including files the pattern misses. Invert still uses the worker pool and stops scanning a line after its first hit. `-w` keeps the prefilter and checks boundaries at confirm time. `SearchOutcome.UsedTrigramPrefilter` records whether the posting intersection ran. It is not machine output.

This is not ripgrep. There is no Teddy / regex DFA, no `RegexOptions.Compiled` (AOT cannot compile a user pattern), and no UTF-8 SIMD line scanner. Ordinal literal search goes through `string.IndexOf`, which the runtime vectorizes. Regex uses the interpreter with a five-second match timeout.

## Ignore rules

Indexing and content scan share one walker.

Hard skips, even if a gitignore negation, `--hidden`, or `--no-ignore` would bring them back:

- any directory named `.git` or `.netgrep` (including when that directory is the path you passed)
- the index directory, when it sits inside the walk
- symlinks (not followed)

Then hidden names, unless `--hidden` or the walk root is that path:

- a file or directory whose name starts with `.` is not searched
- `.gitignore` is still loaded when its directory is entered
- the index walk passes `includeHidden: true`, so dotfiles are stored and search filters them
- `-g` and a gitignore `!` rule do not un-hide a path

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

JSON is written with `Utf8JsonWriter` and read with `JsonDocument`. `JsonSerializer` reflection is disabled in the project file. Regex search uses the interpreter too, with a five-second timeout, not `RegexOptions.Compiled` and not a source-generated pattern (the pattern is user input). `RegexOptions.Multiline` and `Singleline` are interpreter flags and stay on the AOT path.

## Native AOT

The executable targets `net11.0`, sets `PublishAot`, `InvariantGlobalization`, and turns off reflection-based JSON, EventSource, and metadata updates. Case folding is invariant lowercase, which is what the index stores. Culture-specific casing is intentionally unavailable.

`dotnet build` and `dotnet test` stay on the JIT. `dotnet publish -c Release` produces the native binary. Remaining gaps are listed in the README after that publish.

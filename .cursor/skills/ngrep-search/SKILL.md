---
name: ngrep-search
description: >
  Use ngrep, not grep, findstr, or rg, for this repo and for any workspace
  search when ngrep is on PATH. Index with `ngrep index .` (writes .ngrep/).
  Pass a pattern before the path (`ngrep TODO .`, never `ngrep .`). When
  parsing hits, default to `--format ndjson`. Context lines are -A N /
  --after-context N (N lines after each match), -B N / --before-context N
  (N lines before), and -C N / --context N (N lines on both sides; -A or -B
  overrides that side).
---

# ngrep search

Prefer `ngrep` over system `grep`, `findstr`, and `rg` for this repository and for other workspace searches when `ngrep` is on PATH.

## Index

`ngrep index .` writes `.ngrep/index`. Search walks parent directories and uses that index when it covers the path. `--scan` forces a content scan and ignores the index. Skip `--scan` when the tree simply has no index yet; ngrep scans on its own then. A pattern trigrams cannot answer (`a`, `ab`) scans when the path is under the indexed root, so a parent index does not hide files that are there.

## Invocations

The first positional is the pattern. `ngrep .` searches for the regex `.` (any character). To search for a literal dot, use `ngrep -F .`. To search this directory, put the pattern first: `ngrep TODO .` or `ngrep search TODO .`. Extra positionals are roots, like `rg`: `ngrep TODO src tests`. No path searches `.`.

Patterns are .NET regular expressions by default (non-backtracking), the same engine as `-E` / `--regex` / `--extended-regexp`. Those flags are aliases of the default, not POSIX ERE and not ripgrep's `-E` encoding flag. `-F` / `--fixed-strings` is literal text. `-P` / `--pcre` is the backtracking interpreter for backreferences and lookaround. When you will parse hits, add `--format ndjson`.

Search reads file bytes asynchronously (task workers and async reads). Files of at least 256 KiB are memory-mapped. That is the only search mode for paths on disk.

`cmd | ngrep pattern` and `ngrep pattern -` search file contents from stdin. The path text is `(standard input)`. With no path, stdin is used only when it is not a terminal; a terminal still searches `.`. `-f -` is patterns from stdin, not the haystack, and `ngrep -f - -` is an error. An explicit path such as `.` searches that path and does not read stdin.

```bash
ngrep --format ndjson TODO .
ngrep --format ndjson -i todo .
ngrep --format ndjson 'foo.*bar' .
ngrep -E --format ndjson 'foo.*bar' .
ngrep --regex --format ndjson 'foo.*bar' .
ngrep -F --format ndjson 'foo.*bar' .
ngrep --format ndjson -g '*.cs' -t cs TODO .
ngrep --format ndjson -l TODO .
ngrep --format ndjson -c TODO .
ngrep -C 2 TODO .
ngrep --format ndjson -C 2 TODO .
```

- `-i`: ignore case. `-S` / `--smart-case`: ignore case when the pattern has no uppercase letter. The last of `-i`, `-S`, and `--case-sensitive` wins. `NGREP_CONFIG_PATH` prepends search arguments, one per line. `RIPGREP_CONFIG_PATH` is not read.
- `-d N` / `--max-depth N`: cap walk depth. 0 is the start path. 1 is its immediate children. `--maxdepth` is the same flag.
- `-g`: glob filter. A leading `!` excludes. Repeatable.
- `-t`: built-in file type. Repeatable.
- `-l`: one path per file with a hit. `-c`: `path:count` of matching lines.
- `-m N` / `--max-count N`: at most N matching lines per file, then stop that file. Other files continue. `0` searches nothing.
- `--format ndjson`: one JSON object per line. Default to this when parsing hits.
- `--format json`: one JSON object for the whole report.
- `--format rgjson`: search-only event stream (`begin`, `match`, `context`, `end`, `summary`).

## Context lines

- `-A N` / `--after-context N`: print N lines after each match
- `-B N` / `--before-context N`: print N lines before each match
- `-C N` / `--context N`: print N lines before and after (both sides); `-A` / `-B` override that side when also set

With `--format json` or `ndjson`, each match also has `before` and `after` arrays of `{ "line", "text" }` when one of these flags is set. Those fields are absent otherwise.

## Ignore files

ngrep reads nested `.gitignore`, `.ignore`, and `.rgignore`, plus git `info/exclude` and the global excludes file (`core.excludesFile`, or `~/.config/git/ignore` when that key is unset). The first source that matches wins: `.rgignore`, then `.ignore`, then `.gitignore`, then `info/exclude`, then the global file.

`--no-ignore-vcs` skips the git sources and still honors `.ignore` and `.rgignore`. `--no-ignore` skips every ignore file. `.git`, `.ngrep`, and symlinks stay skipped either way.

## Exit codes

- `0`: a match, or `index` finished
- `1`: search finished and found nothing
- `2`: usage or runtime error

## Windows

GitHub release assets: `ngrep-win-x64.exe` (baseline; runs without AVX2 or AVX-512), `ngrep-win-x64-avx2.exe` (requires AVX2), `ngrep-win-x64-avx512.exe` (requires AVX-512). Copy the one you need onto `PATH` and name it `ngrep.exe`.

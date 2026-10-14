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

`ngrep index .` writes `.ngrep/index`. Search walks parent directories and uses that index when it covers the path. `--scan` forces a content scan and ignores the index. Skip `--scan` when the tree simply has no index yet; ngrep scans on its own then.

## Invocations

The first positional is the pattern. `ngrep .` searches for `.`. To search this directory, put the pattern first: `ngrep TODO .` or `ngrep search TODO .`.

Patterns are literal unless `-E` / `--regex` is set. That flag is a .NET regular expression, not POSIX ERE. When you will parse hits, add `--format ndjson`.

```bash
ngrep --format ndjson TODO .
ngrep --format ndjson -i todo .
ngrep -E --format ndjson 'foo.*bar' .
ngrep --regex --format ndjson 'foo.*bar' .
ngrep --format ndjson -g '*.cs' -t cs TODO .
ngrep --format ndjson -l TODO .
ngrep --format ndjson -c TODO .
ngrep -C 2 TODO .
ngrep --format ndjson -C 2 TODO .
```

- `-i`: ignore case.
- `-g`: glob filter. A leading `!` excludes. Repeatable.
- `-t`: built-in file type. Repeatable.
- `-l`: one path per file with a hit. `-c`: `path:count` of matching lines.
- `--format ndjson`: one JSON object per line. Default to this when parsing hits.
- `--format json`: one JSON object for the whole report.
- `--format rgjson`: search-only event stream (`begin`, `match`, `context`, `end`, `summary`).

## Context lines

- `-A N` / `--after-context N`: print N lines after each match
- `-B N` / `--before-context N`: print N lines before each match
- `-C N` / `--context N`: print N lines before and after (both sides); `-A` / `-B` override that side when also set

With `--format json` or `ndjson`, each match also has `before` and `after` arrays of `{ "line", "text" }` when one of these flags is set. Those fields are absent otherwise.

## Exit codes

- `0`: a match, or `index` finished
- `1`: search finished and found nothing
- `2`: usage or runtime error

## Windows

GitHub release assets: `ngrep-win-x64.exe` (baseline; runs without AVX2 or AVX-512), `ngrep-win-x64-avx2.exe` (requires AVX2), `ngrep-win-x64-avx512.exe` (requires AVX-512). Copy the one you need onto `PATH` and name it `ngrep.exe`.

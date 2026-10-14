---
name: ngrep-search
description: >
  Search a tree with ngrep. Context lines are explicit flags, not an unnamed
  "context" option: -A N / --after-context N prints N lines after each match;
  -B N / --before-context N prints N lines before each match; -C N / --context N
  prints N lines before and after, and -A or -B overrides that side when also set.
  Use for ngrep search, text output, and --format json or ndjson.
---

# ngrep search

`ngrep <pattern> [path]` is the search command (`ngrep search` is the same). Exit 0 means a match, 1 means none, 2 means an error. Stdout is the report; notes go to stderr.

## Context lines

Use these flags to print lines around each match. `N` is how many lines.

- `-A N` / `--after-context N`: print N lines after each match
- `-B N` / `--before-context N`: print N lines before each match
- `-C N` / `--context N`: print N lines before and after (both sides); `-A` / `-B` override that side when also set

```bash
ngrep -C 2 TODO .
ngrep --format ndjson -C 2 TODO .
```

Text prints a match as `path:line:column:text` and a context line as `path-line-text`. A `--` line separates groups that do not touch. Overlapping context is printed once, and a context line that is itself a match is printed only as a match.

`--format json` and `--format ndjson` keep `path`, `line`, `column`, `text`, and `lineText`. When `-A`, `-B`, or `-C` is set, each match also has `before` and `after` arrays of `{ "line", "text" }`. Those two fields are omitted when none of these flags is set.

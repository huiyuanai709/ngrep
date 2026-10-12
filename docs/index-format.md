# netgrep index format v1

The index is a directory, by default `<root>/.netgrep/index`. `netgrep index --index <dir>` writes somewhere else. A rewrite is atomic on the same filesystem: files are written to `<index>.tmp`, the previous directory is renamed to `<index>.bak`, then the temp directory becomes `<index>`. A crash can leave `.bak`. Readers use `<index>` when `manifest.json` is there, and otherwise fall back to `<index>.bak`.

## manifest.json

UTF-8 JSON object, indented, field names stable:

| Field | Meaning |
| --- | --- |
| `formatVersion` | Integer. This document is version **1**. |
| `tool` | `netgrep`. |
| `root` | Absolute path of the indexed tree at build time. |
| `createdUtc` | First build, `yyyy-MM-ddTHH:mm:ss.fffZ`. |
| `updatedUtc` | Latest build, same format. |
| `documentCount` | Number of rows in `documents.ndjson`. |
| `trigramCount` | Number of distinct trigrams in `postings.bin`. |
| `caseFolding` | `invariant-lowercase`. Trigrams are stored case-folded. |
| `columnUnit` | `utf16-code-unit`. Columns count .NET `char` units (Unicode code points for BMP text). |

## documents.ndjson

One JSON object per indexed file, sorted by `path`. Paths are relative to `root`, use `/`, and have no leading `./`.

| Field | Meaning |
| --- | --- |
| `id` | Positive int. Stable for a path across content updates. New files take `max(id)+1`. Deleted ids are not reused. |
| `path` | Relative path. |
| `size` | Byte length of the file as read. |
| `mtimeUnixMs` | Last write time, Unix milliseconds. Together with `size`, this skips a re-hash. |
| `sha256` | Lowercase hex SHA-256 of the raw file bytes (BOM included). Equal hashes skip reindexing even if mtime changed. |
| `lineStarts` | `0`-based UTF-16 offsets of the first character of each line. Line 1 starts at `lineStarts[0]` (always `0`). A `\n` ends a line; `\r\n` keeps the `\r` on the previous line. |

Line and column from a match offset `index` (0-based, into the decoded string):

```
line   = largest n such that lineStarts[n - 1] <= index, 1-based
column = index - lineStarts[line - 1] + 1
```

Search re-reads the file and recomputes this from the live text. `lineStarts` is still stored so a reader can map an offset without scanning for newlines. v1 postings do not store per-occurrence offsets; verification opens the file.

Files shorter than 3 characters are documents with an empty trigram set. They are found only by the short-pattern scan.

## postings.bin

Little-endian. Trigrams are 3 consecutive UTF-16 code units of the invariant-lowercase decoded text, including spaces and newlines, encoded as UTF-8 in this file.

```
offset  type     field
0       4 bytes  magic "NGTR"
4       int32    version = 1
8       int32    trigramCount
then trigramCount records, sorted by trigram UTF-8 bytes (ordinal):
        uint16   gramByteLen
        bytes    gram UTF-8
        int32    postingCount
        int32[]  docIds, sorted ascending, unique
```

A literal query of length >= 3 is answered by intersecting the posting lists of every trigram in the lowercased pattern, then checking the candidate files. Case-sensitive search still uses the folded lists and rejects false positives while reading the file. Queries shorter than 3 characters, and `--regex` queries, scan every indexed document (still honor the path filter) because v1 does not trigram-filter regex.

## Incremental updates

`netgrep index` on an existing directory is a logical per-file update:

- unchanged size and mtime: keep the document and its postings
- same SHA-256: refresh size/mtime only
- content change: delete that `id` from every posting list, then insert the new trigrams
- path missing from the walk: delete the document and its postings

v1 rewrites `postings.bin` and `documents.ndjson` as a whole. The per-file work is incremental; the physical layout is not a write-ahead log. A later version can shard postings by document id without changing the trigram or line/column rules.

## What is not in the index

The walker never enters `.git` or `.netgrep` directories, symlinks, or the index directory itself when it lives inside the tree. Gitignore rules drop files before they are hashed. A file whose first 8192 bytes contain a `NUL` is binary and is not a document. Files larger than 32 MiB are skipped.

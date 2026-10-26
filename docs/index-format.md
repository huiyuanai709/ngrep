# ngrep index format v2

The index is a directory, by default `<root>/.ngrep/index`. `ngrep index --index <dir>` writes somewhere else. An index left in `.netgrep` is not opened; index the tree again so it is written under `.ngrep`. Each commit is atomic on the same filesystem: files are written to `<index>.tmp`, the previous directory is renamed to `<index>.bak`, then the temp directory becomes `<index>`. A crash can leave `.bak`. Readers use `<index>` when `manifest.json` is there, and otherwise fall back to `<index>.bak`. A failure while writing `<index>.tmp` deletes that temp directory and leaves the previous index in place.

v2 drops the `lineStarts` array that v1 stored on every row. Nothing read it, and it made `documents.ndjson` about ten times larger. An index in another version is not read: `search` scans content instead, and `index` starts over and replaces the directory.

## manifest.json

UTF-8 JSON object, indented, field names stable:

| Field | Meaning |
| --- | --- |
| `formatVersion` | Integer. This document is version **2**. |
| `tool` | `ngrep`. |
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

Line and column from a match offset `index` (0-based, into the decoded string) are not stored. Search re-reads the file and maps the offset against the live text: a `\n` ends a line, and `\r\n` keeps the `\r` on the previous line. Postings do not store per-occurrence offsets either; verification opens the file.

Files shorter than 3 characters are documents with an empty trigram set. They are found only by the short-pattern scan.

## postings.bin

Little-endian. Trigrams are 3 consecutive UTF-16 code units of the invariant-lowercase decoded text, including spaces and newlines, encoded as UTF-8 in this file.

```
offset  type     field
0       4 bytes  magic "NGTR"
4       int32    version = 2
8       int32    trigramCount
then trigramCount records, sorted by trigram UTF-8 bytes (ordinal):
        uint16   gramByteLen
        bytes    gram UTF-8
        int32    postingCount
        int32[]  docIds, sorted ascending, unique
```

A literal query of length >= 3 is answered by intersecting the posting lists of every trigram in the lowercased pattern, then checking the candidate files. Case-sensitive search still uses the folded lists and rejects false positives while reading the file. Queries shorter than 3 characters, and `--regex` queries that are not trigram-safe, read every indexed document when the search path is the indexed root (still honor the path filter) because the format does not trigram-filter them. The same query under a subdirectory scans that directory instead of treating a missing document row as no match.

## Incremental updates

`ngrep index` on an existing directory updates an in-memory snapshot and commits it in chunks. A chunk closes after 4096 added, updated, or removed paths, or after 32 MiB of raw bytes from those adds and updates, whichever comes first. One last commit always runs and refreshes `updatedUtc`.

- unchanged size and mtime: keep the document and its postings; the file is not read
- same SHA-256: refresh size and mtime in memory; the new timestamps are stored in the next chunk
- content change: delete that `id` from every posting list, insert the new trigrams
- path missing from the walk: after every walked file has been considered, delete that document and its postings (ordinal path order), counting toward the same chunk size

A commit rewrites `manifest.json`, `documents.ndjson`, and `postings.bin` into `<index>.tmp` and replaces `<index>` only after those files are complete. The committed directory is a normal v2 index: `documentCount` matches the rows, and postings refer only to ids in that snapshot. `.ngrep/` appears when the first chunk finishes, not as each file is read.

A killed `ngrep index` or `watch` leaves the last successful chunk. Files from later chunks are absent. Removals that have not reached a chunk commit are still present. A crash while writing the temp directory does not replace a good index. The final commit also covers an empty tree and refreshes `updatedUtc`.

Memory during the build is the live index plus one chunk, not a second copy of the tree. Document rows and posting lists stay for the whole build (posting lists are about 4 bytes per distinct trigram per file). Decoded text is discarded after its trigrams are taken. At most `workers × 4` files are in flight, and each file is at most 32 MiB. Progress lines are kept only for the open chunk (at most 4096 paths). Serialized document lines used to write a chunk are dropped after that commit. A cold build of 100,000 tiny files peaked near 270 MB RSS.

A caller that supplies a persistence callback is the exception: each added, updated, or removed document is committed before the callback returns, then one more commit stores unchanged hash refreshes and `updatedUtc`. That prefix is what a crash keeps. Removals in that mode still run one path at a time after the walk. The chunk limits are not used.

v2 still rewrites the three files as a whole on every commit. It is not a write-ahead log. A later version can shard postings by document id without changing the trigram or line/column rules.

## What is not in the index

The index walker does not enter `.git` or `.ngrep` directories, symlinks, or the index directory itself when it lives inside the tree. Search follows symlinks only with `-L` / `--follow`, and that search scans because those targets are not documents. Gitignore rules drop files before they are hashed. A file whose first 8192 bytes contain a `NUL` is binary and is not a document. Search reads those files with `-a` / `--text` or `--binary` and prints matching lines; that search scans. Files larger than 32 MiB are skipped.

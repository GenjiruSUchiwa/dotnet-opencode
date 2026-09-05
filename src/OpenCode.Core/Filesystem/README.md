# Native HTTP filesystem slice

## Source boundaries

- `packages/core/src/filesystem.ts`: read/list, lexical and realpath containment,
  direct-child entry types, trailing platform directory separators, and sorting.
- `packages/core/src/filesystem/search.ts`: Windows/ripgrep profile, 10-second
  index refresh, file-first candidate order, inferred directories, default limit 50.
- `packages/core/src/ripgrep.ts`, `filesystem/protected.ts`: ignore-respecting
  scanning and protected home-directory exclusions.
- `packages/core/src/filesystem/ignore.ts` is **not** consulted by these upstream
  HTTP operations. Its broad folder deny list must not be applied here.
- Installed fuzzysort 3.1.0 `fuzzysort.js`: no-key sequential/strict/substring and
  multiword scoring, 200-backtrack cutoff, and bounded-result heap tie behavior.
- Installed mime-types 3.0.2 and mime-db 1.54.0: reviewed MIME mappings.

The Server's new `RequestLocation` helper uses the first nonempty query value,
then the directory/workspace headers, then server cwd (directory only). More
precisely, it reads the first query occurrence; if that occurrence is empty,
it falls back rather than searching later occurrences. Query names are
case-sensitive. Directory headers are decoded strictly as UTF-8 percent escapes;
malformed headers are kept unchanged, matching upstream's catch-and-preserve.
Session routes must continue resolving their stored placement, not this helper.

## Implemented behavior

`LocalFileSystem.List` includes hidden/ignored children, excludes symlinks and
devices, returns Location-relative entries, and sorts directories before files.
Read/list missing targets and failures remain errors, not empty success responses.
Read resolves and confines both lexical and symbolic-link paths before opening
an owned asynchronous stream. HTTP streaming uses bounded buffering, not a
whole-file byte array. OS access controls remain in force; these HTTP operations
do not impersonate a Session tool or create permission prompts.

`RipgrepFileScanner` calls the **public** existing `Tools.RipgrepProcess` through
a domain wrapper. It supplies exclusion-only globs: the adapter's positive `*`
glob would otherwise override ignore files. No glob/ignore regex was invented.
Search infers directories from scanned files; empty directories are not indexed.
Initial callers wait for an index. Later stale callers use the prior snapshot
while a host-owned refresh runs. Request cancellation does not kill a shared scan;
application shutdown cancels and awaits outstanding scans.

## Explicit remaining limits

- Workspace placement is unsupported by the existing authoritative Location
  resolver. No host filesystem fallback is used.
- Read/list currently supports Windows. Unix needs a real lstat file-kind adapter
  before arbitrary paths can be opened without treating FIFOs/devices as files.
- FFF is not implemented; this slice implements the upstream Windows-default
  ripgrep profile. It does not claim FFF ranking on other platforms.
- The reused ripgrep adapter imposes a 30-second timeout and 20 MiB captured-row
  limit. Missing executables fail explicitly; this host never downloads one.
- Native result/scan counts are Int32-bounded; larger requested result limits
  return unsupported instead of silently truncating the request.
- The source's full MIME database is not bundled. Reviewed programming, text,
  image, media, and binary suffixes work. Unreviewed suffixes return unsupported,
  not a guessed text or binary type. `.ts` is `video/mp2t` and `.rs` is
  `application/rls-services+xml`, matching the source lookup, not language detection.
- Linguistic sorting, Unicode casing, and Latin normalization use the native
  runtime and the included Latin-script table. Cross-runtime Unicode-version and
  collation parity still needs authorized contract checks.
- Indexes live for the route/application lifetime. Existing shared Location
  eviction is not wired to this route-owned cache; integration belongs to that
  service owner. No filesystem watcher, replay, or global query cache was added.

Validation is compilation only. No tests, filesystem behavior probes, API calls,
database access, process launches, or runtime checks were performed for this slice.

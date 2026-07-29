# ADR 0014: Canonical JPEG Capture Archive

- Status: Accepted
- Date: 2026-07-29
- Decision owners: WinDayFlow maintainers
- Scope: recording artifact format, deduplication, persistence migration,
  analysis evidence, screenshot browsing, and timelapse export
- Supersedes: the H.264/MP4 artifact and native extraction decisions in
  [ADR 0010](0010-transactional-native-capture-writer-components.md) and
  [ADR 0011](0011-authority-checked-native-capture-worker-orchestration.md)

## Context

Sparse H.264 MP4 chunks made every downstream operation depend on video decode:
analysis needed extracted JPEGs, screenshot browsing needed another derivative,
and archive integrity was harder to inspect. The product is still in development,
so retaining compatibility with local MP4 data would add cost without protecting
user data.

The required product behavior is periodic screenshots, low local storage use,
direct image analysis, auditable archives, and a fast MP4 timelapse only when a
user explicitly exports a selected time range.

## Decision

### Canonical chunk

Recording publishes one same-volume atomic directory:

```text
chunks/<chunk-id>/
  manifest.json
  frames/
    frame-000000.jpg
```

The current strict schema 3 manifest records captured and retained frame
counts, JPEG quality 82, dimensions, total bytes, and each frame's canonical
path, capture offset, byte count, and SHA-256. Foreground-scoped chunks may also
contain the identity-validated process name, PID, normalized interval CPU usage,
working set, and private memory. Full executable paths are not persisted.
Schema 2 remains accepted only for read-only development archives. A frame is
limited to 2 MiB and a chunk to 64 MiB. The default capture ceiling is 1600x900.

Frames and the manifest are written under a private `.staging` directory. The
manifest is written last, all files are flushed, and the prepared directory is
renamed without overwrite while the verified directory chain remains locked.
Only a validated reserved `ChunkCommitted` event acknowledges publication.

### Empty compositor surfaces

Windows capture can return a zero-filled BGRA surface while a capture session
is established or its target changes. If every RGB channel is at or below 8,
the worker discards the surface before opening a chunk or encoding a JPEG. It
therefore consumes neither archive space nor LLM image budget. A later useful
frame can still start and publish the chunk normally.

### Consecutive-frame deduplication

The writer samples a 64x36 RGB signature from each BGRA frame. A conservative
threshold identifies only consecutive near-duplicates. The first frame is
written immediately; the newest duplicate remains pending so finalization always
retains the chunk's final frame. A meaningfully changed frame is retained. This
reduces disk use and LLM image volume without merging non-consecutive evidence.

### Analysis and browsing

Managed code is the only archive reader. It rejects path escape and reparse
points, validates the exact schema, checks file stability, size, JPEG markers,
and SHA-256, and fingerprints the manifest plus all retained frame bytes.
Analysis selects at most 32 frames under a 12 MiB request budget and sends the
canonical JPEG bytes directly. No `evidence-v2` directory or native video
decoder/extractor C ABI exists. Timeline screenshot browsing uses the same
validated archive and each evidence reference's contribution range.

### MP4 export

MP4 is never a recording or analysis artifact. Timeline export accepts a local
date and a `[start,end)` range, validates all included canonical frames, orders
them by capture time, and creates one video frame per screenshot at 10, 15, 30,
or 60 FPS. An empty range produces no file. The generated MP4 is disposable and
can always be rebuilt from the JPEG archive.

### Persistence migration

SQLite schema 10 is intentionally destructive for legacy development evidence.
It clears timeline evidence, generated/manual timeline rows, analysis-window
members, analysis jobs, and capture chunks, then recreates `capture_chunks` with
manifest and JPEG summary fields. Settings, privacy choices, and provider
configuration remain. Local artifact cleanup removes the old `Data` directory;
there is no MP4 import or backfill path.

## Consequences

- Recording has predictable bounded writes and no video encoder dependency.
- Analysis and browsing avoid decode and derivative-storage work.
- Storage savings depend on screen stability; rapidly changing screens retain
  more JPEGs than video inter-frame compression would.
- Export is CPU work paid only on explicit user action.
- Legacy development timelines and recordings are deliberately discarded.

## Verification

- Native tests cover JPEG markers and bounds, duplicate endpoint retention,
  changed-frame retention, staging reset, atomic commit/rollback, worker
  authority, and C/C++ ABI behavior.
- Managed tests cover schema 10 migration, scanner integrity rejection,
  canonical fingerprint/extraction, analysis retry and sliding-window commit,
  multi-evidence timeline persistence, and user-edit protection.
- The native DLL dependency audit must contain no Media Foundation libraries.
- Manual smoke must verify recording creates only `manifest.json` and
  `frames/*.jpg`, analysis completes, screenshots open, and a selected range
  exports a playable MP4 at each supported FPS.

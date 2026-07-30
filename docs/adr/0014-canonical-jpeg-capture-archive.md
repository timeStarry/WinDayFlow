# ADR 0014: Canonical JPEG Capture Archive

- Status: Accepted
- Date: 2026-07-29
- Last updated: 2026-07-30
- Decision owners: WinDayFlow maintainers
- Scope: recording artifact format, filtering, persistence reset, analysis
  evidence, screenshot browsing, and timelapse export
- Supersedes: the H.264/MP4 artifact and native extraction decisions in
  [ADR 0010](0010-transactional-native-capture-writer-components.md) and
  [ADR 0011](0011-authority-checked-native-capture-worker-orchestration.md)

## Context

Sparse H.264 MP4 chunks made every downstream operation depend on video decode:
analysis needed extracted JPEGs, screenshot browsing needed another derivative,
and archive integrity was harder to inspect. The product is still in development,
so retaining compatibility with old local evidence would add cost without
protecting production user data.

The required behavior is periodic screenshots, low local storage use, direct
image analysis, auditable archives, and fast MP4 timelapse only when a user
explicitly exports a selected time range.

## Decision

### Canonical chunk

Recording publishes one same-volume atomic directory:

```text
chunks/<chunk-id>/
  manifest.json
  frames/
    frame-000000.jpg
```

The strict schema 4 manifest records the exact interval; sampled, black,
duplicate, and retained counts; JPEG quality 82; dimensions; total bytes; and
each retained frame's stable ID, canonical path, offset, byte count, and SHA-256.
It also records bounded context samples containing safe application ID/display
name, PID, normalized CPU use, working set, private memory, and matched send-rule
revision where available. Window titles and full executable paths are not
persisted. Older manifest schemas are not read.

A chunk may contain zero JPEGs when all samples are black or duplicates. It
still preserves the sampled time range and context counts so capture continuity
and statistics do not disappear. A frame is limited to 2 MiB, a chunk to 64 MiB,
and the default capture ceiling is 1600x900.

Frames and the manifest are written under the private `.staging` directory. The
manifest is written last, all files are flushed, and the prepared directory is
renamed without overwrite while the verified directory chain remains locked.
Only a validated reserved `ChunkCommitted` event acknowledges publication.

### Invalid and duplicate frames

Windows can return a zero-filled BGRA surface while capture is established or a
target is rebuilt. A compositor-invalid frame, or a frame whose RGB channels are
all at or below the conservative black threshold, is discarded before JPEG
encoding. Normal dark interfaces are retained. Filtered black frames consume
neither archive space nor provider image budget.

Consecutive frames use a bounded perceptual signature. The first useful frame is
retained, the newest duplicate remains pending so finalization can preserve the
interval endpoint, and a meaningfully changed frame is retained immediately.
The comparison state continues across chunk boundaries so rollover does not
force a redundant JPEG.

### Analysis and browsing

Managed code is the archive reader. It rejects path escape and reparse points,
validates schema 4, checks file stability, bounds, JPEG markers, and SHA-256, and
fingerprints the manifest plus retained frame bytes. Analysis selects at most 32
frames under a 12 MiB request budget and sends either validated originals or
validated privacy-screening derivatives. Hold/Review/send-rule results create no
timeline request. Screenshot browsing uses the same validated evidence and its
contribution ranges.

### MP4 export

MP4 is never a recording or analysis artifact. Timeline export accepts a
`[start,end)` range, validates included frames, orders them by capture time, and
creates one video frame per retained JPEG at 10, 15, 30, or 60 FPS. An empty
range creates no file. The MP4 is disposable and rebuildable from JPEG evidence.

### Persistence reset

SQLite schema 13 deliberately discards legacy development chunks, timeline
evidence, analysis jobs, and privacy derivatives. Cleanup is restricted to the
WinDayFlow data root and removes `.staging`, `chunks`, `screenings`, `cache`, and
app-local `exports`; files exported elsewhere are untouched. Theme, capture
interval and intent, retention, consent, provider profiles, and DPAPI ciphertext
remain. There is no import, compatibility reader, or backfill path for older
capture manifests.

## Consequences

- Recording has bounded writes and no video encoder dependency.
- Analysis, browsing, privacy screening, and statistics share one auditable
  evidence model.
- Storage savings depend on screen stability; rapidly changing screens retain
  more JPEGs than video inter-frame compression would.
- Export cost is paid only on explicit user action.
- Legacy development evidence is deliberately discarded.

## Verification

- Native tests cover ABI v2, JPEG bounds, black filtering, cross-chunk duplicate
  state, endpoint retention, zero-JPEG manifests, atomic commit/rollback,
  display loss, pause/resume races, health snapshots, and C/C++ ABI layout.
- Managed tests cover schema 13 reset, scanner integrity, context projection,
  privacy derivatives, routing and audit ledgers, evidence fingerprints,
  sliding-window commits, multi-evidence persistence, and user-edit protection.
- The native DLL dependency audit contains no recording-time video encoder.
- Manual smoke verifies rollover continues recording, black/duplicate frames do
  not enter provider requests, screenshots open, and selected ranges export a
  playable MP4 at each supported FPS.
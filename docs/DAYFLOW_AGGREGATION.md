# DayFlow Aggregation Study

This note records the DayFlow behavior used to guide WinDayFlow timeline
aggregation. The source was inspected at the fixed upstream revision
`JerryZLiu/Dayflow@861e9ad3a9e277f00476ad938ef5260c7cfe620e`. It is a design
reference, not a source-code dependency.

## Upstream behavior

- `ScreenRecorder.swift` takes periodic screenshots and defaults to a
  user-configurable 10-second interval.
- `BatchingConfig.standard` groups observations into 15-minute analysis batches.
  A gap over two minutes closes the current batch.
- `AnalysisManager.createScreenshotBatches` holds the newest batch while it is
  shorter than the target duration. This avoids analyzing an unstable trailing
  interval repeatedly.
- `LLMService` generates cards from a 45-minute sliding window containing recent
  observations and existing overlapping cards. It replaces cards across that
  window, so recent history can be revised as new context arrives.
- Card prompts optimize for one coherent activity rather than one card per app
  or screenshot. Cards target 10-60 minutes; related tool switches remain in one
  card, interruptions under five minutes become distractions, and a sustained
  focus change of about ten minutes warrants a new card.
- DayFlow does not remove duplicate screenshots before analysis.

The relevant upstream files are:

- `Dayflow/Dayflow/Core/Recording/ScreenRecorder.swift`
- `Dayflow/Dayflow/Core/AI/LLMTypes.swift`
- `Dayflow/Dayflow/Core/Analysis/AnalysisManager.swift`
- `Dayflow/Dayflow/Core/AI/LLMService.swift`
- `Dayflow/Dayflow/Core/AI/ChatCLIProvider+Prompts.swift`

## WinDayFlow adaptation

WinDayFlow now defaults to a 10-second screenshot interval and exposes
5/10/15/30/60-second choices. Native capture uses 15-minute chunks, aligning the
durable recording unit with DayFlow's observation batch. A setting change while
recording performs an orderly stop, updates native timing, and restarts capture;
timing cannot be changed inside an active native run.

The native JPEG chunk writer computes a 64x36 perceptual signature for each
captured BGRA frame and avoids writing consecutive near-identical frames. The
first and final frames of every chunk are retained. This is deliberately
conservative: it reduces local storage and image tokens without treating
similarity as proof that all intermediate user activity was identical.

`timeline-v5` keeps those semantic rules and adds a persisted cross-chunk
analysis window. For each newly eligible chunk, ingestion walks backward across
continuous chunks for at most 45 minutes without crossing the local day; a gap
greater than one second ends the window. The job stores every ordered member,
its source fingerprint, and the member's contribution range. Its aggregate input
fingerprint therefore changes when any contributing source or range changes.

The v5 provider contract also restricts category and productivity values to the
product enums. An unexpected label is rejected as an invalid provider response
instead of being silently stored as `Unknown`.

The provider receives all retained evidence in the window, together with every
overlapping timeline card and its locked state. Evidence images share one global
request budget and consecutive byte-identical images are removed across chunk
boundaries. Sparse frames may consequently be a deduplication result rather than
a recording gap. The response must still cover the requested interval
continuously, using unknown labels when evidence is insufficient.

## Rewrite and provenance contract

An analyzed `TimelineEntry` can reference multiple ordered
`EvidenceReference` values. Each reference identifies its source chunk,
manifest, and contribution range, so screenshot review and timelapse export keep
their provenance after several source cards are rewritten into one activity.
The legacy single `Evidence` property remains a read-compatible view of the
first reference.

Committing a `timeline-v5` result is a transactional window rewrite:

- user-created or user-edited cards are locked and never replaced;
- generated cards extending beyond the right edge are protected because the
  job does not have evidence for their future portion;
- an unlocked generated card crossing the left edge keeps its earlier portion;
- other unlocked generated cards in the window are replaced atomically by the
  validated provider result; and
- the commit is rejected with `WindowChanged` if the overlapping timeline IDs
  or revisions changed after the analysis snapshot was taken.

SQLite schema version 10 stores ordered timeline evidence in
`timeline_entry_evidence` and persisted job membership in
`analysis_job_window_members`. Because the product is still in development,
schema 10 intentionally clears legacy chunks, analysis jobs, and timeline data,
then recreates `capture_chunks` around canonical JPEG manifest/frame metadata.
Settings and provider configuration are retained.

# ADR 0010: Transactional Native Capture Writer Components

- Status: Superseded for artifact format by
  [ADR 0014](0014-canonical-jpeg-capture-archive.md); safety and publication
  decisions remain accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Native` writer components, native persistence
  authority, artifact publication, and live-capture activation

## Context

ADRs 0003 through 0009 establish target/display-scoped authorization,
single-use Start/Resume admission, callback-time closure, persistence
generations, strict foreground observation, and strict DXGI output resolution.
The native DLL still returned `NOT_IMPLEMENTED` because it had no real frame,
encoder, or artifact components behind those contracts.

The reviewed QiDayflow writer demonstrates the relevant Windows APIs, but its
runner-owned service cannot be reused unchanged. It selects fallback displays,
may reuse old pixels after a timeout, writes video before privacy authority is
revalidated, records raw titles and process paths, publishes video and metadata
separately, and invokes callbacks directly. Those behaviors do not satisfy the
WinDayFlow safety boundary.

This decision introduces a real, independently tested writer-component slice.
It does not connect that slice to C ABI Start/Resume and therefore does not
claim that WinDayFlow is a functional recorder.

## Decision

### Strict Observation and Acquisition

The native target observer reads only the current foreground HWND, owner
TID/PID, opened-process PID and creation time, HMONITOR, and
`MONITORINFOEXW.szDevice`. It double-checks each identity and uses
`MONITOR_DEFAULTTONULL`. It has no cursor, nearest-monitor, or primary-display
fallback and reads no title or process path.

`DxgiDesktopFrameSource` accepts only the display tuple already authorized by
the safety core. It obtains the adapter and `IDXGIOutput1` exclusively through
the ADR 0008 strict resolver and compares the complete adapter LUID, monitor,
device key, desktop rectangle, and rotation fingerprint before and after every
frame acquisition. Desktop Duplication timeout returns no frame; old pixels
are never reused. Access loss, device removal/reset, session disconnect,
unsupported formats, topology changes, invalid row pitch, and mapping failures
clear the output and require an explicit rebuild or fault decision by the
future worker. The source rejects more than 33,177,600 pixels or 132,710,400
packed/mapped BGRA bytes before creating a GPU staging texture or CPU buffer.
Early-return cleanup deterministically unmaps a mapped texture before releasing
the acquired duplication frame.

Mapped BGRA rows use checked byte geometry and the reviewed pixel-buffer copy
contract. Rotation produces a newly owned top-down BGRA frame. WIC then scales
without upscaling, preserves aspect ratio, bounds output to configured maxima,
forces even H.264 dimensions, validates UINT/byte limits, and normalizes alpha.

### Stage Authority and Runtime Token Handoff

Every persistence permit now records the authorization epoch and its issuing
safety-core identity at acquisition. The holder can perform a post-operation
check without reacquiring the shared safety lock, while a different core with
the same numeric epoch cannot validate the permit. Callback-time invalidation
remains nonblocking, but it immediately makes that post-check fail. A future
worker must acquire a new permit from a fresh target observation for every
acquisition, transform, encode, finalize, staging-write, and final-publication
stage. Failed post-checks discard memory or compensate filesystem output before
advancing.

`CaptureRuntimeOwner` passes the Start `PersistenceToken` to its worker by
value. Its control mailbox has a monotonic sequence, sticky Stop, explicit
Pause, and an optional replacement token. Resume is accepted only in the
paused state with a fresh owner-bound command permit; it advances the owner
epoch, transfers the replacement token by value, and wakes the worker. Stop
dominates Resume races, clears Pause and replacement state, wakes waiters even
at sequence saturation, and preserves exactly-once join behavior. A new Start
is rejected until every waiter from the previous run has observed the completed
join and drained, preventing a Stop/Start ABA across runtime generations.

These contracts remove unsafe reference lifetime and stale-token assumptions,
but the current C ABI lifecycle functions do not yet create or drive the real
worker.

### In-Memory H.264

`MfH264ChunkWriter` receives tightly packed, top-down BGRA frames. It owns an
explicit same-thread `Idle -> Writing -> Finalized/Failed` state machine and
pairs COM and Media Foundation lifetime on that thread.

The MP4 sink writes to a custom seekable COM stream backed by native memory.
The stream is capped at 64 MiB. Any seek, resize, or write beyond the cap is a
sticky failure that zeroes and releases buffered evidence. Finalize returns one
complete MP4 byte vector; it creates no path-backed temporary video. Real tests
decode the result through Media Foundation Source Reader and verify dimensions,
nonempty frames, frame count, reset behavior, thread affinity, and bounded
overflow failure.

Keeping the pre-publication MP4 in native memory prevents an asynchronously
revoked session from leaving an encoded temporary video on disk. The bound is
a safety limit for this component, not a final product cadence or retention
policy.

### Privacy-Safe Manifest and Atomic Publication

The chunk manifest is a closed schema containing only:

- schema and capture-scope identifiers;
- chunk ID and start/end Unix milliseconds;
- codec, container, relative video path, frame count, dimensions, and frame
  rate; and
- persistence generation and target epoch.

It never contains HWND, PID, process name/path, window title, HMONITOR, display
device key, application name, or publisher identity.

`AtomicChunkStore` accepts an absolute local output root, a bounded ASCII
artifact ID, encoded MP4 bytes, and a typed `ChunkManifest`. It rejects an
invalid manifest or an ID mismatch before touching the filesystem and performs
the only manifest serialization internally. It rejects remote roots, files in
place of directories, and reparse points while opening and retaining every
directory identity in the root chain without delete sharing. It writes
`capture.mp4` and `manifest.json` with `CREATE_NEW`, no-follow, write-through,
and `FlushFileBuffers` into a unique `.staging` directory on the same volume.
The no-overwrite rename acts on the held staging-directory handle; its target
path is derived from the already locked `chunks` directory identity.

The returned publication object owns compensation. Explicit rollback reports
deletion failure and retains the same identity-bound state for retry; successful
rollback clears it. Destruction remains a final best-effort attempt, so a worker
must observe and retry an explicit rollback failure instead of discarding the
publication. Only successful `ChunkCommitted` delivery may acknowledge and
release compensation.

The bounded event queue therefore supports move-only, queue-instance-bound
required-event reservations. A future chunk reserves capacity before
persistence begins. Ordinary events cannot consume that slot. Final publication
uses the reservation for a required `ChunkCommitted` event carrying the exact
persistence generation and target epoch. Invalid, foreign-queue, or failed
appends do not consume the reservation; cancellation returns capacity. Sequence
and drop accounting advance only after append succeeds. This prevents both a
queue-full race and a cross-queue token collision from leaving a committed
artifact unknown to the managed consumer.

### Capability Boundary

The component slice is not sufficient to advertise live capture. The DLL keeps
`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` disabled. Authorized
Start/Resume still returns `NOT_IMPLEMENTED`, and App composition continues to
use `DenyCaptureRuntimeAuthorization` and `UnavailableCaptureBackend`.

The live bits may be enabled only after one worker proves this sequence end to
end:

```text
fresh target observation + permit
-> strict DXGI bind and acquire + post-check
-> bounded WIC transform + post-check
-> in-memory H.264 sample/finalize + post-checks
-> required-event reservation
-> privacy-safe manifest and staging flush + post-check
-> final permit, whole-directory rename, post-check
-> reserved ChunkCommitted event
-> publication acknowledgement
```

The worker must also prove Pause/Resume token replacement, user Stop partial
finalization, privacy Stop discard, access-loss/topology behavior, bounded
shutdown, stale staging recovery, committed-event replay/idempotency, disk-full
cleanup, and managed generation ordering.

ADR 0011 subsequently implements the standalone, fake-backed orchestration
worker, per-stage authority checks, Pause/Resume/Stop partial handling,
topology rebuild, event linearization, and retryable compensation. C ABI
ownership, dynamic privacy Pause/Stop policy, durable recovery/replay, and live
Desktop Duplication evidence remain closed activation gates.

## Verification

Native tests now cover:

- strict target API failures, pre/post identity races, and HWND/PID reuse;
- DXGI result mapping, 8K/pitched-byte limits, BGRA rotation, cleanup ordering,
  no retained output on failure, and the separately tested strict output
  resolver;
- WIC bounded/even sizing, scaling, ownership, and alpha normalization;
- real four-frame in-memory H.264 encode/decode and capacity failure;
- manifest validation and absence of sensitive fields;
- typed-manifest binding, no-follow junction rejection, staging, flush,
  identity-bound commit, collision, pre/post-rename retryable rollback, root
  validation, exception cleanup, and artifact identifiers;
- required-event reservation, queue-instance isolation, append-failure retry,
  saturation, cancellation, and generation/target delivery; and
- Start token, Pause wake, Resume replacement token, stale Resume, Stop/Resume
  race, sticky Stop, shutdown, exception, and single-join behavior.

No automated test captures the user's live desktop in this milestone. A later
consent-gated Windows smoke test must exercise real Desktop Duplication and
decode the resulting artifact before the capability bits change.

## Provenance

The target observer, authorization-epoch post-check, runtime control mailbox,
event reservation contract, and their tests are original WinDayFlow work.

The atomic store, privacy-safe manifest, Desktop Duplication source, WIC
scaler, and in-memory Media Foundation writer are heavily adapted from the
reviewed QiDayflow `windows/runner/capture_service.cpp` at pinned commit
`8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e`. Each derived production file has
a provenance header. `THIRD_PARTY_NOTICES.md`, the Markdown ledger, and the
machine-verifiable manifest record the source and exact local hashes.

## Consequences

The project now has real Windows acquisition, transform, encoding, and atomic
storage components without weakening fail-closed activation. Native memory use
can reach the explicit 64 MiB encoded-output cap plus up to 126.6 MiB per
bounded BGRA working buffer, so cadence, dimensions, bitrate, and chunk duration
still require a performance ADR before release.

Whole-directory rename and event reservation make publication recoverable, but
crash recovery and replay are not yet implemented. Reparse rejection reduces
path redirection risk but does not replace creating the product evidence root
with a reviewed current-user ACL. ADR 0011 adds component orchestration, but it
does not replace C ABI lifecycle integration or live-system proof. The UI
remains disabled until that final boundary is proven.

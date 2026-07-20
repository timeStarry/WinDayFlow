# ADR 0011: Authority-Checked Native Capture Worker Orchestration

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Native` worker orchestration, Windows component
  composition, chunk-publication linearization, and pre-activation lifecycle
- Follow-up: [ADR 0012](0012-run-isolated-native-capture-instance-control.md)
  supersedes this ADR's open C ABI ownership, run-state, and recoverable
  authorization-loss items while retaining the closed live capability boundary.

## Context

ADR 0010 introduced independently tested target observation, Desktop
Duplication, WIC scaling, in-memory H.264, manifest, atomic-store, event-slot,
and runtime-mailbox components. No code composed those components into one
frame-to-artifact transaction, so component success did not yet prove that an
authorization change between two stages could not publish stale evidence.

The worker also needs different failure semantics for evidence held only in
memory, a staged filesystem directory, a renamed directory, and an event that
managed code may consume. A queue-full or append-allocation failure after the
directory rename must not leave an acknowledged artifact with no corresponding
`ChunkCommitted` event.

This decision implements the native orchestration core and a production Windows
adapter. It deliberately does not connect C ABI Start/Resume or advertise live
capture.

## Decision

### Worker and Backend Boundary

`CaptureWorker` owns the orchestration state machine. It depends on the narrow
`CaptureWorkerBackend` contract rather than mocking DXGI, WIC, Media Foundation,
or Win32 filesystem calls individually. The production adapter composes:

- `ObserveWindowsCaptureTarget`;
- `DxgiDesktopFrameSource`;
- `ScaleBgraFrameWithWic`;
- `MfH264ChunkWriter`; and
- `AtomicChunkStore` through a compensation-owning publication adapter.

The adapter creates one COM apartment on the worker thread before WIC or Media
Foundation use. It resets the writer, DXGI source, and WIC factory on that same
thread before balancing `CoUninitialize`. Device/copy failures are fatal;
topology, output-loss, and access-loss results request a bounded rebuild. A
rebuild finalizes an authorized partial chunk before releasing the old topology,
so frames from two bindings cannot silently share one MP4.

The C ABI now decodes and validates the local absolute output root once at
instance creation and retains its UTF-16 value together with `max_width` and
`max_height`. Relative, UNC/remote, missing-drive, malformed UTF-8, and
embedded-NUL roots fail before capture work can begin. The atomic store retains
the stronger identity, reparse, and filesystem checks at Prepare time.

### Per-Stage Authority

Every sensitive stage uses one fresh guard:

1. Observe the expected foreground target.
2. Acquire a new persistence permit for that exact observation and token.
3. Execute exactly one Initialize, Acquire, Transform, Begin, Add, Finalize,
   Prepare, or Commit operation.
4. Check the issuing safety core and authorization epoch on the held permit.
5. Observe the foreground target again and compare the complete identity.
6. Check the same permit again, then release it before waiting.

A failed check wipes worker-owned BGRA or encoded buffers, resets the encoder,
and explicitly compensates any prepared or committed publication. A chunk never
accepts frames from two persistence tokens. User Pause and graceful Stop
finalize a valid partial chunk; a superseded generation discards the old partial
before a replacement token can encode.

The runtime mailbox adds a per-run monotonic Pause epoch. This preserves a Pause
transition even when Pause and Resume are both issued before the worker observes
the current boolean state. Resume still transfers a fresh token by value.
Authorization changes wake the mailbox so the worker cannot remain asleep for
the maximum capture interval after callback-time closure.

### Artifact and Event Transaction

The publication order is fixed:

```text
reserve required event capacity
-> prepare and flush staging directory
-> commit directory rename
-> append hidden ChunkCommitted event and validate authority
-> expose event
-> acknowledge publication
```

`PushReservedValidated` constructs the required event while holding the queue
mutex, then performs the final safety-epoch load before a reader can observe the
event. A failed load removes the hidden event without consuming the reservation,
sequence, or drop accounting. That load is the publication linearization point:
an invalidation CAS ordered before it wins and forces rollback; a CAS ordered
after it is later than the already-authorized publication, even if the queue
mutex has not yet been released. The worker therefore does not perform a second
queue-external check that could delete an artifact after its event was
linearized.

Only a successful validated append calls `Acknowledge`. Every other path retries
explicit rollback up to the configured bound. If compensation still fails, the
worker retains the same publication object, reports `compensation_pending`, and
offers an explicit retry; destruction is not treated as successful cleanup. The
event reservation has a worker-side RAII guard, so exceptions cannot leak queue
capacity.

Worker-owned BGRA and encoded MP4 vectors are overwritten before release on
normal, invalidation, and exception paths. This is a best-effort boundary for
owned CPU buffers; it does not claim physical erasure of driver, codec, or
filesystem caches.

### Stop Ordering

Graceful user Stop invalidates any unconsumed command-admission record without
closing the current worker's persistence gate. It then requests Stop, lets the
worker finalize and exit, joins it, and only then calls `FinalizeRevoke`.
Destruction follows the same worker-exit-before-revoke ordering. Privacy revoke
continues to close authorization first, which makes every later stage discard or
compensate instead of finalizing stale evidence.

### Capability Boundary

ADR 0012 now places this worker behind a C-ABI-owned controller with run-ID
guards and provisional authorization Pause. Production constructs that
controller in disabled activation mode, so authorized Start/Resume still return
`NOT_IMPLEMENTED`; `ScreenCapture`, `H264Chunks`, and `EvidenceExtraction`
remain disabled; App composition still uses `DenyCaptureRuntimeAuthorization`
and `UnavailableCaptureBackend`.

Before activation, managed policy still needs to distinguish resumable evidence
Pause from sticky privacy Stop. The native controller protocol can pause and
replace authorization without carrying old evidence forward, but it does not
yet claim the product-level blocked-and-resume experience. Crash recovery/replay,
evidence-root ACL creation, stale staging, disk-full integration, consent-gated
live Desktop Duplication, and managed generation ordering also remain release
gates.

## Verification

The fourteenth native CTest executable, `capture_worker_tests`, uses a scripted
in-memory backend and real safety core, runtime mailbox, and event queue. It
proves:

- graceful Stop partial publication and privacy-safe manifest binding;
- Pause finalization, fresh-generation Resume, immediate Pause/Resume
  coalescing, Pause/Resume/Pause token retention, and superseded-generation
  discard;
- invalidation during Initialize, Acquire, Transform, Begin, Add, Finalize,
  Prepare, Commit, and final event append;
- reservation saturation before persistence and append-allocation rollback;
- transient rollback retry, retained permanent compensation, and explicit
  recovery;
- prompt authorization wake from a maximum-interval wait; and
- bounded topology rebuild before capture resumes.

The event-queue tests independently cover invalidation before the final
validator load and invalidation after that load, making the intended
linearization order executable rather than implicit.

No automated test captures the user's desktop in this decision.

## Provenance

`capture_worker.*`, `windows_capture_worker_backend.*`, their tests, the Pause
epoch, and validated event-publication protocol are original WinDayFlow work.
They compose previously reviewed derived components but copy no additional
QiDayflow source, so the sixteen-file derived-source manifest is unchanged.

## Consequences

WinDayFlow now has a deterministic, fake-backed frame-to-artifact worker core
and a real Windows adapter behind the closed capability boundary. Authorization
loss and persistence failures have explicit memory, filesystem, queue, and
compensation outcomes.

This narrows the remaining work to lifecycle/state integration and live-system
evidence rather than component composition. It does not make the development
bundle a recorder and does not justify enabling capture in the UI.

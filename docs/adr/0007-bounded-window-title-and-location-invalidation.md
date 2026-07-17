# ADR 0007: Bounded Window Title Reads and Location Invalidation

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Interop`, Windows privacy observation, and live
  capture activation

## Context

ADR 0005 requires two stable window-title observations while assembling one
foreground-target result. A direct `GetWindowTextW` call can block on
window-procedure work, particularly for an in-process window. A character limit
does not bound that wait. Retrying blocked reads by creating more workers would
turn one hostile or unhealthy HWND into unbounded process growth, while
accepting a late title could publish identity from an expired observation.

ADR 0006 synchronously invalidates capture authority for foreground, desktop,
and selected window-object changes. Window movement can change the display
relationship without changing foreground HWND, owner, or title. The event path
therefore needs location-change invalidation, but a WinEvent callback is only a
change notification. Its HWND and object fields are not proof that the window
is top-level, foreground, or the target later selected for capture.

The project needs bounded caller behavior and conservative invalidation without
turning either mechanism into identity or persistence authority.

## Decision

### One Process-Wide Title Worker

Production P/Invoke title reads use
`FailStopWindowsCaptureWindowTitleReader.ProcessWide`. The process therefore has
one unique, lazily started, dedicated background window-title worker and one
private native text buffer. The worker has these states:

```text
Idle -> Queued -> InFlight -> Idle
                    |
                    +-> Poisoned
Any live state -> Stopping
```

Only `Idle` admits a request. There is no backlog behind `Queued` or `InFlight`;
another caller receives `Unknown` immediately. A request carries only its HWND,
attempt identity, monotonic deadline, and completion state. It does not retain
the verifier's opened process handle.

Every admitted request has a 100 ms wall-clock safety deadline measured from a
monotonic `Stopwatch` timestamp. The deadline controls the caller-visible read,
not the execution time of `GetWindowTextW`, which Windows does not make
cancelable through this API.

### Queue Expiry and Permanent Fail-Stop

Expiry depends on whether native execution began:

- If the request times out while `Queued`, it may be removed before execution
  and return the worker to `Idle`. A later request may then be admitted.
- If the request times out after entering `InFlight`, the reader permanently
  enters `Poisoned`. Every title read for the rest of that process returns
  `Unknown` immediately. The implementation never creates a replacement worker
  or allows new native title work behind the blocked call.

When an expired in-flight native call eventually returns, its output is late and
is discarded. The reader checks request identity, state, and deadline before
building a managed string and again before committing completion. A late result
is never retained, completed, returned, or published into a newer observation.
The worker exits after it regains control from a poisoned call.

The worker owns one fixed `char[32768]` buffer. It clears that buffer before a
native read and, after the native call returns control, in `finally` before the
worker completes or exits. This includes caller expiry, poison, and disposal
paths once native use has ended; timeout or disposal never clears a buffer while
Windows may still be writing it. If the native call never returns, no managed
title is built or published, but the caller cannot safely clear that in-use
buffer. The buffer is not pooled or shared with another reader.

These choices prefer a process-lifetime loss of title observation over stale
identity publication, unbounded thread creation, or reuse of a worker whose
native call crossed its authority deadline. `Unknown` continues to flow through
the existing fail-closed exclusion and privacy policy.

### Exact Location-Invalidation Hook

`WindowsCaptureWinEventSource` registers one
`WINEVENT_OUTOFCONTEXT` object-event hook whose inclusive range is exactly:

```text
EVENT_OBJECT_LOCATIONCHANGE (0x800B)
..
EVENT_OBJECT_NAMECHANGE     (0x800C)
```

No broader object-event range is implied. A location or name callback is
accepted only when all of these predicates hold:

- HWND is nonzero;
- object ID is `OBJID_WINDOW`; and
- child ID is `CHILDID_SELF`.

A qualifying `LOCATIONCHANGE` maps to the value-free
`ObjectLocationChanged` change kind. Before the callback returns, the privacy
monitor closes the managed capture latch, advances its independent observation
generation, and invalidates target continuity. Only after synchronous
invalidation does it offer a wake token for asynchronous re-observation. Event
bursts may coalesce wake work, but they do not coalesce generation advancement.

The three predicates above do not establish that an HWND is top-level or
foreground, and the event does not prove a display mapping. The source does not
call `GetForegroundWindow`, parse a title, inspect process identity, or compare
the callback HWND with a retained target. Location change is deliberately a
conservative invalidation signal. The subsequent verifier observation owns all
foreground, owner, process, and display facts. Extra invalidation caused by a
non-target window is acceptable; treating the event as positive identity proof
is not.

### Activation-Gate Accounting

This decision closes two previously open observation gates:

- bounded window-title reads with rejection of late completion; and
- event-driven conservative window-location invalidation.

It does not activate capture. All of these gates remain open:

1. Publisher signer verification must be bound to the opened running image.
2. Hosted Windows surfaces must be attributed to one unique child application.
3. Display-topology, WTS session, power/resume, presentation, and periodic
   storage signals must join the invalidation and observation lifecycle.
4. The HMONITOR/device key must be natively bound to the selected DXGI output.
5. A writer-side generation/permit gate must reject stale authority through
   acquisition, encoding, temporary output, final publication, and event commit.
6. The real DXGI/WIC/Media Foundation writer, recovery, and cleanup path must use
   that boundary end to end.
7. Dynamic privacy transitions still need an explicit evidence-Pause versus
   sticky-session-Stop policy and live App composition.

`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` remain disabled. The App
continues to register `DenyCaptureRuntimeAuthorization` and
`UnavailableCaptureBackend`; no frame, title metadata, or capture artifact is
persisted by this milestone.

## Required Verification

Deterministic title-reader tests must prove:

- successful reads execute on the dedicated worker and clear its private buffer;
- a queued request can expire back to `Idle` without native execution;
- an in-flight timeout returns `Unknown`, enters permanent `Poisoned`, rejects
  every later read, and does not create another worker;
- a late sensitive value cannot build a managed string, complete an expired
  request, or publish, and the 32K buffer is cleared after the native call
  returns;
- concurrent readers cannot form a queue behind admitted work;
- disposal remains bounded around a blocked native call; and
- verifier process-handle ownership is released after the title deadline.

WinEvent and monitor tests must prove:

- the registered inclusive hook range is exactly `0x800B..0x800C`;
- zero HWND, non-`OBJID_WINDOW`, and non-`CHILDID_SELF` location callbacks are
  ignored;
- every qualifying location event synchronously advances invalidation even when
  worker wakeups coalesce; and
- a location event racing target/title sampling makes the older generation
  stale and prevents publication.

These deterministic contracts do not replace later Windows platform validation
for display topology, session, power, presentation, storage, native DXGI
binding, or real writer persistence races.

## Provenance

The title worker, location-invalidation extension, tests, and this ADR are
original WinDayFlow work. They are not derived from QiDayflow and do not change
the QiDayflow-derived file set or provenance manifest hashes.

## Consequences

A blocked title call no longer makes its verifier caller wait for native
completion: once the 100 ms monotonic deadline is observed, the read fails
closed, subject to normal thread-scheduling latency. A late title cannot become
current evidence. The cost is intentionally strict: one in-flight timeout
disables title observation until process restart, and the blocked background
worker may remain inside Windows until the native call returns.

Location changes now revoke stale managed observation immediately instead of
waiting for a poll. Conservative filtering may cause extra re-observation for a
non-target window, but avoids treating an event notification as target identity.
Neither improvement supplies native display binding or writer-side persistence
authority, so the recorder remains disabled.

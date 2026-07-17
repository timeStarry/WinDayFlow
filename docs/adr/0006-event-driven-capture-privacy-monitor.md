# ADR 0006: Event-Driven Capture Privacy Monitor

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Interop`, Windows privacy observation, and live
  capture activation

## Context

ADR 0005 produces a stable synchronous foreground-target observation, but a
poll alone cannot close capture authority when the foreground window, desktop,
window identity, or display relationship changes between polls. A WinEvent can
also arrive while an older Allow update is awaiting native completion. Reading
window titles, process identity, settings, or policy inside the native callback
would make callback latency unbounded and would still leave publication races.

The event path therefore needs a small synchronous invalidation boundary and a
separate asynchronous observation path. The invalidation must close managed
admission before the callback returns, revoke the verifier's remembered target
continuity, and prevent any older observation from publishing. Native
persistence authority must then receive a forced FailClosed update before a new
observation can authorize capture.

This monitor is a safety foundation, not a lifecycle-policy owner. It must not
read settings, decide evidence Pause versus sticky session Stop, issue capture
commands, or imply that a real writer is active.

## Decision

WinDayFlow implements an inactive event-driven privacy monitor with a dedicated
WinEvent and system-lifecycle source, an independent privacy-observation
generation, a generation-bound native barrier, and one asynchronous sampling
worker.

### Synchronous Callback Boundary

Every accepted event executes this order before returning from the callback:

1. `InvalidatePrivacyObservation()` replaces the coordinator's current signals
   with FailClosed, closes the managed authorization latch and the lock-free
   native admission gate, and advances the independent privacy-observation
   generation.
2. `WindowsCaptureTargetVerifier.InvalidateObservation()` clears the shared
   target fingerprint so the next stable observation must obtain a fresh target
   epoch.
3. The monitor records the greatest returned generation and offers one
   value-free wake token to its worker.

The callback does not await, sample Windows privacy state, read HWND titles or
process identity, evaluate exclusion rules, inspect committed settings, compose
policy, or call Pause/Stop. Every relevant event advances the observation
generation even when the managed latch was already closed. This generation is
deliberately separate from the existing capture-runtime invalidation generation,
which advances only on an authorized-to-blocked latch transition.

If synchronous invalidation cannot complete, the monitor stops accepting
callbacks and enters a sanitized terminal fault. Exceptions never cross the
unmanaged callback boundary.

### Enforced Three-Phase Protocol

For every positive observation generation, the privacy sink enforces these
phases:

```text
Invalidate synchronously
-> force the FailClosed native persistence barrier
-> publish at most one generation-bound resolved observation
```

A resolved update cannot reopen admission before the same generation's forced
barrier completes. A stale generation, a publication that skips the barrier,
or a second resolved publication for an already-consumed generation is
rejected. Generation zero remains only as a pre-monitor compatibility path;
once explicit invalidation begins, the legacy unbound update API cannot bypass
the protocol.

The coordinator serializes native updates with its existing apply gate and
recomposes policy from the latest committed settings at publication time. Once
a signal passes its generation/phase check, the coordinator atomically installs
it and closes managed admission before waiting for the native apply gate. This
prepublication can only reduce authority; it never opens admission. After the
gate is acquired, an update proceeds only if that signal is still the latest
observation, so overlapping updates cannot revive an older value. An already
admitted command may finish while the managed latch is closed, but no new
managed admission can be issued.

If an older native Allow is invalidated before commit, native code reports it as
superseded without consuming a revision. If it commits immediately before the
callback wins, native code reports applied-then-superseded and the coordinator
applies a compensating forced FailClosed update. The completed Block
acknowledges the callback invalidation before another Allow may publish. The
same compensation rule applies to settings reconciliation and post-Stop
reconciliation. Quiescence is monotonic: an authorizing settings commit cannot
clear its forced block or republish Allow after quiescence begins.

The lock-free native gate closes new admission before callback return, but it
does not by itself prove that a future writer discarded a persistence permit it
already held. Live activation therefore still requires writer pre/post callback
generation and permit checks at acquisition and every publication boundary.

### Sampling and Coalescing

The monitor owns one bounded wake channel with capacity one and one worker. A
burst may coalesce worker work, but it never coalesces synchronous generation
advancement. The worker always processes the latest generation and performs:

1. the forced FailClosed native barrier;
2. a base Windows privacy sample;
3. one atomic target/identity/display verification;
4. a second base sample; and
5. a generation-bound signal publication.

The two base samples must be equal. A mismatch or recoverable Windows sampling
failure publishes FailClosed for that generation. An event arriving during the
barrier, target verification, or publication makes the older result stale; it
cannot update the monitor's last observation or reopen authorization. The
monitor carries the identity snapshot to the coordinator but does not read or
cache settings itself.

### System-Event Ownership and Teardown

`WindowsCaptureWinEventSource` owns a dedicated background thread, a Windows
message queue, and an invisible top-level `WS_POPUP` window with
`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` and no parent. It is deliberately not a
message-only window, so it receives broadcast `WM_DISPLAYCHANGE` notifications.
On the same owner thread it registers
`WTSRegisterSessionNotification` with `NOTIFY_FOR_THIS_SESSION` and user32
`RegisterSuspendResumeNotification` with `DEVICE_NOTIFY_WINDOW_HANDLE`. WTS
availability transitions and suspend/resume map to value-free change kinds;
session-unavailable and power-suspended holds keep publication FailClosed until
their matching recovery event is reverified through a new barrier and sample.

The source also installs narrow `WINEVENT_OUTOFCONTEXT` hooks for foreground,
desktop switch, window-object create/destroy, and one exact
`0x800B..0x800C` range for object location/name change. Object events are
accepted only when HWND is nonzero and the callback reports `OBJID_WINDOW` with
`CHILDID_SELF`. Those predicates do not prove that the window is top-level or
foreground. In particular, a qualifying `LOCATIONCHANGE` is a conservative
invalidation signal, not target-identity evidence.

The callback delegates are rooted for the complete hook and HWND lifetime. The
owner thread unhooks WinEvent registrations in reverse order, drains queued
messages, unregisters suspend/resume and WTS notifications, destroys the hidden
window, unregisters its class, and releases the root only after clean teardown.
Start and stop waits are bounded. If clean unhook or window-callback teardown
cannot be established, the source disconnects application callbacks and
conservatively retains the minimum callback bridge. A callback failure requests
owner-thread shutdown instead of leaving the message pump alive. Startup,
runtime, and teardown faults expose stable enum values only.

The monitor's `Completion`, `StartAsync`, and `DisposeAsync` surface only
`WindowsCapturePrivacyMonitorFault`; they do not preserve raw exception messages
or inner exceptions. Teardown first establishes and applies the final
FailClosed generation, then stops the event source and worker. Late callbacks
cannot advance state after terminal shutdown.

## Activation Boundary

Together with ADR 0007, this decision closes the foreground/desktop/window-event
observation-generation and conservative window-location invalidation
foundations. The bounded title-read gate is also closed. None of these decisions
activates capture. At minimum, live integration still requires:

1. publisher identity bound to the opened running image and unique hosted-app
   child attribution;
2. presentation notifications and periodic storage signals;
3. writer-side use of the ADR 0008 HMONITOR/device-key resolver and
   target/display revalidation before and after acquisition;
4. real-writer checks that reject already-held stale permits and callback
   generations through publication;
5. an explicit evidence-Pause versus sticky-session-Stop policy; and
6. the real DXGI/WIC/Media Foundation acquisition, encoding, temporary output,
   atomic publication, cleanup, and recovery path.

`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` remain disabled. The App
composition root continues to register `DenyCaptureRuntimeAuthorization` and
`UnavailableCaptureBackend`; the monitor and native runtime owner are not
connected to application dependency injection.

## Required Verification

Deterministic tests cover callback-before-sampling invalidation, generation
advancement for every event in a burst, latest-generation coalescing, events
during sampling, stale equal-signal rejection, forced barrier ordering,
recoverable FailClosed publication, source start/runtime/teardown faults, late
callbacks, idempotent disposal, terminal generation barriers, and value-redacted
diagnostics. Coordinator tests cover barrier-before-publish enforcement,
single publication, callback-time native closure, pre-commit supersession,
post-commit compensation, current Block acknowledgement,
settings/invalidation races, and quiescence monotonicity.

System-event tests cover hook ranges and filters, the hidden top-level HWND,
display/WTS/power message mapping, all-or-nothing registration, same-owner-thread
cleanup, reverse teardown, queued late callbacks, callback-root lifetime,
callback-failure shutdown, bounded start/stop, the exact `0x800B..0x800C` range,
and rejection of location callbacks without a nonzero HWND, `OBJID_WINDOW`, and
`CHILDID_SELF`. Monitor tests cover topology and location races, independent
session/power holds, resume/reconnect barriers, and stale-publication rejection.

Future activation tests must add presentation and periodic storage-signal
coverage; signer and hosted-app replacement; writer use and revalidation of
native DXGI mapping; held-permit checks; and atomic artifact recovery.

## Provenance

The event source, monitor, generation protocol, tests, and this ADR are original
WinDayFlow work. They are not derived from QiDayflow and do not change the
QiDayflow-derived file set or provenance manifest hashes.

## Consequences

Foreground and desktop changes now have an explicit fail-closed ordering rather
than relying on polling cadence. Event storms remain cheap for the asynchronous
worker while every callback still invalidates stale authority. The additional
thread, generation phase, forced native update, and teardown state machine add
complexity, but make the callback-to-publication races testable.

The implementation remains intentionally disconnected from App composition.
Its event set now includes conservative location and topology invalidation,
current-session WTS, and suspend/resume with callback-time native closure, but
presentation and periodic storage notifications remain open and the foundation
cannot replace real-writer held-permit checks. This ADR must not be used as
evidence that WinDayFlow records frames or that Phase 1 has exited.

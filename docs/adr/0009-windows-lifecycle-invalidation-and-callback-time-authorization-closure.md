# ADR 0009: Windows Lifecycle Invalidation and Callback-Time Authorization Closure

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Interop`, `WinDayFlow.Capture.Native`, Windows
  privacy observation, and live capture activation

## Context

ADRs 0005 through 0008 establish stable foreground-target observation,
event-driven managed invalidation, bounded window-title reads, display-scoped
authorization, and strict DXGI output resolution. The existing WinEvent source
could invalidate foreground, desktop, and window-object changes, but it did not
receive display-topology, current-session, or suspend/resume notifications.
Those notifications require a window procedure and lifecycle registrations
owned by the same thread that pumps their messages.

The ADR 0006 callback path also closed the managed authorization latch before
returning, but its native persistence barrier was asynchronous. An older Allow
could already be entering native code while the callback invalidated its managed
observation. Waiting for the asynchronous Block was fail closed eventually, but
it did not close native command and permit admission at callback time.

This decision closes those foundation gaps without claiming a functional
recorder. System notifications are still observation hints, not positive target
or display identity. Callback-time closure prevents new native authority but
cannot cancel a persistence permit already held by a future writer. The real
writer, product-level Pause/Stop policy, and App composition therefore remain
separate activation boundaries.

## Decision

### One Owner Thread and a Hidden Top-Level Window

`WindowsCaptureWinEventSource` retains one dedicated background owner thread
and one Windows message pump for both WinEvent hooks and system lifecycle
notifications. The owner thread creates an application-private window class and
one zero-sized top-level window with:

```text
WS_POPUP
WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
parent = NULL
```

The window is never shown or activated. It is an implementation endpoint for
messages, not application UI. It must not be an `HWND_MESSAGE` window because
message-only windows do not receive broadcast messages such as
`WM_DISPLAYCHANGE`.

After ensuring that the owner-thread message queue exists, startup performs this
order on that thread:

1. register the private window class;
2. create the hidden top-level window;
3. call `WTSRegisterSessionNotification` with
   `WTS_NOTIFY_FOR_THIS_SESSION`;
4. call `RegisterSuspendResumeNotification` with
   `DEVICE_NOTIFY_WINDOW_HANDLE`; and
5. install the existing narrow WinEvent hooks.

`RegisterSuspendResumeNotification` and
`UnregisterSuspendResumeNotification` are imported from `user32.dll`. They are
not the differently shaped power-setting APIs exported by `PowrProf.dll`.
Session registration uses `wtsapi32.dll` and is intentionally scoped to the
current session rather than every interactive session.

Normal teardown begins by disconnecting application callbacks. It unhooks
WinEvent registrations in reverse order, drains queued messages, unregisters
the suspend/resume and WTS notifications, destroys the hidden window, and
unregisters the window class. Registration, unregistration, window destruction,
and class cleanup all occur on the owner thread. Partial startup failure rolls
back every completed step in the same ownership domain and fails closed.

The WinEvent callback and window procedure delegates remain rooted for their
complete native callback lifetime. If hook removal, window destruction, or
window-class cleanup cannot be proven, application callbacks stay detached and
the minimum callback bridge is conservatively retained. Cleanup and callback
failures expose stable enum values only; exceptions do not cross a native
callback boundary.

### Value-Free Lifecycle Events and Generation-Bound Holds

The hidden window procedure normalizes accepted messages into value-free change
kinds. It does not sample settings, titles, process identity, or capture state.
The mappings are:

- `WM_DISPLAYCHANGE` becomes `DisplayTopologyChanged`;
- WTS console or remote disconnect, logoff, lock, and terminate become
  `SessionUnavailable`;
- WTS console or remote connect, logon, unlock, create, and desktop-ready become
  `SessionAvailable`;
- WTS remote-control change becomes `SessionChanged`;
- an unknown WTS session notification is conservatively
  `SessionUnavailable`;
- `PBT_APMSUSPEND` becomes `PowerSuspending`; and
- `PBT_APMRESUMECRITICAL`, `PBT_APMRESUMESUSPEND`, and
  `PBT_APMRESUMEAUTOMATIC` become `PowerResumed`.

Other power notifications are not interpreted as authorization evidence and
flow to the default window procedure. An accepted `WM_POWERBROADCAST` returns
the documented handled result.

Every accepted change still advances a privacy-observation generation and
invalidates target continuity. Display-topology change is an invalidation hint:
it does not establish a new display mapping and does not create a persistent
hold. The subsequent verifier must issue a fresh target epoch and prove the
complete target/display observation again.

Session and power state add two independent monitor holds:

```text
SessionUnavailable
PowerSuspended
```

A session-unavailable or power-suspending callback sets its hold for the new
generation. While either hold is active, the worker completes the generation's
native Block barrier and publishes FailClosed without sampling Windows. Other
events cannot clear either hold. Session-available and power-resumed callbacks
clear only their corresponding hold; they still create a new invalidation
generation, require a new Block barrier, and require fresh sampling before a
resolved observation may publish. An old observation is never reused as
recovery evidence.

These holds are authorization safety state, not capture lifecycle commands.
They do not decide whether a live session should evidence-Pause or perform a
sticky Stop.

### Callback-Time Native Authorization Gate

For every accepted source callback, the monitor applies this synchronous order
before offering asynchronous wake work:

```text
replace signals with FailClosed, close the managed latch, and advance generation
-> close native authorization admission
-> invalidate verifier target continuity
-> offer one value-free worker wake token
```

The first two operations are implemented by the coordinator's
`InvalidatePrivacyObservation` boundary. A failure still leaves managed
authorization closed, attempts target invalidation, and terminates the monitor
with a sanitized fail-closed fault. Wake tokens may coalesce, but managed and
native invalidation generations advance for every accepted callback.

C ABI v1 adds this capability, result, and export:

```text
WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION = 1 << 11
WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED = -14
```

```c
wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_invalidate_runtime_authorization(
    wdf_capture_handle handle,
    uint64_t* authorization_epoch);
```

The export advances a native callback-invalidation epoch and atomically closes
the safety core's authorization-admission epoch. It does not take the unique
persistence gate and does not wait for a shared persistence permit already held
by a writer. A successful call returns a nonzero closed authorization epoch.
Epoch exhaustion still closes admission, marks the callback epoch exhausted,
and returns `WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED`. A later authorization
update also fails with generation exhaustion; recovery requires owner/handle
recreation.

The managed backend advances its own callback-invalidation generation, clears
its local persistence-boundary snapshot before and after the native call, and
requires the returned native authorization epoch to advance. It serializes
these short native invalidation calls so concurrent backend callers cannot make
valid epochs complete out of order, while invalidation remains concurrent with
a runtime Allow update. Shutdown first closes callback-operation admission and
drains an in-flight invalidation before destroying the native handle. A native
invalidation failure, duplicate or regressed native epoch, managed generation
exhaustion, or owner-lifecycle race is sticky and fail closed. Managed
generation exhaustion still attempts the native close before reporting failure.

The current runtime-owner capability mask is:

```text
PrivacyGuard | EventQueue | TargetScopedAuthorization |
PersistenceGenerationBarrier | DeterministicStop |
DisplayScopedAuthorization | CallbackTimeAuthorizationInvalidation |
DisplayBoundCommandAdmission
```

The DLL advertises these eight runtime-owner foundation capabilities. Legacy
`CommandAdmission` bit 8 remains defined for ABI recognition but is not
advertised by the display-scoped DLL. The safe screen-capture mask additionally
requires `ScreenCapture` and `H264Chunks`; `EvidenceExtraction` remains
independent.

### Supersession and Block Acknowledgement

Every native runtime-authorization update begins by closing admission and
captures the current callback-invalidation epoch in its update ticket. The
managed call also carries the callback-invalidation generation expected by the
observation that produced the update. These values distinguish two callback
races without treating a committed update as uncommitted.

If a callback supersedes an update before native commit, the update returns
`WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED`. It does not consume the proposed
runtime policy revision or advance the persistence generation. Managed code
reports `SupersededBeforeCommit` and retries only from a newly captured privacy
observation. An Allow that enters native after an unacknowledged callback is
rejected by the same result.

A restrictive runtime update acknowledges only the callback epoch captured by
its own current ticket. A callback that occurs after that ticket was created
supersedes the Block as well, so a stale Block cannot acknowledge a newer
invalidation. The coordinator may enter the generation's `BarrierApplied` phase
only after a current FailClosed Block completes. Only that phase permits at most
one resolved publication for the same generation. Native code will not reopen
Allow while its current callback-invalidation epoch remains unconfirmed.

If Allow has already committed its revision and persistence generation when a
callback wins the final reopen race, the native update returns success because
the commit occurred, but the atomic admission gate remains closed. The managed
backend detects the changed callback generation at its local persistence
boundary and reports `AppliedThenSuperseded`. The coordinator records the
consumed revision and persistence generation, keeps managed authorization
closed, and applies a compensating FailClosed Block with the next contiguous
runtime revision. It must not retry the committed revision.

This callback-specific result does not replace ordinary lifecycle semantics.
Stop, explicit revoke, and non-callback admission closure continue to use the
existing `RevokedDuringUpdate`/`INVALID_STATE` behavior. Callback-specific
supersession is accepted only when the corresponding managed or native
invalidation generation actually changed; an unexplained supersession faults
the owner rather than being normalized to a stale observation.

### Held Permits and Remaining Activation Boundary

Callback-time closure prevents new persistence tokens, persistence permits,
command admissions, and stale Allow reopening after the callback returns. It
does not abort a writer that already owns a shared persistence permit. That
holder may complete the single operation protected by its permit.

The subsequent FailClosed Block takes the unique side of the safety gate and
waits for existing shared permit holders. Once that Block returns, the existing
ADR 0003 guarantee applies: no work from the older generation can still be
persisted. A later Allow is forbidden until this Block acknowledgement is
current.

No real writer currently proves that its operation boundaries match this
contract. Before `ScreenCapture` can be enabled, the DXGI/WIC/Media Foundation
worker must revalidate target/display and current generation before and after
acquisition, obtain the correct permit for each persistence boundary, and carry
or reacquire authority through encoding, metadata output, temporary output,
atomic rename, and committed-event publication. Callback closure is not a claim
that an arbitrary in-progress codec or filesystem call can be canceled.

Display topology, current-session WTS changes, and suspend/resume now participate
in the invalidation foundation. The following activation gates remain open:

1. publisher identity bound to the opened running image;
2. unique child-application attribution for hosted Windows surfaces;
3. event integration for presentation state and periodic storage refresh;
4. explicit evidence-Pause versus sticky-session-Stop classification;
5. writer-side strict DXGI resolution, pre/post target and display validation,
   held-permit stage boundaries, atomic artifact recovery, and real lifecycle
   transition tests; and
6. App composition only after the complete safe screen-capture capability mask
   is returned by the packaged architecture-matching binary.

`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` remain disabled.
Authorized Start/Resume still returns `NOT_IMPLEMENTED` without a worker. The
App composition root continues to register `DenyCaptureRuntimeAuthorization`
and `UnavailableCaptureBackend`; the verifier, monitor, coordinator, and native
runtime owner remain inactive foundations.

## Verification

Deterministic WinEvent-source tests cover:

- hidden-window styles, top-level parent, window-procedure calling convention,
  and native DLL ownership;
- owner-thread window-class, window, WTS, power, and hook registration;
- message mapping, current-session WTS behavior, conservative unknown-session
  handling, and handled power results;
- reverse hook and notification cleanup, partial registration rollback,
  bounded startup/stop, callback failure, and uncertain callback-root lifetime;
  and
- a Windows-only smoke test that registers and cleans up the real window,
  current-session, suspend/resume, and WinEvent resources without a fault.

Deterministic monitor tests cover display-topology target invalidation,
generation-bound session and power holds, independent hold recovery, suspension
during sampling, no sampling while held, fresh barrier-before-recovery ordering,
and stale-observation rejection. Existing callback, generation, coalescing,
fault, and teardown tests remain required.

Native C/C++ and managed interop tests cover bit 11, result -14, export
callability, capability dependencies, callback-epoch monotonicity and
exhaustion, immediate admission closure, stale Allow and stale Block rejection,
pre-commit supersession without revision consumption, post-commit closure
without false failure, persistence-permit behavior, backend callback/dispose
races, and local persistence-boundary invalidation.

Coordinator tests cover callback ordering, sticky native-close failure,
generation exhaustion with a final native-close attempt,
`SupersededBeforeCommit`, `AppliedThenSuperseded`, contiguous compensating Block,
barrier-before-publication, quiescence, and disposal races. Debug and Release
native and managed suites must pass.

These tests close the system-event registration and callback-time admission
foundations. The real registration smoke does not synthesize every operating-
system transition, and no current test proves a DXGI frame-to-atomic-artifact
writer. Live lifecycle, held-permit stage, disk-full, access-loss, recovery, and
publication-order tests remain required before writer capabilities can be
advertised.

## Provenance

The hidden system-event window, WTS and suspend/resume integration,
generation-bound holds, callback-time native authorization contract,
implementation, tests, and this ADR are original WinDayFlow work. They are not
derived from QiDayflow and do not change the six files covered by
`docs/provenance/QiDayflow-capture.manifest.json`.

Any later copy or close adaptation of QiDayflow writer code still requires its
source header, provenance ledger, manifest hash, third-party notice, and pinned
upstream revision to be updated before distribution.

## Consequences

Foreground, window, display-topology, current-session, and suspend/resume
changes now enter one owner-thread invalidation path. The hidden top-level
window adds a native resource and cleanup surface, but it does not add visible
UI or another message thread. Restricting WTS registration to the current
session and reducing callbacks to value-free change kinds limits both noise and
sensitive data exposure.

Every callback now performs a small synchronous native admission close rather
than relying only on the later asynchronous Block. Event bursts may coalesce
sampling work but cannot coalesce authority invalidation. The additional ABI
capability, result, export, epochs, update outcomes, and tests increase the
state-machine surface in exchange for making old-Allow races explicit and fail
closed.

The guarantee is deliberately staged. Callback return closes new authority;
current Block acknowledgement drains old permit holders; only a later verified
Allow can reopen admission. Because an already-held permit is not canceled, the
real writer must still define and test its persistence stages. This ADR does not
enable capture, connect App dependency injection, select Pause versus Stop, or
satisfy a Phase 1 exit criterion.

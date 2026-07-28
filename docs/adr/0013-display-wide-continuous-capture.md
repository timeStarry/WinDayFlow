# ADR 0013: User-Authorized Display-Wide Continuous Capture

- Status: Accepted
- Date: 2026-07-28
- Decision owners: WinDayFlow maintainers
- Scope: application privacy settings, recording consent, Windows privacy
  monitoring, managed/native runtime authorization, native worker validation,
  dev-live manual QA, and future production activation
- Related: [ADR 0002](0002-capture-exclusion-rules.md),
  [ADR 0003](0003-native-capture-safety-core.md),
  [ADR 0006](0006-event-driven-capture-privacy-monitor.md),
  [ADR 0008](0008-display-scoped-authorization-and-dxgi-output-resolution.md),
  [ADR 0009](0009-windows-lifecycle-invalidation-and-callback-time-authorization-closure.md),
  [ADR 0011](0011-authority-checked-native-capture-worker-orchestration.md),
  and [ADR 0012](0012-run-isolated-native-capture-instance-control.md)

## Context

The original runtime authorization is bound to one foreground HWND, PID,
process creation time, target epoch, and display. A foreground change therefore
closes native admission, drains the persistence boundary, pauses the worker,
verifies the next target, issues a new authorization, and resumes. That is the
right default for application and window exclusions.

It is not merely a presentation problem. Revoking target-scoped authority can
discard the current unpublished partial chunk, so frequent application switches
can create timeline gaps of up to the active chunk duration. Hiding short
Pause/Resume states in the UI would reduce flicker without restoring evidence
continuity. Shorter chunks reduce the maximum loss but do not remove the
authorization churn or its resource and state-machine cost.

Some users prefer a wider, explicit recording scope so normal work across
multiple applications on one monitor remains continuous. This choice must not
silently weaken the conservative default, inherit old consent, or weaken system
lifecycle boundaries.

## Decision

WinDayFlow persists one of two application privacy modes:

| Mode | Authorization scope | Application/window policy | Foreground switching |
| --- | --- | --- | --- |
| `ProtectByForegroundApplication` | Verified foreground target and its display | Built-in sensitive-application policy and ordered application/window exclusions apply | Revoke, verify, and rebuild as required |
| `AllowAllApplications` | One display selected and pinned when recording starts | Application and window exclusions are retained but temporarily inactive | Ordinary application and cross-display focus switches leave the pinned target unchanged during the active recording |

`ProtectByForegroundApplication` is the default and recommended mode.
`AllowAllApplications` is an explicit wider-scope choice. "All applications"
means ordinary content visible on the one display pinned for the recording; it
does not mean all monitors, the lock screen, the secure desktop, another Windows
session, or content captured outside the existing lifecycle and storage policy.

### Persistence and Consent

Schema v7 adds the constrained singleton setting
`capture_application_privacy_mode`, where `0` is
`ProtectByForegroundApplication` and `1` is `AllowAllApplications`. Migration
from an older schema writes the default value and does not by itself change
capture state, privacy revision, or consent.

A user-initiated mode change is an effective privacy-policy change. It must:

1. close managed and native admission through the settings Prepare barrier;
2. advance the persisted privacy revision exactly once;
3. disable capture in the same transaction;
4. preserve the previous consent only as consent for its old revision;
5. complete the recording Stop boundary; and
6. require the user to read the current scope disclosure, consent to the new
   revision, and explicitly enable capture again.

The same requirements apply when returning to the conservative mode. Stored
application/window rules and their revisions are never deleted by a mode
change. Their controls are inactive while `AllowAllApplications` is selected and
become effective again when the user returns to
`ProtectByForegroundApplication`.

### Compatible C ABI Scope

C ABI v1 retains the 224-byte `wdf_capture_runtime_authorization_v1` layout and
its 112-byte legacy prefix. This decision adds no export and no structure field.
It assigns:

- capability bit 12,
  `WDF_CAPTURE_CAPABILITY_DISPLAY_WIDE_CONTINUOUS_AUTHORIZATION`, to prove that
  both sides implement the display-wide semantics; and
- target-flag bit 2, `WDF_CAPTURE_TARGET_DISPLAY_WIDE_SCOPE`, inside the existing
  `target_flags` field.

A foreground authorization sets `TARGET_PRESENT | TARGET_DISPLAY_PRESENT` and
supplies the complete HWND/PID/process-creation/target-epoch/display tuple. A
display-wide authorization sets
`TARGET_DISPLAY_WIDE_SCOPE | TARGET_DISPLAY_PRESENT`, keeps HWND, PID, and
process-creation time zero, and supplies a positive target epoch plus HMONITOR
and bounded display device key. Foreground-present and display-wide flags are
mutually exclusive. Unknown flags, partial tuples, or display-wide use without
capability bit 12 fail before native update or command admission.

The admission nonce, runtime revision, persistence generation, target epoch,
authorization epoch, native instance, and runtime-owner epoch remain unchanged.
Display-wide scope does not bypass any permit, nonce, generation, or committed-
event check.

### Authorization and Event Boundaries

After renewed consent, the foreground verifier resolves one stable ordinary
target to select the display. The managed coordinator converts that observation
to display-wide identity. Native stage checks double-read and compare the fixed
HMONITOR/device key instead of requiring the original foreground HWND to remain
active. DXGI Desktop Duplication or its AccessDenied-only Windows Graphics
Capture fallback stays bound to that same display.

The normal continuous path is:

```text
verified foreground target selects display
-> display-wide authorization and command admission
-> Recording
-> ordinary application and cross-display focus changes leave the display pinned
-> the current chunk continues and rolls over normally
```

While capture is Starting, Recording, Pausing, Paused, Resuming, or Stopping,
ordinary foreground and window-object changes do not call callback-time native
invalidation, advance the persistence generation, move the capture target,
publish authorization Pause/Resume events, or discard the in-progress partial
chunk. This includes activating a window on another display: capture remains
bound to the original display, and the other display is not recorded. The UI
must therefore remain in the recording state rather than repeatedly flash
"restoring recording."

After capture reaches Stopped, Unavailable, or BlockedByConsent, foreground
events may establish the display for the next recording. Faulted remains pinned
while the runtime owner enters terminal teardown; it cannot prepare a reusable
next run. To change the recording display through the current UI, the user waits
for capture to stop completely, moves the WinDayFlow window to the intended
display, and starts again there. This explicit run boundary is required because
the current native authorization represents exactly one display; silently
following focus would reintroduce authorization churn and risk discarding the
unfinished chunk, while silently retaining more than one display would widen
collection.

Storage headroom is observed on a separate low-frequency path, not by forcing a
periodic foreground verification. The initial full observation establishes its
baseline; the current dev-live policy then samples only the storage decision
every five seconds. Repeating the same Allow, Block, or Unknown value is a no-op:
it does not advance observation or persistence generation, invalidate the
target epoch, run the foreground verifier, or interrupt a healthy chunk pinned
to its original display.

When the storage decision changes, the refresh path uses the callback-time
fail-closed contract before asynchronous work: it closes managed and native
admission, invalidates the old observation, and wakes the generation-bound Block
and full-observation worker. A recoverable read failure becomes Unknown and must
not retain the previous Allow. If storage later becomes available, recovery
still requires a fresh barrier and authorization. A nonrecoverable refresh fault
is terminal and fail closed. Monitor shutdown cancels and awaits the refresh so
no late storage result can reopen authority.

Display-wide authority is still a fixed display identity. Merely activating a
window on another monitor does not move or rebuild that identity. A display loss
or display-topology change crosses a fail-closed boundary. The runtime never
implicitly widens one authorization to multiple displays, and it does not reuse
an old HMONITOR without revalidating the device key and generation for a new run.

The following remain independent, fail-closed boundaries in both modes:

- lock screen, input desktop, secure desktop, and current-session WTS changes;
- suspend/resume and other power invalidation;
- display loss and display-topology change;
- unknown or unavailable storage and storage-headroom failure;
- capture disable, consent revocation, privacy-mode change, runtime fault, and
  shutdown; and
- explicit user Pause or Stop, which remains authoritative and sticky rather
  than being undone by automatic recovery.

Remote Desktop and Windows Presentation Mode continue to follow their separately
persisted policies. Unknown required signals remain fail closed. A safety event
first closes new native admission, then applies the generation-bound Block that
drains persistence authority; stale work cannot publish after that boundary.

Committed manifests distinguish the scopes:

- `authorized-foreground-display` for the default mode; and
- `authorized-display-continuous` for display-wide continuous mode.

This scope is privacy-safe metadata, not proof that production capture is
enabled.

### User Disclosure and Experience

The continuous-mode warning must state that every ordinary application visible
on the display selected at Start may enter local evidence. This includes
WinDayFlow's own Settings UI and provider configuration; the default
special-case protection for the WinDayFlow window is an application-level
exclusion and is therefore suspended. Users should close or move sensitive
content before enabling the broader mode. The warning must also state that focus
moving to another display neither records that display nor moves the recording
target. To change the target, the user must wait for Stop to complete, move the
WinDayFlow window to the intended display, and Start again there.

Local retention, deletion, and storage controls remain unchanged. Cloud
analysis remains separately off by default and requires its own provider test,
disclosure, and enablement. Selecting continuous capture does not authorize a
network request.

The experience benefit is concrete: normal application and cross-display focus
switching no longer triggers visible pause/recovery churn, repeatedly
reconstructs target authorization, or creates partial-chunk gaps. That
improvement is purchased with a wider local collection scope on the pinned
display, so the choice cannot be enabled by migration, inferred from past
settings, or hidden behind a generic recording toggle.

## Verification and Activation Gate

Automated coverage must include:

- schema-v7 migration defaulting and rejection of unknown persisted values;
- atomic privacy-revision advancement, capture disablement, stale consent, and
  retained exclusion rules on each mode transition;
- UI warning, renewed-consent flow, and inactive rule controls in continuous
  mode;
- x64 224-byte layout stability, target-flag validation, capability-bit
  negotiation, and old/new managed-native mismatch rejection;
- native safety-core permits, command admission, and display-only stage
  revalidation without foreground HWND dependence;
- active-recording foreground/window event suppression with no authorization,
  persistence-generation, or target-display churn, including focus moving to a
  different display;
- stopped-state foreground selection of the next recording display, with no
  active-run target movement and no implicit multi-display authorization;
- independent storage-only cadence, unchanged-decision no-op behavior, and no
  foreground verification or target-epoch churn during healthy refreshes;
- storage threshold and recoverable-read-failure transitions closing admission
  without a window event, Unknown-before-recovery behavior, and refresh-task
  cancellation during teardown;
- immediate fail-closed behavior for desktop, WTS, power, topology, storage,
  consent, mode, shutdown, and explicit user commands;
- worker continuity across application and cross-display focus switches,
  partial-chunk preservation, display reselection after Stop completes and the
  WinDayFlow window moves, topology invalidation, and manifest scope; and
- restart recovery and capture-to-analysis ingestion for both manifest scopes.

The consent-gated x64 dev-live smoke must run both modes for longer than one
chunk and inspect state transitions plus committed artifacts. It must also
exercise lock, secure desktop, WTS, power, display, storage, consent, and user
Stop transitions. The continuous pass keeps storage healthy for at least three
refresh periods and verifies stable recording/target epochs, then uses a
controlled low-headroom volume or injected read failure to prove the next
refresh closes admission without a foreground event and does not reuse Allow.
This remains a development QA path. The production App keeps
`UnavailableCaptureBackend`, and the production native controller remains
disabled until the complete production identity, lifecycle, persistence,
recovery, and real-device acceptance gates pass in a separate reviewed change.

## Alternatives Considered

### Keep Target Scope and Coalesce the UI

Rejected as the only solution. It can hide short transitions but cannot prevent
permit revocation, partial-chunk discard, or timeline gaps.

### Shorten Every Chunk

Rejected as the primary solution. It bounds loss but increases file, manifest,
queue, extraction, and recovery overhead and still pauses at each app switch.

### Capture Every Display

Rejected. It silently increases collection scope, resource cost, and privacy
risk beyond the display the user selected.

### Remove Application Protection Globally

Rejected. Conservative foreground protection remains the default; the wider
scope requires an explicit revision-bound choice.

## Consequences

- Users can choose uninterrupted evidence from one display pinned at Start and
  a more stable recording experience across application and focus switches.
- The conservative default and all system lifecycle boundaries remain intact.
- Healthy low-frequency storage checks add no foreground-target churn; a storage
  transition intentionally crosses the same fail-closed rebuild boundary as
  other safety changes.
- Mode changes are intentionally disruptive once: capture stops and consent must
  be renewed before collection resumes.
- Application/window exclusions are dormant, not erased, in continuous mode.
- WinDayFlow UI and other sensitive ordinary applications may be recorded in
  continuous mode; disclosure and visible state are mandatory.
- Native and managed code must maintain two target-validation forms and reject
  partial capability support.
- Focus moving to another display does not move the recording target. Selecting
  another target requires Stop to complete, moving the WinDayFlow window there,
  and a new Start; display loss or topology change remains a legitimate fail-
  closed evidence boundary.
- This decision validates only the gated dev-live implementation and does not
  authorize production capture.

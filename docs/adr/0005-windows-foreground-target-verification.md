# ADR 0005: Windows Foreground Target Verification

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Interop`, Windows privacy observation, and live
  capture activation

## Context

ADR 0003 requires every runtime authorization and native persistence permit to
be bound to a foreground target tuple and a monotonically ordered target epoch.
ADR 0004 additionally binds Start/Resume command admission to that target and
epoch. Neither contract defines how managed code obtains a stable HWND/PID pair,
distinguishes PID reuse, selects the associated display, or observes the
application identity needed by the exclusion-rule matcher.

A single Windows API read is not sufficient. The foreground window, its owner,
the process behind a reused PID, its title, or its monitor may change while an
observation is being assembled. Some Windows surfaces are hosted by
`ApplicationFrameHost.exe`, so accepting the host executable as the real
application could bypass an application-specific exclusion. Handles, titles,
package identities, display keys, and process paths are also sensitive values
that must not leak through diagnostics.

The verifier must therefore produce a size-bounded, stable target slice that
fails closed across ambiguity. It must not become a second policy engine, a
substitute for event-driven invalidation, a DXGI output resolver, or persistence
authority. A character bound alone is not an execution-time bound: direct
`GetWindowTextW` can block for a window owned by the current process.

## Decision

WinDayFlow implements `WindowsCaptureTargetVerifier` as a synchronous,
serialized foreground-target observer. The implementation establishes the
foundation required by the target-scoped safety core, but it does not complete
the live target-verification activation gate or enable capture.

### Result Contract and Ownership

One verification returns exactly three slices:

- `NativeCaptureTargetIdentity`: HWND, PID, process creation time, and target
  epoch for the native runtime-authorization tuple;
- `WindowsCaptureDisplayTarget`: a managed-only HMONITOR and display device key;
  and
- `NativeCaptureIdentitySnapshot`: typed executable-basename, package-family,
  publisher-certificate, and window-title observations for exclusion matching.

The result is not a complete `NativeCapturePrivacySignals` value. The verifier
does not read committed settings, order exclusion rules, choose Allow or Block,
issue a command admission, or mint a persistence permit. The runtime coordinator
must compose the result with the complete committed settings and current Windows
signals and then submit one authoritative decision to native code.

All default verifier instances share one locked process-wide source that
atomically owns the current fingerprint, an invalidated/gap state, and the last
issued epoch. Every verifier construction invalidates the current process-wide
fingerprint. Stable verification resolves through that source, and every
`Absent` or `Unknown` result invalidates it again. Reconstructing a verifier
therefore obtains a higher epoch for its first stable target instead of
restarting at 1 and creating an ABA with an older authorization.

An older overlapping verifier has no private epoch that it can revive. After a
construction or gap invalidates the source, its next stable observation must
resolve against the current process-wide fingerprint: it either receives the
current global epoch for that exact fingerprint or advances to a new epoch for a
different fingerprint.

The shared source does not make live verifier replacement valid. When live
integration is added, one verifier instance must still be scoped to the same
runtime owner and native-handle lifecycle. Code must not replace the verifier
while retaining a native handle whose authorizations or pending work refer to
the old fingerprint and invalidation history.

### Stable Windows Observation

On the supported Windows 10 version 1809+ x64 baseline, one call performs this
fixed-size sequence under the verifier lock. The present implementation does not
yet impose a wall-clock deadline on the sequence:

1. Read the foreground HWND. A successful zero HWND is known `Absent`.
2. Read the owning TID/PID and resolve `MonitorFromWindow` to an HMONITOR plus
   the `GetMonitorInfoW` device key.
3. Open the owner process with
   `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`.
4. Read the PID from the opened handle, process creation time, and process
   liveness. These must agree with the window owner and describe an active
   process.
5. Read a first title observation, executable basename, package-family name,
   publisher-certificate observation, and a second title observation. Both
   title state and value must remain identical.
6. Re-read PID, creation time, liveness, foreground HWND, owner TID/PID, and
   display anchor. Every stability field must still match the first read.

The process handle is released on every path. Unsupported platforms short
circuit to `Unknown` without invoking the remaining native API. A required
target, process, or display read failure; permissions ambiguity; process exit;
or any change during the sequence invalidates the remembered target and returns
`Unknown`. Recoverable platform exceptions also fail closed. Fatal runtime
exceptions are not disguised as ordinary observation failures.

### Size-Bounded Identity Observation

Each matcher input is represented as `Unknown`, known `Absent`, or validated
`Present`. Identity read failures and malformed values remain field-scoped
`Unknown`; they are never converted to `Absent` or treated as a non-match. The
exclusion matcher and policy composer retain responsibility for failing closed
when an enabled rule requires an unresolved field.

The identity readers apply these boundaries:

- The process image API may place a complete path in a temporary pooled buffer,
  but only its executable basename escapes the P/Invoke method. The buffer is
  cleared before it is returned.
- `APPMODEL_ERROR_NO_PACKAGE` is the only package-family result interpreted as
  known `Absent`. Unexpected status, inconsistent sizing, or oversized output
  is `Unknown`.
- Window-title, image-path, and package-family buffers have fixed maximum sizes.
  Pooled buffers are cleared before reuse. These character limits do not bound
  elapsed call time.
- Publisher-certificate identity remains `Unknown`. A path-only certificate
  lookup cannot prove that a signer is bound to the opened running image.

The current title reader calls `GetWindowTextW` directly. For a window owned by
the current process, that API can wait on window-procedure work and block the
verifier indefinitely. Before live activation, title observation must have a
tested wall-clock deadline, return `Unknown` on timeout, and prevent a delayed
completion from publishing into a newer observation generation. The timeout
mechanism must not leak unbounded workers or outlive native handles it uses.

If the observed executable basename is `ApplicationFrameHost.exe`, the complete
verification returns `Unknown` and clears the remembered target. Live capture
may not degrade to the host identity. A future implementation must attribute a
hosted surface to exactly one real child application or continue to fail closed.

### Display Anchor and Target Epoch

HMONITOR plus the case-insensitive display device key is an observation anchor,
not proof of a DXGI output. The stable target fingerprint contains:

```text
HWND | PID | process creation time | owner TID | HMONITOR | display device key
```

The first stable fingerprint receives a nonzero target epoch from the shared
process source. The same process-wide current fingerprint retains that epoch.
Any fingerprint change, including owner-thread or display-key change, receives
the next process-wide epoch. An `Absent`, `Unknown`, or verifier construction
clears the global current fingerprint, so a later stable observation receives a
new epoch even when the numeric tuple appears unchanged.

The process source is monotonic and never wraps. Once it has issued
`ulong.MaxValue`, an existing verifier may continue to return that already-issued
epoch for its unchanged fingerprint, but no verifier in the process can ever
issue a later value. Any subsequent gap, fingerprint change, or verifier
recreation that requires issuance returns `Unknown` for the rest of the process
lifetime. Recovery requires a new process or a future explicitly persisted epoch
namespace. The per-verifier lock prevents concurrent calls from interleaving an
observation sequence; the source lock atomically orders fingerprint resolution,
global invalidation, and issuance across verifier instances.

### Privacy and Diagnostic Boundary

Target, display, identity-snapshot, and complete-result `ToString()` methods
report only observation states and replace values with `[REDACTED]`. Raw HWND,
PID, creation time, title, executable identity, package identity, signer hash,
HMONITOR, and display key must not enter logs, exception messages, native events,
or status details. Values remain available only to the in-process matcher,
coordinator, and native authorization fields that require them.

### Live-Activation Boundary

This decision satisfies only the stable synchronous-observation portion of the
ADR 0003 target gate. Live activation remains blocked by all of the following:

1. Publisher identity must be verified offline against the opened running image
   and represented by the primary signer leaf certificate DER SHA-256. A path
   observed independently of the process handle is insufficient.
2. Hosted Windows surfaces must be attributed to one real child application.
   Unresolved or multiple attribution remains `Unknown`.
3. Window-title observation must have a tested wall-clock deadline, return
   `Unknown` on timeout, and reject late completion from an invalidated
   observation.
4. A WinEvent-driven monitor must synchronously invalidate the process capture
   latch and observation generation when a relevant event arrives, before any
   asynchronous target parsing or policy recomposition.
5. HMONITOR/device key must map to the actual DXGI output. The writer must
   revalidate target and display before and after acquisition and carry the same
   epoch through the native persistence-permit boundary.
6. Real DXGI/WIC/Media Foundation acquisition, encoding, metadata, temporary
   output, atomic final publication, cleanup, and recovery must use that permit
   end to end.

The event monitor must also classify each lock, exclusion, target, display, and
`Unknown` transition as an evidence Pause or sticky session Stop. None of these
items is implied by a successful synchronous `Verify()` call.

`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` remain disabled. The App
composition root continues to register `DenyCaptureRuntimeAuthorization` and
`UnavailableCaptureBackend`; no live frame or context metadata is acquired or
persisted by this milestone.

## Required Verification

Deterministic managed tests cover stable repeated observation, target changes,
PID reuse, owner and foreground races, title instability, display instability,
process identity/creation/liveness changes, `Absent` and `Unknown` gaps,
recoverable API failures, field-scoped malformed identity, unresolved
`ApplicationFrameHost.exe`, process-handle disposal, value redaction, and epoch
exhaustion. Verifier recreation uses the same injected source to prove that an
epoch cannot be reused. A second test overlaps verifier instances and proves
that a gap observed by one instance prevents the other from reviving its old
epoch. Display-key changes, package-family
`Present`/`Absent`/`Unknown`, unsupported-platform short circuiting, concurrent
epoch stability, process-wide source ordering, and permanent source exhaustion
must also remain explicit regression cases.

A Windows P/Invoke test opens the current process and verifies the required
query and synchronize rights, stable PID and creation-time reads, liveness, and
basename-only executable observation. It also asserts that publisher identity
is still `Unknown`, preventing a placeholder implementation from being mistaken
for signer proof.

Future activation tests must inject WinEvent/worker races and prove synchronous
generation invalidation, a blocked title read returning `Unknown` within its
deadline without late publication or unbounded worker growth, unique hosted-app
attribution, image-bound signer verification, DXGI output mapping, acquisition
pre/post revalidation, stale permit rejection, atomic publication ordering,
disk-full cleanup, restart recovery, and teardown across display and process
replacement.

## Provenance

The verifier contract, P/Invoke implementation, tests, and this ADR are original
WinDayFlow work. They are not derived from QiDayflow and do not change the
QiDayflow-derived file set or provenance manifest hashes.

## Consequences

Runtime authorization can now be built on a stable, reusable-target-resistant
Windows observation rather than a single HWND or PID read. The extra reads and
serialization make foreground, owner, process, title, and display races explicit
and fail closed. Buffer size and call count are bounded, but direct title reading
is not yet time-bounded and therefore remains unsuitable for live activation.

The output is intentionally narrower than a policy decision and weaker than a
writer permit. This keeps user-rule evaluation, event invalidation, DXGI output
selection, and persistence linearization in their existing ownership domains
and prevents the verifier milestone from being presented as a functional
recorder.

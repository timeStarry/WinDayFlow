# ADR 0003: Native Capture Safety Core

- Status: Accepted
- Date: 2026-07-16
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Native`, `WinDayFlow.Capture.Interop`, and live
  capture activation

## Context

ADR 0001 established a versioned C ABI and a fail-closed native privacy
context. ADR 0002 established typed application and window exclusion rules.
Those contracts do not, by themselves, prove that a frame or context record
acquired under an older decision cannot be persisted after the foreground
target or privacy state changes.

The foundation DLL also exposes stop, wait, and destroy functions, but a live
backend needs stronger ownership semantics than the no-worker foundation can
exercise. Live activation requires one auditable boundary for target identity,
authorization generations, persistence permits, worker quiescence, and
capability negotiation.

## Decision

ADR 0008 later extends this accepted 112-byte prefix to a 224-byte compatible
structure and makes a display anchor part of every fully allowed target. The
original offsets and `reserved[8]` contract below remain unchanged.

WinDayFlow keeps C ABI major version 1 and adds a native safety core before
connecting a real DXGI writer. The safety core is independently testable with
synthetic work. The Windows target verifier, system-event monitor, and
callback-time native invalidation gate now use this boundary in deterministic
tests, but it remains unavailable as a recorder until the real capture writer,
atomic artifact publisher, and App composition use the same boundary.

### Additive Runtime Authorization Contract

The ABI adds a flat, C-compatible `wdf_capture_runtime_authorization_v1`
structure. It is 112 bytes on the supported x64 ABI, begins with
`struct_size` and `abi_version`, contains no pointers or variable-length text,
and uses the existing fixed numeric policy-decision values.

| Offset | Field | C type | Contract |
| ---: | --- | --- | --- |
| 0 | `struct_size` | `uint32_t` | Caller-provided size; at least 112 |
| 4 | `abi_version` | `uint32_t` | `WDF_CAPTURE_ABI_VERSION` |
| 8 | `runtime_policy_revision` | `uint64_t` | Monotonic managed policy ordering |
| 16 | `target_epoch` | `uint64_t` | Changes whenever the selected foreground target changes |
| 24 | `target_window_handle` | `uint64_t` | Numeric HWND identity, never dereferenced across the ABI |
| 32 | `target_process_creation_time_100ns` | `uint64_t` | Distinguishes PID reuse |
| 40 | `target_process_id` | `uint32_t` | Foreground owner PID |
| 44 | `target_flags` | `uint32_t` | Bit 0 is `PRESENT`; all other bits are zero |
| 48 | `consent_granted` | `int32_t` | Existing tri-state policy decision |
| 52 | `session_unlocked` | `int32_t` | Existing tri-state policy decision |
| 56 | `secure_desktop_clear` | `int32_t` | Existing tri-state policy decision |
| 60 | `remote_session_allowed` | `int32_t` | Existing tri-state policy decision |
| 64 | `presentation_allowed` | `int32_t` | Existing tri-state policy decision |
| 68 | `application_allowed` | `int32_t` | Existing tri-state policy decision |
| 72 | `window_allowed` | `int32_t` | Existing tri-state policy decision |
| 76 | `storage_available` | `int32_t` | Existing tri-state policy decision |
| 80 | `reserved` | `uint32_t[8]` | Zero; additive ABI tail |

The target identity is the tuple of numeric HWND, PID, and process creation
time, further scoped by `target_epoch`. A numeric HWND or PID is never accepted
on its own because Windows may reuse both. `target_flags.PRESENT` is required
for an Allow; absent, unknown, malformed, or zero target fields cannot mint a
persistence permit. Failure to resolve or revalidate any field is Unknown and
fails closed.

`runtime_policy_revision` orders managed policy updates. It is not a
persistence permit. The native instance epoch and persistence generation are
native-owned state recorded in an internal/output permit token, not fields in
the runtime-authorization input. Both are nonzero and monotonic within their
scope. The generation changes on revoke, target replacement, or any other
transition that invalidates acquired work. Exhaustion faults the instance and
requires recreation; neither value is wrapped or reused.

Target-scoped runtime authorization starts at revision 1 and then advances by
exactly one. An identical same-revision update is idempotent, a different
same-revision value conflicts, and stale or skipped revisions fail closed. The
native call returns the effective persistence generation through an output
parameter. Native events retain their 80-byte ABI size and use the additive
tail for the persistence generation and target epoch, allowing managed code to
reject an event from stale work without receiving private target values.

The existing `wdf_capture_update_privacy_context` entry point remains for ABI
compatibility and may only preserve or reduce authority. It can apply Block,
but an Allow received through that legacy structure can never mint a
persistence permit. Only a valid target-scoped runtime authorization can do so.
The legacy and target-scoped revision counters are independent: legacy calls
retain their original positive, monotonically increasing revision rules, while
a pristine target-scoped sequence starts at 1 and then advances exactly by 1.
The first valid legacy update synchronously revokes target authority and
permanently taints that native handle against later target-scoped updates.
Recreating the handle is required before target-scoped authorization can be
used again; this prevents incomparable legacy and runtime revisions from
reviving an older Allow.

### Permit Linearization and Persistence

Every acquired frame or context sample carries an immutable native token with
its target tuple, target epoch, native instance epoch, and persistence
generation. The managed policy revision is folded into the native generation
when authorization changes and is not duplicated in the token. Acquisition
alone grants no right to write.

Immediately before encoding, metadata output, final rename, or a committed
event, the writer acquires the shared side of the safety-core gate and validates
the complete snapshot against the current Allow. A successful permit retains
shared ownership through the corresponding write and atomic publication.
Temporary work that cannot obtain or retain a valid permit is discarded and
cannot produce metadata, a final artifact, or a committed event.

Block or an effective revoke takes unique ownership, waits for existing shared
permit holders to release, advances the native persistence generation, and
installs the restrictive authorization. A repeated revoke of an already
revoked instance is idempotent and returns the current generation. Completion
of the unique operation is the linearization point: after it returns, no work
from an older generation or target can be persisted.

This rule applies equally to pixels and context metadata. A later Allow never
revives a permit or acquired item from an older generation.

The bit-11 callback-time invalidation export closes native authorization
admission with one lock-free atomic transition before a Windows callback
returns. An Allow superseded before native commit does not consume a runtime
revision. If the Allow commits immediately before the callback wins, the result
is reported as applied-then-superseded and the coordinator submits the next
restrictive revision. Only a completed Block update acknowledges that callback
invalidation for later reauthorization. This gate closes new admission; a real
writer must still revalidate any already-held permit at every acquisition and
publication stage.

### Stop, Join, Destroy, and Managed Ownership

`request_stop` is nonblocking. It closes permit admission, enters `STOPPING`,
and signals every owned worker. `wait_stopped` is bounded and returns success
only after workers and finalizers have exited and no persistence permit remains.
One native `destroy` call for a valid handle is blocking: it revokes
authorization, requests stop when needed, joins owned workers, closes the event
queue, releases native resources, and invalidates the caller handle. It never
waits for managed event dispatch. The managed owner guarantees exactly-once
destroy for that handle; callers must not reuse the opaque value after return.

Capture.Interop owns a single asynchronous runtime owner above the coordinator
and native handle. Owner termination and coordinator quiescence are
single-flight, idempotent, and do not accept caller cancellation. Once
termination is requested, it prevents new managed lifecycle calls, applies
Block/revoke, requests stop, waits for join, and destroys the handle in that
order. Cancellation of a preceding caller operation cannot restore
authorization or interrupt cleanup. A timeout or native failure permanently
quarantines the handle generation and keeps capture unauthorized; it is never
resumed or reused.

`SafeHandle` remains a final fallback, not evidence of successful quiescence.
Normal shutdown and every activation failure use the explicit asynchronous
owner.

The Boolean `IsCaptureAuthorized` observation is not accepted as live command
authority. ADR 0004 satisfies this safety-core activation gate with a
native-issued, owner-bound, single-use Start/Resume admission stamp. It binds
the expected persistence generation and target epoch, plus native and managed
owner generations, and consumes the stamp under the current fully allowed
authorization. A stamp from another owner or instance, or any mismatch, fails
closed before capture work starts. Pause and Stop do not require an Allow stamp.

Dynamic privacy transitions also need a product-level action contract. The
Windows monitor now keeps independent fail-closed session-unavailable and
power-suspended holds until matching availability/resume events are reverified.
It still does not distinguish an evidence Pause, which blocks new permits while
retaining a quiescent session, from a sticky session Stop, which performs full
teardown. Lock, application/window exclusion, and Unknown signals must each be
classified and tested across recovery and target changes.

### Capability Gate

ABI v1 adds distinct capability bits for target-scoped authorization,
generation-guarded persistence, deterministic stop, and command admission. The
complete live recording mask requires all of:

```text
PrivacyGuard | EventQueue | TargetScopedAuthorization |
PersistenceGenerationBarrier | DeterministicStop |
DisplayScopedAuthorization | CallbackTimeAuthorizationInvalidation |
DisplayBoundCommandAdmission |
ScreenCapture | H264Chunks
```

Unknown additive bits are ignored. Known dependency violations are rejected;
`ScreenCapture` alone is never sufficient. Evidence extraction remains a
separate capability.

The current DLL advertises the eight runtime-owner foundation capabilities,
including bit 11 `CallbackTimeAuthorizationInvalidation`, but deliberately
leaves `ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` off. The App
continues to register `DenyCaptureRuntimeAuthorization` and
`UnavailableCaptureBackend`, and Start/Resume remain unavailable. A development
or synthetic safety test must not advertise writer capabilities or persist live
user evidence.

### Activation Gates

The safety core is necessary but not sufficient for live capture. Activation
still requires all of the following to use the contract end to end. Display
topology, current-session WTS, suspend/resume invalidation, session/power holds,
and callback-time native admission closure are implemented foundations.

1. Image-bound publisher-signer verification and unique hosted-application
   attribution for Windows surfaces.
2. Presentation notifications, periodic storage observation, and tested
   evidence-Pause versus sticky-session-Stop classification.
3. The real DXGI/WIC/Media Foundation writer and metadata path carrying the
   consumed command grant and acquisition snapshot through every persistence
   boundary, with held-permit and callback-generation revalidation at each
   stage.
4. Atomic temporary-file completion, rename, committed-event ordering, cleanup,
   interruption, disk-full, and recovery tests against real filesystem output.
5. Managed composition-root activation only after the complete capability mask
   is returned by the packaged architecture-matching binary.

## Required Verification

The sixth native CTest target, `capture_safety_core_tests`, deterministically
covers target/PID reuse, target and instance epochs, generation races,
acquire-to-persist invalidation, callback invalidation before and after Allow
commit, Block acknowledgement, Block/revoke linearization, stop/join/destroy,
timeouts, injected failures, concurrency, and idempotence. Tests use explicit
barriers rather than timing sleeps.

`capture_c_api_tests` covers the additive exports, command authenticity, and
capability dependencies; the C17 header test covers the legacy 112-byte prefix,
the ADR 0008 224-byte authorization, and the 64-byte command-admission sizes,
offsets, numeric constants, and C
callability. Managed interop tests cover both layouts, complete-mask
negotiation, owner call order, one-shot stamps, quiescence, timeout/failure
quarantine, and cancellation semantics. Debug and Release native and managed
suites must pass.

These tests and the Windows verifier/system-event integration suites prove only
the safety foundations, not a recorder. The synthetic suite races both
orderings of an Allow A-to-B replacement against Start/Resume, rejects stale
and foreign admission stamps, and validates the complete expected snapshot.
Before `ScreenCapture` can be enabled, the same grant must be carried through a
real worker and persistence path, and evidence Pause versus sticky Stop recovery
must be exercised against live Windows transitions.

## Provenance

The safety-core contract, implementation, tests, managed owner, and this ADR
are original WinDayFlow work. They are not derived from QiDayflow and do not
change the six local derived-file hashes in
`docs/provenance/QiDayflow-capture.manifest.json`.

If a later writer copies or closely adapts QiDayflow `capture_service.*` or
changes an existing derived file, its source header, ledger, manifest hash,
third-party notice, and last-verified commit must be updated under the existing
provenance workflow before distribution.

## Consequences

The additional structure, capability bits, epochs, and owner add explicit state
and test surface. In return, a live recorder cannot be enabled by a single
optimistic capability bit, stale Windows identifiers cannot silently inherit
authorization, and Block has a precise no-old-write-after-return guarantee.

The safety core intentionally delays live activation. Windows target
observation, topology/WTS/power invalidation, session/power holds, and the
callback-time native gate are foundations only; this decision does not claim
DXGI acquisition, Media Foundation output, App integration, or a real writer's
held-permit revalidation.

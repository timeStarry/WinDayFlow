# ADR 0004: Owner-Bound Capture Command Admission

- Status: Accepted
- Date: 2026-07-17
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Application`, `WinDayFlow.Capture.Interop`,
  `WinDayFlow.Capture.Native`, and live capture activation

## Context

ADR 0003 established target-scoped runtime authorization, persistence
generations, shared write permits, deterministic stop, and a managed native
owner. Its Boolean authorization observation was sufficient for status and
early rejection but not for Start or Resume authority. A fully allowed target A
could be replaced by fully allowed target B after the Application layer checked
the Boolean and before a tokenless backend command entered native code.

Live capture therefore needs a single-use command authority that is issued and
consumed by the same native and managed owner generations. It must fail closed
across target replacement, persistence invalidation, settings changes, runtime
shutdown, foreign owners, wrong operations, tampering, replay, and caller
cancellation without making Pause or Stop depend on an Allow decision.

## Decision

WinDayFlow keeps C ABI major version 1 and adds owner-bound command admission
for Start and Resume. The contract is implemented and tested in the synthetic
foundation, but it does not activate screen capture or a real writer.

### Application Contract

`ICaptureRuntimeAuthorization.TryIssueAdmissionAsync` issues an opaque
`ICaptureRuntimeAdmissionStamp` for exactly one `Start` or `Resume` operation.
`ICaptureBackend.StartAsync` and `ResumeAsync` require that stamp. Pause and
Stop remain authorization-reducing lifecycle commands and require no stamp.

`ConsentGatedCaptureService` serializes lifecycle calls and applies this order:

1. Check persisted capture enablement and current-version recording consent.
2. Ask the runtime owner to issue an operation-specific stamp.
3. Recheck persisted authorization, the runtime Boolean, and the process
   invalidation generation.
4. Pass the same stamp to the backend once.

A denial or stale stamp becomes the existing consent-required boundary. The
service does not silently refresh or retry it. The Boolean remains useful for
UI state and early rejection, but only the stamp authorizes Start or Resume.

### Managed Owner and Linearization

`NativeCaptureRuntimeOwner` is the only managed issuer and consumer. Its private
stamp binds the issuer reference, operation, invalidation generation, runtime
policy revision, persistence generation, target epoch, and native admission
structure. Consumption is atomic and single use. A foreign stamp, wrong
operation, malformed snapshot, or replay throws the stable
`CaptureRuntimeAdmissionRejectedException`.

The privacy coordinator uses its existing `_applyGate` for authoritative
settings and signal mutation, native issuance, and command consumption. It does
not introduce a second command gate. This gives two valid Allow A-to-B
linearizations: the command consumes A before the update closes admission, or
the update wins and the A stamp is rejected. No command can be admitted under a
mixed snapshot.

Caller cancellation is honored before native consumption. After the command
enters native consumption, the bounded call uses `CancellationToken.None`; this
prevents managed code from reporting cancellation after native code accepted a
command. Expected admission, policy, and lifecycle-state rejection is nonfatal.
Malformed native output, generation regression, ABI failure, or internal native
failure faults the coordinator and starts owner teardown.

An explicit Stop revokes native authority and advances the persistence
generation. After Stop completes, the coordinator reapplies the current
settings and signals under `_applyGate`, advances the runtime revision, and only
then republishes authorization. A later Start therefore requires a fresh stamp.

### Additive Native ABI v1 Contract

The ABI adds these stable results and capability:

```text
WDF_CAPTURE_RESULT_ADMISSION_REQUIRED = -12
WDF_CAPTURE_RESULT_ADMISSION_REJECTED = -13
WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION = 1 << 8
```

It also adds a flat 64-byte `wdf_capture_command_admission_v1` structure:

| Offset | Field | C type |
| ---: | --- | --- |
| 0 | `struct_size` | `uint32_t` |
| 4 | `abi_version` | `uint32_t` |
| 8 | `instance_epoch` | `uint64_t` |
| 16 | `runtime_policy_revision` | `uint64_t` |
| 24 | `persistence_generation` | `uint64_t` |
| 32 | `target_epoch` | `uint64_t` |
| 40 | `authorization_epoch` | `uint64_t` |
| 48 | `nonce_low` | `uint64_t` |
| 56 | `nonce_high` | `uint64_t` |

The additive exports are:

```c
wdf_capture_issue_command_admission(
    handle,
    command,
    expected_persistence_generation,
    expected_target_epoch,
    out_admission);
wdf_capture_start_authorized(handle, admission);
wdf_capture_resume_authorized(handle, admission);
```

The legacy `wdf_capture_start` and `wdf_capture_resume` exports remain callable
for ABI compatibility but return `ADMISSION_REQUIRED` for every valid handle.

### Native Authenticity and Consumption

The native safety core generates a 128-bit nonce with `BCryptGenRandom` and
stores one pending issued record. The record binds the command, complete target
tuple, native instance epoch, runtime authorization epoch, persistence
generation, runtime revision, and internal runtime owner epoch. A new issue
invalidates any previous unconsumed record.

The nonce is not a derivation of observable generations. A matching nonce is
consumed before the remaining fields, operation, owner epoch, and current
authorization are checked. Consequently, tampering, wrong-action use, stale
state, and replay cannot recover or reuse a matching record. A nonmatching
nonce cannot consume the valid record. RNG failure returns an internal error and
issues no authority.

The lock order for command admission is:

```text
capture instance state mutex
-> command record mutex
-> safety shared gate
-> runtime owner mutex
```

Authorization update or revoke first closes the atomic authorization epoch and
then takes the unique safety gate. A command that already owns the shared permit
linearizes first; otherwise consumption observes the closed or changed epoch
and fails. The runtime owner rechecks its epoch when Start or Resume actually
uses the move-only grant, covering owner changes after issuance.

### Capability and Activation Gates

The native runtime-owner mask is:

```text
PrivacyGuard | EventQueue | TargetScopedAuthorization |
PersistenceGenerationBarrier | DeterministicStop | CommandAdmission
```

An older three-capability safety DLL can remain ABI-compatible for probing, but
it cannot construct the managed runtime owner. The complete live recording mask
also requires `ScreenCapture` and `H264Chunks`; `EvidenceExtraction` remains
independent.

The current DLL advertises the runtime-owner mask only. It leaves
`ScreenCapture`, `H264Chunks`, and `EvidenceExtraction` clear. A valid authorized
Start or Resume is consumed and then returns `NOT_IMPLEMENTED` with no worker or
evidence writer. The App composition root continues to register
`DenyCaptureRuntimeAuthorization` and `UnavailableCaptureBackend`.

Live activation still requires a real Windows target verifier, event-driven
privacy monitor, evidence-Pause versus sticky-session-Stop policy, real
DXGI/WIC/Media Foundation worker and persistence path, atomic artifact tests,
and packaged composition-root capability negotiation. The real worker must
carry the move-only command grant and persistence snapshot through its actual
write boundaries before `ScreenCapture` can be enabled.

## Required Verification

The C17 header and managed layout tests cover the 64-byte size, every field
offset, numeric results, commands, capability, and callability. Export and
capability tests cover legacy rejection and dependency masks. Native safety and
C API tests cover zero/failed random output, tampering, wrong action, foreign
and recreated handles, issue overwrite, matching-nonce single consumption,
replay, both Allow A-to-B orderings, Stop/revoke/destroy invalidation, runtime
owner epoch changes, and idempotent close/reopen behavior.

Managed tests cover persistent and runtime checks, invalidation generation,
foreign and forged stamps, wrong operation, replay, cancellation before native
consumption, non-cancelable consumption after entry, settings/signal races,
expected rejection without fatal teardown, malformed/native failure quarantine,
no automatic stamp refresh, and fresh authorization after Stop. Debug and
Release native and managed suites are release evidence for this contract.

## Provenance

The command-admission contract, implementation, tests, and this ADR are
original WinDayFlow work. They are not derived from QiDayflow and do not change
the QiDayflow-derived file set or provenance manifest hashes.

## Consequences

Start and Resume now have explicit, single-use authority rather than relying on
a Boolean observation. The additional ABI fields, nonce state, owner checks,
and tests increase implementation surface, but make target/generation races and
replay fail closed at the worker-admission boundary.

This decision closes one activation gate only. It does not claim that the
foundation records frames, persists metadata, observes live Windows targets, or
implements the final dynamic Pause/Stop policy.

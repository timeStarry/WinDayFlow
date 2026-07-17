# ADR 0008: Display-Scoped Authorization and DXGI Output Resolution

- Status: Accepted
- Date: 2026-07-17

## Context

ADR 0005 established a stable Windows display anchor for each verified
foreground target: a numeric `HMONITOR` and the bounded GDI device name returned
by `GetMonitorInfoW`. The target epoch already changes when that anchor changes,
but the anchor remained managed-only. `WindowsCapturePrivacyMonitor` published
only `NativeCapturePrivacySignals`, the 112-byte runtime-authorization ABI had
no display fields, and the native safety core therefore could not bind a
persistence permit or command admission to a display.

An `HMONITOR` or a device name alone is not proof of the DXGI output to acquire.
Desktop duplication must enumerate every adapter and output, reject incomplete
or ambiguous matches, and retain a stable output fingerprint that does not
depend on enumeration order. Primary-display, cursor-display, nearest-display,
and first-output fallbacks could silently capture a different user's content
after a topology change and are not acceptable.

This decision closes the display-contract and output-resolution foundations. It
does not activate capture or claim that a real writer revalidates the binding.

## Decision

### One authorization identity

`NativeCaptureTargetIdentity` carries the complete managed authorization tuple:

- HWND;
- process ID;
- process creation time;
- process-wide target epoch;
- numeric `HMONITOR`; and
- the GDI display device key.

A `Present` identity requires every field. Unknown or absent identities clear
every field. Display keys compare with ordinal case-insensitive semantics,
matching Windows device-name behavior. The independent
`WindowsCaptureDisplayTarget` observation remains useful at the Windows
boundary, but a present observation is valid only when its monitor and key
equal the values embedded in the native target identity. This prevents the
policy and display slices of one sample from being mixed.

The existing generation-bound monitor sink can continue to publish one
`NativeCapturePrivacySignals` value because its target now contains the display
anchor. Display-only changes participate in target equality, coordinator
deduplication, native same-revision checks, persistence tokens, and issued
command-admission records.

### Additive C ABI v1 tail

The first 112 bytes of `wdf_capture_runtime_authorization_v1`, including its
existing `reserved[8]`, remain byte-for-byte unchanged. The old reserved fields
remain required to be zero. The display extension is appended:

| Offset | Field | Type | Contract |
| ---: | --- | --- | --- |
| 112 | `target_display_monitor_handle` | `uint64_t` | Nonzero numeric `HMONITOR` when present |
| 120 | `target_display_device_key_utf8_length` | `uint32_t` | 1 through 93 bytes, excluding NUL |
| 124 | `target_display_reserved` | `uint32_t` | Zero |
| 128 | `target_display_device_key_utf8` | `char[96]` | Strict UTF-8; unused bytes are zero |

The extended structure is 224 bytes with packing 8. Ninety-three bytes cover
the worst-case UTF-8 representation of the 31 non-NUL UTF-16 code units in
`MONITORINFOEXW.szDevice`; the fixed 96-byte storage keeps the ABI naturally
aligned and avoids caller-owned pointers.

`WDF_CAPTURE_TARGET_DISPLAY_PRESENT` is target flag bit 1. A fully allowed
authorization requires both target-present flags and a complete 224-byte
target/display tuple. A restrictive authorization clears both flags and all
target/display values. A display key is nonempty, not whitespace-only, contains
no control characters, decodes to at most 31 UTF-16 code units, and leaves its
fixed buffer tail zeroed.

Wire-structure compatibility is intentionally asymmetric and fail closed;
capability negotiation prevents either incompatible runtime owner from opening:

- exactly 112 bytes is a legacy input;
- 113 through 223 bytes is a partial extension and is rejected;
- 224 bytes or more provides the complete known extension; unknown later tail
  bytes are ignored;
- a legacy restrictive authorization remains accepted;
- a legacy fully allowed authorization is rejected because it cannot name a
  display; and
- an old DLL may still pass ABI probing, but it cannot satisfy the new runtime
  owner capability mask; and
- an old managed owner sees legacy command-admission bit 8 absent on the new
  DLL and fails closed during capability negotiation.

### Capability dependencies

`WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION` is bit 9. The legacy
target/generation/stop capability trio remains an independently valid probe
profile. Display-scoped authorization requires that complete trio plus the
privacy-guard and event-queue foundation.

Legacy `WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION` remains defined as bit 8 for
source and binary recognition, but a display-scoped DLL does not advertise it.
`WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION` is bit 10 and proves
that the unchanged 64-byte command stamp is backed by a private issued record
containing the complete target/display identity. This distinction is required
for two-way capability negotiation: an old managed client sees bit 8 absent and
refuses to create its runtime owner, while a new managed client rejects an old
DLL that lacks bits 9 and 10. Neither direction reaches a first Allow update
under a capability profile it does not understand.

The managed runtime owner requires:

```text
PrivacyGuard | EventQueue | TargetScopedAuthorization |
PersistenceGenerationBarrier | DeterministicStop |
DisplayScopedAuthorization | DisplayBoundCommandAdmission
```

The safe screen-capture mask additionally requires `ScreenCapture` and
`H264Chunks`. This milestone advertises display-scoped authorization and
display-bound command admission but keeps both writer capabilities and
`EvidenceExtraction` disabled.

### Strict DXGI resolver

The native resolver enumerates every `IDXGIAdapter1` and every `IDXGIOutput`.
It succeeds only when exactly one usable output matches both the numeric
`HMONITOR` and `DXGI_OUTPUT_DESC.DeviceName`, using Windows ordinal
case-insensitive comparison for the name. A usable output must:

- be attached to the desktop;
- have a nonzero monitor handle;
- have a nonempty device name;
- have a rectangle with positive width and height; and
- report Identity, 90, 180, or 270 degree rotation.

Zero matches, multiple matches, monitor-only matches, name-only matches,
adapter/output enumeration failure, `GetDesc` failure, malformed descriptors,
and unknown rotation all fail closed. The resolver never falls back to a
primary, cursor, nearest, first adapter, first output, or global output index.

A successful fingerprint owns the adapter LUID, monitor handle, canonical
device name, full desktop rectangle, and rotation. Adapter/output enumeration
order is not identity. Before returning, the resolver checks factory freshness,
rereads the selected adapter and output descriptions, checks freshness again,
requires the complete fingerprint and desktop attachment to remain unchanged,
and obtains `IDXGIOutput1`. Empty COM results fail closed even if an API reports
success. The resolver is original WinDayFlow boundary code and does not copy
QiDayflow's resolver implementation.

### Remaining runtime proof

The resolver and display-scoped safety tuple are necessary but not sufficient
for recording. Before enabling `ScreenCapture`, the real native worker must:

1. resolve and compare the current display before acquisition;
2. revalidate foreground target and display after acquisition;
3. reject a frame that cannot obtain the current persistence permit;
4. carry the same permit through encode, temporary output, atomic rename, and
   committed-event publication; and
5. revoke on topology change, DXGI access loss, session/lifecycle changes, or
   any ambiguous revalidation.

Display topology notifications are hints, not proof. Callback-time native
admission closure, periodic revalidation, worker token transfer on Start and
Resume, and the complete writer lifecycle remain separate activation gates.

## Verification

Native tests cover the 224-byte size and offsets, legacy Block/Allow behavior,
partial tails, target/display flag parity, strict UTF-8 and zero-fill rules,
case-insensitive display identity, target/display mismatch, and propagation
through persistence and command-admission records. Resolver tests use
deterministic candidate sets to cover unique matches, ambiguity, partial
matches, malformed descriptors, rotation, and adapter LUID retention. An
injected low-level DXGI seam covers factory, adapter, output, description, and
`IDXGIOutput1` failures, partial catalogs, ownership, freshness changes, and
selected-description races without depending on test-machine display hardware.

Managed tests cover x64 layout and marshalled bytes, required capability masks,
complete target construction, redacted formatting, observation consistency,
display-only authorization changes, and real-DLL compatibility. Debug and
Release managed and native suites must pass.

## Provenance

The ABI extension, managed display flow, safety-core changes, resolver, tests,
and this ADR are original WinDayFlow work. They do not modify the six files
derived from the pinned QiDayflow revision and do not require a provenance
manifest hash change. Any later copy or close adaptation of QiDayflow capture
service code still follows the existing provenance workflow.

## Consequences

Runtime authorization can no longer be fully allowed without naming one stable
Windows display anchor, and native permits cannot treat two display bindings as
the same target. The strict resolver provides one reusable, testable mapping
contract for the future writer without making enumeration order authoritative.

The ABI and test surface are larger, and old managed clients cannot authorize
capture against the new native runtime. That incompatibility is deliberate:
compatibility probing remains additive, while missing display authority fails
closed. App composition remains on the unavailable backend until the real
writer and every remaining activation gate are complete.

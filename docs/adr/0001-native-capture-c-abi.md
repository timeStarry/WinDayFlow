# ADR 0001: Versioned C ABI for Native Capture

- Status: Accepted
- Date: 2026-07-16
- Decision owners: WinDayFlow maintainers
- Scope: `WinDayFlow.Capture.Native` and `WinDayFlow.Capture.Interop`

## Context

WinDayFlow needs a narrow boundary between the managed application and a C++20
capture engine that uses DXGI Desktop Duplication, WIC, and Media Foundation.
The managed application already owns recording consent and feature-facing
lifecycle policy. The native component must own capture, encoding, extraction,
and the lifetime of its native resources without depending on WinUI, managed
domain assemblies, or Flutter.

The reviewed QiDayflow implementation is compiled into a Flutter runner and
uses C++ standard-library types, `std::function` callbacks, MethodChannel and
EventChannel values, and runner window messages. Those types and delivery
mechanisms do not form a stable DLL boundary. The architecture also requires
coarse events, deterministic shutdown, explicit compatibility checks, and
privacy enforcement before any frame or context record is persisted.

## Decision

The first native capture implementation will be a standalone, x64 Windows DLL
with a versioned C ABI consumed by `WinDayFlow.Capture.Interop` through managed
P/Invoke. ARM64 support will follow after the x64 boundary, test suite, and
packaging behavior are stable. The ABI design must remain architecture-neutral
enough that ARM64 does not require a contract redesign.

### Export and Calling Convention

- Public functions use `extern "C"` to prevent C++ name mangling.
- Public functions use an explicit `__cdecl` calling convention even though
  Windows x64 has one platform calling convention.
- Exports use a WinDayFlow-owned prefix and a controlled export definition.
- The public header is valid C as well as C++; it exposes no templates,
  overloads, classes, exceptions, references, or C++ standard-library types.
- The DLL exports an ABI version query. A managed adapter must verify the
  supported major version before creating a capture handle.

### Data Layout and Versioning

- Boundary values use fixed-width integer types and explicitly documented
  numeric enum values.
- Public input and output structures begin with `struct_size` and
  `abi_version` fields.
- A callee rejects an incompatible major version or a structure smaller than
  the minimum required size. It ignores supported unknown tail fields to allow
  additive evolution within a compatible version.
- Handles are opaque pointer-sized tokens. Managed code never dereferences or
  frees their storage directly.
- Text is UTF-8 with explicit byte lengths. Embedded NUL bytes are rejected
  where a path or identifier is required.
- Variable data uses caller-owned buffers and a required-size result. Native
  allocations are not freed by a different C runtime.
- Complete video chunks never cross the ABI as memory buffers. Chunk events
  identify committed artifacts. Bounded extracted JPEG evidence uses a
  separate extraction API rather than a method on the lifecycle interface.

### Error and Exception Containment

- Every exported function is implemented as `noexcept` and contains a catch-all
  boundary for C++ exceptions. No C++ exception, including allocation and
  filesystem exceptions, may unwind into managed code. The `/EHsc` build policy
  does not translate or catch asynchronous SEH, so access violations and other
  asynchronous hardware faults are outside this containment guarantee.
- Functions return a stable numeric result code. Display text is retrieved
  separately into a caller-owned UTF-8 buffer and is not used for retry or
  state-machine decisions.
- Invalid handles, null required pointers, unsupported versions, invalid
  structure sizes, invalid UTF-8, invalid state transitions, and insufficient
  buffers have distinct stable results.
- Internal HRESULT values may be included as diagnostic fields, but HRESULT is
  not the only public result vocabulary.

### Handle and Lifecycle Contract

- Creation returns one opaque handle that owns the engine, worker, event queue,
  privacy guard, and native resources for that instance.
- Lifecycle commands are thread-safe at the ABI boundary and report whether a
  transition request was accepted.
- Start validates the complete configuration before creating artifacts or
  starting capture work.
- Pause and resume are explicit commands. Resume re-evaluates every privacy
  guard before another frame can be persisted.
- `request_stop` is nonblocking. It prevents new capture work and requests that
  a valid partial chunk be finalized.
- `wait_stopped` waits up to a caller-supplied timeout and distinguishes a
  completed stop from a timeout or failure.
- Destroy is blocking. It requests stop if necessary, joins every owned worker,
  releases COM, DXGI, WIC, and Media Foundation resources, closes diagnostics,
  and invalidates the handle before returning.
- Destroy never depends on managed event processing. Because the public ABI
  does not invoke managed callbacks, destruction cannot deadlock waiting for a
  callback on the UI thread.

### Polled Event Queue

Native worker threads never invoke managed callbacks. They publish coarse
state, chunk-committed, and error events into a thread-safe, bounded queue owned
by the handle. `WinDayFlow.Capture.Interop` polls the queue on a managed
background task and marshals results to application services or a UI dispatcher
after leaving native code.

Each event has its own structure size, ABI version, monotonically increasing
sequence number, type, timestamp, and stable state or error code. Variable text
and artifact identifiers use the caller-buffer convention. No frame pixels are
published as lifecycle events.

Queue capacity and overflow behavior are explicit and tested. Redundant state
observations may be coalesced, but a chunk-committed event or terminal error
must never be silently dropped. If the queue cannot preserve a required event,
the engine enters a stable faulted state and stops accepting new capture work.
Polling supports a bounded timeout so managed shutdown and cancellation do not
depend on an uninterruptible native wait.

### Privacy Guard Precondition

The DLL is not considered an available capture backend merely because DXGI and
Media Foundation initialize. Start and resume remain unavailable until the
pre-persistence privacy guard is implemented and verified.

The guard makes one conservative decision before both frame persistence and
context-metadata persistence. It covers current recording authorization,
application and window exclusions, sensitive-context policy, session lock,
unknown session state, secure desktop, Remote Desktop policy, presentation or
screen-sharing policy, sleep, resume, and session switching. Unknown or failed
guard state fails closed. A state transition between acquisition and write is
rechecked so a frame acquired before a lock or exclusion transition cannot be
persisted afterward.

The application-level consent gate remains authoritative for user-facing
Start and Resume commands. The native precondition is defense in depth and
protects the persistence boundary; it does not allow the DLL to invent consent
or silently weaken managed policy.

### Platform and Build Scope

- The first supported binary is x64 and is built and tested on a supported
  Windows runner with the MSVC C++20 toolchain.
- The native project links only the required Windows libraries and does not
  link Flutter, a Flutter wrapper, generated plugin registrants, WinUI, or the
  managed runtime.
- The DLL is packaged beside the architecture-matching managed application and
  loaded from a controlled application location rather than an arbitrary
  search path.
- ARM64 uses the same source-level ABI contract and receives its own binary,
  packaging checks, native tests, and managed interop tests before support is
  declared.
- x86 is not part of this decision.

### Provenance

Any QiDayflow-derived source follows
`docs/provenance/QiDayflow-capture.md`. Original and local hashes, modification
summaries, the upstream copyright notice, and the complete upstream MIT notice
must be maintained before a derived DLL is distributed.

## Required Verification

Acceptance of this ADR does not make native capture complete. The first usable
backend must provide evidence for:

1. ABI version negotiation, structure-size compatibility, invalid argument
   handling, buffer sizing, stale handles, concurrent commands, and exception
   containment.
2. Event ordering, sequence monotonicity, timeout cancellation, queue
   backpressure, required-event preservation, and shutdown without managed
   event draining.
3. Start, pause, resume, request-stop, wait, blocking destroy, partial-chunk
   finalization, repeated stop, and startup or shutdown failure paths.
4. Privacy decisions before frame and metadata writes, including transition
   races and fail-closed behavior for every context named above.
5. Active-display selection, display rotation, topology changes, DXGI access
   loss, device reset, lock and unlock, sleep and resume, and session changes.
6. Atomic MP4 and metadata completion, collision handling, disk-full behavior,
   long Unicode paths, path containment, crash leftovers, and recovery.
7. H.264 timing and frame counts plus extraction count, JPEG, per-frame byte,
   total-byte, path, corruption, and near-duplicate bounds.
8. Clean x64 native and managed builds, native regression tests, managed P/Invoke
   contract tests, and a packaged diagnostic run on supported Windows versions.

## Consequences

The C ABI adds explicit translation code and requires careful ownership and
buffer conventions. It also prevents accidental exposure of unstable C++ or
Flutter implementation details, makes compatibility testable before capture
starts, isolates native exceptions, and gives x64 and future ARM64 packages one
clear contract.

Polling adds a managed background task and bounded queue policy. In return,
native workers do not re-enter managed or UI code, event delivery is observable,
and shutdown does not depend on dispatcher availability.

The privacy precondition delays declaring the backend available until the
safety boundary is real. A diagnostic build may exercise initialization and
synthetic contract tests earlier, but it must not persist live user evidence
without the guard.

## Alternatives Considered

### C++ ABI

Rejected because standard-library layout, compiler version, exception, runtime
allocation, and name-mangling details would become part of the binary contract.

### Flutter MethodChannel and EventChannel

Rejected because WinDayFlow is a WinUI application and the Flutter runner,
messenger, value, and window-message lifetime would become an unnecessary
runtime dependency.

### Direct Native-to-Managed Callbacks

Rejected for the first boundary because callbacks complicate thread affinity,
reentrancy, object lifetime, queue backpressure, and deterministic destruction.

### C++/WinRT Component

Deferred rather than prohibited. It offers Windows projections but adds
projection and packaging complexity before the capture contract is stable. A
future adapter may use C++/WinRT internally or above the same native engine only
if it preserves the application-facing behavior and does not invalidate this
versioned boundary without a replacement ADR.

# ADR 0012: Run-Isolated Native Capture Instance Control

- Status: Accepted
- Date: 2026-07-20
- Decision owners: WinDayFlow maintainers
- Scope: native C ABI instance ownership, lifecycle publication, authorization
  replacement, and deterministic Stop finalization

## Context

ADR 0011 composed the Windows capture components behind an authority-checked
worker, but the public C ABI still owned authorization, lifecycle state, and the
runtime mailbox separately. Authorized Start/Resume consumed a command stamp
and returned `NOT_IMPLEMENTED`; Pause published a state without pausing a
worker. This split could not safely activate the worker.

The old Stop path also let every `WaitStopped` caller independently join, revoke,
and publish STOPPED. One old waiter could therefore finish after a newer run had
started and revoke that newer run. Authorization replacement had a second race:
closing the persistence gate during any worker stage caused the worker to exit
before a resumable Pause was visible.

## Decision

### One Instance Concurrency Boundary

`CaptureInstanceController` is the single owner of:

- the bounded event queue;
- `CaptureSafetyCore` authorization and command admission;
- the capture backend and worker;
- lifecycle state and the active run record; and
- `CaptureRuntimeOwner` thread and control-mailbox ownership.

The C ABI retains only versioned-structure validation, handle registry leases,
copying, and public result mapping. It delegates authorization, admission,
Start/Pause/Resume/Stop, event polling, and shutdown to the controller. Member
declaration and explicit shutdown order ensure the runtime joins before the
worker, backend, safety core, or event queue can be destroyed.

### Disabled and Enabled Activation Modes

The controller has two explicit modes:

- `Disabled` is used by the production C ABI in this milestone. It consumes a
  valid Start/Resume admission exactly once and returns `NOT_IMPLEMENTED`. It
  creates no worker thread, calls no backend method, and publishes no fake live
  transition.
- `Enabled` is used by deterministic native tests. It drives the real controller
  state machine with an injected backend.

The public capability mask still omits `ScreenCapture`, `H264Chunks`, and
`EvidenceExtraction`. The managed adapter now checks the complete screen-capture
capability before authorized Start/Resume and performs no P/Invoke when that
capability is closed. Live worker activation and the two native capture
capabilities must be enabled in one reviewed change.

### Run Identity and State Checkpoints

Each accepted enabled Start allocates a controller-local monotonic `run_id`.
This ID is independent from the runtime owner epoch, which also changes during
Pause, Resume, Stop, and worker exit. Worker checkpoints, completion callbacks,
waiters, and terminal publication capture their run ID and ignore stale IDs.

State is published from completed work rather than optimistic commands:

```text
Start accepted        -> STARTING
first authorized frame encoded -> RECORDING
Pause accepted        -> PAUSING
old chunk cleared and acquisition reset -> PAUSED
Resume accepted       -> RESUMING
fresh token's first authorized frame encoded -> RECORDING
Stop accepted         -> STOPPING
join and revoke done  -> STOPPED
fatal worker exit     -> ERROR with state FAULTED
```

The Start worker waits behind a per-run gate until STARTING is appended, so a
fast backend cannot publish RECORDING first. The runtime completion callback is
invoked only after `worker_exited` is visible. A Paused checkpoint carries its
Pause epoch and must match the controller's expected epoch. Display-acquisition
initialization alone does not publish Ready: Starting or Resuming remains
visible through timeouts and first-frame failures, and each fresh Resume token
must pass its first Acquire, Transform, Begin, and Encode authority post-checks
before the worker may publish one Ready checkpoint.

### Authorization Replacement

Every active authorization replacement, including Allow-to-Allow target
refresh, first requests a provisional runtime Pause while holding the controller
boundary. It then closes or replaces native authorization. The worker routes
authorization loss from every sensitive stage through one handler:

- Stop intent discards sensitive state and exits without an unauthorized
  partial finalize;
- a new Pause epoch clears the old chunk and frames, resets acquisition,
  acknowledges Paused, and waits for a fresh admitted Resume token; and
- authorization loss with no Pause or Stop intent remains a fatal error.

An allowed replacement that initiates a Pause uses a neutral reason. A blocked
replacement uses the privacy decision's concrete reason. While Pausing, a later
non-neutral authorization decision may refine an existing automatic reason, but
a neutral Allow does not erase it. A user-initiated Pause remains UserPaused
through folded authorization updates. Once Paused is acknowledged, its reason
remains stable until Resume or Stop. Managed policy must still decide whether a
blocked context remains paused and later resumes or becomes a sticky Stop.

### Stop Single Flight

Each run owns a shared run record with leader, completion, cached-result, and
condition-variable state. Exactly one waiter performs:

```text
runtime WaitStopped
-> worker join
-> FinalizeRevoke
-> run-ID-guarded STOPPED publication
-> cancel unused terminal reservations
-> cache the run and controller terminal result
-> detach the completed run and wake followers
```

Followers only wait for and return the cached result. A leader timeout releases
leadership without completing the run; a leader exception also releases
leadership before returning an internal error. State remains STOPPING and a
later waiter can take over. Shutdown uses the same unbounded
RequestStop/WaitStopped path.
Stopping an authorized instance before a worker starts uses a synthetic stop
record so authorization is still finalized. Terminal publication, unused
reservation release, result caching, and active-run detachment occur under one
controller-to-run lock order. A caller that reaches `WaitStopped` during that
atomic detachment receives the retained controller terminal result instead of a
synthetic success. If an already-full required-event queue prevents a synthetic
Stop from reserving its control events, the run record still completes revoke;
`WaitStopped` reports the delivery failure instead of leaving authorization
open.

An enabled run reserves required queue capacity for STOPPING, STOPPED, and ERROR
before spawning. Unused reservations are canceled after terminal publication
and before the run detaches. This keeps control delivery available when required
chunk events occupy the rest of the queue and prevents a replacement run from
observing the old run's reserved capacity.

## Verification

The native `capture_instance_controller_tests` CTest executable
covers disabled admission consumption, no backend calls, revoke compatibility,
STARTING/Ready/Pause/Resume/Stop ordering, stale run callbacks, two-waiter Stop,
stale deferred-Stop rejection across replacement runs, leader timeout and
takeover, leader-exception takeover, pre-Start revoke, fatal ERROR publication,
atomic terminal-failure sharing, required-queue saturation, non-neutral
automatic Pause-reason refinement, sticky user Pause reasons, reservation
release, and destructor join ordering.

Worker tests cover Ready/Paused/Ready checkpoint order, no early Ready on
timeout or first-frame failure, exactly-once Ready after a first authorized
encoded frame, per-Resume readiness, callback execution after permit release,
and provisional Pause recovery during Initialize, Acquire, Finalize, and Commit
with a fresh generation. Runtime-owner tests prove the completion callback
observes the worker as exited. C ABI tests retain the versioned header,
admission, destroy-race, and disabled-mode compatibility contract.

Debug and Release each pass all 18 native CTest executables. No automated test
captures the user's desktop.

## Provenance

The controller, run-record protocol, worker checkpoint additions, completion
callback, and their tests are original WinDayFlow work. They copy no additional
reference-project source, so the derived-source manifest is unchanged.

## Consequences

The native instance now has one testable lifecycle and authority owner, and the
old stale-waiter revoke window is closed. Authorization replacement can keep a
worker alive without allowing old evidence to cross generations.

This decision does not make the development bundle a recorder. App composition,
the managed pause-versus-stop supervisor, crash recovery and stale staging
replay, evidence-root ACL/reparse hardening, disk-full integration, a real
consent-gated Desktop Duplication smoke test, and downstream evidence extraction
remain activation gates.

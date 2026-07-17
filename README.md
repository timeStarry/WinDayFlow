# WinDayFlow

WinDayFlow is an independent, Windows-native, local-first automatic work
journal. Its design combines the zero-input, context-aware work journal and
review experience of [Dayflow](https://github.com/JerryZLiu/Dayflow), the
Windows capture and reliable processing foundations of
[QiDayflow](https://github.com/liujiaqi7998/QiDayflow), and WinDayFlow's own
WinUI 3, Fluent, Windows Shell, and accessibility-first implementation.

The goal is not to copy either application. WinDayFlow aims to give Windows
users a quieter, more transparent, and more elegant DayFlow experience that
feels at home on the platform.

## Why WinDayFlow

The project is guided by these design principles:

- **Native to Windows.** Use WinUI 3, standard caption and window behavior,
  responsive `NavigationView` navigation, system theme and contrast, keyboard
  navigation, and meaningful accessibility automation.
- **Zero input, with user control.** Minimize manual tracking while keeping
  capture state, evidence use, corrections, exclusions, and deletion visible
  and understandable.
- **Local first and explicit about cloud use.** Keep recordings, settings, and
  journal data on the device by default; any future provider integration must
  disclose exactly what leaves it.
- **Reliable over long-running sessions.** Treat capture, analysis, persistence,
  retries, recovery, and shutdown as durable workflows rather than transient UI
  tasks.
- **Useful without AI.** Keep timeline review, editing, search, deterministic
  metrics, and export independent from optional generated summaries. New local
  evidence remains visible as unprocessed intervals instead of being turned
  into invented semantic activity when a provider is unavailable.
- **Graceful across supported Windows versions.** Use enhanced system visuals
  where available without making them a requirement for a coherent interface.

## Current Status

WinDayFlow is in active implementation. The first persistent, no-capture
vertical slice and the settings privacy foundation are complete. The repository
contains:

- a working WinUI 3 Fluent shell with a custom system-integrated title bar,
  responsive navigation, and a retained hamburger menu;
- versioned SQLite migrations plus local timeline and settings repositories,
  initialized before the main window opens;
- a timeline review surface with date navigation, search, filters, and durable
  manual activity creation, editing, and deletion;
- a settings surface with persistent system/light/dark theme selection,
  capture-off and cloud-off defaults, consent policy v2, evidence-retention and
  conservative exclusion/session choices, plus ordered EXE, package-family,
  publisher-certificate, and application-anchored window exclusion rules;
- schema v4 settings persistence that commits the complete ordered rule
  snapshot, capture-off state, and exact privacy-revision change atomically,
  with optimistic concurrency checks over the complete settings snapshot;
- an x64 C++20 native-capture foundation with a versioned C ABI, stable status
  codes, a bounded polled event queue, fail-closed privacy inputs, and an
  original target-scoped safety core; its additive 224-byte runtime
  authorization preserves the legacy 112-byte prefix and binds Windows target
  identity, target epoch, HMONITOR, and display device key, while each
  native permit adds a native-instance epoch and persistence generation behind
  tested shared/unique write permits; a native-issued 64-byte, single-use,
  display-bound command admission additionally binds Start/Resume to the current
  native instance, runtime revision, persistence generation, target epoch, and
  runtime owner epoch; capability bit 11 and a dedicated C ABI export now let a
  Windows callback close native command and persistence admission immediately,
  without waiting for an already-held persistence permit;
- real but not yet C-ABI-connected native writer components: a strict
  no-fallback HWND/PID/process-creation/display observer, pre/post fingerprinted
  DXGI Desktop Duplication source with an 8K/126.6 MiB BGRA ceiling, bounded
  even-dimension WIC scaler, 64 MiB fail-closed in-memory Media Foundation H.264
  writer, typed privacy-safe manifest, handle-identity-bound whole-directory
  chunk store with observable retryable rollback, queue-bound required-event
  reservation, and a Pause/Resume control mailbox that transfers persistence
  tokens by value;
- a managed P/Invoke adapter with x64 ABI layout checks, safe-handle ownership,
  bounded event polling, privacy-revision updates, complete display-scoped
  safety-capability negotiation, issuer-bound opaque admission stamps,
  strict callback-epoch advancement, a generation-bound local persistence
  boundary, explicit pre-commit versus post-commit supersede outcomes,
  asynchronous owner/quiesce behavior, and real-DLL integration tests;
- a synchronous, fail-closed Windows foreground-target verifier foundation that
  double-checks the foreground HWND, owner TID/PID, process creation time and
  liveness, window title, and monitor selection; production title reads share
  one process-wide background worker with a 100 ms wall-clock deadline,
  permanent fail-stop behavior after an in-flight timeout, late-result
  rejection, fatal-error preservation, and a cleared private 32K-character
  buffer; the verifier emits a stable numeric target/display anchor plus
  size-bounded identity observations,
  rejects unresolved `ApplicationFrameHost.exe` attribution,
  obtains target epochs from a process-wide monotonic source across
  target/display changes, Unknown gaps, and verifier recreation, and redacts
  observed values from diagnostic text;
- an inactive event-driven Windows privacy-monitor foundation with one owner
  thread and message pump, a never-shown top-level HWND, narrow
  foreground/desktop/window hooks, `WM_DISPLAYCHANGE`, current-session WTS,
  suspend/resume notifications, and an exact `0x800B..0x800C` location/name
  range; a qualifying nonzero-HWND
  `OBJID_WINDOW`/`CHILDID_SELF` location event conservatively invalidates the
  managed latch, observation generation, and target continuity, but does not
  prove that the window is top-level or foreground; the monitor also provides a
  forced native FailClosed barrier, latest-generation worker coalescing,
  generation-bound publication, independent session-unavailable and
  power-suspended holds, stale-Allow compensation, bounded teardown, and
  value-only sanitized fault contracts;
- a tested settings commit barrier, process-local capture latch with monotonic
  invalidation generations, sticky automatic-stop handling, a pure Windows
  privacy-policy composer, and a native coordinator whose runtime generations
  are deliberately independent from persisted consent revisions; the App still
  uses the unavailable path because the native binary does not yet advertise a
  write-safe screen-capture capability;
- an inactive, fail-closed Windows probe for session lock, input desktop,
  Remote Desktop, Windows Presentation Mode, and storage headroom, plus a pure
  typed exclusion matcher that evaluates application and window rules
  independently without returning observed identity or title text; live
  application/window identity collection remains intentionally disconnected;
- domain, application, infrastructure, presentation, and capture-interoperability
  project boundaries with automated persistence and mutation tests; and
- an unpackaged, self-contained development bundle for manual UI verification.

Manual activities, consent, privacy settings, and user-authored exclusion rules
are stored at `%LOCALAPPDATA%\WinDayFlow\Data\windayflow.db` and survive
application restarts.
The C-ABI-connected DXGI/WIC/Media Foundation worker and persistence path,
analysis
queue and providers, generated Daily and Weekly views, journal editing, and
timeline-grounded chat are **not integrated yet**. The foreground verifier and
event monitor are inactive foundations, not a live activation claim:
image-bound publisher-signer verification, unique hosted-app attribution,
presentation notifications, periodic storage refresh, worker-side stage
orchestration and generation/permit revalidation, crash recovery/replay, and
real consent-gated Desktop Duplication smoke remain open gates. The bounded
title-read, conservative window-location
invalidation, display-topology/current-session/power event source,
display-scoped authorization, callback-time native admission closure, and
strict no-fallback DXGI output resolver gates are closed. The new native frame
source revalidates the resolved output before and after acquisition, but no C
ABI worker yet composes it with the target observer and stage permits. Callback
closure denies new native permits
and command admission, but it cannot interrupt a permit already held by a
writer stage. The following generation-bound Block acknowledgement drains that
boundary; the pending worker must use the implemented epoch post-check at every
acquisition, encode, metadata, rename, and committed-event phase.
Owner-bound Start/Resume admission is implemented:
the Application service obtains a single-use opaque stamp, rechecks persisted
and runtime authorization, and the native owner atomically consumes the stamp
against the current generation and target without retrying a stale command.
The foundation consumes a valid authorized command but still returns
`NotImplemented`; it starts no capture worker. Dynamic lock, exclusion, and
Unknown transitions must also receive an explicit evidence-Pause or sticky
session-Stop policy; this milestone does not implement that distinction. The
native foundation deliberately advertises no screen-capture, H.264-chunk, or
evidence-extraction capability, App DI continues to use the unavailable
backend, and the development bundle is not yet a functional recorder.

See the [architecture design](docs/ARCHITECTURE.md) for delivery phases and the
[Reference Baseline](docs/ARCHITECTURE.md#2-reference-baseline) for the reviewed
upstream revisions, adopted ideas, adaptations, and rejected coupling. The
[capture-exclusion rule ADR](docs/adr/0002-capture-exclusion-rules.md) records
the typed matching, ordering, privacy-revision, and persistence contract. The
[native safety-core ADR](docs/adr/0003-native-capture-safety-core.md) records the
target identity, generation, write-permit, quiescence, command-admission, and
capability gates that must precede live capture. The
[owner-bound command-admission ADR](docs/adr/0004-owner-bound-command-admission.md)
records the single-use managed/native Start/Resume authority and its
linearization, cancellation, and failure semantics. The
[Windows foreground-target verification ADR](docs/adr/0005-windows-foreground-target-verification.md)
records the stable observation, process-wide monotonic epoch, privacy-redaction,
title-observation contract, and remaining live-activation boundaries.
The [event-driven privacy-monitor ADR](docs/adr/0006-event-driven-capture-privacy-monitor.md)
records callback-time invalidation, the enforced three-phase generation
protocol, WinEvent thread ownership, worker coalescing, sanitized failure
semantics, and remaining native-writer and Windows-lifecycle gates. The
[bounded title and location-invalidation ADR](docs/adr/0007-bounded-window-title-and-location-invalidation.md)
records the process-wide 100 ms title-worker fail-stop contract, exact
location/name hook range, conservative object filtering, and the two activation
gates closed by that implementation.
The [display-scoped authorization and DXGI output-resolution ADR](docs/adr/0008-display-scoped-authorization-and-dxgi-output-resolution.md)
records the compatible 224-byte ABI tail, display-bound safety identity,
capability dependencies, and strict unique-output resolver contract.
The [Windows lifecycle invalidation and callback-time authorization ADR](docs/adr/0009-windows-lifecycle-invalidation-and-callback-time-authorization-closure.md)
records the hidden-window system notification source, session and power holds,
native callback gate, supersede outcomes, and Block-before-Allow recovery
contract.
The [transactional native capture writer-components ADR](docs/adr/0010-transactional-native-capture-writer-components.md)
records strict native target/DXGI observation, bounded WIC and in-memory H.264,
privacy-safe manifests, whole-directory publication, event reservation, runtime
token handoff, and the remaining C ABI activation boundary.

## Platform Support

- **Baseline:** Windows 10 version 1809 (build 17763) or later.
- **Enhanced presentation:** On supported Windows 11 versions, WinDayFlow uses
  system-backed visuals such as Mica. Windows 10 and unsupported configurations
  receive a solid Fluent theme background while retaining the same information
  architecture and controls.
- **Architectures:** Projects and the development-package script define x64 and
  ARM64 targets. The documented local commands and current CI workflow target
  x64; the development-package command defaults to x64. ARM64 is defined but
  has not yet been validated end to end.

## Prerequisites

- Windows 10 version 1809 or later
- .NET SDK 10.0.302, or a later patch in the 10.0.3xx feature band, as selected
  by [`global.json`](global.json)
- PowerShell 7 (`pwsh`) for native validation and development packaging
- Visual Studio with **Desktop development with C++**, CMake, and Windows SDK
  components for the native capture build; it is not required for managed-only
  build and test commands

## Build and Test

From the repository root, restore once and then build and test the x64
application:

```powershell
dotnet restore WinDayFlow.sln -p:Platform=x64
dotnet build WinDayFlow.sln -c Debug -p:Platform=x64 --no-restore
dotnet test WinDayFlow.sln -c Debug -p:Platform=x64 --no-build
dotnet format WinDayFlow.sln --verify-no-changes --no-restore
pwsh -File .\scripts\Test-NativeProvenance.ps1
pwsh -File .\scripts\Build-Native.ps1 -Configuration Debug
```

`Build-Native.ps1` selects an installed Visual Studio generator supported by
CMake, ignores ambient `CMAKE_GENERATOR*` overrides, preserves x64
multi-configuration output, builds the C++20 DLL, and runs all thirteen native C
and C++ tests with per-test timeouts. Use `-Fresh` to recreate CMake state, or
`-Generator` to select a specific installed Visual Studio generator.

## Run the Development App

Run the unpackaged app directly from source:

```powershell
dotnet run --project src/WinDayFlow.App/WinDayFlow.App.csproj -c Debug -p:Platform=x64
```

## Build a Development Bundle

Create a self-contained x64 bundle for manual verification:

```powershell
pwsh -File .\scripts\Build-DevPackage.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

After a successful restore, append `-NoRestore` to skip restoring during
publish. For Windows on ARM, use `-RuntimeIdentifier win-arm64`.

The script validates the WinUI PRI and compiled XAML resources, the .NET
runtime, the Windows App SDK runtime, the MIT `LICENSE`, and every repository
third-party notice and provenance record before replacing an existing bundle.
It writes:

```text
artifacts/dev/WinDayFlow-dev-x64/
artifacts/dev/WinDayFlow-dev-x64.zip
```

These artifacts are for local development and manual testing on the machine
where they were built. Do not share, publish, distribute, upload, or deploy the
directory or ZIP while it contains `Microsoft.WindowsAppSDK.WinUI` 2.2.1. The
bundle includes [DEV_BUNDLE_LOCAL_ONLY.txt](DEV_BUNDLE_LOCAL_ONLY.txt) as an
explicit top-level warning.

Launch `WinDayFlow.App.exe` from the generated directory. When using the ZIP,
extract it completely before launching the executable.

## Reference Projects and Licensing

[Dayflow](https://github.com/JerryZLiu/Dayflow) and
[QiDayflow](https://github.com/liujiaqi7998/QiDayflow) are upstream references
used to audit product goals, Windows capture techniques, reliability practices,
and trade-offs. They are not runtime dependencies. WinDayFlow is an independent
implementation and is not affiliated with, sponsored by, or endorsed by either
project or its maintainers.

Both reference repositories publish their source under the MIT License. If
WinDayFlow incorporates or derives substantive code from either repository, the
applicable copyright and permission notices must be preserved and the source
provenance recorded. Their upstream names are used only for attribution and
never to imply project affiliation. Branding, logos, fonts, screenshots, and
other visual assets are not reused by WinDayFlow unless separately licensed and
explicitly documented.

WinDayFlow remains distributed under the [MIT License](LICENSE). This project
license applies to WinDayFlow-owned work; it does not replace the terms or
notices of incorporated third-party material, and it does not require a change
away from WinUI-related components. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the redistributed runtime
terms and [docs/provenance/QiDayflow-capture.md](docs/provenance/QiDayflow-capture.md)
for capture-source provenance.

WinUI 3 and Windows App SDK remain the project's selected UI stack. Current
development-package redistribution restrictions do not reopen that technology
decision. Dependency terms are handled by choosing an appropriate servicing
release and honoring its packaging and distribution conditions, not by
replacing the native Windows architecture.

The current self-contained bundle is for local development and manual
verification only. Its transitive `Microsoft.WindowsAppSDK.WinUI` 2.2.1 package
carries Engineering Preview terms that prohibit sharing, publishing,
distribution, and live use. Production packaging still requires a
redistributable WinUI/Windows App SDK servicing release, or explicit permission
for the selected dependency. That release gate applies to the concrete binary
dependency, not to the choice of WinUI 3, and it does not change the MIT license
for WinDayFlow-owned source code.

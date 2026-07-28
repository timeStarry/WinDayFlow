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

WinDayFlow is in active implementation. The persistent UI/data foundation and
the capture-to-analysis P0 plumbing are now composed. Production capture remains
fail closed; a separately built and explicitly launched x64 dev-live bundle is
available for controlled manual verification. The repository contains:

- a working WinUI 3 Fluent shell with a custom system-integrated title bar,
  responsive navigation, and a retained hamburger menu;
- schema-v7 SQLite migrations plus local timeline, settings, capture-chunk,
  durable analysis-job, and AI-provider-profile persistence, initialized before
  the main window opens;
- a timeline review surface with date navigation, search, filters, and durable
  manual activity creation, editing, and deletion;
- a settings surface with persistent system/light/dark theme selection,
  capture-off and cloud-off defaults, consent policy v2, evidence-retention and
  conservative exclusion/session choices, plus ordered EXE, package-family,
  publisher-certificate, and application-anchored window exclusion rules; its
  application privacy mode defaults to `ProtectByForegroundApplication`, while
  the explicitly broader `AllowAllApplications` choice trades application/window
  exclusions for continuous capture on one display pinned when each recording
  starts;
- transactional settings persistence that commits the complete ordered rule
  snapshot, capture-off state, and exact privacy-revision change atomically,
  with optimistic concurrency checks over the complete settings snapshot;
- an OpenAI-compatible vision-analysis adapter with bounded JPEG requests,
  strict structured-response validation, stable failure mapping, redirect and
  response-size limits, and provider-specific data kept outside the domain;
- a Settings workflow for provider endpoint, vision model, timeout, and
  DPAPI-protected API credentials, with a synthetic-image connection test,
  revision-bound validation, explicit cloud-transfer disclosure, and cloud-off
  default;
- an application-hosted analysis pipeline that composes committed-manifest
  scanning with an exact two-value capture-scope allowlist, SQLite chunk/job
  persistence, source fingerprinting, native evidence extraction, durable
  ingestion and supervision, provider analysis, atomic timeline commit, and
  startup plus chunk-event wakeups;
- a root-bound native Media Foundation/WIC evidence extractor that accepts a
  canonical chunk ID, verifies the source before and after extraction, publishes
  a strict versioned manifest atomically, and limits output to 32 JPEG frames,
  2 MiB per frame, and 12 MiB total; the managed adapter revalidates every frame
  hash before constructing a provider request;
- an x64 C++20 native-capture foundation with a versioned C ABI, stable status
  codes, a bounded polled event queue, fail-closed privacy inputs, and an
  original scope-aware safety core; its additive 224-byte runtime
  authorization preserves the legacy 112-byte prefix and binds Windows target
  identity, target epoch, HMONITOR, and display device key, while each
  native permit adds a native-instance epoch and persistence generation behind
  tested shared/unique write permits; a native-issued 64-byte, single-use,
  display-bound command admission additionally binds Start/Resume to the current
  native instance, runtime revision, persistence generation, target epoch, and
  runtime owner epoch; capability bit 11 and a dedicated C ABI export now let a
  Windows callback close native command and persistence admission immediately,
  without waiting for an already-held persistence permit; capability bit 12 and
  a target flag add display-wide continuous authorization without changing the
  224-byte ABI structure;
- real native writer components and orchestration behind a C-ABI-owned
  controller whose production mode is disabled and whose dev-live mode is
  available only through an explicit compile-time switch:
  a strict no-fallback HWND/PID/process-creation/display observer with a
  pre/post-fingerprinted, DXGI-first display source and an 8K/126.6 MiB BGRA
  ceiling; only an explicit Desktop Duplication `E_ACCESSDENIED` selects the
  Windows Graphics Capture monitor fallback, while other DXGI failures remain
  fail closed; bounded
  even-dimension WIC scaler, 64 MiB fail-closed in-memory Media Foundation H.264
  writer, typed privacy-safe manifest, handle-identity-bound whole-directory
  chunk store with observable retryable rollback, queue-bound required-event
  reservation, and a Pause/Resume control mailbox that transfers persistence
  tokens by value; the fake-backed worker now guards every stage with fresh
  target observation and persistence authority, preserves merged Pause events,
  finalizes valid partial chunks, compensates stale filesystem output, and uses
  a validated hidden-event append as the final publication linearization point;
  the instance controller adds independent run IDs, worker-completed state
  checkpoints, resumable authorization Pause, single-flight Stop finalization,
  stale-callback rejection, and reserved STOPPING/STOPPED/ERROR capacity;
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
- an event-driven Windows privacy-monitor foundation with one owner
  thread and message pump, a never-shown top-level HWND, narrow
  foreground/desktop/window hooks, `WM_DISPLAYCHANGE`, current-session WTS,
  suspend/resume notifications, and an exact `0x800B..0x800C` location/name
  range; qualifying nonzero-HWND `OBJID_WINDOW`/`CHILDID_SELF` object events
  invalidate only when they concern the published target or the latest
  foreground candidate, while unrelated-window noise is ignored; those events
  still do not prove that a window is top-level or foreground; the monitor also provides a
  forced native FailClosed barrier, latest-generation worker coalescing,
  generation-bound publication, independent session-unavailable and
  power-suspended holds, stale-Allow compensation, bounded teardown, and
  value-only sanitized fault contracts; production does not register it, while
  the dev-live composition owns and starts it before admitting capture; in the
  explicit continuous mode, an active recording pins its initially authorized
  display, and ordinary foreground/window-object changes do not revoke or move
  that authorization even when focus moves to another display; changing the
  recording display requires waiting for Stop to complete, moving the WinDayFlow
  window to the intended display, and starting there; lifecycle, display
  topology, storage, consent, and user-stop boundaries remain fail closed; an
  independent
  low-frequency storage-headroom refresh samples no window identity, leaves a
  stable decision and target epoch untouched, and applies callback-time
  fail-closed invalidation when headroom changes or its read becomes unknown;
- a tested settings commit barrier, process-local capture latch with monotonic
  invalidation generations, sticky automatic-stop handling, a pure Windows
  privacy-policy composer, and a native coordinator whose runtime generations
  are deliberately independent from persisted consent revisions; production App
  registration uses the unavailable path, while the dev-live App maps the same
  native owner to the capture backend, commit notifier, settings barrier, runtime
  authorization, and privacy-signal contracts;
- a fail-closed Windows probe for session lock, input desktop,
  Remote Desktop, Windows Presentation Mode, and storage headroom, including a
  separate five-second dev-live storage refresh that does not rebuild a healthy
  pinned-display target, plus a pure typed exclusion matcher that evaluates
  application and window rules
  independently without returning observed identity or title text; dev-live
  activation adds a QA-only policy that accepts verifier-resolved classic and
  packaged foreground targets; the default mode preserves explicit exclusions,
  the all-applications mode suspends them after renewed consent, and both retain
  all non-application privacy signals; production-grade signer and
  hosted-application attribution remain open;
- domain, application, infrastructure, presentation, and capture-interoperability
  project boundaries with automated persistence and mutation tests; and
- an unpackaged, self-contained development bundle for manual UI verification.

Manual activities, consent, privacy settings, user-authored exclusion rules,
provider profiles, capture metadata, analysis jobs, and generated timeline
entries are stored at `%LOCALAPPDATA%\WinDayFlow\Data\windayflow.db` and survive
application restarts. App startup initializes the database, settings, and
provider configuration before starting the hosted analysis runner. The runner
rescans committed manifests on startup and on chunk-completed wake hints,
idempotently persists chunks/jobs, extracts bounded evidence through the native
adapter, and atomically commits validated results to the editable Timeline.
Integration coverage exercises that path with a fake OpenAI-compatible HTTP
endpoint, including restart idempotency and protection of user edits.

The default native build advertises none of `ScreenCapture`, `H264Chunks`, or
`EvidenceExtraction`; its controller remains disabled, and the default App uses
`UnavailableCaptureBackend`. Native evidence extraction is exposed as a
separate strict C ABI operation and is used by the composed analysis pipeline,
without publishing the `EvidenceExtraction` capability bit. Both native builds
advertise display-wide authorization capability bit 12 as a foundation
contract, but that bit cannot activate a writer. A dev-live build adds only
`ScreenCapture | H264Chunks` and enables the controller. It can be selected by
the App only when all three independent gates agree:

1. native and managed projects are compiled with
   `EnableDevLiveCapture=true`;
2. the App is published as a development bundle with `DevBundleBuild=true`; and
3. the executable is launched with exactly one argument,
   `--enable-dev-live-capture`.

Omitting any gate leaves capture unavailable. The dev-live privacy sampler is
intentionally unsuitable for production: it initially admits a display only
when the Windows verifier has resolved its foreground target, display,
executable, title, and package-family state. In the default
`ProtectByForegroundApplication` mode, explicit application/window exclusions
still block and WinDayFlow's own window is protected. `AllowAllApplications` is
an explicit wider-scope choice: it temporarily suspends those application and
window exclusions, including the special protection for the WinDayFlow window,
and keeps recording the display selected at Start across ordinary application
and cross-display focus switches. It does not record every display, and changing
the target display requires waiting for recording to stop completely, moving the
WinDayFlow window to that display, and starting again there.
Session, secure-desktop, remote-session, presentation, display-topology, power,
storage, consent, and user-stop controls remain independent. This is a manual
QA harness, not application trust or signer proof, and production capture
remains unavailable.

P0 is not complete. The composed path still needs a clean-profile real capture
smoke across the DXGI-first/WGC-fallback path, forced-restart checks at the
native persistence boundaries, and manual lock, secure-desktop, RDP,
presentation, display, sleep, exclusion, consent-revocation, and shutdown
transitions. Startup reconciliation of a
persisted recording intent is implemented and covered by targeted recovery and
user-intent regression tests, but remains an acceptance gate until clean-profile
dev-live smoke proves the startup behavior.
Generated Daily and Weekly views, journal editing, timeline-grounded chat,
export, and additional providers remain deferred.

### Immediate Development Priority

Development is now frozen around one P0 vertical slice:

```text
consent-gated recording
  -> committed MP4 + manifest
  -> idempotent discovery and durable job
  -> bounded JPEG evidence extraction
  -> configured and explicitly enabled LLM API
  -> validated response and atomic timeline commit
```

The MP4 artifact remains the P0 compatibility path so the existing native
writer, recovery, extractor, and analysis pipeline can be validated without a
second storage rewrite. The production storage direction is periodic JPEG
frames plus a typed manifest as canonical evidence, with MP4 generated only on
demand for playback or provider compatibility. That migration is a separate,
reviewed change after this vertical slice passes; derived video must be
rebuildable and must never become the sole source of evidence.

A provider result is commit-eligible only when it contains at least one activity
and continuously covers `0` through `range_duration_ms`; uncertain spans use
`unknown` rather than being omitted. Empty or partial coverage is terminal
`ProviderResponseInvalid`, and no generated Timeline entries are committed.

The code path now includes dev-live recording composition, strict ingestion and
restart recovery for both `authorized-foreground-display` and
`authorized-display-continuous` manifests, root-bound native evidence extraction,
background analysis supervision, atomic Timeline commit, and visible
unprocessed/job failure states.
The remaining implementation priority is to exercise startup intent, the real
dev-live path, and privacy transitions on Windows, then fix any failures found
by that smoke. New Daily, Weekly, Journal, Chat, export,
additional-provider, and visual-polish work remains deferred unless required to
complete or verify this chain.

This slice is usable only when a clean Windows x64 profile can record, configure
and test an OpenAI-compatible endpoint, opt in to cloud analysis, and obtain an
editable normalized timeline entry; the same data must recover after forced
restart without duplicates. With cloud analysis off or unavailable, recording
must perform no network request and must remain visible as an unprocessed local
interval. Production capture capabilities stay disabled until those conditions
and the privacy transition smoke tests pass. See the architecture design's
`Immediate P0` section for the complete gates.

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
The [authority-checked native worker ADR](docs/adr/0011-authority-checked-native-capture-worker-orchestration.md)
records per-stage target/permit guards, Pause epochs, graceful Stop ordering,
validated event linearization, compensation retention, the real Windows
adapter, and the remaining run-state and live-activation gates. The
[run-isolated native instance-control ADR](docs/adr/0012-run-isolated-native-capture-instance-control.md)
records C ABI controller ownership, checkpoint-driven state, provisional
authorization Pause, run-ID isolation, Stop single flight, and the still-closed
live capability boundary. The
[display-wide continuous-capture ADR](docs/adr/0013-display-wide-continuous-capture.md)
records the explicit user choice, schema-v7 consent transition, compatible ABI
target flag, active-recording display pinning, retained fail-closed lifecycle
events, and privacy trade-off.

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
multi-configuration output, builds the C++20 DLL, and runs all eighteen native C
and C++ tests with per-test timeouts. Use `-Fresh` to recreate CMake state, or
`-Generator` to select a specific installed Visual Studio generator.

## Run the Development App

Run the unpackaged app directly from source:

```powershell
dotnet run --project src/WinDayFlow.App/WinDayFlow.App.csproj -c Debug -p:Platform=x64
```

This source-run command uses the production capture posture: live capture is
unavailable, while the local Timeline, provider configuration, manifest scan,
and analysis pipeline still start normally.

## Build a Development Bundle

Create the default self-contained x64 bundle, which preserves the production
capture posture:

```powershell
pwsh -File .\scripts\Build-DevPackage.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

Launch it without a live-capture argument:

```powershell
.\artifacts\dev\WinDayFlow-dev-x64\WinDayFlow.App.exe
```

To build the separate x64 dev-live recorder for controlled manual smoke, run:

```powershell
pwsh -File .\scripts\Build-DevPackage.ps1 `
  -Configuration Release `
  -RuntimeIdentifier win-x64 `
  -EnableDevLiveCapture
```

Close any existing WinDayFlow instance, then launch the dev-live executable with
the exact activation argument:

```powershell
.\artifacts\dev\WinDayFlow-dev-live-x64\WinDayFlow.App.exe `
  --enable-dev-live-capture
```

Run this smoke only from an unlocked local interactive desktop. First exercise
the default `ProtectByForegroundApplication` mode. An unresolved foreground
target or an explicitly excluded application/window must remain fail closed;
WinDayFlow's own window is protected in this mode. Keep one ordinary external
window stable for at least 60 seconds to cross the default 10-second frame
interval and 60-second chunk boundary. A complete first chunk contains six
frames and spans about 60 seconds; the next transformed frame starts the next
chunk instead of extending the first chunk to 70 seconds. The dev-live encoder
uses a 500 kbps average bitrate so this QA path does not retain the earlier
2.5 Mbps storage cost.

Then select `AllowAllApplications`. This is an effective privacy-policy change:
the app must advance the privacy revision, disable capture, and make the prior
recording consent stale. Read the wider-scope warning, grant consent for the new
revision, wait for recording to be completely stopped, move the WinDayFlow
window to the display under test, and enable capture there. Then switch
repeatedly among ordinary applications, WinDayFlow, and a window on another
display for longer than one chunk. These foreground switches must not publish
privacy Pause/Resume transitions, move the recording target, or discard the
in-progress chunk; committed manifests use the
`authorized-display-continuous` scope. Application/window exclusions remain
stored but temporarily do not apply, so WinDayFlow settings and provider UI can
become local evidence in this mode.

Continuous authorization covers the display selected when recording starts,
not every monitor. Merely activating a window on another display must leave the
original display and authorization generation unchanged. To change the target,
wait until recording is completely stopped, move the WinDayFlow window to the
intended display, then Start again there; the new run may select that display. A
display disconnect or topology change remains a fail-closed boundary rather than
silently widening or moving the target. In both modes, verify session lock and
secure desktop, current-session WTS changes, suspend/resume, display-topology
loss, storage loss, consent revocation, and an explicit user Stop still close
capture. Remote Desktop and Windows Presentation Mode continue to follow their
separately persisted policies. This broader mode improves day-to-day continuity
because application and focus switches no longer create visible recovery churn
or partial-chunk gaps; the cost is the intentionally wider local capture scope
on the pinned display.

Keep storage healthy across at least three five-second refresh periods and
verify that no privacy transition or target-epoch change occurs. In a controlled
test volume or fault-injection run, crossing the configured headroom threshold
or failing the storage read must close admission on the next refresh without a
window event; a read failure is treated as Unknown, not Allow. Restoring storage
requires a fresh fail-closed barrier and authorization before recording resumes.

Desktop Duplication remains the preferred frame source. If Windows explicitly
denies `DuplicateOutput`, the same authorized display is captured through
Windows Graphics Capture without changing the privacy decision. A WGC access
denial is terminal, while transient closed/device-loss sessions receive at most
four interruptible exponential-backoff rebuilds; the budget resets only after
an authorized frame is encoded.

The dev-live flavor is intentionally rejected for `win-arm64`. Passing no
argument, an additional argument, or a differently spelled argument keeps the
App on the unavailable capture path even when the dev-live flavor was built.

After a successful restore, append `-NoRestore` to skip restoring during
publish. For Windows on ARM, use `-RuntimeIdentifier win-arm64`.

The script validates the WinUI PRI and compiled XAML resources, the .NET
runtime, the Windows App SDK runtime, the MIT `LICENSE`, and every repository
third-party notice and provenance record before replacing an existing bundle.
It writes:

```text
artifacts/dev/WinDayFlow-dev-x64/
artifacts/dev/WinDayFlow-dev-x64.zip
artifacts/dev/WinDayFlow-dev-live-x64/       # with -EnableDevLiveCapture
artifacts/dev/WinDayFlow-dev-live-x64.zip    # with -EnableDevLiveCapture
```

These artifacts are only for development and testing on Windows under the
selected dependency's license and must not be used in a live operating
environment. The license permits the licensee to install multiple copies for
those purposes; it prohibits sharing, publishing, distributing, leasing, or
transferring `Microsoft.WindowsAppSDK.WinUI` 2.2.1 to a third party. The bundle
includes [DEV_BUNDLE_LOCAL_ONLY.txt](DEV_BUNDLE_LOCAL_ONLY.txt) as an explicit
top-level warning.

When using either ZIP, extract it completely before launching the executable.

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

The current self-contained bundle is for licensed development and testing only.
Its transitive `Microsoft.WindowsAppSDK.WinUI` 2.2.1 package carries Engineering
Preview terms that prohibit live use and third-party sharing, publishing,
distribution, leasing, or transfer. Production packaging still requires a
redistributable WinUI/Windows App SDK servicing release, or explicit permission
for the selected dependency. That release gate applies to the concrete binary
dependency, not to the choice of WinUI 3, and it does not change the MIT license
for WinDayFlow-owned source code.

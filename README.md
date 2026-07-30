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
- **User-owned evidence and user-selected processing.** Keep the canonical
  archive, settings, and journal database under the user's control; disclose
  every provider endpoint and payload while allowing the user to decide which
  provider, if any, serves each processing stage.
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

WinDayFlow is in active implementation. The P0 capture-to-timeline path is
runnable in the explicitly enabled x64 dev-live bundle; ordinary builds keep the
native recorder unavailable. The current repository contains:

- a WinUI 3 Fluent shell with Timeline, Statistics, Settings, native folder
  launching, system backdrop, accessible controls, and a notification-area icon;
- schema v13 SQLite persistence. The migration deliberately discards old
  development recordings, screenings, jobs, and timeline evidence, cleans only
  WinDayFlow-owned internal artifacts, preserves settings and DPAPI ciphertext,
  and does not read old capture manifests;
- persisted `CaptureIntent` (`Recording`, `Paused`, or `Stopped`) separated from
  transient system gates. Hiding the main window does not change intent, and
  recoverable gates resume only while intent remains `Recording`;
- native ABI v2 with display-bound command admission, generation barriers, an
  atomic JPEG writer, and a polled `CaptureHealthSnapshot`. Managed health checks
  use the successful-sample heartbeat instead of waiting for a 15-minute commit;
- immutable schema 4 chunks containing JPEG frames, exact time ranges, sampling,
  all-black, duplicate, and retained counts, plus bounded application-context
  samples. A valid chunk may contain zero JPEGs when every sample is filtered;
- pre-persistence rejection of compositor-black frames and perceptual
  consecutive-frame deduplication across chunk boundaries. Neither class can
  enter provider requests;
- safe application IDs, display names, process IDs, normalized CPU use, working
  set, and private memory projections. Window titles are used only for immediate
  send-rule matching and are never persisted;
- revision-checked OpenAI-compatible provider profiles and independent bindings
  for `PrivacyInspection` and `TimelineAnalysis`. Creating a profile never binds
  it, and deleting a referenced profile is rejected;
- optional privacy inspection with cached `Clear`, `Sensitive`, and
  `Inconclusive` results, user-selected match/error policy, immutable masked JPEG
  derivatives, one-operation send overrides, and payload-free invocation audit;
- send rules that are re-evaluated before each provider request. They can block
  privacy or timeline transfer but never stop local capture;
- a durable analysis pipeline that selects at most 32 validated JPEGs under a
  12 MiB request budget, records the actual invocation, validates complete
  interval coverage, and transactionally rewrites a 45-minute multi-chunk
  timeline window while preserving user edits and all evidence references;
- newest-first Timeline review with collapsible processing state, screenshot
  browsing, application telemetry, and 10/15/30/60 FPS MP4 export where each
  retained JPEG becomes exactly one video frame. Recording never creates MP4;
- Statistics ranges for today, 7 days, 30 days, and all data, including interval
  unions, timeline categories, capture filtering, provider calls, and cancellable
  storage accounting; and
- tray behavior where left click opens the home window, right click opens only
  the tray menu, close hides the window, and explicit Exit ends recording and the
  process.

Application data is stored at
`%LOCALAPPDATA%\WinDayFlow\Data\windayflow.db`, with canonical chunks under
`Data\chunks`, optional redactions under `Data\screenings`, and bounded
payload-free diagnostics under `%LOCALAPPDATA%\WinDayFlow\Diagnostics`.

The default build advertises no live screen-capture capability. A dev-live build
can start the recorder only when all three gates agree:

1. native and managed projects use `EnableDevLiveCapture=true`;
2. the App is published with `DevBundleBuild=true`; and
3. the executable receives exactly `--enable-dev-live-capture`.

### Immediate Development Priority

Development remains frozen around this vertical slice:

```text
persistent recording intent + recoverable system gates
  -> schema 4 JPEG chunk + context samples
  -> black/duplicate filtering
  -> optional privacy inspection/redaction
  -> independently routed timeline analysis
  -> 45-minute multi-evidence rewrite
  -> review, statistics, and selected-range MP4 export
```

Automated coverage exercises schema v13 reset, ABI v2 contracts, capture health,
filtering, routing, privacy caching/redaction, send policy, invocation audit,
sliding-window commits, statistics, and presentation state. Manual release
acceptance still includes the Windows-only lock/sleep/display transition matrix,
tray interaction, and playback checks on the packaged dev-live executable.

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
multi-configuration output, builds the C++20 DLL, and runs all sixteen native C
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

The current smoke validates schema v13 and the continuous-capture model. Run it
only from an unlocked local interactive desktop. Grant consent, set intent to
`Recording`, and keep an ordinary external window on the selected display. The
default screenshot interval is 10 seconds and can be changed to 5/10/15/30/60
seconds in Settings.

Hide the main window and continue switching among applications for longer than
one 15-minute chunk. Foreground changes, WinDayFlow send rules, provider routes,
privacy-stage settings, Remote Desktop classification, and presentation mode
must not change local capture intent. The status surface must follow the native
successful-sample heartbeat within two capture intervals. A completed chunk must
contain only `manifest.json` and `frames/*.jpg`, use schema 4 and
`authorized-display-continuous`, report sampling/filtering counters, and be
followed by a new active staging chunk. No recording-time MP4 may exist.

Exercise send rules separately: a matching rule blocks the corresponding
provider request without pausing capture. With privacy inspection disabled,
timeline analysis uses original JPEGs. With it enabled, verify Clear,
redaction-and-continue, Hold/Review, and failure policy using the selected stage
profile. Provider calls must appear in the invocation ledger without payloads,
and generated timeline rows must retain all contributing evidence references.

Verify explicit Pause persists across restart and never auto-resumes. Lock,
secure desktop, suspend/session loss, display loss, storage loss, consent
revocation, and fatal capture failure must close admission. Recoverable system
gates require a fresh generation and resume only if intent is still
`Recording`. Hiding or closing the main window must keep capture running; only
tray Exit ends the process.
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

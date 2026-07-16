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
  codes, a bounded polled event queue, fail-closed privacy inputs, and native
  pixel, scheduling, queue, and ABI contract tests;
- a managed P/Invoke adapter with x64 ABI layout checks, safe-handle ownership,
  bounded event polling, privacy-revision updates, capability negotiation, and
  real-DLL integration tests;
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
The DXGI/WIC/Media Foundation engine, native persistence barrier, event-driven
privacy monitoring, analysis queue and providers, generated Daily and Weekly
views, journal editing, and timeline-grounded chat are **not integrated yet**.
The native foundation deliberately
advertises no screen-capture capability, and the app reports capture as
unavailable even after consent is recorded. The development bundle can be used
as a local manual timeline, but it is not yet a functional recorder.

See the [architecture design](docs/ARCHITECTURE.md) for delivery phases and the
[Reference Baseline](docs/ARCHITECTURE.md#2-reference-baseline) for the reviewed
upstream revisions, adopted ideas, adaptations, and rejected coupling. The
[capture-exclusion rule ADR](docs/adr/0002-capture-exclusion-rules.md) records
the typed matching, ordering, privacy-revision, and persistence contract.

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
multi-configuration output, builds the C++20 DLL, and runs all five native C
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

WinDayFlow is distributed under the [MIT License](LICENSE). This project
license applies to WinDayFlow-owned work; it does not replace the terms or
notices of incorporated third-party material. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the redistributed runtime
terms and [docs/provenance/QiDayflow-capture.md](docs/provenance/QiDayflow-capture.md)
for capture-source provenance.

WinUI 3 and Windows App SDK remain the project's selected UI stack. Dependency
terms are handled by choosing an appropriate WinUI servicing release and by
honoring its packaging and distribution conditions; they are not a reason to
replace the native Windows architecture.

The current self-contained bundle is for local development and manual
verification only. Its transitive `Microsoft.WindowsAppSDK.WinUI` 2.2.1 package
carries Engineering Preview terms that prohibit sharing, publishing,
distribution, and live use. External testing and production release remain
blocked until a production-redistributable WinUI version or explicit permission
is verified. That release gate applies to the concrete binary dependency, not
to the choice of WinUI 3, and it does not change the MIT license for
WinDayFlow-owned source code.

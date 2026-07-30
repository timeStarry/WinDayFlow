# WinDayFlow Architecture Design

Status: Architecture and product design baseline  
Project: WinDayFlow  
Repository: https://github.com/timeStarry/WinDayFlow  
Developer: timeStarry <timestarry@qq.com>  
Last updated: 2026-07-30

## 1. Purpose

WinDayFlow combines Dayflow's zero-input, context-aware work journal and review
experience with QiDayflow's Windows capture, recoverable processing, and privacy
engineering. Its own differentiator is a native WinUI 3 application shaped by
Windows shell conventions, accessibility, and user-controlled local data. The
north-star outcome is a quieter, more transparent, correctable, and dependable
DayFlow experience for Windows users.

The application records bounded evidence of screen activity, converts that
evidence into an auditable activity timeline, and helps the user review,
correct, summarize, search, and discuss their work. It should remain useful
without accounts, cloud services, or AI availability. Background operation may
be quiet, but capture state, retained evidence, provider disclosure, failures,
and recovery must never be hidden from the user.

WinDayFlow is not a line-by-line port, visual clone, or redistribution of either
reference project. It adopts useful product semantics, selectively reuses code
only where provenance and license obligations are satisfied, and establishes a
distinct Windows-native architecture and identity.

### 1.1 Current Capture And Analysis Decision

[ADR 0014](adr/0014-canonical-jpeg-capture-archive.md) is authoritative for the
current artifact format. Recording publishes immutable schema 4 manifests and
canonical JPEG frames and does not read older development manifests. It never
records MP4. Compositor-invalid/all-black surfaces and consecutive perceptual
duplicates are rejected before provider evidence is assembled. Each manifest
preserves the exact time range, sampled/black/duplicate/retained counts, bounded
frame metadata, and application-context samples; a sampled interval with zero
retained JPEGs remains a valid context-only chunk. MP4 is only a rebuildable
10/15/30/60 FPS export for a user-selected `[start,end)` range.

SQLite schema 13 intentionally resets all legacy development chunks, timeline
evidence, analysis jobs, screenings, caches, staging data, and app-local exports
without touching exports outside the WinDayFlow data root. It preserves user
settings, recording consent, provider profiles, and DPAPI ciphertext; a legacy
active provider migrates only to the timeline-analysis binding. Schema 13 also
adds stage routing, privacy screening and invocation ledgers, multi-evidence
analysis-window members, application context projections, and statistics
installation state.

The current analysis version uses persisted 45-minute sliding-window members.
Route/profile/screening revisions and actual evidence fingerprints are part of
the analysis input identity, and transactional `WindowChanged` checks prevent
stale results from overwriting a changed window. User-edited fields remain
locked while unaffected generated fields may be rewritten.

### 1.2 Current Privacy and Provider-Routing Decision

[ADR 0015](adr/0015-user-controlled-capture-and-provider-routing.md) defines the
implemented privacy boundary. Continuous local capture is separate from
provider-request policy. Application/window send rules, optional privacy
inspection, redaction, provider routing, and provider failures do not interrupt
local frame persistence. Capture admission is limited to explicit user intent,
recording consent, Windows lock/secure desktop, suspend or session/display loss,
storage or capture access loss, fatal failure, and shutdown.

WinDayFlow does not infer trust from whether a provider is local or remote.
Revisioned OpenAI-compatible profiles are independently assigned to
`PrivacyInspection` and `TimelineAnalysis`; the user decides whether either
stage is enabled and which profile it uses. Privacy match/error actions are also
user policy. WinDayFlow protects credentials, validates stage capabilities,
rechecks send rules immediately before each request, creates immutable redacted
derivatives when requested, and records payload-free invocation facts.

## 2. Reference Baseline

The references below are review snapshots, not floating dependencies. A future
review must record a new commit rather than silently changing the baseline.

| Project | Repository and reviewed revision | Version | License | Role | Reviewed |
| --- | --- | --- | --- | --- | --- |
| Dayflow | [JerryZLiu/Dayflow](https://github.com/JerryZLiu/Dayflow) [`main@861e9ad`](https://github.com/JerryZLiu/Dayflow/tree/861e9ad3a9e277f00476ad938ef5260c7cfe620e) | 2.0.0 | MIT | Product semantics and interaction reference | 2026-07-16 |
| QiDayflow | [liujiaqi7998/QiDayflow](https://github.com/liujiaqi7998/QiDayflow) [`master@8b82f8a`](https://github.com/liujiaqi7998/QiDayflow/tree/8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e) | 0.1.4 | MIT | Windows capture and reliability reference | 2026-07-16 |

The references inform the project through an explicit Adopt/Adapt/Reject
boundary:

| Source | Adopt | Adapt | Reject |
| --- | --- | --- | --- |
| Dayflow | Zero-input journal semantics; contextual timeline; periodic JPEG evidence; Daily and Weekly review; timeline-grounded work-journal questions; export; provider choice; sensitive-application exclusion | Translate review and correction workflows into native Windows information architecture, offline-first atomic evidence storage, explicit disclosure, navigable citations, capability-based providers, and on-demand derived video | macOS visual conventions; mandatory account or hosted-backend assumptions; default telemetry; copied branding or product assets |
| QiDayflow | DXGI, WIC, and Media Foundation capture approach; bounded evidence; atomic chunks; persistent jobs; transaction boundaries; retry and recovery patterns; DPAPI; privacy-aware logging | Remove Flutter and MethodChannel coupling; version the native boundary; benchmark capture cadence and resource policy; integrate native Windows lifecycle, consent, and exclusion controls | Flutter Material UI; fixed navigation; hard-coded model defaults; treating an upstream sampling default as an architecture constant; behavior that obscures capture, retention, or upload state |
| WinDayFlow | WinUI 3 Fluent shell; collapsible hamburger navigation; Windows title-bar and lifecycle behavior; accessibility; local data control; user correction as durable provenance | Evolve only through measured usability, resource, privacy, and recovery evidence | Becoming a skin over either reference or inheriting a platform convention that conflicts with Windows |

Reference influence is separated into three categories:

- **Product semantics:** concepts and workflows may be independently
  implemented when they fit this architecture and product contract.
- **Reusable code:** copied or adapted source requires file-level provenance,
  preservation of applicable copyright and MIT license notices, compatibility
  review, and tests against the pinned revision. The current Phase 1-derived
  files are covered by the QiDayflow provenance record and manifest; additional
  extraction cannot land until its record exists.
- **Project license:** WinDayFlow is distributed under the MIT License in the
  repository-root `LICENSE`. This applies to WinDayFlow-owned work; third-party
  code and assets retain their own terms. Required notices and provenance
  records must be maintained before substantive source reuse or distribution.
- **Identity and assets:** repository licenses do not imply permission to use
  project names as branding, logos, screenshots, fonts, icons, illustrations,
  or other third-party assets. WinDayFlow uses its own identity and only ships
  assets with separately verified rights.

### 2.1 Licensing and Distribution Gates

WinDayFlow remains MIT-licensed, and WinUI 3 plus Windows App SDK remain fixed
architecture choices. Third-party license review may constrain which concrete
package version can be distributed and how a build is packaged, but current
development-package redistribution restrictions do not reopen the native
Windows technology-stack decision.

- Every distributable bundle must include the repository-root `LICENSE`,
  `THIRD_PARTY_NOTICES.md`, the applicable provenance records, and the complete
  repository `licenses/` tree. A restricted development bundle must also carry
  `DEV_BUNDLE_LOCAL_ONLY.txt`. Packaging checks must fail before replacing an
  artifact when any required file is absent.
- The current development bundle resolves the transitive
  `Microsoft.WindowsAppSDK.WinUI` 2.2.1 package. Its Engineering Preview terms
  limit use to development and testing on Windows and prohibit live use. The
  licensee may install multiple copies for those purposes, but must not share,
  publish, distribute, lease, or transfer the component to a third party.
  Third-party testing and production release are blocked until a
  production-redistributable WinUI/Windows App SDK servicing release is selected
  and its terms are verified, or explicit permission is obtained.
- Resolving that distribution gate must stay within the WinUI 3 and Windows App
  SDK stack, normally by moving to a production-redistributable servicing
  release. Replacing WinUI with a cross-platform UI framework is not a licensing
  mitigation and is outside the product architecture.
- Shipping license text records third-party obligations and restrictions; it
  does not grant rights beyond the applicable terms or remove a release block.

## 3. Goals

- Deliver a genuine Windows-native Fluent application using WinUI 3 and Windows
  shell conventions rather than a cross-platform visual approximation.
- Run quietly in the background while keeping capture, exclusion, upload,
  failure, and recovery state immediately understandable.
- Adapt the proven QiDayflow native capture approach, remove Flutter coupling,
  and preserve code provenance where implementation is reused.
- Keep the canonical archive and product database under user-controlled local
  storage, and make every provider route and evidence transfer inspectable and
  explicitly configurable.
- Make all background work recoverable, observable, and safe to retry.
- Support user-selected AI providers through capability-based standard-API
  adapters without inferring trust or eligibility from deployment location.
- Build daily, weekly, export, and chat features on one stable activity model.
- Allow users to inspect, edit, merge, split, reclassify, and delete their data.
- Preserve user corrections as durable provenance that automated reanalysis
  cannot silently overwrite.
- Degrade gracefully when AI, a provider, or the network is unavailable:
  capture, editing, search, deterministic metrics, and export remain usable.
- Meet documented privacy, accessibility, resource, performance, and
  long-running reliability gates before release.

## 4. Non-goals

- Cross-platform support in the initial architecture.
- Keystroke, mouse trajectory, click, microphone, or system-audio collection.
- Uploading complete video chunks to AI providers.
- Allowing an AI provider direct, unrestricted access to the SQLite database.
- Reproducing macOS-specific visual effects or interaction conventions.
- Cloud synchronization or account requirements in the first release.
- Treating AI-generated content as authoritative or making core journal access
  dependent on a model response.
- Hiding capture, retention, failure, or network-transfer state in pursuit of an
  invisible background experience.
- Reusing reference-project branding, bundled fonts, or visual assets without
  separately verified permission.

## 5. Product Structure

The primary navigation is organized around user goals, not internal workers:

```text
Today
|-- Timeline
|-- Daily
`-- Journal

Insights
|-- Weekly
|-- Statistics
`-- Chat

System
`-- Settings
```

The analysis queue is not a primary page. A compact status indicator appears in
the title or command area. Selecting it opens a details panel containing pending,
running, and failed jobs with retry and evidence-location actions.

### 5.1 Timeline

- Shows normalized activities in chronological order.
- Supports date navigation, text search, category filters, and productivity
  filters.
- Uses virtualization and incremental loading.
- Supports title and summary editing, category/productivity changes, merge,
  split, deletion, and evidence review.
- Opens an evidence review surface with thumbnails, a time scrubber, and an
  on-demand derived timelapse when continuous playback is useful.
- Exports an arbitrary date range as Markdown.

### 5.2 Daily

- Presents a day activity grid and highlights.
- Generates yesterday's accomplishments, today's priorities, and blockers.
- Keeps generated content separate from user-authored edits.
- Can be regenerated without overwriting user-authored content.

### 5.3 Journal

- Provides durable user-authored notes associated with a date.
- May reference timeline activities without duplicating their content.
- Supports daily and weekly browsing.

### 5.4 Weekly

- Aggregates focus time, category distribution, application usage, work rhythm,
  and distracting sessions.
- Produces a review summary from deterministic metrics plus optional AI text.
- Keeps numerical metrics reproducible independently of AI availability.

### 5.5 Chat

- Answers questions about the work journal using controlled read-only tools.
- Includes citations that navigate to referenced timeline ranges.
- Starts with structured queries and SQLite FTS5; embeddings are deferred until
  real retrieval quality requires them.

### 5.6 Statistics

- Shows today, 7-day, 30-day, and lifetime deterministic metrics.
- Includes recorded and focused duration, active days, activity/category/
  productivity distributions, and most-used applications.
- Includes system metrics such as first-use date, captured and retained frame
  counts, deduplication ratio, timeline entries, provider invocations, failures,
  and storage usage.
- Uses one compact KPI band plus full-width chart/list sections rather than
  nested decorative cards. Numeric definitions remain reproducible without AI.

### 5.7 Settings

- Uses a compact landing page with navigable Recording, Storage, Privacy and
  processing, Providers, Appearance, and About pages instead of one flattened
  form.
- The Storage page shows a read-only data path and a standard Open Folder action.
- The Providers page manages reusable profiles. Privacy and processing owns
  independent stage switches, provider assignments, no-send rules, and failure
  behavior; creating or editing a provider never silently assigns it to a stage.
- Provider selection is capability-aware: text support does not imply vision or
  structured privacy-inspection support, but capability does not imply trust.
- Exclusion-rule rows use a stable enable switch and edit action, with reorder
  and delete in an overflow menu.
- Destructive actions use explicit confirmation and show their exact scope.

## 6. Technology Baseline

```text
UI                  C# + WinUI 3 + Windows App SDK
Runtime             Current supported .NET LTS at implementation time
Architecture        Domain + Application + Infrastructure + Presentation
MVVM                 CommunityToolkit.Mvvm
Dependency injection Microsoft.Extensions.DependencyInjection
Background services Microsoft.Extensions.Hosting + Channel<T>
Database            Microsoft.Data.Sqlite with explicit SQL
Serialization       System.Text.Json
HTTP                 HttpClientFactory
Logging              Microsoft.Extensions.Logging
Native capture       C++20, DXGI, Windows Graphics Capture, WIC JPEG
Native interop       Versioned C ABI v1 DLL; managed P/Invoke adapter implemented
Packaging            MSIX plus documented unpackaged development mode
Tests                xUnit and native C/C++ test executables
```

Explicit SQL is preferred over an ORM because queue claiming, migrations,
transaction boundaries, merge behavior, recovery, and compatibility require
precise control.

### 6.1 Windows Compatibility Baseline

- Windows 10 version 1809 or later is the minimum supported operating-system
  baseline, subject to the selected supported Windows App SDK servicing channel.
- Windows 11 receives progressive enhancements such as Mica where the platform
  supports them; those enhancements are never required for navigation,
  readability, capture control, or data access.
- Unsupported backdrops and newer shell capabilities fall back to documented
  solid surfaces and standard window behavior without creating a second UI
  architecture.
- Release validation covers the minimum Windows 10 baseline and the current
  supported Windows 11 releases. Packaging, bootstrapper prerequisites, and
  architecture support are documented per release.

## 7. Solution Layout

This layout distinguishes the implemented foundation from projects and slices
still marked `(planned)`.

```text
src/
|-- WinDayFlow.App/                 WinUI views, navigation, window and tray
|-- WinDayFlow.Presentation/        ViewModels and UI-facing projections
|-- WinDayFlow.Application/         Use cases, orchestration, workers
|-- WinDayFlow.Domain/              Models, value objects, policies, rules
|-- WinDayFlow.Infrastructure/      SQLite, providers, logging, settings, update
|-- WinDayFlow.Capture.Interop/     P/Invoke, fail-closed owner and quiescence
`-- WinDayFlow.Capture.Native/      x64 C++20 DLL, C ABI, policy and safety core
    `-- tests/                      Eighteen native CTest executable targets

tests/
|-- WinDayFlow.Domain.Tests/
|-- WinDayFlow.Application.Tests/
|-- WinDayFlow.Infrastructure.Tests/
|-- WinDayFlow.Presentation.Tests/
`-- WinDayFlow.IntegrationTests/    (planned)

docs/
|-- ARCHITECTURE.md
`-- adr/                           Accepted capture boundary and policy decisions
```

The current managed dependencies point inward, with App acting as the
composition root:

```text
App -> Presentation, Application, Infrastructure, Capture.Interop, Domain
Presentation -> Application, Domain
Application -> Domain
Infrastructure -> Application, Domain
Capture.Interop -> Application

Current: Capture.Interop <-> Capture.Native is built and integration-tested
Default runtime: App registers the unavailable backend; production capture is off
Dev-live runtime: an explicitly gated x64 bundle composes monitor, owner, writer, and chunk wakeups
Analysis runtime: App always composes scanner, durable jobs, native extraction,
                  provider, and Timeline commit
```

The paragraphs below describe the schema-v12 transitional implementation. ADR
0015 is authoritative for the target product boundary: the consent service will
retain only the hard capture gate, while provider routing and privacy processing
move to separate application services and persistence. Foreground verification
remains useful for display selection and optional metadata, not as permission to
persist each frame.

`ICaptureService` and `ICaptureBackend` are owned by Application. The
Application-layer `ConsentGatedCaptureService` implements the feature-facing
service, rejects Start/Resume unless capture is enabled and current recording
consent covers the active privacy revision, and always allows Pause/Stop through
the authorization gate. Redundant Stop calls are idempotent once the backend is
already stopped. `IAppSettingsCommitBarrier` and
`ICaptureRuntimeAuthorization` add a process-local latch before repository
writes and lifecycle calls. Capture.Interop supplies both
`UnavailableCaptureBackend` and `NativeCaptureBackend`. The latter negotiates
the complete safety-capability mask, owns the opaque handle through `SafeHandle`,
updates versioned privacy context and either foreground-target-scoped or
display-wide runtime authorization, and polls bounded native events without
frame callbacks. A separate synchronous
callback path closes native authorization admission before system-event
callbacks return. An explicit asynchronous owner quiesces by applying Block,
stopping, joining, and destroying in order;
`SafeHandle` remains a final fallback rather than the normal shutdown proof.
The default/production App registers the unavailable backend. The x64 dev-live
composition registers one `NativeCaptureRuntimeOwner` instance as the backend,
chunk-commit notifier, settings commit barrier, runtime authorization, and
native privacy-signal sink. A synchronous Windows foreground target verifier is
connected to that owner through an event-driven monitor, which synchronously
invalidates managed admission and target continuity, closes native admission,
applies a generation-bound native barrier, and asynchronously re-observes
through one Windows event owner thread. Its hidden top-level HWND receives
display-topology, current-session WTS, and suspend/resume notifications. The
monitor is hosted and disposed before its owner. Dev-live adds a QA-only,
non-production policy that admits verifier-resolved classic and packaged
foreground targets with a present executable/title and a resolved package
state. The default mode preserves explicit exclusions; the explicitly wider
mode suspends them while retaining independent privacy signals.
This admission is not application trust or signer proof. Image-bound publisher-signer verification,
unique hosted-app child attribution, complete presentation notifications, and
real privacy-transition smoke remain production activation gates. The dev-live
monitor now owns an independent low-frequency storage-headroom refresh; a stable
result does not touch target continuity, while a changed or unreadable result
closes admission before asynchronous recovery.
The persisted application privacy mode changes how that dev-live observation is
used. `ProtectByForegroundApplication` keeps the foreground verifier and the
application/window exclusion policy on every target change.
`AllowAllApplications` uses one fully resolved target to select a display, then
holds display-wide authority across ordinary foreground changes on that same
display. Application and window exclusions remain stored but are inactive until
the user returns to the default mode. Lock/secure-desktop, current-session WTS,
power, display-topology, storage, consent, and explicit user-stop boundaries are
unchanged and remain fail closed. Neither mode changes the production-disabled
capture posture.
The bounded title-read, conservative window-location invalidation,
display-scoped authorization, strict DXGI resolver, native target observer, and
pre/post fingerprinted frame-source gates are closed. A C-ABI-owned controller
now composes the worker with held stage permits and the DXGI-first,
AccessDenied-only Windows Graphics Capture fallback plus WIC JPEG/storage
components. Production keeps that controller in disabled
activation mode; only the separately compiled dev-live DLL enables it and adds
`ScreenCapture | CanonicalJpegChunks`. The native foundation does not reference managed
UI or domain assemblies. The App project may reference concrete adapters for
dependency-injection registration;
feature code consumes their inward-facing contracts. The domain project must
not reference WinUI, SQLite, HTTP, Windows App SDK, or the native capture
implementation.

## 8. Core Domain Model

```text
CaptureSession
CaptureChunk
EvidenceFrame
AnalysisJob
Observation
Activity
TimelineEntry
DailyJournal
StandupReport
WeeklyReview
Conversation
ConversationMessage
AiProviderProfile
StoragePolicy
```

The processing chain is explicit:

```text
CaptureChunk
  -> bounded EvidenceFrame set
  -> validated Observation set
  -> normalized Activity set
  -> merge/edit rules
  -> TimelineEntry set
  -> Daily/Weekly/Chat projections
```

`Activity` is an analyzed fact candidate. `TimelineEntry` is the durable,
user-visible record. This separation prevents reanalysis from overwriting user
edits.

### 8.1 Activity Contract

```csharp
public sealed record Activity(
    TimeRange Range,
    string Title,
    string Summary,
    ActivityCategory Category,
    ProductivityKind Productivity,
    IReadOnlyList<AppUsage> Apps,
    IReadOnlyList<string> Tags,
    double Confidence,
    EvidenceReference Evidence);
```

`ProductivityKind` is independent from category:

```text
Focused | Neutral | Distracting | Break | Unknown
```

For example, an activity can be categorized as communication while separately
classified as focused or distracting.

## 9. Native Capture Component

The target capture engine adapts the reviewed QiDayflow approach:

- `GetForegroundWindow` and `MonitorFromWindow` locate the active display.
- DXGI Desktop Duplication captures that display by default.
- An explicit `DuplicateOutput` access denial selects Windows Graphics Capture
  for the same fingerprinted monitor; other DXGI failures do not select it.
- WIC scales into a bounded BGRA canvas.
- WIC encodes bounded quality-82 JPEG frames directly into a private staging
  chunk; a sampled signature removes only consecutive near-duplicates.
- The native writer atomically publishes `manifest.json` and `frames/*.jpg`.
- Managed analysis, browsing, and export read and validate the canonical JPEGs.
- Native workers never invoke WinUI objects or callbacks directly.

Flutter runner and MethodChannel dependencies are removed. The capture code is
packaged as a native component behind a narrow, versioned adapter.
[ADR 0001](adr/0001-native-capture-c-abi.md), "Versioned C ABI for Native
Capture," is Accepted and selects the C ABI rather than leaving C++/WinRT and a
C ABI as competing boundary designs. The current application-facing contract
remains intentionally limited to lifecycle commands and observable status:

```csharp
public interface ICaptureService
{
    CaptureStatus CurrentStatus { get; }

    event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
```

[ADR 0002](adr/0002-capture-exclusion-rules.md), "Typed, Ordered Capture
Exclusion Rules," remains accepted for application-anchored identities, bounded
window-title operators, ordering, and safe matching. ADR 0015 supersedes its
capture-off/privacy-revision semantics and reuses the rules as explicit no-send
policy at the provider-request boundary.

[ADR 0003](adr/0003-native-capture-safety-core.md), "Native Capture Safety
Core," fixes the additive C ABI v1 runtime-authorization layout, target and
instance identity, native persistence generations, shared/unique write-permit
linearization, revoke and quiescence ordering, and the complete capability mask.
The production build deliberately leaves `ScreenCapture` disabled. The separate
dev-live compile flavor enables it only after composing the real writer and
Windows observers under the same contracts.

[ADR 0004](adr/0004-owner-bound-command-admission.md), "Owner-Bound Capture
Command Admission," closes the Boolean-to-backend Start/Resume TOCTOU boundary
with a native-issued, owner-bound, single-use stamp. It fixes the additive
64-byte C ABI layout, command capability, nonce authenticity, rejection and
cancellation semantics, and managed/native linearization. It does not activate
the backend or authorize a real writer.

[ADR 0005](adr/0005-windows-foreground-target-verification.md), "Windows
Foreground Target Verification," fixes the synchronous HWND/process/display
observation, process-wide monotonic target-epoch, hosted-app fail-closed, and
redaction contracts. It deliberately remains separate from policy composition,
WinEvent invalidation, DXGI output resolution, and writer persistence authority.

[ADR 0006](adr/0006-event-driven-capture-privacy-monitor.md), "Event-Driven
Capture Privacy Monitor," fixes callback-time latch and target invalidation, an
independent observation generation, the forced native barrier and single
generation-bound publication protocol, WinEvent thread ownership, worker
coalescing, stale-Allow compensation, sanitized terminal faults, and teardown
ordering. It remains inactive in production. The dev-live host activates it as
a narrow manual-test harness; that activation does not satisfy the production
identity or lifecycle-smoke gates.

[ADR 0007](adr/0007-bounded-window-title-and-location-invalidation.md),
"Bounded Window Title Reads and Location Invalidation," fixes the unique
process-wide title worker, its 100 ms fail-stop deadline and late-result
rejection, the exact `0x800B..0x800C` location/name hook, and conservative
location invalidation without treating that event as foreground or top-level
identity proof. It closes those two observation gates without activating
capture.

[ADR 0008](adr/0008-display-scoped-authorization-and-dxgi-output-resolution.md),
"Display-Scoped Authorization and DXGI Output Resolution," fixes the compatible
224-byte authorization tail, complete target/display identity, capability
dependencies, and strict unique-output resolver. It closes the display contract
and resolver foundations without claiming that a real writer uses them.

[ADR 0009](adr/0009-windows-lifecycle-invalidation-and-callback-time-authorization-closure.md),
"Windows Lifecycle Invalidation and Callback-Time Authorization Closure," fixes
the hidden top-level notification window, owner-thread WTS and suspend/resume
registrations, independent session/power holds, native callback gate, and
pre/post-commit supersede contract. It closes new admission immediately but
does not interrupt an already-held persistence permit or activate a writer.

[ADR 0010](adr/0010-transactional-native-capture-writer-components.md),
"Transactional Native Capture Writer Components," introduces the real strict
target observer, pre/post fingerprinted DXGI frame source, bounded WIC scaler,
the historical in-memory H.264 writer, privacy-safe manifest, whole-directory atomic store,
required-event reservation, authorization-epoch post-check, and runtime token
mailbox. These components are independently tested and are connected to C ABI
Start/Resume only in the explicitly compiled dev-live controller. The production
controller remains disabled.

[ADR 0011](adr/0011-authority-checked-native-capture-worker-orchestration.md),
"Authority-Checked Native Capture Worker Orchestration," composes those
components behind a fakeable worker backend, enforces fresh target/permit guards
for each stage, defines Pause epochs and graceful Stop, and makes validated
required-event insertion the final publication linearization point. The worker
and real Windows adapter remain behind the closed production C ABI capability
boundary and the explicit dev-live build gate.

[ADR 0012](adr/0012-run-isolated-native-capture-instance-control.md),
"Run-Isolated Native Capture Instance Control," makes one controller own native
authorization, admission, lifecycle state, worker/backend, event delivery, and
runtime shutdown. It adds run-ID-guarded checkpoints, provisional authorization
Pause, single-flight Stop finalization, and required terminal-event capacity.
The production C ABI uses its disabled mode, so this ownership change does not
advertise or start live capture. The dev-live C ABI selects enabled mode and
advertises only `ScreenCapture | CanonicalJpegChunks` in addition to the safety
capabilities.

[ADR 0013](adr/0013-display-wide-continuous-capture.md), "User-Authorized
Display-Wide Continuous Capture," is superseded as product policy by ADR 0015.
Its compatible target flag, capability bit 12, single-display pinning, and
generation implementation remain useful history; its two user-facing modes and
exclusion suspension no longer define the target experience.

[ADR 0014](adr/0014-canonical-jpeg-capture-archive.md), "Canonical JPEG Capture
Archive," supersedes the H.264 artifact and native extraction portions of ADRs
0010 and 0011. It defines the canonical JPEG chunk, write-time black-surface
rejection and deduplication, schema 10 reset, schema 3 process telemetry,
direct managed analysis, and export-only MP4 behavior.

[ADR 0015](adr/0015-user-controlled-capture-and-provider-routing.md),
"User-Controlled Capture, Privacy Processing, and Provider Routing," separates
the hard capture gate, canonical archive, optional privacy stage, user policy,
and per-stage provider bindings. It is authoritative wherever earlier capture
ADRs coupled application/window classification or provider policy to frame
persistence.

`CaptureStatus` is a stable machine-readable contract, not just display text.
It carries an unsigned 64-bit `Sequence`, `CaptureReasonCode`, and
`CaptureErrorCode` in addition to state, timestamp, and optional localized
detail. Sequence `0` supports compatibility snapshots and unsequenced backends;
once a nonzero sequence is observed, `CaptureStatusChangedEventArgs` requires
every subsequent status to advance it. The numeric reason and error values are
shared with C ABI v1 and are covered by compatibility tests:

```text
Reason: None=0, ConsentRequired=1, UserPaused=2, UserStopped=3,
        ExcludedApplication=4, ExcludedWindow=5, SessionLocked=6,
        SecureDesktop=7, RemoteSession=8, PresentationMode=9,
        SystemSleep=10, DisplayUnavailable=11, AccessLost=12,
        StorageConstrained=13, PolicyBlocked=14, BackendUnavailable=15,
        BackendFault=16, Shutdown=17
Error:  None=0, AbiVersionMismatch=1, InvalidConfiguration=2,
        InvalidState=3, DeviceUnavailable=4, AccessLost=5,
        EncoderUnavailable=6, EncoderFailure=7, StorageUnavailable=8,
        StorageFull=9, IoFailure=10, OperationTimedOut=11,
        NativeFailure=12, Unknown=255
```

A faulted status requires a non-`None` error code, while a non-faulted status
cannot carry one. `Detail` is diagnostic and localizable; retry, policy, and
state-machine decisions use the stable fields.

The current Application layer also defines the matching `ICaptureBackend`
lifecycle contract. Its Start/Resume methods require an opaque
`ICaptureRuntimeAdmissionStamp`; Pause and Stop remain authorization-reducing
commands and do not require one. `ConsentGatedCaptureService` projects backend
state while giving unavailable and faulted technology states priority over
consent state.
`AppSettingsService` runs commit-barrier Prepare before persistence, then
Committed after persistence and its in-memory snapshot update; Aborted never
restores a restrictive runtime latch. Start/Resume check that latch inside the
capture lifecycle gate. Runtime invalidation carries a separate monotonic
generation; once observed, the lifecycle service completes one sticky Stop
boundary even if authorization quickly recovers. Capture.Interop's tested
coordinator serializes native updates without holding the native gate across a
settings repository save. A phase-eligible signal is atomically published and
closes managed admission before it waits for the native apply gate; only the
latest still-current signal proceeds to native. Authorizing settings commits
therefore reconcile observations that arrive during an in-flight native update,
while an already admitted command may finish after the managed latch closes.
The coordinator assigns a process-local `ulong` runtime policy generation and
never derives it from the persisted privacy revision. Once a restrictive
Prepare or signal drops the process latch, caller cancellation cannot cancel
the native block update.
Changing the application privacy mode is an effective privacy-policy change.
The settings transaction advances the privacy revision exactly once, disables
capture, and leaves the previous consent attached to its old revision so it is
no longer current. The runtime closes admission and completes the Stop boundary
before the user can review the wider disclosure, grant consent for the new
revision, and explicitly enable capture again. Stored exclusion rules are not
deleted when `AllowAllApplications` suspends them.
The additive 224-byte runtime-authorization contract preserves its legacy
112-byte prefix. A foreground-scoped Allow binds HWND, PID, process creation
time, target epoch, HMONITOR, and display device key. A display-wide Allow uses
target-flag bit 2, keeps the HWND/PID/creation fields zero, and binds the target
epoch plus the same HMONITOR/device-key display identity. The foreground-present
and display-wide flags are mutually exclusive; both require display-present.
Capability bit 12 proves that both sides understand these semantics without
changing the ABI version or structure size. The native-issued
permit adds an internal native-instance epoch and persistence generation. The
safety core validates immutable acquisition snapshots again under a shared
write permit, while Block or an effective revoke takes the unique side, drains
existing permits, and advances the generation. The legacy privacy-context
update can block but cannot mint a write permit. Legacy and target-scoped
revisions use independent ordering rules. The first valid legacy update also
revokes target authority and permanently prevents further target-scoped
authorization on that native handle; switching back requires handle
recreation, so the two revision namespaces cannot revive one another.

Each accepted Windows event callback first replaces managed signals with
FailClosed, closes managed admission, and advances the observation generation.
In `AllowAllApplications`, Starting, Recording, Pausing, Paused, Resuming, and
Stopping pin the authorized display. Ordinary foreground and window-object
notifications are deliberately not accepted as revocation events in those
states, even when focus moves to another display; they cannot move the target,
rotate authorization or persistence generation, or discard the current partial
chunk. After capture reaches Stopped, Unavailable, or BlockedByConsent, a
foreground event may select the display for the next recording. Faulted remains
pinned while the runtime owner performs terminal teardown. Desktop/session,
power, display-topology, storage, consent, mode, and user lifecycle boundaries
retain the immediate fail-closed path.
An accepted invalidating event then calls the callback-safe native invalidation
export, invalidates verifier target continuity, and wakes the worker. The native
close is atomic and does not wait for the unique safety gate or an already-held
shared persistence permit.
The subsequent generation-bound Block update is the acknowledgement that drains
the current permit boundary; a later Allow is rejected until a Block has
confirmed the latest callback epoch.

An Allow superseded before native commit returns
`SupersededBeforeCommit` and consumes neither runtime revision nor persistence
generation. If Allow committed but callback closure defeats its final reopen,
managed code records `AppliedThenSuperseded`, consumes that committed revision
and generation, clears the local generation-bound persistence boundary, and
uses the next revision for a compensating Block. Ordinary Stop/Revoke retains
its existing invalid-state semantics. Callback, admission, and observation
generations never wrap; exhaustion is sticky, fail closed, and requires handle
recreation, while managed exhaustion still attempts the native close.

Start/Resume command admission now closes the Boolean-to-backend TOCTOU
boundary. Under the coordinator's update gate, the current native instance
issues a single-use admission bound to the Start or Resume operation, native
instance, runtime revision, persistence generation, target epoch, authorization
epoch, and runtime owner epoch. The managed owner wraps it in a private opaque
stamp that also carries the process invalidation generation. Consumption
rechecks the complete snapshot in the same gate used by authoritative settings
and signal changes, then native atomically consumes the nonce under its current
authorization before worker admission. Foreign-owner, wrong-operation, stale,
forged, and replayed stamps fail closed. The Application service never refreshes
or retries a rejected stamp. Its Boolean observation remains useful for UI state
and early rejection but is not authority.

Caller cancellation is honored before native consumption. Once native command
consumption begins, the bounded call uses a non-cancelable token so managed code
cannot report cancellation after native accepted the command. Expected policy,
state, or stamp rejection is nonfatal; malformed native output and internal
native failures quarantine the owner. A successful explicit Stop advances and
reconciles runtime authorization before another stamp can be issued.

The Application service tracks recording, user-paused, and user-stopped intent
separately from transient runtime authorization. Runtime invalidation can own a
resumable Pause, while an explicit user Pause/Stop remains sticky and prevents
automatic recovery. A persisted authorized recording intent is reconciled to a
fresh Start only after runtime authorization becomes current. Targeted tests
cover startup, transient invalidation recovery, and sticky user intent. A
clean-profile dev-live smoke must still prove that behavior before production
activation.

`WindowsCapturePrivacyProbe` can synchronously sample documented
Windows 10 1809+ signals for session unlock, input desktop, RDP/remote control,
Windows Presentation Mode, and storage headroom. API failure or ambiguity is
isolated per signal and becomes Unknown while later signals continue sampling;
its own application/window fields remain Unknown and it reads no window title.
The monitor also uses a narrow storage-only sampler on an independent
low-frequency loop (five seconds in the current dev-live policy). The initial
full observation establishes the baseline. Repeating the same storage decision
does not advance a privacy generation, invalidate target observation, run the
foreground verifier, or disturb an active pinned-display chunk. A change among
Allow, Block, and Unknown synchronously closes managed/native admission with the
same callback-time fail-closed contract, then wakes the normal barrier and
full-observation worker. A recoverable storage read failure is Unknown; it is
never reused as the prior Allow. Recovery likewise requires a fresh barrier and
authorization. The refresh task is canceled and awaited during monitor teardown.
The separate synchronous `WindowsCaptureTargetVerifier` now observes a stable
foreground target, display anchor, and size-bounded identity snapshot. A pure
typed matcher evaluates the committed application and window rule scopes
independently and returns only a matched rule ID. Each observed identity and
title is `Unknown`, known `Absent`, or `Present`; Unknown and malformed present
identities fail closed when a rule requires them, while Absent is a conclusive
non-match.
The verifier does not compose a policy decision by itself. Production leaves it
unregistered; dev-live wires it through the monitor to the runtime owner and
then applies a QA-only admission for fully resolved classic or packaged
foreground targets. In `ProtectByForegroundApplication`, explicit
application/window Block decisions are preserved. In `AllowAllApplications`,
those two decisions are forced Allow after the initial display selection while
the independent session, desktop, remote, presentation, storage, topology,
power, consent, and user-intent boundaries remain effective. Image-bound
publisher-signer verification, unique hosted-app child attribution, complete
presentation notifications, and production policy remain pending. The event
monitor supplies foreground, desktop, conservative window-object, display-topology,
current-session, and power invalidation. Object events are not foreground or
top-level identity proof, and recovery notifications only request a fresh
barrier and sample. Production activation still requires those platform inputs
and the complete reviewed screen-capture policy. Evidence extraction is already
implemented as an independent root-bound interface under Capture.Interop; it is
not an unrelated method on the lifecycle service. Capture options come from
validated application settings, and the lifecycle adapter maps native events to
`CaptureStatus` and `CaptureStatusChangedEventArgs`.

Interop remains coarse-grained. There are no per-frame managed callbacks.
Native events are queued and marshalled onto the appropriate managed
dispatcher.

### 9.1 Current Native Foundation and Safety-Core Slice

The repository now contains an independently buildable x64 C++20 DLL and C ABI
v1 foundation under `WinDayFlow.Capture.Native`. Its implemented boundary has:

- fixed-width numeric enums, opaque handles, and C-compatible POD structures
  whose first fields are `struct_size` and `abi_version`, with caller-owned UTF-8
  buffers and catch-all `noexcept` exports;
- the additive 224-byte flat runtime-authorization input extended by ADR 0008,
  preserving the 112-byte ADR 0003 prefix while adding a numeric HMONITOR and
  fixed UTF-8 display device key to the target tuple; ADR 0013 assigns existing
  target-flag bit 2 to display-wide scope and does not add a field or change the
  224-byte size;
- the additive 64-byte command-admission output defined by ADR 0004, containing
  native instance, runtime revision, persistence generation, target epoch,
  authorization epoch, and a cryptographically random 128-bit nonce;
- a native-issued permit token that adds native-instance and persistence
  generations, plus shared/unique admission linearization that prevents legacy
  privacy Allow updates from minting persistence authority;
- callback-time authorization invalidation through capability bit 11 and a
  dedicated C ABI export, with `AUTHORIZATION_SUPERSEDED` distinguishing an old
  update rejected before commit from ordinary Stop/Revoke invalidation;
- display-wide continuous-authorization capability bit 12, which is required
  before a managed owner can submit the display-only target form;
- validated capture-policy inputs and a bounded, polled event queue with
  monotonic sequence numbers, `dropped_before` gap reporting, and required-event
  reservations that protect future chunk publication, without native callbacks
  into managed or UI code;
- a strict native authorization observer that revalidates the full foreground
  tuple in the default mode or double-reads only the fixed HMONITOR/device key in
  display-wide mode, plus a strict pre/post fingerprinted DXGI-first display
  source with 8K pixel and 126.6 MiB packed/mapped BGRA ceilings; only
  Desktop Duplication access denial selects the Windows Graphics Capture
  monitor fallback, and a permanent WGC denial is terminal;
  bounded even-dimension WIC scaler, pre-encode all-black surface rejection,
  2 MiB-per-frame/64 MiB-per-chunk JPEG writer, and typed privacy-safe schema 3
  manifest whose scope distinguishes `authorized-foreground-display` from
  `authorized-display-continuous`; foreground chunks include process name, PID,
  normalized interval CPU, working set, and private memory only after PID plus
  process-creation-time identity validation, while executable paths are omitted;
- a root-bound managed canonical archive that rejects path escape/reparse
  points, validates exact manifest metadata plus frame size, JPEG markers, and
  SHA-256, and selects at most 32 images under a 12 MiB request budget;
- a two-phase same-volume chunk store that locks each no-follow directory
  identity, serializes the typed manifest internally, flushes both files in one
  staging directory, renames by the held source identity without overwrite,
  and retains retryable compensation ownership until the committed event is
  acknowledged;
- queue-instance-bound, move-only required-event reservations whose capacity,
  sequence, and drop accounting are committed only after a successful append,
  plus a hidden validated append whose final epoch load is the artifact/event
  publication linearization point;
- issuer-bound authorization-epoch post-checks for already-held permits and a
  runtime Pause/Resume mailbox that transfers initial and replacement
  persistence tokens by value, retains merged Pause transitions with a per-run
  Pause epoch, and wakes promptly on authorization changes, while preventing a
  new run until all old Stop waiters have drained;
- a fake-backed `CaptureWorker` and real Windows adapter that acquire a fresh
  target observation and permit before every sensitive stage, recheck target
  and epoch afterward, keep one token per chunk, clear owned CPU evidence
  buffers, finalize authorized partial chunks, and retain retryable compensation
  when persistence or event publication fails; and
- explicit nonblocking stop, bounded wait-for-join, and one blocking destroy for
  each valid handle. Graceful user Stop invalidates an unconsumed command stamp,
  lets the worker finish and join, and revokes afterward; privacy revoke still
  closes persistence first so stale work is discarded.

This is a real production-disabled writer foundation plus a deliberately narrow
dev-live recorder, not a production recorder. The
native and managed tests prove target/PID/display reuse rejection, target and
instance epochs, persistence-generation invalidation, permit linearization,
quiescence, timeout/failure quarantine, ABI layout, capability dependencies,
  strict no-cross-output DXGI resolution, AccessDenied-only WGC fallback,
  callback pre/post-commit races, and
Block-before-Allow acknowledgement. Native component tests additionally prove
real WIC JPEG encoding and consecutive-frame retention, bounded DXGI/WIC geometry, handle-bound
whole-directory publication and retryable rollback, typed privacy-safe
manifests, cross-instance-safe event reservation, and deterministic worker
  orchestration across Pause/Resume/Stop, every invalidation stage, finite
  topology rebuild and exhaustion, event failure, compensation retry, C ABI controller ownership,
run-ID-guarded publication, and Stop single flight. They do not prove the
end-to-end live desktop write path; that still requires a manual dev-live smoke.

The baseline complete live mask requires privacy guard, event queue,
target-scoped and display-scoped authorization, persistence-generation barrier,
deterministic stop, display-bound command admission, callback-time authorization
invalidation, screen capture, and canonical JPEG chunks. `AllowAllApplications`
additionally requires display-wide continuous-authorization bit 12; a partial
capability match fails before native update or command admission. Evidence
extraction is independent. Every current binary advertises the eight
runtime-owner core capabilities plus bit 12. The default build still advertises
neither `ScreenCapture` nor `CanonicalJpegChunks`; its authorized Start/Resume
path remains disabled. The dev-live native build adds
`ScreenCapture | CanonicalJpegChunks`, enables the controller, and starts the
worker after valid command admission. Analysis has no native decoder/extractor
capability or C ABI surface.
Legacy command-admission capability bit 8 remains defined but is not advertised
by a display-scoped DLL. Current owners require display-bound command-admission
bit 10, so both old-client/new-DLL and new-client/old-DLL combinations fail at
capability negotiation instead of at the first fully allowed authorization.
The default App uses `DenyCaptureRuntimeAuthorization` and
`UnavailableCaptureBackend`. The dev-live App registers one runtime owner across
all capture/privacy contracts and feeds its chunk-completed notification into
the analysis runner.

Dev-live activation requires three independent gates: the native and managed
projects must be compiled with `EnableDevLiveCapture=true`, publishing must also
set `DevBundleBuild=true`, and the process must receive exactly one argument,
`--enable-dev-live-capture`. The packaging script enforces the first two and
rejects dev-live ARM64. The supported commands are:

```powershell
# Default development bundle: production capture posture
pwsh -File .\scripts\Build-DevPackage.ps1 `
  -Configuration Release `
  -RuntimeIdentifier win-x64
.\artifacts\dev\WinDayFlow-dev-x64\WinDayFlow.App.exe

# Controlled x64 dev-live smoke bundle
pwsh -File .\scripts\Build-DevPackage.ps1 `
  -Configuration Release `
  -RuntimeIdentifier win-x64 `
  -EnableDevLiveCapture
.\artifacts\dev\WinDayFlow-dev-live-x64\WinDayFlow.App.exe `
  --enable-dev-live-capture
```

Omitting any gate selects the unavailable backend. Extra, missing, or
differently spelled launch arguments do not activate capture.

Manual dev-live acceptance also requires an unlocked local interactive desktop.
The first pass uses default `ProtectByForegroundApplication`: unresolved targets
and explicitly excluded application/window rules retain the fail-closed state.
The seeded, removable WinDayFlow executable rule blocks WinDayFlow itself while
that rule remains configured. The default sampling interval is 10 seconds. Graceful Stop
publishes a valid partial chunk for a short smoke; validating full rollover
requires keeping one stable ordinary external window in the foreground through
the 15-minute chunk boundary.

The second pass selects `AllowAllApplications` and must observe capture turn off,
the privacy revision advance, and old consent become stale before renewed
consent. After recording is completely stopped, move the WinDayFlow window to
the test display and re-enable capture there, then switch among ordinary apps,
WinDayFlow, and a window on another display for more than one chunk. No
foreground-driven Pause/Resume, target movement, generation change, or
partial-chunk loss is acceptable; the committed manifest records
`authorized-display-continuous` and remains bound to the display selected at
Start. This pass also confirms the warning is accurate: application/window
exclusions remain stored but are inactive, and WinDayFlow UI can be captured.
To select another display, wait for recording to stop completely, move the
WinDayFlow window to that display, and Start a new recording there. A topology
change remains fail closed rather than silently widening scope.

Both passes verify session lock/secure desktop, current-session WTS, power,
display topology, storage, consent revocation, and explicit Stop remain fail
closed. Remote Desktop and Windows Presentation Mode continue to follow their
separate settings. Passing either smoke is dev-live QA evidence only; it does
not open production capture.

During the continuous pass, keep storage healthy across at least three
five-second refresh periods and confirm that privacy generation, target epoch,
and recording state remain stable. A controlled low-headroom volume or injected
storage-read failure must close admission on the next refresh without any
foreground/window event; failure publishes Unknown rather than retaining Allow.
Restored headroom must pass a new Block/observation/authorization sequence before
recording recovers.

The remaining activation gates are integration work beyond command admission,
synchronous target observation, the bounded title worker, and conservative
window-location invalidation. Publisher identity must be bound to the running
image, and hosted windows must be attributed to one real child application.
Display-topology, current-session WTS, suspend/resume invalidation, and the
independent low-frequency storage-headroom refresh now exist; presentation
notifications remain missing. The
selected HMONITOR/device key now has both a strict resolver and a frame source
that revalidates the complete binding before and after acquisition. The
controller-owned worker now composes target observation and a fresh permit
around acquisition, WIC JPEG encoding, staging, rename, and reserved-event publication.
Remaining production gates include filesystem interruption and disk-full
integration, durable compensation across object/process loss, stale-staging
recovery, committed-event replay, owner-epoch races, production-grade target
attribution, and consent-gated Windows lifecycle/live-desktop tests. Until those
gates pass, the production build must keep registering
`DenyCaptureRuntimeAuthorization` and `UnavailableCaptureBackend`; only the
explicitly gated dev-live harness may persist live frames.

The safety core, target observer, runtime mailbox, event reservation, worker
orchestration, and Windows backend adapter are original WinDayFlow work. The
atomic store, manifest, DXGI frame source, WIC scaler, and JPEG writer
are derived from the reviewed QiDayflow
`capture_service.cpp`; their source headers, exact hashes, pinned revision, and
MIT notice are recorded in the provenance ledger and manifest.

### 9.2 Windows Foreground Target Verification Foundation

`WindowsCaptureTargetVerifier` is a synchronous, serialized observer for the
supported Windows x64 baseline. One verification performs this fixed-size
sequence. Each title attempt has an independent 100 ms wall-clock safety
deadline; this decision does not claim that every other Windows API in the
complete verification sequence has a common deadline:

1. Read the foreground HWND, its owner TID/PID, and the HMONITOR plus
   `GetMonitorInfoW` device key selected by `MonitorFromWindow`.
2. Open the owner with `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`, then
   establish the process ID, creation time, and that the process is still
   active.
3. Read the window title, executable basename, package-family name, publisher
   observation, and the window title again. The two title observations must be
   identical. A known `ApplicationFrameHost.exe` result is unresolved and fails
   closed instead of being treated as the foreground application.
4. Re-read process ID, creation time, liveness, foreground HWND, owner TID/PID,
   and display anchor. Any target-stability mismatch becomes `Unknown` and the
   process handle is always released.

A zero foreground HWND is known `Absent`. Unsupported platforms, API ambiguity,
permissions failures, process exit, unstable title/owner/display observations,
and recoverable platform exceptions are `Unknown`. Identity-field failures are
kept field-scoped as `Unknown` inside `NativeCaptureIdentitySnapshot`; they are
not invented as `Absent` or a non-match. The coordinator must evaluate the
complete committed exclusion-rule snapshot again and fail closed wherever a
rule requires an unresolved field.

Identity observation is deliberately bounded in character count. The executable
reader returns only the basename; the complete process path exists only inside
the P/Invoke read buffer and that pooled buffer is cleared on return.
Package-family absence is accepted only for `APPMODEL_ERROR_NO_PACKAGE`; other
results are `Unknown`. Package and process-image buffers have fixed upper
bounds. Production title reads use one lazy, process-wide dedicated background
worker and one private fixed 32K-character buffer. The buffer is cleared before
and after native use.

The title worker admits only one request while `Idle`. Every request receives a
100 ms monotonic wall-clock deadline. A request that expires while still
`Queued` may be removed and return the worker to `Idle`. Once the worker enters
`InFlight`, or after it claims the bounded local `Completing` phase, a timeout
permanently changes the process-wide reader to `Poisoned`; every later ordinary
title read in that process immediately returns `Unknown`, and no replacement
worker or request queue is created. The caller waits on a request-private
persistent signal outside the reader-state lock, so blocked native or local
completion work cannot hold the caller or `Dispose` indefinitely. A native call
that returns after expiry cannot build, retain, complete, or publish its title.
If local construction began in time but crosses the deadline, its temporary
value is discarded before commit. After native use, the private buffer is
cleared in `finally`; timeout never races a clear against Windows still writing
the buffer. Recoverable failures become `Unknown`, while fatal failures are
re-thrown to the current caller or retained as a sticky fatal when they arrive
after timeout. This bounds caller wait without treating a late native return as
current evidence. Publisher-certificate identity remains `Unknown` until an
offline trust check can bind the primary signer leaf to the opened running
image and hash its DER bytes with SHA-256. Looking up a certificate from an
unbound path is not accepted as proof.

The stable fingerprint is HWND, PID, process creation time, owner TID,
HMONITOR, and a case-normalized display device key. One locked, process-wide
source atomically owns the current fingerprint, its invalidated/gap state, and
the last issued epoch. Constructing any verifier globally invalidates the
current fingerprint. A stable verification resolves through the source: the
same current fingerprint keeps its epoch, while an invalidated or changed
fingerprint receives a newly issued nonzero epoch. Every `Absent` or `Unknown`
result also globally invalidates the fingerprint, so even the same tuple
receives a fresh process-wide epoch after the gap.

Because resolution and invalidation share the source lock, an older overlapping
verifier cannot revive its prior epoch after another verifier is constructed or
observes a gap. Its next stable verification either joins the process-wide
current fingerprint and epoch or advances the source for a different target.
Verifier recreation therefore cannot create an ABA by restarting local state.

The source never wraps. After it issues `ulong.MaxValue`, an unchanged target
may continue reporting that already-issued value, but the process can never
issue another epoch. Any later gap, target/display change, or verifier
recreation that needs a new value fails closed for the rest of the process
lifetime. A new process, or a future explicitly persisted epoch namespace, is
required to resume issuance. The verifier lock serializes one complete
observation within an instance, while the source lock serializes fingerprint
resolution, invalidation, and issuance across every instance. Live integration
must still keep one verifier instance with the native runtime owner/handle
lifecycle so construction and invalidation linearize with native revocation;
the shared source prevents epoch reuse but does not itself revoke native work.

The result contains three slices: `NativeCaptureTargetIdentity` for the native
authorization tuple including its display anchor, a consistency-checking
`WindowsCaptureDisplayTarget`, and a `NativeCaptureIdentitySnapshot` for typed
rule matching. It is not a complete
`NativeCapturePrivacySignals` value, does not evaluate user policy, does not
mint command admission or persistence authority, and does not prove that the
HMONITOR maps to the DXGI output being acquired. Target, display, and identity
`ToString()` representations expose states only and replace observed values
with `[REDACTED]`; raw paths, titles, package identities, handles, and display
keys must not enter logs or native events.

Together with ADR 0007, this foundation closes the stable synchronous
observation and bounded-title portions of the ADR 0003 target gate. Dev-live
uses them under its QA-only verifier-resolved classic/packaged target admission.
The default application-protection mode evaluates WinDayFlow through the same
ordered rule matcher as other applications; schema 12 seeds a normal executable
rule that users may remove. The explicitly consented all-applications mode
suspends all application/window rules on the selected display. Production live use
remains blocked until all of the following are implemented and tested together:

- primary publisher-signer verification bound to the running image;
- unique child-application attribution for hosted Windows surfaces;
- production policy integration beyond the dev-live QA target admission,
  and complete presentation notifications;
- clean-profile startup-intent, evidence-Pause, sticky user Pause/Stop, and
  repeated recovery smoke; and
- clean-profile real acquisition, encoding, temporary output, atomic
  publication, metadata, cleanup, and privacy-transition smoke under the native
  persistence-permit boundary.

### 9.3 Event-Driven Privacy Monitor Foundation

Production does not register `WindowsCapturePrivacyMonitor`; the dev-live host
does, closing the polling-only race around the ADR 0005 verifier for controlled
manual testing. One owner thread registers a never-shown, non-activating
top-level HWND rather than a message-only window because it must receive system
broadcasts. The same message pump owns current-session WTS registration,
user32 suspend/resume registration, and `WINEVENT_OUTOFCONTEXT` hooks for
foreground and desktop switches, window-object create/destroy, and the exact
`0x800B..0x800C` window-object location/name range. Object callbacks are
accepted only when the HWND is
nonzero and the event reports `OBJID_WINDOW` with `CHILDID_SELF`. These
predicates do not prove that the HWND is top-level or foreground. The callback
carries only a stable change kind; it does not read HWND titles, process
identity, settings, exclusion policy, or capture lifecycle state.

Event eligibility is mode-dependent. `ProtectByForegroundApplication` accepts
foreground and relevant window-object events, immediately revoking the old
target before asynchronous re-observation. After `AllowAllApplications` has
established a display-wide target, ordinary foreground and window-object events
do not invalidate authority, advance persistence generation, or pause the
worker merely because HWND/PID/title or the foreground display changed while
capture is Starting, Recording, Pausing, Paused, Resuming, or Stopping. This is
the behavior that prevents visible "restoring recording" churn and preserves an
in-progress chunk during ordinary application and cross-display focus switches.
The capture source stays on the display selected at Start; other displays are
not admitted. Once capture is Stopped, Unavailable, or BlockedByConsent,
foreground events may establish the display for the next recording. Faulted is
terminal and stays pinned during teardown. Desktop/security boundaries,
current-session WTS, power, `WM_DISPLAYCHANGE`, storage, consent/mode changes,
and user Pause/Stop remain independent immediate authorization or lifecycle
events. Changing the recording display requires
waiting for Stop to complete, moving the WinDayFlow window to the intended
display, and starting there.

Before each accepted callback returns, the monitor replaces the coordinator's
signals with FailClosed, closes managed admission, advances a
privacy-observation generation, synchronously closes native authorization
admission, invalidates target-epoch continuity, and offers one wake token.
Every event advances both managed observation and native callback generations
even when a burst coalesces to one worker sample. A qualified `LOCATIONCHANGE`
is therefore only a
conservative invalidation signal: the next verifier sample must establish any
foreground, owner, and display facts. This observation generation is
independent from the runtime invalidation generation used by Application
command admission.

The coordinator enforces the per-generation sequence:

```text
synchronous managed and native admission invalidation
-> forced FailClosed native persistence barrier
-> at most one generation-bound resolved publication
```

The worker owns no settings snapshot. After the barrier it double-samples the
base Windows privacy probe around one atomic target/identity/display verification
and publishes through the coordinator, which recomposes against the latest
committed settings. A changed generation rejects an old sample even when its
values equal the new sample. An older native Allow rejected before commit does
not consume its revision. An Allow committed before callback closure consumes
its revision and persistence generation but cannot reopen the native gate; it
receives a compensating FailClosed update under the same apply gate using the
next revision. Quiescence cannot be reversed by an overlapping authorizing
settings commit.

`WM_DISPLAYCHANGE` invalidates display continuity and requires a fresh sample but
does not create a lasting hold. WTS lock, disconnect, logoff, and terminate enter
an independent session-unavailable hold; corresponding connect, logon, unlock,
create, and desktop-ready events clear it, while remote-control changes only
request revalidation. The low-frequency health refresh also samples only the
current session decision while that hold is active. An observed unlocked session
clears the hold through a new SessionAvailable invalidation when Windows omitted
the matching event. Suspend enters an independent power hold and the supported
resume notifications clear it. Unknown WTS events fail closed as unavailable.
While either hold remains, the worker completes the current native Block barrier
and publishes FailClosed without a full target sample. Such publications do not
replace the last independently sampled storage decision, so a healthy disk does
not alternate between Allow and Unknown every refresh. Clearing a hold never
restores old authority: the new generation still requires a Block acknowledgement
and a fresh sample. All registrations and reverse-order cleanup run on the owner
thread;
partial startup or uncertain cleanup remains fail closed and conservatively
retains callback roots when detachment cannot be proven.

The single-slot wake channel is a work-coalescing mechanism, not an invalidation
coalescer. Start, runtime, callback, generation, and teardown failures close the
monitor and expose stable enum-only exceptions without raw Windows values or
inner exceptions. Hook teardown occurs on the owner thread in reverse order;
the callback bridge is released after clean unhook and conservatively retained
only while native callback completion cannot be proven.

Callback closure prevents new command admission and new native persistence
permits, but it cannot revoke a permit already held by the writer. The
following Block barrier drains existing holders before acknowledging the
generation; the writer must additionally recheck authority at acquisition,
encode, metadata, rename, and committed-event boundaries. The present source
covers conservative window-location, display-topology, current-session,
sleep/resume invalidation, and independent periodic storage-headroom changes;
presentation notifications remain pending. The monitor itself does not own user
intent: the Application service distinguishes runtime-owned resumable Pause from
sticky user Pause/Stop.
Only dev-live dependency injection registers the monitor. See ADRs 0006, 0007,
and 0009 for the complete contract.

Capture invariants:

- Capture uses a configurable image-evidence cadence and an independently
  configurable foreground-window/resource-context cadence. Neither cadence is
  fixed in the architecture contract.
- Shipping defaults, output bounds, chunk duration, and adaptive behavior are
  selected through an ADR backed by timeline-quality, CPU/GPU, power, storage,
  and thermal benchmarks on representative Windows hardware. They are versioned
  policies rather than hard-coded assumptions inherited from a reference.
- Display changes, topology changes, pause, idle, error, and stop commit a
  partial chunk when it contains valid frames.
- Chunk rollover uses the transformed frame's monotonic timestamp rather than
  only the earlier schedule poll. When that frame reaches the configured chunk
  duration, the worker commits the old chunk first and encodes the current frame
  as the next chunk's first frame, without dropping it or extending the old
  chunk by another sampling interval.
- Files are written as partial artifacts and atomically renamed on completion.
- The current writer publishes individually bounded JPEG frames plus a typed
  schema 3 manifest as the only source of truth. Schema 2 remains accepted for
  read-only legacy archives. MP4 is a rebuildable on-demand
  export and is never a recording or analysis artifact.
- Consecutive near-duplicate frames may be omitted, but the first and final
  frame of each chunk are retained.
- A single JPEG is bounded to 2 MiB and one analysis request is bounded to
  12 MiB of image payload by default.

## 10. Analysis Workflow

Analysis is a durable state machine, not an in-memory task list.

```text
Pending -> Claimed -> Extracting -> Observing -> Summarizing -> Committing
   ^          |            |            |             |             |
   |          `------------+------------+-------------+-------------'
   |                         retryable failure
   |
FailedRetryable --retry/backoff-->

Any active state -> FailedTerminal
Pending/FailedRetryable -> Cancelled
Committing -> Completed
```

The current App host composes this workflow at startup. It initializes SQLite,
settings, and provider configuration before starting a hosted background runner.
The runner treats a full scan of committed `chunks/<id>/manifest.json` files as
the source of truth; startup and chunk-completed events are only wake reasons.
It idempotently stores chunks and jobs, fingerprints each source, calls the
currently validated OpenAI-compatible provider, and commits normalized Timeline
entries with job completion in one SQLite transaction.

ADR 0015 replaces the single active-provider send gate with independently
enabled processing-stage routes. A route is ready only when its selected profile
is complete, validated for its current revision, technically capable of the
stage, and still enabled immediately before request creation. An optional
privacy route may precede timeline analysis, use the same provider, use another
provider, or be disabled. The selected privacy match/error policy determines
whether original evidence, a distinct redacted derivative, or no evidence
continues. No endpoint is preferred or rejected because WinDayFlow infers it to
be local, remote, trusted, or untrusted.

Route changes are serialized against creation of a new provider request but do
not stop capture. Completed chunks are not reanalyzed merely because a profile
or route revision changes, preserving user-edited Timeline history unless the
user explicitly requests reanalysis.

Rules:

- Only one worker may claim a job at a time.
- Claiming uses a transaction and a lease timestamp.
- Expired active leases are recovered after abnormal process termination.
- Attempts are bounded and persisted.
- Retry decisions use stable error codes, not display text.
- Observation and activity outputs are schema-validated.
- A commit-eligible provider result contains at least one activity and covers
  the complete request range contiguously: the first `start_offset_ms` is `0`,
  every later `start_offset_ms` equals the preceding `end_offset_ms`, and the
  final `end_offset_ms` equals `range_duration_ms`. Leading, internal, and
  trailing gaps are invalid.
- Evidence uncertainty is represented by an activity covering the affected
  interval with `unknown` labels; uncertainty never permits omitted time.
- Empty or incomplete coverage maps to `ProviderResponseInvalid`, transitions
  the job to `FailedTerminal`, and is rejected before `Committing`. No generated
  Timeline entries or `Completed` transition enter the atomic result
  transaction.
- Observations, activities, timeline writes, and job completion commit in one
  SQLite transaction.
- Duplicate chunk completion events are idempotent.
- Stale provider responses cannot overwrite a newer attempt.
- Evidence remains available until successful analysis or explicit user action.

AI-degraded behavior is explicit:

- Capture, context collection, evidence persistence, retention protection, and
  manual pause/stop do not depend on an AI provider or network connection.
- When analysis cannot run, jobs remain visibly pending or failed-retryable;
  the application does not fabricate semantic activities or mark them complete.
- The timeline can project locally recorded chunk and context boundaries as
  clearly labeled unprocessed intervals. Users can inspect evidence and promote
  an interval into a manual timeline entry without waiting for a model.
- Search, deterministic metrics, and export distinguish normalized entries,
  manual entries, and unprocessed intervals so incomplete analysis is never
  silently presented as a complete journal.
- When analysis later resumes, generated activities remain proposals governed
  by merge rules and cannot overwrite manual entries or user corrections.

## 11. Timeline Merge and Editing

Automatic merge is allowed only when entries:

- occur on the same local calendar day;
- are separated by no more than the configured threshold, initially 120 seconds;
- have compatible title, category, productivity, and primary application;
- are not locked by a user edit;
- originate from compatible analysis versions.

Durations are summed. Weighted metrics use source duration as weight. Peaks use
the maximum source value. User edits set explicit provenance fields and are not
silently replaced by reanalysis.

## 12. AI Provider Architecture

```csharp
public interface IAiProvider
{
    ProviderCapabilities Capabilities { get; }

    Task<ProviderStageResult> ExecuteAsync(
        ProviderStageRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatDelta> ChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken);
}
```

Capabilities include:

```text
VisionInput | StructuredOutput | PrivacyInspection | TimelineAnalysis
TextGeneration | Streaming | ToolCalling
```

Current and deferred adapters:

1. OpenAI-compatible HTTP is implemented for bounded vision analysis. Any
   compatible user-configured endpoint can use this adapter without a branded or
   deployment-specific product mode.
2. Additional standard API formats are added only when they cannot be expressed
   through an existing adapter; Gemini is currently deferred.
3. CLI/process adapters are deferred until their interfaces and process boundary
   are stable. Their execution location would still not establish user trust.

Provider profiles are reusable connection descriptions, not trust labels and not
stage assignments. An independently persisted binding selects one profile for
each enabled stage. The initial stages are `PrivacyInspection` and
`TimelineAnalysis`; later stages may add Daily, Weekly, and Chat without changing
capture or rewriting existing profiles. The same profile may be bound to several
stages, and a stage may point at any compatible saved profile.

All provider-specific payloads and labels are normalized in Infrastructure.
Provider responses cannot write domain state directly. Structured analysis uses
strict DTO and semantic validation before entering the application workflow.
Privacy findings are normalized before WinDayFlow performs any redaction, hold,
review, or pass-through action. A remote privacy route necessarily receives the
original evidence; the UI discloses that endpoint and payload but does not
forbid the user's selection.

## 13. Chat Retrieval Boundary

Chat receives controlled, read-only tools rather than unrestricted database
access:

```text
get_timeline(date_range, filters)
get_daily_summary(date)
get_weekly_metrics(week)
search_activities(query, date_range)
get_app_usage(date_range)
```

Every tool has bounded date ranges and result sizes. Answers retain references
to source timeline entries. The UI can navigate from a reference to its exact
date and time range.

SQLite FTS5 is the initial search implementation. Embeddings and a vector index
are deliberately deferred.

## 14. Persistence

Initial table groups:

```text
capture_sessions
capture_chunks
analysis_jobs
observations
activities
timeline_entries
timeline_entry_apps
timeline_entry_tags
daily_journals
standup_reports
weekly_reviews
conversations
conversation_messages
ai_provider_profiles
analysis_stage_bindings
privacy_screenings
provider_invocations
app_installation
application_catalog
app_settings
capture_exclusion_rules
schema_migrations
```

Persistence rules:

- Schema migrations are ordered, versioned, transactional where SQLite permits,
  and tested against representative old databases.
- Queue and timeline writes use explicit SQL and explicit transactions.
- User-authored values and provenance remain distinguishable from regenerable
  analysis state and cannot be overwritten by regeneration.
- Generated Daily and Weekly projections are rebuildable and record their input
  range and generation version.
- Deleting timeline data and deleting recording evidence are separate actions.
- Changing the data directory never overwrites an unrelated existing database.
- A QiDayflow import tool is planned; source data remains unchanged until the
  import result has been verified.

### 14.1 Current Persistence Slice

The implemented persistence slice uses schema version 12. Version 1 contains
`schema_migrations`, `timeline_entries`, `timeline_entry_apps`, and
`timeline_entry_tags`; version 2 adds the singleton `app_settings` row while
preserving existing timeline data. Version 3 adds evidence-retention,
sensitive-application exclusion, remote-session, screen-sharing, and privacy-
revision fields. It retains only the version and acceptance time of version 1
consent as stale metadata, not its covered revision or a complete snapshot of
the old privacy choices. It forces capture off because that consent did not
cover the new choices. Version 4 adds an initially empty, ordered
`capture_exclusion_rules` child table without changing capture state or privacy
revision during migration. Version 5 adds constrained `capture_chunks` and
durable leased `analysis_jobs`; version 6 adds the active OpenAI-compatible
provider profile, protected credential metadata, validation revision, and
provider/job indexes, while forcing cloud analysis off during migration.
Version 7 adds the constrained `capture_application_privacy_mode` setting and
migrates every existing profile to `ProtectByForegroundApplication` (`0`)
without changing capture state, privacy revision, or consent. A later user
change to `AllowAllApplications` (`1`) is an effective privacy change and, unlike
the migration, atomically advances the privacy revision and disables capture.
Version 8 adds the constrained `capture_interval_seconds` setting, defaults it
to 10 seconds, and accepts only 5, 10, 15, 30, or 60 seconds.
Version 9 adds ordered `timeline_entry_evidence` references and persisted
`analysis_job_window_members`, including source fingerprints and contribution
ranges. Version 10 intentionally clears legacy timeline rows, analysis jobs,
window members, and capture chunks, then rebuilds `capture_chunks` around the
canonical manifest path, captured/retained counts, JPEG dimensions, and total
frame bytes. Settings and provider configuration are retained. Version 11 adds
nullable process name, PID, CPU basis points, working set, and private memory
columns for identity-validated foreground telemetry. Version 12 seeds
`WinDayFlow.App.exe` as an enabled, removable application exclusion rule unless
an equivalent rule already exists. It is not a hard-coded capture exception.
`timeline-v5` can rewrite
a continuous same-local-day window of up to 45 minutes without losing ordered
capture provenance and requires provider categories and productivity labels to
match the product enums exactly.
The application completes the idempotent migrations and initializes settings and
provider configuration before starting the host and creating the main window.
Every timeline write plus its ordered child rows commits in one SQLite
transaction. Settings writes
use `BEGIN IMMEDIATE`, compare the complete expected snapshot, and atomically
persist and read back the singleton settings row plus the complete ordered rule
snapshot. An effective rule change also disables capture and advances the
privacy revision exactly once in that transaction. New rules must begin at
revision 1; changed or explicitly moved rules advance their own revision exactly
once, and revisions cannot be rolled back or skipped.

Manual entries are explicitly identified as user-authored. They do not invent
capture evidence, model confidence, or an analysis version, and all editable
fields carry user provenance timestamps. The WinUI timeline loads durable
entries from this repository and supports create, edit, and delete; date-scoped
results are searched and filtered in the ViewModel. The settings store persists
theme, capture-enabled, cloud-analysis, consent version/timestamp/privacy
revision, evidence-retention days, conservative exclusion/session choices, the
current privacy revision, application privacy mode, capture interval, and typed
ordered application/window rules with
database constraints. Committed capture chunks, analysis jobs, and provider
profiles are durable. Analysis jobs retain their ordered sliding-window members;
normalized results transactionally replace only unlocked generated entries after
checking the captured timeline ID/revision baseline. Unprocessed intervals
project directly from chunk/job truth, and completed results commit into the
editable Timeline. Daily,
Weekly, Journal, Chat, audit, retention, and import table groups remain pending.

The next development schema implements ADR 0015. It replaces the single active
provider and capture-coupled privacy mode with `analysis_stage_bindings`,
`privacy_screenings`, and `provider_invocations`. Existing provider profiles and
DPAPI credentials are retained. An existing explicitly enabled analysis profile
may seed only the `TimelineAnalysis` binding; migration never enables a privacy
stage. Existing exclusion rules preserve their enabled state as no-send rules
without disabling capture or advancing recording consent. Because this remains
a development product, the obsolete application privacy-mode column may be
removed by a documented reset rather than a compatibility bridge.

### 14.2 Statistics Read Model

Statistics is a read model over canonical operational data, not a second source
of truth. Definitions are fixed before visualization:

- recorded duration is the union of available capture-chunk ranges, not retained
  frame count multiplied by capture interval;
- focused/category/productivity duration is the sum of non-overlapping normalized
  timeline ranges with the corresponding classification;
- active days are distinct local dates with available capture or user-authored
  timeline data;
- captured and retained frames come from `capture_chunks`; deduplicated frames
  are their non-negative difference;
- provider request count, success, latency, token usage, and reported cost come
  from `provider_invocations`, never inferred from job attempts;
- evidence usage comes from a cancellable file-system scan and reports database,
  original evidence, derivatives, exports, and logs separately; and
- accompanied days use an explicit installation timestamp, while accompanied
  work duration uses recorded-duration truth.

Basic timeline, frame, deduplication, job-state, and process metrics are already
derivable from schema v12. Accurate invocation usage, first-use time, privacy-
stage results, and complete storage breakdown require the target tables/services
listed above. Missing provider usage is displayed as unavailable, never estimated
as an exact token count or cost.

## 15. Privacy and Security

The following are mandatory release requirements. ADR 0015 is authoritative for
the target boundary; schema v12 and the current dev-live foreground coordinator
are transitional implementation, not the final product policy. Production live
capture remains unavailable until the refactor and its clean-profile acceptance
tests pass. Current local data is not protected by application-level database or
evidence encryption.

### Capture Safety

- Recording is opt-in. Onboarding explains the selected display, screenshot
  cadence, local storage location, retention, and direct Pause, Stop, and Exit
  actions. Expanding the collected data classes requires renewed recording
  consent; changing an analysis route does not.
- The hard capture gate is limited to explicit user intent or revoked recording
  consent, lock or secure desktop, suspend/session/display loss, insufficient
  storage, capture-access loss, fatal runtime failure, and shutdown.
- Ordinary foreground changes, application/window exclusions, provider state,
  optional privacy-inspection state, Remote Desktop, and presentation state do
  not interrupt the target local archive. These observations may inform metadata
  or a user-configured provider-request rule.
- One capture run remains pinned to one selected display. Focus moving to
  another display neither moves the target nor expands collection to every
  monitor. A display loss crosses the hard gate.
- Unknown optional identity or classification input means that metadata is
  unavailable. It does not revoke persistence authority. Unknown lock, secure-
  desktop, display, or storage state remains a hard-gate failure because those
  inputs establish whether capture can be performed safely.
- Primary status is derived from native state plus the last successful frame-
  persistence heartbeat. It never reports "privacy protected" while frames are
  being committed. Processing and privacy statuses are displayed separately.

### User-Controlled Processing and Trust

- Provider profiles contain standard API, endpoint, model, credential, timeout,
  validation, and capability data. WinDayFlow does not assign trust from the
  endpoint, model, vendor, loopback status, or deployment location.
- Each processing stage has an independent enabled switch and provider binding.
  The same provider may serve every stage, or different providers may serve
  privacy inspection and timeline analysis. A provider profile is never assigned
  to a stage merely because it was saved or validated.
- Privacy inspection is optional. When disabled, the user may route original
  bounded evidence directly to timeline analysis. When enabled, its provider may
  use any configured endpoint, including an endpoint that receives the original
  evidence over the network.
- Match behavior and failure behavior are explicit user policy: audit, redact,
  hold, require review, or pass through where applicable. WinDayFlow discloses
  consequences and preserves the user's choice rather than enforcing one
  product-selected trust hierarchy.
- Enabled application/window exclusion rules are user-authored no-send rules at
  the request boundary. They do not stop capture. The seeded WinDayFlow rule is
  an ordinary removable rule.
- A privacy provider returns a normalized typed result. WinDayFlow performs any
  configured redaction and writes a separate derivative artifact; provider
  output never overwrites original evidence or directly mutates timeline state.

### Request, Credential, and Audit Boundary

- Immediately before each provider call, the application rechecks the stage
  binding, profile and route revisions, credentials, no-send rules, configured
  prior-stage result, and exact original or derivative evidence references.
- The UI names the selected provider and endpoint and shows the evidence and
  metadata classes that will be sent. Loopback versus non-loopback may be shown
  as a factual endpoint property, never as a trust verdict.
- Provider calls produce a local payload-free invocation record containing the
  stage, profile/route revisions, endpoint origin, evidence references, byte and
  item counts, time, outcome, correlation ID, and usage data when the provider
  reports it. Accurate call counts come from this ledger, not job attempts.
- API credentials are protected for the current Windows user using DPAPI.
- Provider-specific payloads, credentials, authorization headers, base64/JPEG
  data, and raw window titles never enter normal logs. INFO logging is event-
  oriented and never per-frame; diagnostic mode uses an explicit allowlist.
- Providers cannot access SQLite directly and cannot authorize another stage.
  Adapters normalize and validate responses before application policy executes.

### Local Evidence and Retention

- Original canonical JPEG evidence and sanitized derivatives are different
  artifacts with explicit provenance. Derivatives are rebuildable and cannot
  replace or silently weaken the original evidence record.
- Retention does not delete evidence needed by pending, active, held, review, or
  failed jobs unless the user explicitly chooses a destructive action that names
  the affected scope.
- Storage cleanup is quota-based, deterministic, auditable, and interruptible.
- Consent history is not fully audit-ready until immutable snapshots record the
  concrete recording disclosure and provider-route disclosure accepted at each
  revision.
- No built-in or provider classifier may claim universal password, secret,
  financial, health, or private-content detection. Optional OCR, deterministic
  rules, and VLM stages expose their version and limitations.

## 16. Windows Application Lifecycle

At completion, the WinUI application owns:

- single-instance activation;
- main-window show/hide behavior;
- tray icon and tray commands;
- startup registration;
- notifications;
- update presentation;
- coordinated shutdown.

The current slice implements a single main instance with activation
redirection, main-window creation, host startup and shutdown, theme switching,
and dependency injection. Tray behavior, startup registration, notifications,
update presentation, close-to-tray disclosure, and persisted window preferences
remain pending.

The native capture component owns only capture, encoding, extraction, and its
internal resource lifetime.

Closing the main window hides it after the behavior has been disclosed during
onboarding and remains changeable in Settings. The tray menu exposes the current
capture state and direct commands for show, pause/resume, and explicit exit.
Explicit application exit performs:

1. Stop accepting new analysis work.
2. Flush a valid partial capture chunk.
3. Finish or safely checkpoint SQLite transactions.
4. Stop and release native capture resources.
5. Dispose tray and window resources.
6. Exit the process.

## 17. Windows Experience Contract

- The title bar and application body read as one continuous Fluent surface.
  Content extends into the title-bar region, with Mica where supported and a
  coherent solid theme fallback, rather than stacking an unrelated system strip
  above an application card.
- Minimize, maximize/restore, close, resize, drag regions, Snap Layouts, the
  `Alt+Space` system menu, taskbar activation, and multi-monitor placement retain
  standard Windows behavior. Custom content never overlaps system caption
  buttons or makes their hit targets ambiguous.
- The primary shell uses `NavigationView` in Expanded, Compact, and Minimal
  modes according to available content width. The hamburger command remains
  available whenever the pane is collapsed; Minimal mode opens an overlay that
  preserves page context and keyboard focus.
- The system tray is part of the experience contract, not a hidden worker UI.
  Its icon, tooltip, and commands communicate capture state and provide direct
  show, pause/resume, and exit actions.
- Use standard WinUI controls and system interaction patterns.
- Use `NavigationView`, `CommandBar`, `InfoBar`, `ContentDialog`, `TeachingTip`,
  and system notifications for their intended purposes.
- Follow system light, dark, and high-contrast themes, text scaling, contrast,
  accent color, and reduced-motion preferences. Theme changes update the custom
  title bar and caption-button colors without restarting.
- Keep timelines virtualized; do not render an entire history in nested panels.
- Settings uses a landing page plus secondary pages. Storage uses a read-only
  path row with an Open Folder command; provider details never flatten into the
  settings landing page.
- Category and productivity metadata use restrained semantic icons, text, and
  theme-aware badges rather than unrelated bright pills. Tags use compact
  four-pixel-radius tokens and collapse excess values behind a count.
- Application icons are fixed at 20-24 pixels with a stable neutral fallback.
  App names remain available in tooltips and details; mixed missing/icon states
  must not shift timeline layout.
- Statistics uses one compact KPI band followed by unframed full-width trend,
  distribution, and top-application sections. It does not nest cards or use
  decorative charts without reproducible metric definitions.
- Ensure complete keyboard navigation, logical tab order, visible focus,
  accelerator discoverability, focus restoration after overlays, and Narrator
  announcements for capture, progress, failure, and destructive-action state.
- Expose stable names, roles, states, and AutomationIds for navigation, capture,
  provider disclosure, evidence preview, and destructive actions.
- Validate window layout and title-bar hit testing at 100%, 125%, 150%, and 200%
  display scaling, including mixed-DPI monitor transitions and text scaling.
- Motion is restrained, follows the system animation setting, and never carries
  essential state by itself. Capture controls and timeline editing remain usable
  when animations are disabled.
- Responsive states reflow controls and timeline items without clipped text,
  inaccessible commands, or horizontal dependence at narrow window sizes.
- Prefer Windows-native information architecture over copying macOS glass and
  pill styling.

## 18. Resource, Performance, and Long-Running Budgets

Numeric budgets are not invented in this architecture document. Before a
release candidate, representative measurements establish explicit thresholds in
a versioned performance ADR and release checklist. A release must meet those
thresholds; recording results without a pass/fail gate is insufficient.

The benchmark contract covers:

- idle, active-capture, frame-extraction, and analysis CPU, GPU, working-set,
  handle, thread, and energy impact;
- evidence and database growth per hour and per retained day, cleanup behavior,
  free-space reserve, and disk-full degradation;
- cold launch, window restoration, tray-command response, pause/resume, first
  useful timeline render, date navigation, search, and large-history scrolling;
- the quality/resource trade-off for image-evidence and context cadences, output
  dimensions, chunk duration, and evidence bounds;
- 24-hour and multi-day soak tests for leaks, queue growth, database contention,
  timestamp continuity, and recovery after sleep, hibernate, session switch,
  display topology change, GPU reset, provider outage, and forced termination;
- representative low, mainstream, and high hardware tiers on the supported
  Windows 10 and Windows 11 baselines, including mixed-DPI multi-monitor setups.

Benchmark inputs, hardware, Windows and driver versions, power mode, provider or
fixture, data set, sample count, percentile method, thresholds, and regressions
are stored with the release evidence. Automated tests gate stable metrics; soak,
energy, thermal, and visual checks use reproducible documented protocols.

## 19. Observability

Stable event categories:

```text
capture.session.*
capture.chunk.*
capture.persistence.heartbeat
analysis.job.*
privacy.screening.*
provider.invocation.*
timeline.write.*
storage.cleanup.*
database.migration.*
application.lifecycle.*
```

Every background job log includes a non-sensitive correlation ID, stage, profile
and route revision, attempt, state transition, duration, and stable outcome code.
The heartbeat records time and status only, never pixels or window text. Metrics
remain local unless the user explicitly enables a future telemetry feature.

## 20. Testing Strategy

### Native

- The current native foundation is exercised by sixteen CTest executables:
  `pixel_buffer_tests`, `atomic_chunk_store_tests`, `capture_policy_tests`,
  `capture_event_queue_tests`, `capture_instance_controller_tests`,
  `capture_safety_core_tests`, `capture_worker_tests`, `chunk_manifest_tests`,
  `dxgi_output_resolver_tests`, `dxgi_desktop_frame_source_tests`,
  `windows_capture_target_observer_tests`,
  `windows_graphics_capture_frame_source_tests`, `wic_bgra_scaler_tests`,
  `jpeg_frame_chunk_writer_tests`, `capture_c_api_tests`, and the C17
  `c_header_compatibility_test`. These tests prove the current ABI, policy,
  queue, C header, pixel/runtime safety, bounded WIC JPEG encoding and
  deduplication, DXGI/WIC bounds, handle-bound transactional storage, typed
  manifests, and retryable compensation. The worker test adds deterministic per-stage
  invalidation, Pause/Resume/Stop, topology, event-linearization, and rollback
  coverage. The controller test adds run-ID, checkpoint, Stop single-flight,
  stale deferred-Stop rejection, atomic terminal-result sharing and exception
  takeover, saturated synthetic-Stop revoke, latest Pause-reason folding,
  terminal-capacity, and destructor-join coverage. These tests intentionally do
  not capture the user's live desktop or prove a live production C ABI worker
  run. The native build script explicitly
  selects an installed Visual Studio generator, filters ambient
  `CMAKE_GENERATOR*` overrides, and retains x64 multi-configuration output.
- Pixel-buffer and scaling correctness.
- Capture start, pause, resume, stop, and partial chunk behavior.
- Active-display switching and topology changes.
- Consent, application/window exclusion, lock, secure desktop, Remote Desktop,
  presentation-mode, sleep, resume, and session-switch capture boundaries.
- JPEG encoding, frame/chunk size limits, and consecutive-frame deduplication.
- Atomic file completion and recovery.
- Native event queue ordering and shutdown.

### Capture Interoperability

- Deterministic managed verifier tests cover stable double-read observation,
  HWND/owner/process/display races, PID reuse, target changes and display
  instability,
  `Absent` and `Unknown` gaps, process exit and API failures, unresolved
  `ApplicationFrameHost.exe`, field-scoped malformed identity, epoch exhaustion,
  verifier recreation, a gap observed by one overlapping verifier preventing
  another from reviving an old epoch, process-handle disposal, and
  value-redacting text representations.
- A Windows-only P/Invoke smoke test opens the current process with query and
  synchronize access, proves creation-time and liveness reads, verifies that
  only an executable basename escapes the process-image reader, and confirms
  that publisher identity remains `Unknown` until signer binding is implemented.
- Epoch-source tests must prove that verifier recreation cannot reuse a value,
  concurrent issuers remain strictly ordered, and exhaustion permanently denies
  every later issuance in that process.
- Deterministic title-reader tests cover the dedicated worker, the 100 ms
  deadline contract, queued expiry returning to `Idle`, in-flight expiry
  permanently entering `Poisoned`, process-lifetime `Unknown` after poison,
  late-result rejection, blocked `Completing`, fatal and recoverable failures,
  sequential single-worker reuse, one-request concurrency, private 32K-buffer
  clearing, self-disposal, bounded teardown, and verifier process-handle release
  after timeout.
- Deterministic monitor tests cover callback-time invalidation, generation
  advancement under event bursts, forced barrier ordering, stale observation
  rejection during sampling and publication, recoverable FailClosed sampling,
  owner-thread hidden-window/WTS/power/WinEvent registration and reverse cleanup,
  a real Windows registration smoke path, late callbacks, terminal faults,
  exact `0x800B..0x800C` registration, qualified location and topology
  invalidation, default foreground protection, continuous-mode foreground/object
  suppression with active-recording display pinning, mode-transition
  invalidation, independent session/power holds,
  storage-only refresh cadence, stable-decision no-op behavior, Block/Unknown
  change invalidation and recovery, refresh teardown cancellation, generation
  phase enforcement, callback pre/post-commit supersede, quiescence races, and
  redacted diagnostics.
- Live-activation tests must later cover hosted-app attribution, image-bound
  signer replacement, presentation integration, explicit Pause/Stop behavior,
  writer-side native display resolution and pre/post
  mapping checks, held-permit phase revalidation, and stale-work rejection at
  every native persistence boundary.

### Domain and Application

- Time ranges, merge rules, weighted metrics, and user-edit protection.
- Every allowed and rejected analysis-job state transition.
- Duplicate events, stale leases, retries, cancellation, and shutdown races.
- Strict analysis response validation and normalization.
- Daily and weekly deterministic aggregation.

### Infrastructure

- Fresh database creation and every schema migration, including schema-v7
  default-mode compatibility, the schema-v8 capture-interval allowlist,
  schema-v9 ordered evidence/window membership, and the destructive schema-v10
  canonical JPEG reset, schema-v11 foreground process telemetry, and schema-v12
  removable WinDayFlow exclusion seeding.
- ADR-0015 migration coverage for reusable provider profiles, independent stage
  bindings, no implicit privacy-stage enablement, screening provenance, and the
  provider-invocation ledger.
- Transaction rollback with no partial observations or timeline entries.
- Provider fixture mapping and strict category/productivity enum rejection.
- Per-stage provider disclosure and evidence-preview construction with no
  request before that route is enabled, plus payload-free invocation-audit
  completeness.
- Same-provider, different-provider, disabled-privacy, redacted, held, reviewed,
  and user-selected pass-through integration fixtures.
- Log-safety tests that reject secrets, raw window titles, and encoded evidence.
- DPAPI round-trip under the current user.
- Retention protection for non-completed work.
- QiDayflow import compatibility.

### Presentation and Integration

- ViewModel commands and observable projections.
- Expanded, Compact, Minimal, hamburger-overlay, tray, and title-bar behavior.
- System caption buttons, Snap Layouts, `Alt+Space`, resize, and drag hit testing.
- Keyboard, Narrator, high-contrast, text-scaling, reduced-motion, and mixed-DPI
  accessibility tests.
- Timeline virtualization with large datasets.
- End-to-end capture-to-timeline flow using deterministic provider fixtures.
- The implemented SQLite/native/fake-HTTP integration test covers manifest scan,
  provider save/test/enable, fingerprint and JPEG extraction, durable processing,
  atomic Timeline commit, restart idempotency, provider-revision stability, and
  user-edit preservation without duplicate network or extraction work.
- Coordinated application exit during capture and analysis.
- Performance regression checks and documented 24-hour and multi-day soak runs
  against the budgets established under Section 18.

## 21. Delivery Plan

Phases are release gates, not a claim of strict implementation order. As of
2026-07-30, schema v10, consent policy v2, manual and analyzed Timeline storage,
capture chunk/job/provider persistence, provider configuration and validation,
canonical JPEG validation, and the hosted analysis pipeline are implemented.
Sixteen native tests cover the C ABI, safety core, JPEG writer, atomic store,
and worker, while managed integration coverage proves
the deterministic capture-manifest-to-editable-Timeline path with a fake HTTP
provider and restart idempotency.

The x64 dev-live flavor also composes the native owner, privacy monitor, target
verifier, real DXGI-first/WGC-fallback JPEG worker, chunk notifier, and analysis
runner. It is protected by compile property, development-bundle property, and
exact launch-argument gates. Its foreground-protection and re-consented
continuous modes are now transitional implementation under ADR 0015 rather than
the production policy. Production remains disabled and advertises neither
`ScreenCapture` nor `CanonicalJpegChunks` until continuous capture uses only the
hard gate, status is heartbeat-consistent, provider routing is independent, and
the resulting lifecycle/request-boundary acceptance suite passes.

### Immediate P0: Runnable Capture-to-Analysis Vertical Slice

Until this slice meets its exit criterion, it takes precedence over new Daily,
Weekly, Journal, Chat, and unrelated visual-polish work. Provider expansion here
means the reusable profile and stage-routing boundary required by ADR 0015, not
adding vendor-specific product policy.

Current implementation status follows the end-to-end order:

1. **Record and publish: implemented behind dev-live gates; hard-gate refactor
   pending.** The
   native runtime owner, monitor, verifier, writer, and committed
   `chunks/<id>/manifest.json` plus `frames/*.jpg` path are composed. Production
   uses DXGI first and WGC only for explicit Desktop Duplication access denial.
   The atomic JPEG writer, black-frame rejection, deduplication, and pinned-
   display capability remain. Foreground identity, exclusions, provider state,
   and privacy inspection must be removed from persistence authorization.
2. **Project truthful capture status: refactor pending.** Native state and the
   last successful persistence heartbeat must produce Recording, Paused,
   Stopped, or NeedsAttention independently from privacy-processing state.
3. **Discover and enqueue: implemented.** Startup and every chunk-completed wake
   rescan committed manifests, strictly accept the two authorized foreground or
   continuous display scope values and idempotently upsert chunks. Enqueueing
   must next depend on the selected stage route rather than a global active
   provider. Wake events are never the source of truth.
4. **Configure processing routes: profile foundation implemented; routing
   pending.** Endpoint, model, timeout, DPAPI credentials, and synthetic tests
   exist. The next schema adds reusable profile lists, independent privacy and
   timeline bindings, user-selected privacy match/error policy, and revision-
   checked request admission without local/remote trust inference.
5. **Load bounded evidence: implemented.** The root-bound managed archive
   accepts only canonical chunk identifiers, selects at most 32 JPEG frames
   under a 12 MiB request budget, and verifies source plus per-frame hashes. No
   derived evidence directory or video decode is involved.
6. **Analyze and commit: timeline stage implemented; optional privacy stage
   pending.** The durable job
   state machine validates provider output, rechecks readiness/revision, and
   atomically commits normalized Timeline entries with completion. Retry,
   timeout, cancellation, lease recovery, stale responses, completed-version
   idempotency, and user-edit protection use persisted state.
7. **Expose truth in the UI: partial.** Timeline
   projects unprocessed chunks, job attempts/failures, and editable normalized
   results; provider configuration is available in Settings. A compact command-
   area indicator opens stable checking, running, waiting, recent-summary, and
   fault details. Pipeline-wide faults can wake the existing scheduler, while
   row-level retry outcomes distinguish stale UI state, unavailable evidence,
   and attempt exhaustion. Consecutive supervisor batches remain Running rather
   than flashing Idle between batches; the visible run summary accumulates
   batch results until the queue is drained, while data revisions still advance
   only for batches that persist changes. The narrow toolbar collapses commands
   to stable icon widths and constrains the status Flyout to available width.
   Settings navigation, provider lists and stage assignment, separate processing
   status, statistics, final recording-state, accessibility, and clean-profile
   retry smoke remain acceptance work.

P0 acceptance requires one clean-profile Windows x64 run to demonstrate all of
the following:

- with all provider stages off, recording creates a visible local unprocessed
  interval and performs no network request;
- after any compatible provider is saved, tested, disclosed, and bound to
  `TimelineAnalysis`, a newly recorded chunk reaches a normalized, editable
  timeline entry using bounded canonical JPEG evidence;
- privacy inspection can be disabled, assigned to the same profile as timeline
  analysis, or assigned to a different profile without changing capture;
- privacy match/error policies exercise audit, redaction, hold, review, and
  user-selected pass-through behavior, and a remote privacy fixture receives
  original evidence only after that exact route is enabled and disclosed;
- forced termination after chunk publication, extraction, provider response,
  and commit is recoverable without duplicate chunks, jobs, or timeline entries;
- lock, secure desktop, display/session loss, sleep, storage, consent revocation,
  fatal capture errors, and shutdown stop or pause persistence with an accurate
  visible reason;
- RDP, presentation, foreground changes, unresolved optional identity,
  exclusions, and provider/privacy-stage failures do not interrupt a healthy
  local archive; an enabled no-send rule prevents request creation instead;
- capture state agrees with native persistence heartbeat within two configured
  capture intervals and never reports privacy protection while frames persist;
- provider authentication, rate-limit, timeout, malformed-output, storage, and
  missing-evidence failures remain visible and retryable or terminal according
  to their stable disposition; and
- focused automated tests pass, followed by a development-bundle manual smoke
  when WinUI or real Desktop Duplication cannot be validated reliably in the
  automated environment.

Only after this acceptance passes may production advertise
`ScreenCapture | CanonicalJpegChunks`, register the native runtime owner, and
switch the controller from disabled to enabled activation in the same reviewed
change. Analysis remains managed code over the canonical archive.

### Phase 0: Foundation

- Create the solution and project boundaries.
- Establish build, formatting, test, packaging, and architecture checks.
- Record reference-project provenance and license obligations before source reuse.
- Maintain required third-party notices and a source-provenance manifest for
  every reused source or shipped third-party asset.
- Record ADRs for interop, SQLite access, packaging, capture/context cadence,
  evidence bounds, and the Windows App SDK servicing channel.
- Define the first-run recording consent, provider-route disclosure, optional
  privacy-stage policy, and resource/performance benchmark protocol.

Exit criterion: clean build and tests on a supported Windows CI runner, with
source provenance, privacy gates, and benchmark protocols reviewed and versioned.

### Phase 1: Capture Kernel

- Extract reviewed QiDayflow C++ code from the Flutter runner with preserved
  file-level provenance and required notices.
- Implement the versioned interop contract.
- Port the native regression suite.
- Build a minimal WinUI capture diagnostic surface with first-run consent,
  visible state, and one-action pause/resume.
- Enforce only lock, secure desktop, display/session loss, storage, user intent,
  consent, fatal runtime, and shutdown at the persistence boundary. Feed bounded
  application/window context into metadata and provider-request policy without
  interrupting capture.

Exit criterion: repeatable recording, pause/resume, display switch, stop,
extraction, no-send request policy, hard-gate transitions, and clean shutdown
without Flutter; capture cannot begin without recorded consent.

### Phase 2: Reliable Timeline Vertical Slice

- Extend the implemented schema-versioned manual timeline store with capture,
  job, observation, and activity repositories.
- Implement job claiming, leases, retry, recovery, and transactions.
- Build on the implemented manual create, edit, and delete path by adding the
  deterministic unprocessed-interval projection and promotion before requiring
  any provider response.
- Add reusable OpenAI-compatible profiles behind per-stage enablement, provider
  and endpoint disclosure, bounded evidence preview, and payload-free invocation
  audit. Keep privacy and timeline bindings independent.
- Extend the implemented WinUI manual timeline with observations, analyzed
  activities, and merge rules.

Exit criterion: with providers disabled, capture produces visible unprocessed
intervals that can be inspected and promoted manually; with a provider enabled,
capture-to-normalized-timeline works end to end, survives forced restart, and
makes no provider request before the user can verify what will leave the device.

### Phase 3: Review and Editing

- Build on the implemented date navigation, client-side search,
  category/productivity filters, and manual create, edit, and delete; add
  thumbnails, video playback, a scrubber, merge/split, scalable search, and
  Markdown export.

Exit criterion: users can audit and correct the complete generated record.

### Phase 4: Daily and Journal

- Add deterministic daily metrics, standup generation, and user journal notes.

Exit criterion: a daily report can be generated, edited, regenerated safely, and
exported.

### Phase 5: Weekly

- Add focus, application, category, distraction, rhythm, and weekly summaries.

Exit criterion: weekly numerical results are reproducible from timeline data.

### Phase 6: Chat and Providers

- Add controlled retrieval tools, cited chat, FTS5, streaming, and additional
  standard-API provider adapters without deployment-location trust policy.

Exit criterion: answers cite navigable timeline entries and cannot exceed the
configured data/query boundary.

### Phase 7: Product Hardening

- Complete startup, updates, retention UI, import, and MSIX distribution.
- Audit the already-delivered onboarding, tray, privacy, accessibility, DPI,
  performance, and recovery contracts; close measured gaps without deferring
  their foundational controls to this phase.

Exit criterion: a release candidate passes a documented clean-install, upgrade,
long-running capture, resource-budget, privacy, accessibility, crash-recovery,
and uninstall checklist on the supported Windows baseline.

## 22. Definition of Done for the Initial Product

- Native Fluent UI with no Flutter runtime dependency satisfies the Windows
  Experience Contract, including the continuous title bar, standard system
  commands, adaptive navigation with hamburger access, tray, theme, DPI,
  high-contrast, keyboard, Narrator, and reduced-motion behavior.
- C++ capture component passes ported native regression tests.
- Capture, analysis, and persistence recover after abnormal termination.
- The user can always tell whether capture is recording, paused, stopped, or
  needs attention, and can pause it with one direct action. Privacy and provider
  processing states are visible separately.
- First-run consent, the hard capture gate, no-send rules, optional privacy-stage
  policy, retention, and destructive-action checks pass.
- Before any provider stage is enabled, the user can verify its provider,
  endpoint, evidence, metadata, and configured failure behavior; completed
  requests are locally auditable without retaining their payloads.
- Timeline entries are inspectable and editable, and user corrections are
  protected from automatic overwrite.
- Daily standup works from the same canonical timeline data.
- Statistics reproduces its documented duration, frame, deduplication,
  invocation, and storage definitions from canonical data and fixture queries.
- With AI and the network unavailable, users can still capture, pause, review,
  inspect unprocessed intervals, create and edit manual entries, search, compute
  explicitly scoped deterministic metrics, and export their journal.
- Per-stage provider behavior and data disclosure are explicit and verifiable;
  the product does not assign trust from provider deployment location.
- Existing QiDayflow data can be imported or a documented migration limitation
  is approved before release.
- MSIX release and unpackaged development workflows are documented and tested.
- The minimum Windows 10 version 1809 baseline and supported Windows 11 releases
  pass documented clean-install, upgrade, shell-integration, and fallback tests.
- Numeric resource, performance, storage-growth, and long-running thresholds are
  established through the Section 18 protocol and all release gates pass.
- Privacy, retention, DPI, keyboard, accessibility, recovery, and shutdown checks
  pass with retained release evidence.
- Any reused reference source and every shipped third-party asset has recorded
  provenance, license compatibility, notices, and redistribution rights.
- Every release ships WinDayFlow's MIT `LICENSE`, `THIRD_PARTY_NOTICES.md`, the
  applicable provenance records, and the complete repository `licenses/` tree,
  consistent with every reused source and shipped dependency.
- Production packaging does not contain a development-only or preview runtime;
  specifically, `Microsoft.WindowsAppSDK.WinUI` 2.2.1 Engineering Preview must
  be replaced with a verified production-redistributable WinUI/Windows App SDK
  servicing release or covered by explicit permission before release.

## 23. Deferred Decisions

These decisions should be resolved through small prototypes or ADRs rather than
assumptions:

- Which additive fields and capabilities enter compatible C ABI v1 revisions,
  and which compatibility break would justify a v2 ABI and replacement ADR.
- The exact production-redistributable Windows App SDK and WinUI version and
  servicing channel within the fixed Windows 10 version 1809-or-later baseline;
  the current `Microsoft.WindowsAppSDK.WinUI` 2.2.1 Engineering Preview
  dependency is not a candidate for production release. This choice is between
  WinUI/Windows App SDK servicing releases, not UI technology stacks, and
  WinDayFlow remains MIT-licensed.
- MSIX-only distribution versus an additional unpackaged installer.
- Exact local provider process lifecycle and sandboxing.
- Whether embeddings materially improve retrieval beyond FTS5 and structured
  tools.
- Whether optional cloud synchronization is ever in product scope.

The native boundary itself is no longer deferred: Accepted ADR 0001 establishes
C ABI v1. Additive ABI evolution requires compatibility tests, while a breaking
change requires a replacement ADR. Selecting a redistributable WinUI/Windows
App SDK servicing release is an explicit production-release blocker, not a
reason to change the WinUI 3 architecture, and it does not prevent continued
development and local test packaging.

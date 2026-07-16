# WinDayFlow Architecture Design

Status: Architecture and product design baseline  
Project: WinDayFlow  
Repository: https://github.com/timeStarry/WinDayFlow  
Developer: timeStarry <timestarry@qq.com>  
Last updated: 2026-07-16

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
| Dayflow | Zero-input journal semantics; contextual timeline; Daily and Weekly review; timeline-grounded work-journal questions; export; provider choice; sensitive-application exclusion | Translate review and correction workflows into native Windows information architecture, offline-first storage, explicit disclosure, navigable citations, and capability-based providers | macOS visual conventions; mandatory account or hosted-backend assumptions; default telemetry; copied branding or product assets |
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

WinUI 3 and Windows App SDK are fixed architecture choices for WinDayFlow.
Third-party license review may constrain which concrete package version can be
distributed and how a build is packaged, but it does not reopen the native
Windows technology-stack decision. WinDayFlow-owned source remains MIT-licensed.

- Every distributable bundle must include the repository-root `LICENSE`,
  `THIRD_PARTY_NOTICES.md`, the applicable provenance records, and the complete
  repository `licenses/` tree. A restricted development bundle must also carry
  `DEV_BUNDLE_LOCAL_ONLY.txt`. Packaging checks must fail before replacing an
  artifact when any required file is absent.
- The current development bundle resolves the transitive
  `Microsoft.WindowsAppSDK.WinUI` 2.2.1 package. Its Engineering Preview terms
  limit the bundle to local development and testing and prohibit live use,
  sharing, publishing, and distribution. The directory and ZIP must remain on
  the build machine. External testing and production release are blocked until
  a production-redistributable WinUI version is selected and its terms are
  verified, or explicit permission is obtained.
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
- Keep recordings, evidence, timeline data, settings, and the database local by
  default, with behavior that users can inspect and verify.
- Make all background work recoverable, observable, and safe to retry.
- Support cloud and local AI providers through capability-based adapters.
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
- Opens a review surface with video playback, thumbnails, and a time scrubber.
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

### 5.6 Settings

- Recording, storage, retention, provider, model, privacy, startup, update, and
  diagnostic settings.
- The current foundation persists system/light/dark theme preference, the
  capture-enabled preference, the cloud-off default, and versioned recording
  consent before the main window is created.
- Provider selection is capability-aware: text support does not imply vision
  support.
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
Native capture       C++20, DXGI, Media Foundation, WIC
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
    `-- tests/                      Six native CTest executable targets

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
Runtime: App remains unavailable until the real writer, target verifier, and event monitor exist
```

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
updates versioned privacy context and target-scoped runtime authorization, and
polls bounded native events without callbacks. An explicit asynchronous owner
quiesces by applying Block, stopping, joining, and destroying in order;
`SafeHandle` remains a final fallback rather than the normal shutdown proof.
The native backend remains unregistered because the real Windows target
verifier, event-driven privacy monitor, and DXGI/WIC/Media Foundation writer are
not connected to that safety boundary. The native foundation does not reference
managed UI or domain assemblies. The App project may reference concrete
adapters for
dependency-injection registration; feature code consumes their inward-facing
contracts. The domain project must not reference WinUI, SQLite, HTTP, Windows
App SDK, or the native capture implementation.

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
- DXGI Desktop Duplication captures that display.
- WIC scales into a bounded BGRA canvas.
- Media Foundation encodes low-frame-rate H.264 MP4 chunks.
- Media Foundation and WIC extract and JPEG-encode bounded evidence frames.
- Complete MP4 data never crosses into managed memory.
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
Exclusion Rules," is also Accepted. It defines application-anchored identities,
bounded window-title operators, ordered first-match reporting, complete-snapshot
concurrency, and the atomic capture-off/privacy-revision transition. It does not
authorize live capture or live window enumeration.

[ADR 0003](adr/0003-native-capture-safety-core.md), "Native Capture Safety
Core," fixes the additive C ABI v1 runtime-authorization layout, target and
instance identity, native persistence generations, shared/unique write-permit
linearization, revoke and quiescence ordering, and the complete capability mask.
It deliberately leaves `ScreenCapture` disabled until a real writer and Windows
observers use those contracts end to end.

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
lifecycle contract. `ConsentGatedCaptureService` projects backend state while
giving unavailable and faulted technology states priority over consent state.
`AppSettingsService` runs commit-barrier Prepare before persistence, then
Committed after persistence and its in-memory snapshot update; Aborted never
restores a restrictive runtime latch. Start/Resume check that latch inside the
capture lifecycle gate. Runtime invalidation carries a separate monotonic
generation; once observed, the lifecycle service completes one sticky Stop
boundary even if authorization quickly recovers. Capture.Interop's tested
coordinator serializes native updates without holding the native gate across a
settings repository save, reconciles concurrent signals to the latest snapshot,
assigns a process-local `ulong` runtime policy generation, and never derives it
from the persisted privacy revision. Once a restrictive Prepare or signal drops
the process latch, caller cancellation cannot cancel the native block update.
The additive 112-byte runtime-authorization contract binds that decision to an
HWND/PID/process-creation-time target tuple and target epoch. The native-issued
permit adds an internal native-instance epoch and persistence generation. The
safety core validates immutable acquisition snapshots again under a shared
write permit, while Block or an effective revoke takes the unique side, drains
existing permits, and advances the generation. The legacy privacy-context
update can block but cannot mint a write permit. Legacy and target-scoped
revisions use independent ordering rules. The first valid legacy update also
revokes target authority and permanently prevents further target-scoped
authorization on that native handle; switching back requires handle
recreation, so the two revision namespaces cannot revive one another.

This safety core does not yet close Start/Resume admission. The current
Application service and runtime owner observe a Boolean authorization snapshot
and then call a tokenless backend method. An Allow A-to-B update between those
operations can admit work for a generation or target different from the one the
caller checked. For live activation, the same native instance must issue an
admission stamp bound to its owner; Start/Resume must carry the expected
persistence generation and target epoch, and the native/owner boundary must
atomically compare both with the current fully allowed authorization before a
worker can enter capture. A stale or foreign stamp fails closed, and every
effective Allow transition requires a new stamp. The Boolean remains useful for
UI state and early rejection but is not authority.

The current sticky automatic Stop is likewise a conservative foundation, not
the final dynamic-policy model. The event monitor and owner must explicitly
classify lock, application/window exclusion, and Unknown transitions as either
an evidence Pause that preserves a quiescent session or a sticky session Stop
that tears it down. Recovery, target changes, and repeated signals require
tests for both paths. This milestone does not implement that distinction.

The inactive `WindowsCapturePrivacyProbe` can synchronously sample documented
Windows 10 1809+ signals for session unlock, input desktop, RDP/remote control,
Windows Presentation Mode, and storage headroom. API failure or ambiguity is
isolated per signal and becomes Unknown while later signals continue sampling;
application/window identity remains Unknown and no window title is read. A pure
typed matcher now evaluates persisted application and window rule scopes
independently and returns only a matched rule ID. Each observed identity and
title is `Unknown`, known `Absent`, or `Present`; Unknown and malformed present
identities fail closed when a rule requires them, while Absent is a conclusive
non-match. A real Windows target verifier, live identity acquisition,
event-driven signal monitoring, real-writer permit integration, and App
registration remain pending. Phase 1 activates the implemented native backend
only after those platform inputs and the complete screen-capture capability
mask are present, then adds evidence-extraction
interfaces under Capture.Interop. Capture options come from validated
application settings; extraction is not added as an unrelated method on the
lifecycle service. The adapter maps native events to `CaptureStatus` and
`CaptureStatusChangedEventArgs`.

Interop remains coarse-grained. There are no per-frame managed callbacks.
Native events are queued and marshalled onto the appropriate managed
dispatcher.

### 9.1 Current Native Foundation and Safety-Core Slice

The repository now contains an independently buildable x64 C++20 DLL and C ABI
v1 foundation under `WinDayFlow.Capture.Native`. Its implemented boundary has:

- fixed-width numeric enums, opaque handles, and C-compatible POD structures
  whose first fields are `struct_size` and `abi_version`, with caller-owned UTF-8
  buffers and catch-all `noexcept` exports;
- the additive 112-byte flat runtime-authorization input defined by ADR 0003,
  containing the monotonic runtime policy revision, target epoch, numeric
  HWND/PID/process-creation-time tuple, target flags, and eight policy decisions;
- a native-issued permit token that adds native-instance and persistence
  generations, plus shared/unique admission linearization that prevents legacy
  privacy Allow updates from minting persistence authority;
- validated capture-policy inputs and a bounded, polled event queue with
  monotonic sequence numbers and `dropped_before` gap reporting, without native
  callbacks into managed or UI code; and
- explicit nonblocking stop, bounded wait-for-join, and one blocking destroy for
  each valid handle, coordinated by a single-flight managed owner that applies
  Block/revoke before stop, join, and exactly-once destroy.

This is a contract and synthetic safety foundation, not a usable recorder. The
native and managed tests prove target/PID reuse rejection, target and instance
epochs, persistence-generation invalidation, permit linearization, quiescence,
timeout/failure quarantine, ABI layout, and capability dependencies. They do
not prove a real capture write path.

The complete live mask requires privacy guard, event queue, target-scoped
authorization, persistence-generation barrier, deterministic stop, screen
capture, and H.264 chunk capabilities. Evidence extraction is independent.
`ScreenCapture` remains deliberately disabled, so the live mask is incomplete.
Start/Resume remain unavailable, the App continues to use
`UnavailableCaptureBackend`, and the shell recording control remains disabled.

The remaining activation gates include one command-admission contract as well
as integration work. Start/Resume must atomically validate an issuer-bound
expected persistence generation and target epoch. A real Windows target
verifier must supply and revalidate the target tuple and epoch; an event-driven
monitor must publish every supported privacy transition and select evidence
Pause versus sticky session Stop; and the real DXGI/WIC/Media Foundation pixel
and metadata writer must carry the native permit through encode, temporary
output, final rename, and committed-event publication. Atomic filesystem
interruption, cleanup, disk-full, recovery, and Windows lifecycle tests must
then prove that end-to-end path. Until those gates pass, no live frame or
context metadata can be persisted and App DI must continue to register
`UnavailableCaptureBackend`.

The safety-core implementation and ADR are original WinDayFlow work. They do
not modify any of the six QiDayflow-derived files or require a provenance
manifest/hash update. A later adaptation of QiDayflow `capture_service.*` or a
change to an existing derived file still follows the provenance workflow before
commit or distribution.

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
- Files are written as partial artifacts and atomically renamed on completion.
- Video and metadata completion are coordinated.
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

Rules:

- Only one worker may claim a job at a time.
- Claiming uses a transaction and a lease timestamp.
- Expired active leases are recovered after abnormal process termination.
- Attempts are bounded and persisted.
- Retry decisions use stable error codes, not display text.
- Observation and activity outputs are schema-validated.
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

    Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatDelta> ChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken);
}
```

Capabilities include:

```text
VisionAnalysis | TextGeneration | Streaming | ToolCalling | LocalExecution
```

Planned adapters:

1. OpenAI-compatible HTTP.
2. Gemini.
3. Ollama and LM Studio.
4. Codex CLI and Claude Code CLI where their stable interfaces permit it.

All provider-specific payloads and labels are normalized in Infrastructure.
Provider responses cannot write domain state directly. Structured analysis uses
strict DTO and semantic validation before entering the application workflow.

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

The implemented persistence slice uses schema version 4. Version 1 contains
`schema_migrations`, `timeline_entries`, `timeline_entry_apps`, and
`timeline_entry_tags`; version 2 adds the singleton `app_settings` row while
preserving existing timeline data. Version 3 adds evidence-retention,
sensitive-application exclusion, remote-session, screen-sharing, and privacy-
revision fields. It retains only the version and acceptance time of version 1
consent as stale metadata, not its covered revision or a complete snapshot of
the old privacy choices. It forces capture off because that consent did not
cover the new choices. Version 4 adds an initially empty, ordered
`capture_exclusion_rules` child table without changing capture state or privacy
revision during migration. The application completes the idempotent migrations
and initializes settings before creating the main window. Every timeline write
plus its ordered child rows commits in one SQLite transaction. Settings writes
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
current privacy revision, and typed ordered application/window rules with
database constraints. Capture evidence,
unprocessed intervals, analysis jobs, generated projections, and the remaining
table groups in this section are still pending.

## 15. Privacy and Security

The following are mandatory release requirements. The current build implements
the persistent, versioned recording-consent gate and defaults capture and cloud
analysis to off. Native capture and cloud providers remain unavailable. Schema
version 4 stores manual timeline content, settings, and user-authored exclusion
rules locally without
application-level database encryption.

- Recording is opt-in. Before the first capture, onboarding explains the data
  collected, excluded data classes, local storage location, retention policy,
  whether any configured provider is local or cloud, and how to pause or exit.
  Capture cannot start until this consent and the initial privacy choices are
  persisted. A later release that expands collection scope requires renewed
  consent.
- The current service enforces this gate for Start/Resume using the persisted
  capture-enabled choice and consent policy version 2; consent records the exact
  privacy revision it covered, and disabling capture, revoking consent, or any
  privacy change triggers an automatic stop. Privacy changes disable capture
  until renewed consent is persisted. Pause/Stop remain available regardless of
  consent. Settings persist a 30-day conservative
  default plus user-selectable retention, sensitive-application exclusion,
  remote-session pause, screen-sharing pause choices, and typed ordered
  application/window rule lists. Full first-run onboarding and live
  application/window identity monitoring remain Phase 1 work.
- The current `pause_during_screen_sharing` storage name is retained for schema
  compatibility, but the Windows UI describes only Windows Presentation Mode.
  Windows has no public, universal signal for arbitrary third-party screen
  sharing; unsupported sharing contexts remain unknown and fail closed rather
  than being reported as positively detected.
- Consent history is not considered fully audit-ready until immutable snapshots
  record the concrete disclosure and privacy values accepted at each revision.
  The current stale metadata is sufficient only to reject superseded consent.
- Capture state is always available from the window and tray. Pause is a
  one-action command, and the resulting state is distinguishable from stopped,
  excluded, failed, locked, and storage-constrained states.
- Users can exclude applications and windows. Rules support stable process or
  publisher identity where available and bounded window-title matching where
  necessary. The UI previews which rule matched without writing raw sensitive
  titles to normal logs.
- A documented sensitive-application policy provides conservative defaults for
  authentication, password, secret, financial, health, and private-browsing
  contexts that Windows can identify reliably. Because classification cannot be
  perfect, users can inspect and extend the rules; no UI may claim universal
  sensitive-content detection.
- Lock and secure-desktop transitions pause evidence capture. Remote Desktop
  and presentation or screen-sharing contexts use explicit policies with a
  conservative pause default and visible override state. Sleep, session switch,
  and resume transitions are auditable and never silently backfill evidence.
- An unknown privacy input uses the generic policy-blocked reason. Specific
  reasons such as session locked, remote session, or excluded application are
  shown only after that condition is positively observed.
- API secrets are protected for the current Windows user using DPAPI.
- Production logs and startup diagnostics must reject or redact secrets,
  authorization headers, base64/JPEG data, and raw window titles by
  construction.
- INFO logging is event-oriented and never per-frame.
- Diagnostic mode uses an explicit metadata allowlist.
- Before cloud analysis is enabled, the UI names the provider and endpoint,
  shows the exact classes of evidence and metadata that may leave the device,
  and provides a preview of the bounded images and context for a representative
  request. A per-job preview remains available before retrying or manually
  sending sensitive evidence.
- Cloud requests produce a local, payload-free audit record containing provider
  profile, endpoint origin, evidence references, byte and item counts, time,
  outcome, and correlation ID. Users can inspect and clear this audit according
  to a documented retention policy.
- Local-provider mode keeps analysis on the machine, subject to the selected
  provider's own behavior.
- Retention never deletes evidence for pending, active, or failed jobs.
- Storage cleanup is quota-based, deterministic, auditable, and interruptible.
- Destructive operations list the affected date range and data classes.
- Privacy enforcement occurs before capture or request construction, not only
  as a presentation filter after evidence has been stored.

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
analysis.job.*
provider.request.*
timeline.write.*
storage.cleanup.*
database.migration.*
application.lifecycle.*
```

Every background job log includes a non-sensitive correlation ID, attempt,
state transition, duration, and stable outcome code. Metrics remain local unless
the user explicitly enables a future telemetry feature.

## 20. Testing Strategy

### Native

- The current native foundation is exercised by six CTest executables:
  `pixel_buffer_tests`, `capture_policy_tests`, `capture_event_queue_tests`,
  `capture_safety_core_tests`, `capture_c_api_tests`, and the C17
  `c_header_compatibility_test`. These tests prove the current ABI, policy,
  queue, C header, pixel/runtime foundation, and synthetic safety-core
  authorization and quiescence contracts, not a live DXGI-to-artifact write
  chain. The native build script explicitly
  selects an installed Visual Studio generator, filters ambient
  `CMAKE_GENERATOR*` overrides, and retains x64 multi-configuration output.
- Pixel-buffer and scaling correctness.
- Capture start, pause, resume, stop, and partial chunk behavior.
- Active-display switching and topology changes.
- Consent, application/window exclusion, lock, secure desktop, Remote Desktop,
  presentation-mode, sleep, resume, and session-switch capture boundaries.
- Media Foundation encoding and frame extraction bounds.
- Atomic file completion and recovery.
- Native event queue ordering and shutdown.

### Domain and Application

- Time ranges, merge rules, weighted metrics, and user-edit protection.
- Every allowed and rejected analysis-job state transition.
- Duplicate events, stale leases, retries, cancellation, and shutdown races.
- Strict analysis response validation and normalization.
- Daily and weekly deterministic aggregation.

### Infrastructure

- Fresh database creation and every schema migration.
- Transaction rollback with no partial observations or timeline entries.
- Provider fixture mapping and unknown-label fallback.
- Provider disclosure and upload-preview construction with no network request
  before consent, plus payload-free network-audit completeness.
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
- Coordinated application exit during capture and analysis.
- Performance regression checks and documented 24-hour and multi-day soak runs
  against the budgets established under Section 18.

## 21. Delivery Plan

Phases are release gates, not a claim of strict implementation order. As of
2026-07-16, the no-capture manual-timeline portions of Phases 2 and 3 plus
schema v4, consent policy v2, persistent retention/exclusion/session choices,
and user-authored typed exclusion rules are implemented. Phase 1 also has
Accepted ADRs 0001 through 0003, verified QiDayflow source provenance, the x64
C++20 C ABI v1 foundation, six native tests, the target-scoped safety core, the
managed asynchronous owner/quiescence contract, the inactive runtime privacy
coordinator, the pure exclusion matcher, and the on-demand Windows privacy
probe. The safety core covers synthetic target reuse, generation,
acquire-to-persist permit, and stop/join/destroy races. The real Windows target
verifier, event-driven privacy monitor, DXGI/WIC/Media Foundation writer, and
atomic artifact publisher remain open activation gates. Issuer-bound,
generation/target-stamped Start/Resume admission and the evidence-Pause versus
sticky-Stop dynamic policy are also unresolved. `ScreenCapture` and
managed-adapter runtime activation remain disabled, so no phase exit criterion
is met.

### Phase 0: Foundation

- Create the solution and project boundaries.
- Establish build, formatting, test, packaging, and architecture checks.
- Record reference-project provenance and license obligations before source reuse.
- Maintain required third-party notices and a source-provenance manifest for
  every reused source or shipped third-party asset.
- Record ADRs for interop, SQLite access, packaging, capture/context cadence,
  evidence bounds, and the Windows App SDK servicing channel.
- Define the first-run consent contract, sensitive-context policy, cloud
  disclosure contract, and resource/performance benchmark protocol.

Exit criterion: clean build and tests on a supported Windows CI runner, with
source provenance, privacy gates, and benchmark protocols reviewed and versioned.

### Phase 1: Capture Kernel

- Extract reviewed QiDayflow C++ code from the Flutter runner with preserved
  file-level provenance and required notices.
- Implement the versioned interop contract.
- Port the native regression suite.
- Build a minimal WinUI capture diagnostic surface with first-run consent,
  visible state, and one-action pause/resume.
- Enforce application/window exclusions, sensitive-context rules, and lock,
  secure-desktop, Remote Desktop, presentation, sleep, and session transitions
  before evidence is persisted.

Exit criterion: repeatable recording, pause/resume, display switch, stop,
extraction, exclusion, privacy-state transitions, and clean shutdown without
Flutter; capture cannot begin without recorded consent.

### Phase 2: Reliable Timeline Vertical Slice

- Extend the implemented schema-versioned manual timeline store with capture,
  job, observation, and activity repositories.
- Implement job claiming, leases, retry, recovery, and transactions.
- Build on the implemented manual create, edit, and delete path by adding the
  deterministic unprocessed-interval projection and promotion before requiring
  any provider response.
- Add the OpenAI-compatible provider behind explicit enablement, provider and
  endpoint disclosure, bounded evidence preview, and payload-free request audit.
- Extend the implemented WinUI manual timeline with observations, analyzed
  activities, and merge rules.

Exit criterion: with providers disabled, capture produces visible unprocessed
intervals that can be inspected and promoted manually; with a provider enabled,
capture-to-normalized-timeline works end to end, survives forced restart, and
makes no cloud request before the user can verify what will leave the device.

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

- Add controlled retrieval tools, cited chat, FTS5, streaming, and local-provider
  adapters.

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
- The user can always tell whether capture is active, paused, excluded, failed,
  or blocked, and can pause it with one direct action.
- First-run consent, application/window exclusions, sensitive-context rules,
  lock/RDP/presentation policies, retention, and destructive-action checks pass.
- Before cloud analysis, the user can verify the provider, endpoint, evidence,
  and metadata that may leave the device; completed requests are locally
  auditable without retaining their payloads.
- Timeline entries are inspectable and editable, and user corrections are
  protected from automatic overwrite.
- Daily standup works from the same canonical timeline data.
- With AI and the network unavailable, users can still capture, pause, review,
  inspect unprocessed intervals, create and edit manual entries, search, compute
  explicitly scoped deterministic metrics, and export their journal.
- Cloud/local provider behavior and data disclosure are explicit and verifiable.
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
  be replaced with a verified production-redistributable version or covered by
  explicit permission before release.

## 23. Deferred Decisions

These decisions should be resolved through small prototypes or ADRs rather than
assumption:

- Which additive fields and capabilities enter compatible C ABI v1 revisions,
  and which compatibility break would justify a v2 ABI and replacement ADR.
- The exact production-redistributable Windows App SDK and WinUI version and
  servicing channel within the fixed Windows 10 version 1809-or-later baseline;
  the current WinUI 2.2.1 Engineering Preview dependency is not a candidate for
  production release. This choice is between WinUI servicing releases, not UI
  technology stacks.
- MSIX-only distribution versus an additional unpackaged installer.
- Exact local provider process lifecycle and sandboxing.
- Whether embeddings materially improve retrieval beyond FTS5 and structured
  tools.
- Whether optional cloud synchronization is ever in product scope.

The native boundary itself is no longer deferred: Accepted ADR 0001 establishes
C ABI v1. Additive ABI evolution requires compatibility tests, while a breaking
change requires a replacement ADR. Selecting a redistributable WinUI servicing
release is an explicit production-release blocker, not a reason to change the
WinUI 3 architecture, and it does not prevent continued development and local
test packaging.

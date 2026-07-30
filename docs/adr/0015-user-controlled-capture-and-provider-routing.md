# ADR 0015: User-Controlled Capture, Privacy Processing, and Provider Routing

- Status: Accepted
- Date: 2026-07-30
- Decision owners: WinDayFlow maintainers
- Scope: capture policy, privacy processing, provider profiles, analysis routing,
  settings information architecture, status projection, persistence, and audit
- Supersedes: the capture-blocking product semantics in
  [ADR 0002](0002-capture-exclusion-rules.md) and the two-mode product policy in
  [ADR 0013](0013-display-wide-continuous-capture.md)

## Context

WinDayFlow currently composes recording consent, Windows lifecycle state,
foreground identity, built-in sensitive-application policy, user exclusion
rules, remote-session state, presentation state, display identity, and storage
availability into one fail-closed runtime authorization. Managed authorization,
native worker state, projected UI state, and actual frame persistence can then
temporarily disagree. In practice the UI can report privacy protection while
the native worker continues to publish frames.

That coupling is disproportionate for a personal, local work-journal product.
Application and window classification are useful when deciding what may be sent
to a provider, but they do not need to interrupt the local screenshot archive.
They also must not force one product-selected trust model. A user may choose no
provider, one provider for every processing stage, or different providers for
privacy inspection and timeline generation. Whether an endpoint is operated on
the same machine or elsewhere does not let WinDayFlow decide whether the user
trusts it.

## Decision

WinDayFlow separates capture safety, local evidence, processing policy, provider
configuration, and provider routing. These are independently persisted and
independently observable.

```text
user capture intent
-> hard capture gate
-> canonical JPEG archive
-> user-configured processing route
   -> optional privacy inspection stage
   -> optional redaction or review policy
   -> optional timeline-analysis stage
-> validated 45-minute timeline rewrite
```

### User Authority Invariants

- WinDayFlow does not label a provider as trusted or untrusted and does not infer
  trust from its name, endpoint, loopback status, model family, or deployment
  location.
- Provider profiles describe how to call a standard API. Stage bindings describe
  where a profile is used. User policy describes whether that stage is enabled
  and what happens on a result or failure. These concerns do not share one
  switch or one active-provider singleton.
- Privacy inspection is optional. It is not a prerequisite for timeline
  analysis unless the user explicitly configures it as one.
- A privacy-inspection provider may use any configured endpoint. If that endpoint
  is remote, the original evidence necessarily reaches it; WinDayFlow discloses
  that fact but does not prohibit the choice.
- The same profile may serve privacy inspection, timeline analysis, summaries,
  or chat. Different profiles may serve each stage.
- The user may allow original evidence to pass directly to a selected analysis
  provider, require a privacy stage, require redaction, hold inconclusive items,
  request manual review, or allow pass-through after a privacy-stage failure.
- An enabled user exclusion rule is an explicit no-send decision. Seeded rules,
  including the WinDayFlow rule, use the same model and remain removable.
- Changing provider routes or privacy-processing policy never revokes recording
  consent, stops capture, or changes the capture status.

The application may ship conservative initial values, but every non-technical
processing restriction remains visible and changeable. A default is not a
permanent product decision on the user's behalf.

### Hard Capture Gate

The capture gate contains only conditions that make capture unauthorized,
impossible, or unsafe to persist:

- explicit Stop, Pause, or revoked recording consent;
- Windows lock screen or secure desktop;
- suspend, session loss, or unavailable capture display;
- storage below the supported headroom;
- capture-access loss or a fatal native/runtime failure; and
- application shutdown.

Ordinary foreground changes, application/window exclusions, provider state,
privacy-inspection state, Remote Desktop policy, and presentation policy do not
block local capture in the target design. Process and window observations may
still be collected as bounded metadata or routing inputs. Unknown optional
metadata means "metadata unavailable," not "recording unauthorized."

The current native display-scoped authorization, generation checks, atomic JPEG
writer, and lifecycle invalidation remain useful implementation foundations.
Foreground identity ceases to be a persistence permit. One run remains bound to
one explicitly selected display until a separately reviewed multi-display
design exists.

### Capture and Processing Status

The primary capture status is derived from native runtime state plus a last-
successful-persistence heartbeat. It uses only:

```text
Recording | Paused | Stopped | NeedsAttention
```

Lock, storage, display, and fatal-error details may explain a non-recording
state. "Privacy protected" is not a capture state. Processing has a separate
projection:

```text
NotRouted | WaitingForPrivacy | ReadyForAnalysis | Analyzing
Redacted | HeldByRule | NeedsReview | Failed | Completed
```

A foreground-observation or privacy-stage failure cannot make the capture UI
claim that recording stopped when frames continue to be committed.

### Provider Profiles and Stage Bindings

A provider profile stores transport and model configuration only:

```text
id, display_name, adapter_kind, endpoint, model, timeout,
protected_credentials, revision, validation_state, capabilities
```

Credentials remain protected for the current Windows user. Endpoint origin is
displayed factually before a stage is enabled; it is not converted into a trust
score. Adapter validation covers protocol compatibility, bounded payloads,
transport security, response size, and structured-output support.

Routing is a separate collection:

```text
stage, provider_profile_id, enabled, route_revision, stage_options
```

Initial stable stages are `PrivacyInspection` and `TimelineAnalysis`. Later
stages may include `DailySummary`, `WeeklySummary`, and `Chat`. One provider per
stage is sufficient for the first implementation, but the schema must not
restore a global active-provider singleton as the routing model.

The privacy stage uses the same provider-adapter boundary as other stages and
returns a normalized, versioned result. A provider-specific response cannot
directly delete evidence, edit the database, or authorize another provider
request. Redaction is performed by WinDayFlow from normalized findings and
produces a separate derivative; original evidence is never overwritten.

Privacy-stage policy is independently configurable:

```text
on_match: AuditOnly | RedactAndContinue | Hold | RequireReview
on_error: Hold | PassThrough | RequireReview
```

Disabling the stage routes original bounded evidence directly to the next
enabled stage. Enabling it does not imply that its provider is local, remote, or
more trusted than the next provider.

### Request Boundary and Audit

Every provider request is checked immediately before HTTP or process creation:

- the stage and its provider binding are still enabled;
- the profile revision and credentials are current;
- user exclusion rules do not block the evidence;
- any configured prior-stage requirement has a matching current result; and
- the bounded request references only approved original or derivative frames.

The audit record is payload-free and records stage, profile and route revisions,
endpoint origin, evidence/derivative references, item and byte counts, start and
completion time, outcome, correlation ID, and available token/usage metadata.
It records what happened without claiming that a provider was trustworthy.

### Settings Experience

Settings uses navigable pages instead of one flattened form:

```text
Settings
|-- Recording
|-- Storage
|-- Privacy and processing
|-- Providers
|-- Appearance
`-- About
```

`Providers` lists saved profiles and opens a profile editor. `Privacy and
processing` owns stage enablement, provider assignment, exclusion rules, and
failure policy. Provider creation never silently assigns a stage, and assigning
one stage never changes another.

The storage page exposes a read-only path plus an Open Folder action. Exclusion
rows use one enable switch, one primary edit action, and an overflow menu for
ordering and deletion. All network-facing choices show the selected endpoint
and evidence class at the decision point.

### Persistence and Migration

The target persistence additions are:

- `analysis_stage_bindings` for independently enabled provider routes;
- `privacy_screenings` for normalized status, detector revision, policy result,
  and derivative-manifest reference;
- `provider_invocations` for accurate request counts, duration, result, usage,
  and payload-free audit data; and
- optional `privacy_findings` only if typed frame-region findings cannot remain
  bounded inside the screening artifact.

The existing `ai_provider_profiles` rows and DPAPI credentials are retained.
The current active profile becomes the initial `TimelineAnalysis` binding only
when provider analysis was already explicitly enabled. No privacy binding is
created by migration. Existing capture exclusion rules migrate to disabled or
enabled no-send rules with the same user-visible state; they no longer mutate
capture consent or capture-enabled state.

The legacy `capture_application_privacy_mode` is removed in the development
schema reset or ignored until removal. Existing native foreground/display scope
metadata remains readable for evidence provenance but does not select a privacy
policy.

## Consequences

- Local capture remains continuous across ordinary application changes and is
  insulated from provider and privacy-classifier failures.
- Users, not WinDayFlow, choose the trust boundary and provider topology.
- A remote privacy provider can see original evidence when selected; disclosure
  and audit make this explicit, but the product does not forbid it.
- Provider management requires list, create, update, validate, delete, and
  per-stage binding operations instead of only `GetActive`/`SaveActive`.
- Privacy inspection, redaction, and timeline analysis become durable async
  stages with independent retries and visible states.
- The runtime privacy coordinator can be reduced substantially, while its
  diagnostic logging remains useful during migration and acceptance testing.
- Lock screen, secure desktop, storage, display, native safety, request bounds,
  credential protection, schema validation, and user-authored no-send rules
  remain enforced technical boundaries rather than provider-trust judgments.

## Verification

- Foreground switching, disabling a seeded exclusion, closing the main window,
  and privacy/provider failures do not interrupt frame persistence.
- Lock, secure desktop, storage exhaustion, display loss, Stop, and shutdown do
  interrupt or terminate capture with an accurate visible reason.
- Capture status agrees with the persistence heartbeat within two configured
  capture intervals.
- Every combination of disabled privacy stage, same-provider routing,
  different-provider routing, and user-selected privacy failure behavior has an
  integration test with fake standard-API endpoints.
- An enabled no-send rule prevents request creation without stopping capture.
- A remote privacy-stage fixture receives original evidence only after its exact
  route is enabled and disclosed.
- A sanitized derivative is distinct from, and cannot overwrite, its source.
- Route or profile revision changes reject stale stage results and requests.
- Provider invocation counts come from the invocation ledger, not inferred job
  attempts.

## Superseded Semantics

ADR 0002 remains the historical source for bounded typed identity matching,
stable rule IDs, ordering, normalization, and safe logging. Its requirements to
disable capture, advance recording-consent privacy revision, or fail closed at
the persistence boundary are superseded.

ADR 0013 remains the historical source for the compatible display-wide native
authorization and pinned-display implementation. Its two user-facing capture
modes, recommended foreground-protection default, dormant-rule behavior, and
mode-change consent churn are superseded.

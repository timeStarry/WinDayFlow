# ADR 0002: Typed, Ordered Capture Exclusion Rules

Status: Accepted
Date: 2026-07-16

## Context

WinDayFlow must let users exclude an application or a bounded set of that
application's windows without persisting observed window titles, full executable
paths, or an ambiguous free-form expression language. An effective exclusion
change also changes the privacy policy covered by recording consent. The rule
snapshot, disabled capture state, and privacy revision therefore cannot be
stored by independent best-effort writes.

## Decision

Capture exclusion rules are immutable, user-named records in one ordered rule
set owned by `CapturePrivacySettings`. Each rule has a stable GUID, enabled
state, per-rule revision, scope, typed application identity, and optional window
title matcher.

Supported application identities are:

- executable file name, without a directory;
- Windows package family name; and
- publisher certificate SHA-256.

An application rule compares one typed identity exactly with
`OrdinalIgnoreCase`. A window rule must include the same typed application
anchor and then applies one bounded `Exact`, `StartsWith`, or `Contains` title
comparison. Regular expressions, glob syntax, unanchored global title rules,
and persisted executable paths are not supported. Names, identities, and title
patterns reject control characters. Identity values are normalized by type;
title patterns preserve their exact leading and trailing whitespace so storage
does not silently change a match boundary.

Rule order is stable. Within each scope, enabled-rule order determines the first
rule reported for an exclusion. Application and window scopes are evaluated
independently, so moving one scope across the other without changing either
scope's relative order is not an effective policy change. Stable rule GUIDs are
part of the effective ordered policy because runtime evaluation returns only the
matched GUID. Observed application context and window titles stay process-local
and must not cross the native ABI, enter a normal log, or be stored as
rule-match evidence.

Runtime observations use three states: `Unknown`, known `Absent`, and `Present`.
Unknown, including a malformed value supplied as Present, fails closed when an
enabled rule requires that field. Absent is a conclusive non-match and permits
evaluation to continue; this prevents a PFN or certificate rule from blocking
every unpackaged or unsigned application.

Schema version 4 stores the ordered rules in a child table of the singleton
settings row. Repository writes use an expected and proposed complete settings
snapshot under `BEGIN IMMEDIATE`. An effective ordered-rule change must be
committed in the same SQLite transaction as `capture_enabled = 0` and exactly
one privacy-revision increment. The transaction reads the complete snapshot
back before commit. A stale expected snapshot or any partial write fails the
whole transaction. New rules start at revision 1. An edited or explicitly moved
rule advances its per-rule revision exactly once; rollback, skipped revisions,
and silent replacement of an enabled rule's stable GUID are rejected.

Disabled drafts may be added, edited, removed, or reordered without changing
the effective capture boundary. Enabling a rule, disabling or deleting an
enabled rule, changing an enabled match boundary, or reordering enabled rules is
an effective privacy change when it changes that scope's ordered chain. Renaming
a rule changes its local presentation but not its match boundary.

The Settings UI manages the complete ordered collection while capture remains
unavailable. It must state that saving a rule does not enable recording and
must not enumerate or preview live windows.

## Consequences

- A future foreground-context monitor has a deterministic, typed matcher input
  and can fail closed when a required identity or title is unknown.
- User rules remain enforceable even when optional built-in sensitive-context
  classification is disabled.
- Reordering enabled rules within one scope invalidates consent because it
  changes the auditable first-match result, even when both orders would block
  capture. Cross-scope interleaving alone does not.
- Adding a new identity kind or title operator requires a schema/domain review,
  matcher tests, UI disclosure, and compatibility handling.
- This ADR does not authorize live capture. Target-scoped native authorization,
  persistence-generation barriers, event-driven monitoring, and native runtime
  ownership remain separate activation gates.

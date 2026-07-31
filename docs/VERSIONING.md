# Versioning

WinDayFlow uses Semantic Versioning with a prerelease suffix while the capture,
privacy, analysis, and release workflows are still being validated.

The single version source is `Directory.Build.props`:

- `VersionPrefix` changes only when the supported product contract changes.
- `VersionSuffix` advances for each distributable beta package.
- Fixes, UI polish, diagnostics, and internal refactors normally increment only
  the beta number.
- A minor version is reserved for a coherent new user-facing capability or a
  material data/runtime contract change.
- Version `1.0.0` is reserved for a stable release with supported migration,
  upgrade, rollback, and packaging behavior.

The current line is `0.2.0-beta.2`. It follows the first end-to-end capture and
analysis implementation and the second schema/runtime architecture cycle, while
remaining intentionally below a stable release.

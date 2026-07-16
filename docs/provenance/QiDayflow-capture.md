# QiDayflow Native Capture Provenance

## Purpose

This record defines the only reviewed QiDayflow source baseline from which the
first WinDayFlow native capture component may be derived. It records immutable
upstream hashes, planned local treatment, license obligations, and the rules
for maintaining local derived-file hashes.

This document is a provenance record, not permission to copy additional files
from the reference checkout. Any file not listed as "Derive" requires a new
review and an update to this record before source is copied or adapted.

## Pinned Source

| Field | Value |
| --- | --- |
| Project | QiDayflow |
| Repository | `https://github.com/liujiaqi7998/QiDayflow.git` |
| Branch at review | `master` |
| Commit | `8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e` |
| Version at review | `0.1.4` |
| Review date | `2026-07-16` |
| License | MIT |
| Copyright notice | `Copyright (c) 2026 Qi Day Flow contributors` |
| Upstream license Git blob SHA-256 | `8534461B0B8263F5145B229F0C1BA4F4B5BF8A535278C2B3124F289AD10926CB` |

All SHA-256 values below were calculated over the exact Git blob bytes at the
pinned commit, before any checkout line-ending conversion. They are immutable
evidence and must never be replaced with a hash from a later upstream revision.

## Planned Derived Production Files

| Upstream file | Original SHA-256 | Planned local treatment | Required modification summary |
| --- | --- | --- | --- |
| `windows/runner/capture_pixel_buffer.h` | `4EDE4160ACACEAE291B62CB347A20DC3891356F6A6E487CCE98FD6EB9165199C` | Derive as an internal pixel-buffer header | Rename namespace and include guard; keep C++ types internal to the DLL. |
| `windows/runner/capture_pixel_buffer.cpp` | `8955ADA13EDCCC15729D195D8548881CD8B554188DF40DFA0D164DF379AFACD3` | Derive as internal pixel-buffer implementation | Preserve row-order behavior; make signed-stride magnitude handling safe for the minimum `ptrdiff_t` value; retain overflow validation. |
| `windows/runner/capture_runtime.h` | `C6269DD460B0461E0C962944648C075EC0B7D7DA544A3E3AC352C6EDAE221DF0` | Derive as an internal capture-policy header | Rename project identifiers; replace inherited fixed cadence and chunk assumptions with validated, versioned policy inputs. |
| `windows/runner/capture_runtime.cpp` | `459928A5744C9AD2E1D30434FA41EBF5881E70D6D8AA6608CBA4C06400595DE3` | Derive as internal capture-policy implementation | Retain scheduling, media timing, and resource-sampling rules; parameterize cadence and chunk duration; adapt stop planning to the C ABI lifecycle contract. |
| `windows/runner/frame_similarity.h` | `80DC42444BB88F0BD651000B83DB558796C54DF08D769B5BB30BF4264F417D4C` | Derive as an internal extraction helper header | Rename namespace and include guard; do not expose the C++ signature type through the ABI. |
| `windows/runner/frame_similarity.cpp` | `616B54224A102E5A0CFAF7F6A92002202F296753A64AFF91C2C4976B198E825D` | Derive as internal extraction helper implementation | Preserve fail-open comparison behavior initially; validate thresholds with the capture evidence benchmark before release. |
| `windows/runner/capture_service.h` | `804F30B0A0954A67E465D32E71182867E332073A4BD516024448364664E150EA` | Heavily derive as an internal capture-engine header | Rename the service; keep all C++ containers, strings, optionals, callbacks, and enums behind the DLL; split lifecycle and frame extraction behind separate C ABI entry points. |
| `windows/runner/capture_service.cpp` | `FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD` | Heavily derive as internal capture-engine implementation | Remove runner assumptions; replace direct callback delivery with an internal event queue; parameterize policy; add pre-persistence privacy decisions; preserve bounded DXGI, WIC, Media Foundation, atomic chunk, and extraction behavior where tests support it. |
| `windows/runner/native_frame_logger.h` | `0DBFA02D7E8264E6D70BD5CEAC01DDF9E22FCF74266961AD0E7B236305A926AD` | Derive as an internal optional diagnostic logger header | Rename project identifiers; keep the allowlisted diagnostic record private to the DLL. |
| `windows/runner/native_frame_logger.cpp` | `58616B654B06265135A419D3BB350E8A4DAE4D89476B5E9A9C21DD67A237544D` | Derive as internal optional diagnostic logger implementation | Preserve INFO-level per-frame suppression, bounded rotation, and the safe field allowlist; integrate with WinDayFlow diagnostic policy. |

The first DLL also requires WinDayFlow-owned files for the public C header,
export implementation, opaque-handle ownership, event queue, privacy guard,
and build definition. Those files are new work and must not be represented as
QiDayflow-derived unless their implementation later copies or closely adapts
upstream source.

## Planned Derived Native Tests

| Upstream file | Original SHA-256 | Planned local treatment | Required modification summary |
| --- | --- | --- | --- |
| `windows/runner/capture_pixel_buffer_test.cpp` | `5D7D15389F0FB83CDA0B775225A47225A4B1D58A0A2C62EF8E48464FEC0016BF` | Port and extend | Preserve top-down row regression cases; add null, zero, short buffer, padded stride, negative stride, minimum signed stride, and overflow cases. |
| `windows/runner/capture_runtime_test.cpp` | `541193A401AC7CBD039DB9AB86C9B0AAD5CF41F3633619B6643CAC5EBECD1E0E` | Port and adapt | Preserve timing, scheduling, topology, lock, CPU, memory, and stop-plan cases; replace assertions tied to inherited fixed product defaults. |
| `windows/runner/frame_similarity_test.cpp` | `7AF376561E680E87F1C3626D9417993E0A92E979B33F7C3816A535F79434D802` | Port and extend | Preserve threshold, fail-open, alpha, near-duplicate, sequence, and bound cases; add representative real capture fixtures later. |
| `windows/runner/native_frame_logger_test.cpp` | `AFB0AC9CBF915FC430626DA34F87C815D358087D83207C9814922C703A3510C9` | Port and adapt | Preserve INFO filtering, allowlist, close, rotation, and concurrency cases; assert WinDayFlow event names and privacy policy. |

QiDayflow has no native `capture_service_test.cpp` at the pinned revision.
WinDayFlow must add its own C ABI, lifecycle, Windows integration, privacy,
encoder, extractor, filesystem failure, recovery, and shutdown tests. The Dart
test `test/services/native/native_capture_service_test.dart`, SHA-256
`5FFFE14AECED9D5A92F51BC9853F21EF1E47F1E58673E307A2A45EC72340882C`,
is contract reference material only. It mocks Flutter MethodChannel behavior
and is not evidence that the native engine or the WinDayFlow DLL works.

## Rejected Files

These files must not be copied into WinDayFlow. Useful behavior must be
implemented behind WinDayFlow-owned Windows and C ABI boundaries. If any source
fragment is later adapted, its disposition must first change to "Derive" and
the derived-file ledger must identify the exact upstream input.

| Upstream file | Original SHA-256 | Reason for rejection |
| --- | --- | --- |
| `windows/runner/native_bridge.h` | `158EF9BD534434BADE7F5FEAF0E95B3D7807796C34C7A44CE3AC697D80D8F08C` | Public and private types depend on Flutter channels, messenger ownership, Flutter values, runner window messages, tray behavior, and runner shutdown. |
| `windows/runner/native_bridge.cpp` | `16C4C41069716ADEDFBFF69FE47108BCE54AE7624A8C0D0625FD26238CA30F11` | MethodChannel parsing, EventChannel delivery, Flutter value serialization, and runner UI operations are incompatible with the narrow native boundary. |
| `windows/runner/flutter_window.h` | `E0795C3092CA213C050D5F5EB8CC0DBA4FDE54799A2EFDF49A97C6D3587255B2` | The type is a Flutter view host and mixes capture protection with Flutter window, tray, and application lifecycle. |
| `windows/runner/flutter_window.cpp` | `3BC0A4C7BE182773685806809A7D8ABEC7350DAF8B758D9AEBFD86DD8D245723` | WTS session handling must be reimplemented in the WinDayFlow privacy guard rather than retaining a Flutter host dependency. |
| `windows/runner/CMakeLists.txt` | `DFC65848407ABF4CBD722063922F34EF11CFD7FAF591E5F2B068CD734AA3F2E1` | It builds a Flutter runner executable and depends on Flutter-managed targets, generated registrants, resources, and build helpers. WinDayFlow requires a standalone DLL and native test targets. |

The remaining Flutter runner, generated plugin, branding, icon, resource, tray,
window-host, startup, update, and application-lifecycle files are outside this
review and are not approved for reuse.

## Privacy and Safety Adaptation Requirements

The pinned capture service is not ready to ship unchanged. It records raw
window titles and full process paths in chunk metadata, while application and
window exclusions, sensitive-context rules, secure desktop, Remote Desktop,
presentation mode, sleep, and session transitions are not enforced by the
service before evidence is persisted. Lock notification is supplied by the
rejected Flutter window.

The derived engine must make one conservative privacy decision before both
frame persistence and metadata persistence. Unknown guard state must fail
closed. The native backend must not be reported as available until the guard,
transition, race, and recovery tests defined by the architecture pass.

## License and Distribution Requirements

Before the first derived source file is committed or distributed:

1. Preserve a byte-identical copy of the pinned QiDayflow MIT license Git blob
   as `licenses/QiDayflow-LICENSE.txt`.
2. Add a `THIRD_PARTY_NOTICES.md` entry named "QiDayflow native
   capture-derived components" containing the repository URL, full pinned
   commit, derived-file list, the upstream copyright notice, and the complete
   MIT permission and warranty text.
3. Add a concise header to each derived source and test file containing its
   upstream path, pinned commit, original SHA-256, and a reference to the
   third-party notice.
4. Do not reuse the QiDayflow name as WinDayFlow branding and do not reuse its
   icons, screenshots, fonts, or other visual assets under this source-code
   review.

WinDayFlow's root MIT license covers WinDayFlow-owned work. It does not replace
the notice required for derived QiDayflow material.

## Derived-File Ledger and Local Hash Rules

The following derived files are present. The repository has not yet created its
initial commit, so the commit field remains an explicit worktree marker until
that commit exists.

| Local file | Upstream input or inputs | Local SHA-256 | Last verified WinDayFlow commit | Modification summary |
| --- | --- | --- | --- | --- |
| `src/WinDayFlow.Capture.Native/internal/pixel_buffer.h` | `windows/runner/capture_pixel_buffer.h` | `6C9FD5370BDFB230300137F6046EEA37555214D0E4F5CD34508E597BE4FB8A05` | `WORKTREE (pre-initial commit)` | Renamed the namespace and include guard while keeping all C++ types internal. |
| `src/WinDayFlow.Capture.Native/internal/pixel_buffer.cpp` | `windows/runner/capture_pixel_buffer.cpp` | `2E2A2D55EA51FD25D0E3BEC875DE78F1480DFEE2FA1CD76108295DF1D6C67ADD` | `WORKTREE (pre-initial commit)` | Added shared overflow helpers, safe minimum-`ptrdiff_t` stride magnitude, exact last-row buffer bounds, and final-row pointer-arithmetic guards. |
| `src/WinDayFlow.Capture.Native/internal/capture_policy.h` | `windows/runner/capture_runtime.h` | `90C3A37EE700DFBC53E4F4FD05BEFFC2FC502852ADB5AA7188D91DC9764D05E2` | `WORKTREE (pre-initial commit)` | Replaced inherited fixed intervals with validated millisecond policy inputs and removed the platform-thread stop plan from this helper. |
| `src/WinDayFlow.Capture.Native/internal/capture_policy.cpp` | `windows/runner/capture_runtime.cpp` | `51A526E9C101721CF0E54A675103665121A1373EC3C1C38BD9A5FA1080F8E69E` | `WORKTREE (pre-initial commit)` | Parameterized frame, context, and chunk cadence; retained saturating schedule, media timing, chunk, CPU, and memory calculations. |
| `src/WinDayFlow.Capture.Native/tests/pixel_buffer_tests.cpp` | `windows/runner/capture_pixel_buffer_test.cpp` | `7DD6C4A0215528265B8D8E453B51872F2E10452DDC4D818E3F6CD51AD49D8A7F` | `WORKTREE (pre-initial commit)` | Preserved top-down regression coverage and added exact padded-row bounds, negative stride, one-row positive/negative/minimum stride, short buffer, zero stride, and overflow cases. |
| `src/WinDayFlow.Capture.Native/tests/capture_policy_tests.cpp` | `windows/runner/capture_runtime_test.cpp` | `06E1B9C0FD0D4A2C4CA35F61B536AEF0FF8BA2F933A2AD1163A16A5D933469DB` | `WORKTREE (pre-initial commit)` | Adapted timing and scheduling assertions to versioned policy inputs and added fail-closed privacy decision coverage. |

Maintain the ledger under the following rules:

1. Keep every original upstream SHA-256 unchanged. A new upstream baseline is
   a new review, not an update to an old hash.
2. Calculate the local SHA-256 over the exact committed file bytes. Record the
   full 64-character uppercase hexadecimal value.
3. Add or update the ledger row in the same change that adds or modifies a
   derived file. Replace the "Last verified" value with the resulting
   WinDayFlow commit as soon as that commit exists.
4. If one local file is derived from multiple upstream files, list every input
   path and original hash; do not select only the dominant input.
5. Describe behaviorally significant departures, deleted coupling, security
   fixes, and policy changes. A generic entry such as "modified" is not
   sufficient.
6. Renames retain their provenance. Deletion removes the active ledger row but
   remains discoverable in version history and third-party notices for any
   release that shipped the file.
7. CI or a release verification script must recompute each local hash and fail
   when the ledger is stale. Generated binaries are tracked by the release
   manifest, not by this source ledger.

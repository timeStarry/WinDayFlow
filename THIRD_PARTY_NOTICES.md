# Third-Party Notices

WinDayFlow is distributed under the repository-root MIT `LICENSE`. The notices
below apply to third-party source or binary components that are incorporated
into or redistributed with WinDayFlow. They do not change the license of
WinDayFlow-owned work.

## QiDayflow native capture-derived components

- Source: `https://github.com/liujiaqi7998/QiDayflow.git`
- Pinned revision: `8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e`
- Upstream version reviewed: `0.1.4`
- License: MIT
- Provenance: `docs/provenance/QiDayflow-capture.md`
- Byte-identical pinned Git blob license copy:
  `licenses/QiDayflow-LICENSE.txt` (SHA-256
  `8534461B0B8263F5145B229F0C1BA4F4B5BF8A535278C2B3124F289AD10926CB`)

WinDayFlow derives and modifies the following native capture foundations from
the pinned source:

- `windows/runner/capture_pixel_buffer.h`
- `windows/runner/capture_pixel_buffer.cpp`
- `windows/runner/capture_runtime.h`
- `windows/runner/capture_runtime.cpp`
- `windows/runner/capture_service.cpp`
- `windows/runner/capture_pixel_buffer_test.cpp`
- `windows/runner/capture_runtime_test.cpp`

The corresponding local derived production files are:

- `src/WinDayFlow.Capture.Native/internal/pixel_buffer.h`
- `src/WinDayFlow.Capture.Native/internal/pixel_buffer.cpp`
- `src/WinDayFlow.Capture.Native/internal/capture_policy.h`
- `src/WinDayFlow.Capture.Native/internal/capture_policy.cpp`
- `src/WinDayFlow.Capture.Native/internal/atomic_chunk_store.h`
- `src/WinDayFlow.Capture.Native/internal/atomic_chunk_store.cpp`
- `src/WinDayFlow.Capture.Native/internal/chunk_manifest.h`
- `src/WinDayFlow.Capture.Native/internal/chunk_manifest.cpp`
- `src/WinDayFlow.Capture.Native/internal/dxgi_desktop_frame_source.h`
- `src/WinDayFlow.Capture.Native/internal/dxgi_desktop_frame_source.cpp`
- `src/WinDayFlow.Capture.Native/internal/mf_h264_chunk_writer.h`
- `src/WinDayFlow.Capture.Native/internal/mf_h264_chunk_writer.cpp`
- `src/WinDayFlow.Capture.Native/internal/wic_bgra_scaler.h`
- `src/WinDayFlow.Capture.Native/internal/wic_bgra_scaler.cpp`

The corresponding derived native tests are
`src/WinDayFlow.Capture.Native/tests/pixel_buffer_tests.cpp` and
`src/WinDayFlow.Capture.Native/tests/capture_policy_tests.cpp`. The writer
tests are new WinDayFlow tests because the pinned upstream has no native
capture-service test.

The following notice applies to those derived components:

MIT License

Copyright (c) 2026 Qi Day Flow contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Bundled binary dependencies

The unpackaged self-contained development build also redistributes Microsoft
.NET, Windows App SDK, WebView2 Loader, SQLite, and managed NuGet components.
Their license and notice files are retained separately under `licenses/` and
must be included by the development and release packaging checks. See each
file for the applicable terms. The WinDayFlow MIT license does not replace
those terms.

| Components redistributed by the current x64 development build | Version | Terms | Included files |
| --- | --- | --- | --- |
| .NET Runtime and Host; `Microsoft.Data.Sqlite`; `Microsoft.Extensions.*`; `System.Diagnostics.EventLog` | 10.0.10 | MIT plus aggregated third-party notices | `licenses/dotnet-10.0.10/` |
| `System.Numerics.Tensors` | 9.0.0 | MIT plus aggregated third-party notices | `licenses/system-numerics-tensors-9.0.0/` |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT plus toolkit third-party notices | `licenses/communitytoolkit-mvvm-8.4.2/` |
| `SQLitePCLRaw.bundle_e_sqlite3`, `.core`, and `.provider.e_sqlite3` | 2.1.11 | Apache-2.0; Copyright 2014-2024 SourceGear, LLC | `licenses/sqlitepclraw-2.1.x/LICENSE-Apache-2.0.txt` |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.12 | Apache-2.0; bundles SQLite 3.53.3, which is dedicated to the public domain | `licenses/sqlitepclraw-2.1.x/LICENSE-Apache-2.0.txt` |
| Microsoft WebView2 Loader, Core, and Core Projection | 1.0.3719.77 | Microsoft WebView2 license and required notice | `licenses/webview2-1.0.3719.77/` |
| Microsoft Windows App SDK, Base, DWrite, Runtime, Foundation, InteractiveExperiences, AI, and Widgets | 2.2.0 family | Microsoft Windows App SDK terms plus aggregated third-party notices | `licenses/windows-app-sdk-2.2.0/` |
| Microsoft Windows App SDK WinUI | 2.2.1 | Microsoft Windows App SDK Engineering Preview terms plus notices | `licenses/windows-app-sdk-winui-2.2.1/` |
| Microsoft Windows App SDK ML | 2.1.70 | Microsoft Windows App SDK and Windows ML terms plus notices | `licenses/windows-app-sdk-ml-2.1.70/` |
| Microsoft Windows AI Machine Learning runtime | 2.1.70 | Microsoft Windows ML runtime terms plus notices | `licenses/windows-ml-runtime-2.1.70/` |
| Microsoft Windows SDK .NET projection (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`) | 10.0.19041.57 | Microsoft Windows SDK terms | `licenses/windows-sdk-net-ref-10.0.19041.57/LICENSE.txt` |

The listed Windows App SDK family expands to the exact packages selected by
the current restore graph: `Microsoft.WindowsAppSDK` 2.2.0, Base 2.0.4,
DWrite 2.1.0, Runtime 2.2.0, Foundation 2.1.0,
InteractiveExperiences 2.0.15, AI 2.2.3, and Widgets 2.0.5.

### Development-only WinUI restriction

`Microsoft.WindowsAppSDK.WinUI` 2.2.1 is currently governed by Engineering
Preview terms that limit it to development and testing and prohibit use in a
live operating environment unless Microsoft permits that use under another
agreement. The terms also prohibit sharing, publishing, or distributing this
component. Therefore the current bundle is for local use on the build machine
only and must not be shared, published, distributed, uploaded, or deployed. A
production release or external test bundle is blocked until WinDayFlow selects
and verifies a WinUI runtime with production redistribution rights, or obtains
explicit permission. Shipping these terms in the bundle records the
restriction; it does not remove or weaken it.

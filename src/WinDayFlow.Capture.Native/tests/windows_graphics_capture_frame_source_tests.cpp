#include <Windows.h>
#include <dxgi.h>
#include <roerrorapi.h>

#include <iostream>

#include "dxgi_desktop_frame_source.h"
#include "windows_capture_worker_backend.h"
#include "windows_graphics_capture_frame_source.h"

namespace {

using windayflow::capture::DxgiDesktopFrameResult;

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

bool TestDesktopNameMatching() {
  return Expect(windayflow::capture::IsDefaultCaptureDesktopName(L"Default"),
                "Default desktop was rejected") &&
         Expect(windayflow::capture::IsDefaultCaptureDesktopName(L"default"),
                "desktop comparison was not case-insensitive") &&
         Expect(!windayflow::capture::IsDefaultCaptureDesktopName(L"Winlogon"),
                "secure desktop was accepted") &&
         Expect(!windayflow::capture::IsDefaultCaptureDesktopName(L""),
                "empty desktop name was accepted");
}

bool TestFailureMapping() {
  return Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(S_OK) ==
                    DxgiDesktopFrameResult::kOk,
                "WGC success was not preserved") &&
         Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(
                    E_ACCESSDENIED) == DxgiDesktopFrameResult::kAccessDenied,
                "WGC access denial was not terminal") &&
         Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(
                    RO_E_CLOSED) == DxgiDesktopFrameResult::kAccessLost,
                "closed WGC session was not rebuildable") &&
         Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(
                    DXGI_ERROR_DEVICE_REMOVED) ==
                    DxgiDesktopFrameResult::kAccessLost,
                "removed WGC device was not rebuildable") &&
         Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(
                    E_INVALIDARG) ==
                    DxgiDesktopFrameResult::kInvalidArgument,
                "invalid WGC argument was not rejected") &&
         Expect(windayflow::capture::MapWindowsGraphicsCaptureFailure(E_FAIL) ==
                    DxgiDesktopFrameResult::kDeviceFailure,
                "unknown WGC failure was not terminal");
}

bool TestFallbackSelection() {
  return Expect(windayflow::capture::ShouldFallbackToWindowsGraphicsCapture(
                    DxgiDesktopFrameResult::kAccessDenied),
                "DXGI access denial did not select WGC") &&
         Expect(!windayflow::capture::ShouldFallbackToWindowsGraphicsCapture(
                    DxgiDesktopFrameResult::kAccessLost),
                "transient DXGI access loss selected WGC") &&
         Expect(!windayflow::capture::ShouldFallbackToWindowsGraphicsCapture(
                    DxgiDesktopFrameResult::kDeviceFailure),
                "unknown DXGI device failure selected WGC") &&
         Expect(!windayflow::capture::ShouldFallbackToWindowsGraphicsCapture(
                    DxgiDesktopFrameResult::kUnsupportedFormat),
                "unsupported DXGI format selected WGC");
}

}  // namespace

int main() {
  return TestDesktopNameMatching() && TestFailureMapping() &&
                 TestFallbackSelection()
             ? 0
             : 1;
}

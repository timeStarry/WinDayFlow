#include <dxgi1_2.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <limits>

#include "dxgi_desktop_frame_source.h"

namespace {

using windayflow::capture::BgraFrame;
using windayflow::capture::DxgiDesktopFrameResult;
using windayflow::capture::DxgiOutputFingerprint;
using windayflow::capture::DxgiOutputResolveResult;

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

BgraFrame IndexedFrame(uint32_t width, uint32_t height) {
  BgraFrame frame;
  frame.width = width;
  frame.height = height;
  frame.pixels.resize(static_cast<size_t>(width) * height * 4U);
  for (uint32_t index = 0; index < width * height; ++index) {
    const size_t offset = static_cast<size_t>(index) * 4U;
    frame.pixels[offset] = static_cast<uint8_t>(index + 1U);
    frame.pixels[offset + 1U] = 0;
    frame.pixels[offset + 2U] = 0;
    frame.pixels[offset + 3U] = 0xFF;
  }
  return frame;
}

std::array<uint8_t, 6> BlueValues(const BgraFrame& frame) {
  std::array<uint8_t, 6> values{};
  for (size_t index = 0; index < values.size(); ++index) {
    values[index] = frame.pixels[index * 4U];
  }
  return values;
}

struct CleanupProbe {
  int* next_order = nullptr;
  int calls = 0;
  int order = 0;
  HRESULT result = S_OK;
};

HRESULT RecordCleanup(void* context) noexcept {
  auto* probe = static_cast<CleanupProbe*>(context);
  if (probe == nullptr || probe->next_order == nullptr) {
    return E_POINTER;
  }
  ++probe->calls;
  probe->order = ++*probe->next_order;
  return probe->result;
}

void SimulateMappedFrameEarlyReturn(CleanupProbe* release_frame,
                                    CleanupProbe* unmap) {
  windayflow::capture::ScopedDxgiCleanupAction acquired(release_frame,
                                                        RecordCleanup);
  acquired.Arm();
  windayflow::capture::ScopedDxgiCleanupAction mapped(unmap, RecordCleanup);
  mapped.Arm();
}

DxgiOutputFingerprint TestFingerprint() {
  DxgiOutputFingerprint fingerprint;
  fingerprint.adapter_luid.LowPart = 17;
  fingerprint.adapter_luid.HighPart = 23;
  fingerprint.monitor = reinterpret_cast<HMONITOR>(uintptr_t{1});
  fingerprint.canonical_device_name = L"\\\\.\\DISPLAY1";
  fingerprint.desktop_coordinates = RECT{100, 200, 2'020, 1'280};
  fingerprint.rotation = DXGI_MODE_ROTATION_IDENTITY;
  return fingerprint;
}

bool TestRotationMappings() {
  const BgraFrame source = IndexedFrame(2, 3);
  BgraFrame output;
  if (!Expect(
          windayflow::capture::RotateBgraFrame(
              source, DXGI_MODE_ROTATION_IDENTITY, &output) &&
              output.width == 2 && output.height == 3 &&
              BlueValues(output) == std::array<uint8_t, 6>{1, 2, 3, 4, 5, 6},
          "identity rotation changed the frame")) {
    return false;
  }
  if (!Expect(
          windayflow::capture::RotateBgraFrame(
              source, DXGI_MODE_ROTATION_ROTATE90, &output) &&
              output.width == 3 && output.height == 2 &&
              BlueValues(output) == std::array<uint8_t, 6>{5, 3, 1, 6, 4, 2},
          "90-degree rotation was incorrect")) {
    return false;
  }
  if (!Expect(
          windayflow::capture::RotateBgraFrame(
              source, DXGI_MODE_ROTATION_ROTATE180, &output) &&
              output.width == 2 && output.height == 3 &&
              BlueValues(output) == std::array<uint8_t, 6>{6, 5, 4, 3, 2, 1},
          "180-degree rotation was incorrect")) {
    return false;
  }
  return Expect(
      windayflow::capture::RotateBgraFrame(source, DXGI_MODE_ROTATION_ROTATE270,
                                           &output) &&
          output.width == 3 && output.height == 2 &&
          BlueValues(output) == std::array<uint8_t, 6>{2, 4, 6, 1, 3, 5},
      "270-degree rotation was incorrect");
}

bool TestInvalidRotationAndFrameFailClosed() {
  BgraFrame output = IndexedFrame(2, 3);
  BgraFrame invalid = IndexedFrame(2, 3);
  invalid.pixels.pop_back();
  return Expect(!windayflow::capture::RotateBgraFrame(
                    invalid, DXGI_MODE_ROTATION_IDENTITY, &output) &&
                    output.pixels.empty(),
                "short source buffer was accepted") &&
         Expect(
             !windayflow::capture::RotateBgraFrame(
                 IndexedFrame(2, 3), DXGI_MODE_ROTATION_UNSPECIFIED, &output) &&
                 output.pixels.empty(),
             "unspecified rotation was accepted") &&
         Expect(!windayflow::capture::RotateBgraFrame(
                    IndexedFrame(2, 3), static_cast<DXGI_MODE_ROTATION>(99),
                    nullptr),
                "null rotation destination was accepted");
}

bool TestFailureMapping() {
  return Expect(windayflow::capture::MapDesktopDuplicationFailure(S_OK) ==
                    DxgiDesktopFrameResult::kOk,
                "success HRESULT was not mapped") &&
         Expect(
             windayflow::capture::MapDesktopDuplicationFailure(
                 DXGI_ERROR_WAIT_TIMEOUT) == DxgiDesktopFrameResult::kTimeout,
             "wait timeout was not mapped") &&
         Expect(
             windayflow::capture::MapDesktopDuplicationFailure(
                 DXGI_ERROR_ACCESS_LOST) == DxgiDesktopFrameResult::kAccessLost,
             "access loss was not mapped") &&
         Expect(windayflow::capture::MapDesktopDuplicationFailure(
                    DXGI_ERROR_SESSION_DISCONNECTED) ==
                    DxgiDesktopFrameResult::kAccessLost,
                "session disconnect was not mapped") &&
         Expect(windayflow::capture::MapDesktopDuplicationFailure(
                    DXGI_ERROR_UNSUPPORTED) ==
                    DxgiDesktopFrameResult::kUnsupportedFormat,
                "unsupported output was not mapped") &&
         Expect(windayflow::capture::MapDesktopDuplicationFailure(E_FAIL) ==
                     DxgiDesktopFrameResult::kDeviceFailure,
                 "unknown device failure was not mapped") &&
         Expect(windayflow::capture::MapDesktopDuplicationFailure(
                    E_ACCESSDENIED) == DxgiDesktopFrameResult::kAccessDenied,
                "desktop access denial was not distinguished") &&
         Expect(windayflow::capture::MapDesktopTextureMapFailure(
                    DXGI_ERROR_DEVICE_REMOVED) ==
                    DxgiDesktopFrameResult::kAccessLost,
                "Map device removal did not invalidate duplication") &&
         Expect(windayflow::capture::MapDesktopTextureMapFailure(
                    DXGI_ERROR_DEVICE_RESET) ==
                    DxgiDesktopFrameResult::kAccessLost,
                "Map device reset did not invalidate duplication") &&
         Expect(
             windayflow::capture::MapDesktopTextureMapFailure(
                 DXGI_ERROR_ACCESS_LOST) == DxgiDesktopFrameResult::kAccessLost,
             "Map access loss did not invalidate duplication") &&
         Expect(windayflow::capture::MapDesktopTextureMapFailure(E_FAIL) ==
                    DxgiDesktopFrameResult::kCopyFailure,
                "ordinary Map failure was not kept local to the copy");
}

bool TestFrameGeometryBounds() {
  size_t bytes = 99;
  if (!Expect(windayflow::capture::TryCalculateDxgiFrameBgraBytes(7'680, 4'320,
                                                                  &bytes) &&
                  bytes == windayflow::capture::kMaximumDxgiFrameBgraBytes,
              "maximum bounded DXGI frame was rejected")) {
    return false;
  }
  bytes = 99;
  if (!Expect(!windayflow::capture::TryCalculateDxgiFrameBgraBytes(7'680, 4'321,
                                                                   &bytes) &&
                  bytes == 0,
              "over-limit DXGI pixel geometry was accepted")) {
    return false;
  }
  bytes = 99;
  if (!Expect(!windayflow::capture::TryCalculateDxgiFrameBgraBytes(
                  std::numeric_limits<uint32_t>::max(),
                  std::numeric_limits<uint32_t>::max(), &bytes) &&
                  bytes == 0,
              "overflow-scale DXGI geometry was accepted")) {
    return false;
  }

  size_t source_size = 0;
  size_t destination_size = 0;
  if (!Expect(
          windayflow::capture::TryCalculateDxgiMappedFrameSizes(
              7'680U * 4U, 7'680, 4'320, &source_size, &destination_size) &&
              source_size == windayflow::capture::kMaximumDxgiFrameBgraBytes &&
              destination_size ==
                  windayflow::capture::kMaximumDxgiFrameBgraBytes,
          "maximum mapped DXGI frame size was rejected")) {
    return false;
  }
  source_size = 99;
  destination_size = 99;
  return Expect(!windayflow::capture::TryCalculateDxgiMappedFrameSizes(
                    static_cast<uint32_t>(
                        windayflow::capture::kMaximumDxgiFrameBgraBytes),
                    2, 2, &source_size, &destination_size) &&
                    source_size == 0 && destination_size == 0,
                "mapped row pitch bypassed the DXGI BGRA byte limit");
}

bool TestCleanupActionsCoverEarlyReturns() {
  int order = 0;
  CleanupProbe release_frame{&order};
  CleanupProbe unmap{&order};
  SimulateMappedFrameEarlyReturn(&release_frame, &unmap);
  if (!Expect(unmap.calls == 1 && unmap.order == 1 &&
                  release_frame.calls == 1 && release_frame.order == 2,
              "Map early return did not unmap before ReleaseFrame")) {
    return false;
  }

  CleanupProbe explicit_release{&order, 0, 0, DXGI_ERROR_DEVICE_REMOVED};
  HRESULT release_result = S_OK;
  {
    windayflow::capture::ScopedDxgiCleanupAction cleanup(&explicit_release,
                                                         RecordCleanup);
    cleanup.Arm();
    release_result = cleanup.RunNow();
  }
  if (!Expect(release_result == DXGI_ERROR_DEVICE_REMOVED &&
                  explicit_release.calls == 1,
              "explicit ReleaseFrame cleanup was lost or repeated")) {
    return false;
  }

  CleanupProbe unarmed{&order};
  {
    windayflow::capture::ScopedDxgiCleanupAction cleanup(&unarmed,
                                                         RecordCleanup);
  }
  return Expect(unarmed.calls == 0,
                "unacquired frame executed ReleaseFrame cleanup");
}

bool TestFingerprintValidationEarlyReturns() {
  const DxgiOutputFingerprint expected = TestFingerprint();
  DxgiOutputFingerprint current = expected;
  if (!Expect(windayflow::capture::ValidateDxgiOutputFingerprint(
                  expected, DxgiOutputResolveResult::kResolved, current) ==
                  DxgiDesktopFrameResult::kOk,
              "matching output fingerprint was rejected")) {
    return false;
  }
  current.rotation = DXGI_MODE_ROTATION_ROTATE90;
  if (!Expect(windayflow::capture::ValidateDxgiOutputFingerprint(
                  expected, DxgiOutputResolveResult::kResolved, current) ==
                  DxgiDesktopFrameResult::kTopologyChanged,
              "changed output fingerprint did not fail closed")) {
    return false;
  }
  if (!Expect(windayflow::capture::ValidateDxgiOutputFingerprint(
                  expected, DxgiOutputResolveResult::kNotFound, expected) ==
                  DxgiDesktopFrameResult::kOutputUnavailable,
              "resolver failure did not stop fingerprint validation")) {
    return false;
  }
  if (!Expect(windayflow::capture::ValidateDxgiFrameDimensions(
                  1'920, 1'080, expected) == DxgiDesktopFrameResult::kOk,
              "frame dimensions did not match the output fingerprint")) {
    return false;
  }
  if (!Expect(windayflow::capture::ValidateDxgiFrameDimensions(1'919, 1'080,
                                                               expected) ==
                  DxgiDesktopFrameResult::kTopologyChanged,
              "dimension mismatch did not stop frame publication")) {
    return false;
  }
  current = expected;
  current.desktop_coordinates = RECT{std::numeric_limits<LONG>::min(), 0,
                                     std::numeric_limits<LONG>::max(), 1'080};
  return Expect(
      windayflow::capture::ValidateDxgiFrameDimensions(1'920, 1'080, current) ==
          DxgiDesktopFrameResult::kTopologyChanged,
      "invalid fingerprint rectangle was accepted");
}

bool TestUninitializedSourceDoesNotCapture() {
  windayflow::capture::DxgiDesktopFrameSource source;
  BgraFrame frame = IndexedFrame(2, 3);
  return Expect(source.Acquire(100, &frame) ==
                        DxgiDesktopFrameResult::kInvalidArgument &&
                    frame.pixels.empty(),
                "uninitialized source returned or retained a frame") &&
         Expect(!source.initialized(),
                "new source reported itself initialized");
}

}  // namespace

int main() {
  if (!TestRotationMappings() || !TestInvalidRotationAndFrameFailClosed() ||
      !TestFailureMapping() || !TestFrameGeometryBounds() ||
      !TestCleanupActionsCoverEarlyReturns() ||
      !TestFingerprintValidationEarlyReturns() ||
      !TestUninitializedSourceDoesNotCapture()) {
    return 1;
  }
  std::cout << "DXGI desktop frame source tests passed\n";
  return 0;
}

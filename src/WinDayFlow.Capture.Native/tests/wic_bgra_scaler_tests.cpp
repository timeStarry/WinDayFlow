#include <Windows.h>

#include <algorithm>
#include <iostream>

#include "wic_bgra_scaler.h"

namespace {

using windayflow::capture::BgraFrame;
using windayflow::capture::BoundedFrameSize;

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

BgraFrame SolidFrame(uint32_t width, uint32_t height, uint8_t blue,
                     uint8_t green, uint8_t red, uint8_t alpha) {
  BgraFrame frame;
  frame.width = width;
  frame.height = height;
  frame.pixels.resize(static_cast<size_t>(width) * height * 4U);
  for (size_t offset = 0; offset < frame.pixels.size(); offset += 4) {
    frame.pixels[offset] = blue;
    frame.pixels[offset + 1] = green;
    frame.pixels[offset + 2] = red;
    frame.pixels[offset + 3] = alpha;
  }
  return frame;
}

bool TestBoundedEvenSize() {
  BoundedFrameSize size;
  return Expect(windayflow::capture::CalculateBoundedEvenFrameSize(
                    2560, 1440, 1920, 1080, &size) &&
                    size == BoundedFrameSize{1920, 1080},
                "landscape frame size was not bounded") &&
         Expect(windayflow::capture::CalculateBoundedEvenFrameSize(
                    1080, 1920, 1920, 1080, &size) &&
                    size == BoundedFrameSize{608, 1080},
                "portrait frame size was not bounded") &&
         Expect(windayflow::capture::CalculateBoundedEvenFrameSize(
                    1365, 767, 1920, 1080, &size) &&
                    size == BoundedFrameSize{1364, 766},
                "odd frame size was not normalized without upscaling") &&
         Expect(!windayflow::capture::CalculateBoundedEvenFrameSize(
                    1, 1080, 1920, 1080, &size),
                "one-pixel frame dimension was accepted") &&
         Expect(!windayflow::capture::CalculateBoundedEvenFrameSize(
                    1920, 1080, 1, 1080, &size),
                "one-pixel maximum dimension was accepted");
}

bool TestWicScaleAndAlphaNormalization() {
  const HRESULT com =
      CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
  const bool uninitialize = SUCCEEDED(com);
  if (!uninitialize && com != RPC_E_CHANGED_MODE) {
    return Expect(false, "COM could not initialize for WIC test");
  }

  Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
  HRESULT result = windayflow::capture::CreateWicImagingFactory(&factory);
  BgraFrame scaled;
  if (SUCCEEDED(result)) {
    result = windayflow::capture::ScaleBgraFrameWithWic(
        factory.Get(), SolidFrame(4, 4, 10, 20, 30, 0xFF), 2, 2, &scaled);
  }
  const bool valid =
      Expect(SUCCEEDED(result) && scaled.width == 2 && scaled.height == 2 &&
                 windayflow::capture::IsValidBgraFrame(scaled),
             "WIC did not produce a bounded BGRA frame") &&
      Expect(scaled.pixels[0] == 10 && scaled.pixels[1] == 20 &&
                 scaled.pixels[2] == 30 && scaled.pixels[3] == 0xFF &&
                 scaled.pixels[7] == 0xFF && scaled.pixels[11] == 0xFF &&
                 scaled.pixels[15] == 0xFF,
             "WIC scaling changed a solid color or alpha");

  factory.Reset();
  if (uninitialize) {
    CoUninitialize();
  }
  return valid;
}

bool TestNoScaleStillOwnsOutputAndNormalizesAlpha() {
  const HRESULT com =
      CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
  const bool uninitialize = SUCCEEDED(com);
  if (!uninitialize && com != RPC_E_CHANGED_MODE) {
    return Expect(false, "COM could not initialize for WIC copy test");
  }
  Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
  HRESULT result = windayflow::capture::CreateWicImagingFactory(&factory);
  BgraFrame source = SolidFrame(2, 2, 1, 2, 3, 4);
  BgraFrame output;
  if (SUCCEEDED(result)) {
    result = windayflow::capture::ScaleBgraFrameWithWic(factory.Get(), source,
                                                        8, 8, &output);
  }
  source.pixels[0] = 99;
  const bool valid =
      Expect(SUCCEEDED(result) && output.width == 2 && output.height == 2 &&
                 output.pixels[0] == 1 && output.pixels[3] == 0xFF,
             "unscaled output aliased input or retained alpha");
  factory.Reset();
  if (uninitialize) {
    CoUninitialize();
  }
  return valid;
}

}  // namespace

int main() {
  if (!TestBoundedEvenSize() || !TestWicScaleAndAlphaNormalization() ||
      !TestNoScaleStillOwnsOutputAndNormalizesAlpha()) {
    return 1;
  }
  std::cout << "WIC BGRA scaler tests passed\n";
  return 0;
}

// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Heavily
// modified for bounded WinDayFlow frame scaling; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_WIC_BGRA_SCALER_H_
#define WINDAYFLOW_WIC_BGRA_SCALER_H_

#include <Windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cstdint>
#include <vector>

namespace windayflow::capture {

struct BgraFrame {
  uint32_t width = 0;
  uint32_t height = 0;
  std::vector<uint8_t> pixels;

  bool operator==(const BgraFrame&) const = default;
};

struct BoundedFrameSize {
  uint32_t width = 0;
  uint32_t height = 0;

  bool operator==(const BoundedFrameSize&) const = default;
};

bool IsValidBgraFrame(const BgraFrame& frame) noexcept;

bool CalculateBoundedEvenFrameSize(uint32_t source_width,
                                   uint32_t source_height,
                                   uint32_t maximum_width,
                                   uint32_t maximum_height,
                                   BoundedFrameSize* size) noexcept;

HRESULT CreateWicImagingFactory(
    Microsoft::WRL::ComPtr<IWICImagingFactory>* factory) noexcept;

HRESULT ScaleBgraFrameWithWic(IWICImagingFactory* factory,
                              const BgraFrame& source, uint32_t maximum_width,
                              uint32_t maximum_height,
                              BgraFrame* destination) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_WIC_BGRA_SCALER_H_

// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Heavily
// modified for bounded WinDayFlow frame scaling; see THIRD_PARTY_NOTICES.md.

#include "wic_bgra_scaler.h"

#include <algorithm>
#include <limits>

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;

bool TryCalculateBgraBytes(uint32_t width, uint32_t height, size_t* stride,
                           size_t* bytes) noexcept {
  if (stride == nullptr || bytes == nullptr || width == 0 || height == 0 ||
      width > std::numeric_limits<size_t>::max() / 4U) {
    return false;
  }
  const size_t calculated_stride = static_cast<size_t>(width) * 4U;
  if (height > std::numeric_limits<size_t>::max() / calculated_stride) {
    return false;
  }
  *stride = calculated_stride;
  *bytes = calculated_stride * static_cast<size_t>(height);
  return true;
}

uint32_t DivideRounded(uint64_t numerator, uint32_t denominator) noexcept {
  if (denominator == 0) {
    return 0;
  }
  return static_cast<uint32_t>(
      (numerator + static_cast<uint64_t>(denominator) / 2U) / denominator);
}

uint32_t MakeEvenAtMost(uint32_t value, uint32_t maximum) noexcept {
  value = std::min(value, maximum);
  return value & ~uint32_t{1};
}

}  // namespace

bool IsValidBgraFrame(const BgraFrame& frame) noexcept {
  size_t stride = 0;
  size_t bytes = 0;
  return TryCalculateBgraBytes(frame.width, frame.height, &stride, &bytes) &&
         frame.pixels.size() == bytes;
}

bool CalculateBoundedEvenFrameSize(uint32_t source_width,
                                   uint32_t source_height,
                                   uint32_t maximum_width,
                                   uint32_t maximum_height,
                                   BoundedFrameSize* size) noexcept {
  if (size == nullptr) {
    return false;
  }
  *size = {};
  if (source_width < 2 || source_height < 2 || maximum_width < 2 ||
      maximum_height < 2) {
    return false;
  }

  uint32_t width = source_width;
  uint32_t height = source_height;
  if (width > maximum_width || height > maximum_height) {
    if (static_cast<uint64_t>(source_width) * maximum_height >=
        static_cast<uint64_t>(source_height) * maximum_width) {
      width = maximum_width;
      height = DivideRounded(static_cast<uint64_t>(source_height) * width,
                             source_width);
    } else {
      height = maximum_height;
      width = DivideRounded(static_cast<uint64_t>(source_width) * height,
                            source_height);
    }
  }

  width = MakeEvenAtMost(width, std::min(source_width, maximum_width));
  height = MakeEvenAtMost(height, std::min(source_height, maximum_height));
  if (width < 2 || height < 2) {
    return false;
  }
  *size = BoundedFrameSize{width, height};
  return true;
}

HRESULT CreateWicImagingFactory(ComPtr<IWICImagingFactory>* factory) noexcept {
  if (factory == nullptr) {
    return E_POINTER;
  }
  factory->Reset();
  HRESULT result =
      CoCreateInstance(CLSID_WICImagingFactory2, nullptr, CLSCTX_INPROC_SERVER,
                       IID_PPV_ARGS(factory->GetAddressOf()));
  if (FAILED(result)) {
    result =
        CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                         IID_PPV_ARGS(factory->GetAddressOf()));
  }
  return result;
}

HRESULT ScaleBgraFrameWithWic(IWICImagingFactory* factory,
                              const BgraFrame& source, uint32_t maximum_width,
                              uint32_t maximum_height,
                              BgraFrame* destination) noexcept {
  if (destination == nullptr) {
    return E_POINTER;
  }
  *destination = {};
  if (factory == nullptr || !IsValidBgraFrame(source)) {
    return E_INVALIDARG;
  }

  BoundedFrameSize output_size;
  if (!CalculateBoundedEvenFrameSize(source.width, source.height, maximum_width,
                                     maximum_height, &output_size)) {
    return E_INVALIDARG;
  }
  size_t source_stride = 0;
  size_t source_bytes = 0;
  size_t output_stride = 0;
  size_t output_bytes = 0;
  if (!TryCalculateBgraBytes(source.width, source.height, &source_stride,
                             &source_bytes) ||
      !TryCalculateBgraBytes(output_size.width, output_size.height,
                             &output_stride, &output_bytes) ||
      source_stride > std::numeric_limits<UINT>::max() ||
      source_bytes > std::numeric_limits<UINT>::max() ||
      output_stride > std::numeric_limits<UINT>::max() ||
      output_bytes > std::numeric_limits<UINT>::max()) {
    return E_INVALIDARG;
  }

  try {
    if (output_size.width == source.width &&
        output_size.height == source.height) {
      *destination = source;
      for (size_t alpha = 3; alpha < destination->pixels.size(); alpha += 4) {
        destination->pixels[alpha] = 0xFF;
      }
      return S_OK;
    }

    ComPtr<IWICBitmap> bitmap;
    HRESULT result = factory->CreateBitmapFromMemory(
        source.width, source.height, GUID_WICPixelFormat32bppBGRA,
        static_cast<UINT>(source_stride), static_cast<UINT>(source_bytes),
        const_cast<BYTE*>(source.pixels.data()), bitmap.GetAddressOf());
    if (FAILED(result)) {
      return result;
    }

    ComPtr<IWICBitmapScaler> scaler;
    result = factory->CreateBitmapScaler(scaler.GetAddressOf());
    if (FAILED(result)) {
      return result;
    }
    result =
        scaler->Initialize(bitmap.Get(), output_size.width, output_size.height,
                           WICBitmapInterpolationModeFant);
    if (FAILED(result)) {
      return result;
    }

    BgraFrame output;
    output.width = output_size.width;
    output.height = output_size.height;
    output.pixels.resize(output_bytes);
    result = scaler->CopyPixels(nullptr, static_cast<UINT>(output_stride),
                                static_cast<UINT>(output_bytes),
                                output.pixels.data());
    if (FAILED(result)) {
      return result;
    }
    for (size_t alpha = 3; alpha < output.pixels.size(); alpha += 4) {
      output.pixels[alpha] = 0xFF;
    }
    *destination = std::move(output);
    return S_OK;
  } catch (...) {
    *destination = {};
    return E_OUTOFMEMORY;
  }
}

}  // namespace windayflow::capture

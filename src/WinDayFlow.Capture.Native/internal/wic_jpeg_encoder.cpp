#include "wic_jpeg_encoder.h"

#include <propidl.h>
#include <wrl/client.h>

#include <limits>

namespace windayflow::capture {

using Microsoft::WRL::ComPtr;

HRESULT EncodeBgraFrameAsJpeg(
    IWICImagingFactory* factory,
    std::span<const uint8_t> top_down_bgra,
    uint32_t width,
    uint32_t height,
    float quality,
    size_t maximum_bytes,
    std::vector<uint8_t>* jpeg) noexcept {
  if (jpeg == nullptr) {
    return E_POINTER;
  }
  jpeg->clear();
  const uint64_t required_bytes =
      static_cast<uint64_t>(width) * static_cast<uint64_t>(height) * 4U;
  if (factory == nullptr || width < 2 || height < 2 ||
      (width & 1U) != 0 || (height & 1U) != 0 ||
      required_bytes != top_down_bgra.size() || quality <= 0.0F ||
      quality > 1.0F || maximum_bytes < 4U ||
      maximum_bytes > static_cast<size_t>(std::numeric_limits<DWORD>::max())) {
    return E_INVALIDARG;
  }

  try {
    std::vector<uint8_t> buffer(maximum_bytes, 0);
    ComPtr<IWICStream> stream;
    HRESULT result = factory->CreateStream(stream.GetAddressOf());
    if (SUCCEEDED(result)) {
      result = stream->InitializeFromMemory(
          buffer.data(), static_cast<DWORD>(buffer.size()));
    }
    ComPtr<IWICBitmapEncoder> encoder;
    if (SUCCEEDED(result)) {
      result = factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr,
                                      encoder.GetAddressOf());
    }
    if (SUCCEEDED(result)) {
      result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    }
    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    if (SUCCEEDED(result)) {
      result = encoder->CreateNewFrame(frame.GetAddressOf(),
                                       properties.GetAddressOf());
    }
    if (SUCCEEDED(result) && properties != nullptr) {
      PROPBAG2 option{};
      option.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
      VARIANT value;
      VariantInit(&value);
      value.vt = VT_R4;
      value.fltVal = quality;
      result = properties->Write(1, &option, &value);
      VariantClear(&value);
    }
    if (SUCCEEDED(result)) {
      result = frame->Initialize(properties.Get());
    }
    if (SUCCEEDED(result)) {
      result = frame->SetSize(width, height);
    }
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    if (SUCCEEDED(result)) {
      result = frame->SetPixelFormat(&format);
    }
    if (SUCCEEDED(result) && format != GUID_WICPixelFormat24bppBGR) {
      result = WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
    }
    ComPtr<IWICBitmap> bitmap;
    if (SUCCEEDED(result)) {
      result = factory->CreateBitmapFromMemory(
          width, height, GUID_WICPixelFormat32bppBGRA, width * 4U,
          static_cast<UINT>(top_down_bgra.size()),
          const_cast<BYTE*>(top_down_bgra.data()), bitmap.GetAddressOf());
    }
    ComPtr<IWICFormatConverter> converter;
    if (SUCCEEDED(result)) {
      result = factory->CreateFormatConverter(converter.GetAddressOf());
    }
    if (SUCCEEDED(result)) {
      result = converter->Initialize(
          bitmap.Get(), GUID_WICPixelFormat24bppBGR, WICBitmapDitherTypeNone,
          nullptr, 0.0, WICBitmapPaletteTypeCustom);
    }
    if (SUCCEEDED(result)) {
      result = frame->WriteSource(converter.Get(), nullptr);
    }
    if (SUCCEEDED(result)) {
      result = frame->Commit();
    }
    if (SUCCEEDED(result)) {
      result = encoder->Commit();
    }
    LARGE_INTEGER zero{};
    ULARGE_INTEGER position{};
    if (SUCCEEDED(result)) {
      result = stream->Seek(zero, STREAM_SEEK_CUR, &position);
    }
    if (FAILED(result) || position.QuadPart < 4U ||
        position.QuadPart > maximum_bytes) {
      return FAILED(result) ? result : STG_E_MEDIUMFULL;
    }
    buffer.resize(static_cast<size_t>(position.QuadPart));
    if (buffer[0] != 0xFFU || buffer[1] != 0xD8U ||
        buffer[buffer.size() - 2U] != 0xFFU || buffer.back() != 0xD9U) {
      return WINCODEC_ERR_BADIMAGE;
    }
    *jpeg = std::move(buffer);
    return S_OK;
  } catch (...) {
    jpeg->clear();
    return E_OUTOFMEMORY;
  }
}

}  // namespace windayflow::capture

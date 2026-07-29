#ifndef WINDAYFLOW_WIC_JPEG_ENCODER_H_
#define WINDAYFLOW_WIC_JPEG_ENCODER_H_

#include <Windows.h>
#include <wincodec.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace windayflow::capture {

HRESULT EncodeBgraFrameAsJpeg(
    IWICImagingFactory* factory,
    std::span<const uint8_t> top_down_bgra,
    uint32_t width,
    uint32_t height,
    float quality,
    size_t maximum_bytes,
    std::vector<uint8_t>* jpeg) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_WIC_JPEG_ENCODER_H_

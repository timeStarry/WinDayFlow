// Derived from QiDayflow windows/runner/capture_pixel_buffer.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: 8955ADA13EDCCC15729D195D8548881CD8B554188DF40DFA0D164DF379AFACD3.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#include "pixel_buffer.h"

#include <cstring>
#include <limits>

namespace windayflow::capture {
namespace {

bool TryCalculateRowBytes(uint32_t width, size_t* row_bytes) {
  if (row_bytes == nullptr || width == 0 ||
      width > std::numeric_limits<size_t>::max() / 4U) {
    return false;
  }
  *row_bytes = static_cast<size_t>(width) * 4U;
  return true;
}

bool TryCalculateRequiredBytes(size_t row_bytes,
                               size_t stride,
                               uint32_t height,
                               size_t* required_bytes) {
  if (required_bytes == nullptr || height == 0 || stride < row_bytes) {
    return false;
  }
  const size_t row_count_before_last = static_cast<size_t>(height - 1U);
  if (row_count_before_last >
      (std::numeric_limits<size_t>::max() - row_bytes) / stride) {
    return false;
  }
  *required_bytes = row_count_before_last * stride + row_bytes;
  return true;
}

size_t AbsoluteStride(ptrdiff_t stride) {
  if (stride >= 0) {
    return static_cast<size_t>(stride);
  }
  return static_cast<size_t>(-(stride + 1)) + 1U;
}

}  // namespace

bool CopyTopDownBgraRows(const uint8_t* source,
                         size_t source_size,
                         uint32_t width,
                         uint32_t height,
                         uint8_t* destination,
                         size_t destination_size) {
  size_t row_bytes = 0;
  size_t byte_count = 0;
  if (source == nullptr || destination == nullptr ||
      !TryCalculateRowBytes(width, &row_bytes) ||
      !TryCalculateRequiredBytes(row_bytes, row_bytes, height, &byte_count) ||
      source_size != byte_count || destination_size < byte_count) {
    return false;
  }
  std::memcpy(destination, source, byte_count);
  return true;
}

bool CopyDecodedRgb32Rows(const uint8_t* source,
                          size_t source_size,
                          uint32_t width,
                          uint32_t height,
                          ptrdiff_t source_stride,
                          uint8_t* destination,
                          size_t destination_size) {
  size_t row_bytes = 0;
  if (source == nullptr || destination == nullptr || source_stride == 0 ||
      !TryCalculateRowBytes(width, &row_bytes)) {
    return false;
  }

  const size_t absolute_stride = AbsoluteStride(source_stride);
  size_t source_required = 0;
  size_t destination_required = 0;
  if (!TryCalculateRequiredBytes(
          row_bytes, absolute_stride, height, &source_required) ||
      !TryCalculateRequiredBytes(
          row_bytes, row_bytes, height, &destination_required) ||
      source_size < source_required || destination_size < destination_required) {
    return false;
  }

  const uint8_t* row = source;
  if (source_stride < 0) {
    row += absolute_stride * static_cast<size_t>(height - 1U);
  }
  for (uint32_t index = 0; index < height; ++index) {
    std::memcpy(destination + row_bytes * index, row, row_bytes);
    if (index + 1U < height) {
      row += source_stride;
    }
  }
  return true;
}

}  // namespace windayflow::capture

// Derived from QiDayflow windows/runner/capture_pixel_buffer.h at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: 4EDE4160ACACEAE291B62CB347A20DC3891356F6A6E487CCE98FD6EB9165199C.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_CAPTURE_PIXEL_BUFFER_H_
#define WINDAYFLOW_CAPTURE_PIXEL_BUFFER_H_

#include <cstddef>
#include <cstdint>

namespace windayflow::capture {

bool CopyTopDownBgraRows(const uint8_t* source,
                         size_t source_size,
                         uint32_t width,
                         uint32_t height,
                         uint8_t* destination,
                         size_t destination_size);

bool CopyDecodedRgb32Rows(const uint8_t* source,
                          size_t source_size,
                          uint32_t width,
                          uint32_t height,
                          ptrdiff_t source_stride,
                          uint8_t* destination,
                          size_t destination_size);

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_PIXEL_BUFFER_H_

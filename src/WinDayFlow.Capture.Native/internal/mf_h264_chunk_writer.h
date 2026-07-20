// Heavily derived from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Derived and
// modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_MF_H264_CHUNK_WRITER_H_
#define WINDAYFLOW_MF_H264_CHUNK_WRITER_H_

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace windayflow::capture {

inline constexpr size_t kMaximumH264ChunkBytes = 64U * 1024U * 1024U;
inline constexpr int64_t kMediaFoundationTicksPerSecond = 10'000'000;

enum class MfH264ChunkWriterState {
  kIdle,
  kWriting,
  kFinalized,
  kFailed,
};

struct MfH264ChunkWriterConfig {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t frame_rate_numerator = 1;
  uint32_t frame_rate_denominator = 1;
  uint32_t average_bitrate = 2'500'000;
  size_t max_output_bytes = kMaximumH264ChunkBytes;
};

// The writer owns a matching CoInitializeEx/MFStartup pair while it is
// writing. Begin, AddFrame, Finalize, Reset, and destruction must occur on the
// same thread. An apartment already initialized by the caller is preserved.
class MfH264ChunkWriter final {
 public:
  MfH264ChunkWriter();
  ~MfH264ChunkWriter();

  MfH264ChunkWriter(const MfH264ChunkWriter&) = delete;
  MfH264ChunkWriter& operator=(const MfH264ChunkWriter&) = delete;
  MfH264ChunkWriter(MfH264ChunkWriter&&) = delete;
  MfH264ChunkWriter& operator=(MfH264ChunkWriter&&) = delete;

  HRESULT Begin(const MfH264ChunkWriterConfig& config) noexcept;

  // Pixels must be tightly packed, top-down BGRA for the dimensions supplied
  // to Begin. Timestamps use 100-nanosecond Media Foundation ticks.
  HRESULT AddFrame(std::span<const uint8_t> top_down_bgra,
                   int64_t timestamp_ticks) noexcept;

  // end_timestamp_ticks is the exclusive end of the final sample. On every
  // failure, output_mp4 is cleared. A successful call transfers the complete
  // in-memory MP4 to the caller without creating a temporary file.
  HRESULT Finalize(int64_t end_timestamp_ticks,
                   std::vector<uint8_t>* output_mp4) noexcept;

  HRESULT Reset() noexcept;

  MfH264ChunkWriterState state() const noexcept;
  HRESULT last_result() const noexcept;
  uint32_t frame_count() const noexcept;

 private:
  class Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_MF_H264_CHUNK_WRITER_H_

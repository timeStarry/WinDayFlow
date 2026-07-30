#ifndef WINDAYFLOW_JPEG_FRAME_CHUNK_WRITER_H_
#define WINDAYFLOW_JPEG_FRAME_CHUNK_WRITER_H_

#include <Windows.h>
#include <wincodec.h>

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <string_view>

#include "atomic_chunk_store.h"
#include "chunk_manifest.h"

namespace windayflow::capture {

struct JpegFrameChunkWriterConfig {
  uint32_t width = 0;
  uint32_t height = 0;
  float quality = 0.82F;
  size_t maximum_frame_bytes = kMaximumChunkFrameFileBytes;
  size_t maximum_chunk_bytes = kMaximumChunkFrameBytes;
};

enum class JpegFrameChunkWriterResult {
  kOk,
  kInvalidArgument,
  kEncoderFailure,
  kStorageFailure,
};

enum class JpegFrameDisposition {
  kRetained,
  kDuplicate,
};

class JpegFrameChunkWriter final {
 public:
  explicit JpegFrameChunkWriter(std::wstring output_root);
  ~JpegFrameChunkWriter();

  JpegFrameChunkWriter(const JpegFrameChunkWriter&) = delete;
  JpegFrameChunkWriter& operator=(const JpegFrameChunkWriter&) = delete;

  JpegFrameChunkWriterResult Begin(
      IWICImagingFactory* factory,
      std::string_view chunk_id,
      const JpegFrameChunkWriterConfig& config) noexcept;
  JpegFrameChunkWriterResult AddFrame(
      std::span<const uint8_t> top_down_bgra,
      uint64_t offset_milliseconds,
      JpegFrameDisposition* disposition = nullptr) noexcept;
  JpegFrameChunkWriterResult Finalize(
      ChunkManifest* manifest,
      AtomicChunkPublication* publication) noexcept;
  JpegFrameChunkWriterResult Reset() noexcept;

 private:
  class Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_JPEG_FRAME_CHUNK_WRITER_H_

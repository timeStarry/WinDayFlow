// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Sensitive
// window and process metadata were removed; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_CHUNK_MANIFEST_H_
#define WINDAYFLOW_CHUNK_MANIFEST_H_

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace windayflow::capture {

inline constexpr uint32_t kCanonicalJpegQuality = 82U;

struct ChunkFrameManifest {
  uint32_t index = 0;
  uint64_t offset_milliseconds = 0;
  uint32_t byte_count = 0;
  std::string sha256;

  bool operator==(const ChunkFrameManifest&) const = default;
};

struct ChunkApplicationManifest {
  std::string process_name_utf8;
  uint32_t process_id = 0;
  uint32_t cpu_usage_basis_points = 0;
  uint64_t working_set_bytes = 0;
  uint64_t private_memory_bytes = 0;

  bool operator==(const ChunkApplicationManifest&) const = default;
};

struct ChunkContextSampleManifest {
  uint32_t sample_index = 0;
  uint64_t offset_milliseconds = 0;
  std::optional<ChunkApplicationManifest> application;

  bool operator==(const ChunkContextSampleManifest&) const = default;
};

struct ChunkManifest {
  std::string chunk_id;
  int64_t start_time_unix_ms = 0;
  int64_t end_time_unix_ms = 0;
  uint32_t captured_frame_count = 0;
  uint32_t frame_width = 0;
  uint32_t frame_height = 0;
  uint64_t frame_byte_count = 0;
  uint64_t persistence_generation = 0;
  uint64_t target_epoch = 0;
  bool display_wide_scope = false;
  std::vector<ChunkFrameManifest> frames;
  std::optional<ChunkApplicationManifest> application;
  uint32_t black_frame_count = 0;
  uint32_t duplicate_frame_count = 0;
  std::vector<ChunkContextSampleManifest> context_samples;
};

bool IsValidChunkManifest(const ChunkManifest& manifest) noexcept;
bool BuildChunkManifestJson(const ChunkManifest& manifest,
                            std::string* json_utf8) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CHUNK_MANIFEST_H_

// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Sensitive
// window and process metadata were removed; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_CHUNK_MANIFEST_H_
#define WINDAYFLOW_CHUNK_MANIFEST_H_

#include <cstdint>
#include <string>

namespace windayflow::capture {

struct ChunkManifest {
  std::string chunk_id;
  int64_t start_time_unix_ms = 0;
  int64_t end_time_unix_ms = 0;
  uint32_t frame_count = 0;
  uint32_t video_width = 0;
  uint32_t video_height = 0;
  uint32_t frame_rate_numerator = 0;
  uint32_t frame_rate_denominator = 0;
  uint64_t persistence_generation = 0;
  uint64_t target_epoch = 0;
};

bool IsValidChunkManifest(const ChunkManifest& manifest) noexcept;
bool BuildChunkManifestJson(const ChunkManifest& manifest,
                            std::string* json_utf8) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CHUNK_MANIFEST_H_

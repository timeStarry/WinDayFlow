// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Sensitive
// window and process metadata were removed; see THIRD_PARTY_NOTICES.md.

#include "chunk_manifest.h"

#include <locale>
#include <sstream>
#include <utility>

#include "atomic_chunk_store.h"

namespace windayflow::capture {

bool IsValidChunkManifest(const ChunkManifest& manifest) noexcept {
  return IsValidChunkArtifactId(manifest.chunk_id) &&
         manifest.start_time_unix_ms >= 0 &&
         manifest.end_time_unix_ms > manifest.start_time_unix_ms &&
         manifest.frame_count != 0 && manifest.video_width >= 2 &&
         manifest.video_height >= 2 && (manifest.video_width & 1U) == 0 &&
         (manifest.video_height & 1U) == 0 &&
         manifest.frame_rate_numerator != 0 &&
         manifest.frame_rate_denominator != 0 &&
         manifest.persistence_generation != 0 && manifest.target_epoch != 0;
}

bool BuildChunkManifestJson(const ChunkManifest& manifest,
                            std::string* json_utf8) noexcept {
  if (json_utf8 == nullptr) {
    return false;
  }
  json_utf8->clear();
  if (!IsValidChunkManifest(manifest)) {
    return false;
  }

  try {
    std::ostringstream output;
    output.imbue(std::locale::classic());
    output << "{\n"
           << "  \"schemaVersion\": 1,\n"
           << "  \"captureScope\": \"authorized-foreground-display\",\n"
           << "  \"chunkId\": \"" << manifest.chunk_id << "\",\n"
           << "  \"startTimeUnixMs\": " << manifest.start_time_unix_ms << ",\n"
           << "  \"endTimeUnixMs\": " << manifest.end_time_unix_ms << ",\n"
           << "  \"authorization\": {\"persistenceGeneration\": "
           << manifest.persistence_generation
           << ", \"targetEpoch\": " << manifest.target_epoch << "},\n"
           << "  \"video\": {\"path\": \"capture.mp4\", \"codec\": "
              "\"h264\", \"container\": \"mp4\", \"frameCount\": "
           << manifest.frame_count << ", \"width\": " << manifest.video_width
           << ", \"height\": " << manifest.video_height
           << ", \"frameRateNumerator\": " << manifest.frame_rate_numerator
           << ", \"frameRateDenominator\": " << manifest.frame_rate_denominator
           << "}\n"
           << "}\n";
    if (!output) {
      return false;
    }
    std::string json = output.str();
    if (json.size() > kMaximumChunkManifestBytes) {
      return false;
    }
    *json_utf8 = std::move(json);
    return true;
  } catch (...) {
    json_utf8->clear();
    return false;
  }
}

}  // namespace windayflow::capture

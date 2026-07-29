// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Sensitive
// window and process metadata were removed; see THIRD_PARTY_NOTICES.md.

#include "chunk_manifest.h"

#include <algorithm>
#include <limits>
#include <locale>
#include <sstream>
#include <string_view>
#include <utility>

#include "atomic_chunk_store.h"

namespace windayflow::capture {
namespace {

constexpr uint64_t kManifestMaximumChunkFrameBytes = 64U * 1024U * 1024U;
constexpr uint32_t kManifestMaximumFrameBytes = 2U * 1024U * 1024U;
constexpr uint32_t kMaximumFramesPerChunk = 720U;

bool IsCanonicalSha256(std::string_view value) noexcept {
  return value.size() == 64U &&
         std::all_of(value.begin(), value.end(), [](unsigned char value) {
           return (value >= '0' && value <= '9') ||
                  (value >= 'A' && value <= 'F');
         });
}

bool IsValidApplication(
    const ChunkApplicationManifest& application) noexcept {
  return !application.process_name_utf8.empty() &&
         application.process_name_utf8.size() <= 260U &&
         application.process_id != 0 &&
         application.cpu_usage_basis_points <= 10'000U &&
         application.working_set_bytes <=
             static_cast<uint64_t>(std::numeric_limits<int64_t>::max()) &&
         application.private_memory_bytes <=
             static_cast<uint64_t>(std::numeric_limits<int64_t>::max()) &&
         std::none_of(application.process_name_utf8.begin(),
                      application.process_name_utf8.end(),
                      [](unsigned char value) { return value < 0x20U; });
}

std::string EscapeJsonString(std::string_view value) {
  std::string escaped;
  escaped.reserve(value.size());
  for (const char character : value) {
    if (character == '"' || character == '\\') {
      escaped.push_back('\\');
    }
    escaped.push_back(character);
  }
  return escaped;
}

std::string FrameId(uint32_t index) {
  std::ostringstream output;
  output.imbue(std::locale::classic());
  output << "frame-";
  output.width(6);
  output.fill('0');
  output << index;
  return output.str();
}

}  // namespace

bool IsValidChunkManifest(const ChunkManifest& manifest) noexcept {
  if (!IsValidChunkArtifactId(manifest.chunk_id) ||
      manifest.start_time_unix_ms < 0 ||
      manifest.end_time_unix_ms <= manifest.start_time_unix_ms ||
      manifest.captured_frame_count == 0 || manifest.frames.empty() ||
      manifest.frames.size() > kMaximumFramesPerChunk ||
      manifest.captured_frame_count < manifest.frames.size() ||
      manifest.frame_width < 2 || manifest.frame_height < 2 ||
      (manifest.frame_width & 1U) != 0 ||
      (manifest.frame_height & 1U) != 0 || manifest.frame_byte_count == 0 ||
      manifest.frame_byte_count > kManifestMaximumChunkFrameBytes ||
      manifest.persistence_generation == 0 || manifest.target_epoch == 0) {
    return false;
  }
  if (manifest.application.has_value() &&
      (!IsValidApplication(*manifest.application) ||
       manifest.display_wide_scope)) {
    return false;
  }

  const uint64_t duration = static_cast<uint64_t>(
      manifest.end_time_unix_ms - manifest.start_time_unix_ms);
  uint64_t total_bytes = 0;
  uint64_t previous_offset = 0;
  for (size_t ordinal = 0; ordinal < manifest.frames.size(); ++ordinal) {
    const ChunkFrameManifest& frame = manifest.frames[ordinal];
    if (frame.index != ordinal || frame.byte_count < 4U ||
        frame.byte_count > kManifestMaximumFrameBytes ||
        !IsCanonicalSha256(frame.sha256) ||
        frame.offset_milliseconds >= duration ||
        (ordinal != 0 && frame.offset_milliseconds <= previous_offset) ||
        total_bytes > kManifestMaximumChunkFrameBytes - frame.byte_count) {
      return false;
    }
    total_bytes += frame.byte_count;
    previous_offset = frame.offset_milliseconds;
  }
  return total_bytes == manifest.frame_byte_count;
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
           << "  \"schemaVersion\": 3,\n"
           << "  \"captureScope\": \""
           << (manifest.display_wide_scope
                   ? "authorized-display-continuous"
                   : "authorized-foreground-display")
           << "\",\n"
           << "  \"chunkId\": \"" << manifest.chunk_id << "\",\n"
           << "  \"startTimeUnixMs\": " << manifest.start_time_unix_ms
           << ",\n"
           << "  \"endTimeUnixMs\": " << manifest.end_time_unix_ms << ",\n"
           << "  \"authorization\": {\"persistenceGeneration\": "
           << manifest.persistence_generation
           << ", \"targetEpoch\": " << manifest.target_epoch << "},\n"
           << "  \"application\": ";
    if (manifest.application.has_value()) {
      const ChunkApplicationManifest& application = *manifest.application;
      output << "{\"processName\":\""
             << EscapeJsonString(application.process_name_utf8)
             << "\",\"processId\":" << application.process_id
             << ",\"cpuUsageBasisPoints\":"
             << application.cpu_usage_basis_points
             << ",\"workingSetBytes\":" << application.working_set_bytes
             << ",\"privateMemoryBytes\":"
             << application.private_memory_bytes << "},\n";
    } else {
      output << "null,\n";
    }
    output
           << "  \"frames\": {\"format\": \"jpeg\", \"quality\": "
           << kCanonicalJpegQuality << ", \"capturedFrameCount\": "
           << manifest.captured_frame_count << ", \"retainedFrameCount\": "
           << manifest.frames.size() << ", \"width\": "
           << manifest.frame_width << ", \"height\": "
           << manifest.frame_height << ", \"totalByteCount\": "
           << manifest.frame_byte_count << ", \"items\": [";
    for (size_t ordinal = 0; ordinal < manifest.frames.size(); ++ordinal) {
      const ChunkFrameManifest& frame = manifest.frames[ordinal];
      if (ordinal != 0) {
        output << ',';
      }
      const std::string id = FrameId(frame.index);
      output << "{\"id\":\"" << id << "\",\"index\":" << frame.index
             << ",\"path\":\"frames/" << id << ".jpg\""
             << ",\"offsetMilliseconds\":" << frame.offset_milliseconds
             << ",\"byteCount\":" << frame.byte_count
             << ",\"sha256\":\"" << frame.sha256 << "\"}";
    }
    output << "]}\n}\n";
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

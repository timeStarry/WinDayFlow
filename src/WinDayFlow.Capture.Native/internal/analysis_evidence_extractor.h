#ifndef WINDAYFLOW_ANALYSIS_EVIDENCE_EXTRACTOR_H_
#define WINDAYFLOW_ANALYSIS_EVIDENCE_EXTRACTOR_H_

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace windayflow::capture {

inline constexpr uint32_t kMaximumAnalysisEvidenceFrames = 32U;
inline constexpr size_t kMaximumAnalysisEvidenceFrameBytes =
    2U * 1024U * 1024U;
inline constexpr size_t kMaximumAnalysisEvidenceTotalBytes =
    12U * 1024U * 1024U;
inline constexpr size_t kMaximumAnalysisEvidenceManifestBytes = 64U * 1024U;

enum class AnalysisEvidenceResult {
  kOk,
  kInvalidArgument,
  kNotFound,
  kUnsafeEvidence,
  kTooLarge,
  kChangedDuringRead,
  kIoFailure,
  kCryptoFailure,
  kInvalidEvidence,
  kDecoderFailure,
  kConflict,
};

struct AnalysisEvidenceRequest {
  std::wstring data_root;
  std::string canonical_chunk_id;
  uint64_t expected_video_byte_count = 0;
  uint32_t expected_frame_count = 0;
  uint32_t expected_video_width = 0;
  uint32_t expected_video_height = 0;
  uint64_t expected_duration_ms = 0;
  std::string expected_source_fingerprint;
};

AnalysisEvidenceResult ExtractAnalysisEvidence(
    const AnalysisEvidenceRequest& request,
    std::string* manifest_utf8) noexcept;

AnalysisEvidenceResult ReadAnalysisEvidenceFrame(
    const std::wstring& data_root,
    std::string_view canonical_chunk_id,
    std::string_view canonical_source_fingerprint,
    uint32_t frame_index,
    std::vector<uint8_t>* jpeg_bytes) noexcept;

bool IsCanonicalSourceFingerprint(std::string_view value) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_ANALYSIS_EVIDENCE_EXTRACTOR_H_

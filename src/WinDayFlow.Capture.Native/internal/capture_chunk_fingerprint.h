#ifndef WINDAYFLOW_CAPTURE_CHUNK_FINGERPRINT_H_
#define WINDAYFLOW_CAPTURE_CHUNK_FINGERPRINT_H_

#include <array>
#include <cstddef>
#include <string>
#include <string_view>

namespace windayflow::capture {

inline constexpr size_t kCaptureChunkFingerprintHexLength = 64U;
inline constexpr size_t kCaptureChunkFingerprintBufferSize =
    kCaptureChunkFingerprintHexLength + 1U;
inline constexpr size_t kMaximumFingerprintManifestBytes = 64U * 1024U;
inline constexpr size_t kMaximumFingerprintVideoBytes =
    64U * 1024U * 1024U;

enum class CaptureChunkFingerprintResult {
  kOk,
  kInvalidArgument,
  kNotFound,
  kUnsafeEvidence,
  kTooLarge,
  kChangedDuringRead,
  kIoFailure,
  kCryptoFailure,
};

// Hashes domain || LE64(manifest length) || manifest bytes ||
// LE64(video length) || video bytes. The domain includes its terminating NUL.
CaptureChunkFingerprintResult ComputeCaptureChunkFingerprint(
    const std::wstring& data_root, std::string_view canonical_chunk_id,
    size_t expected_video_byte_count,
    std::array<char, kCaptureChunkFingerprintBufferSize>* fingerprint) noexcept;

bool IsCanonicalCaptureChunkId(std::string_view value) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_CHUNK_FINGERPRINT_H_

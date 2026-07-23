#include <Windows.h>
#include <bcrypt.h>
#include <mfapi.h>

#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "analysis_evidence_extractor.h"
#include "capture_chunk_fingerprint.h"
#include "mf_h264_chunk_writer.h"

namespace {

using windayflow::capture::AnalysisEvidenceRequest;
using windayflow::capture::AnalysisEvidenceResult;
using windayflow::capture::MfH264ChunkWriter;
using windayflow::capture::MfH264ChunkWriterConfig;

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

std::filesystem::path UniqueTestRoot() {
  std::array<wchar_t, MAX_PATH + 1> temporary{};
  const DWORD length =
      GetTempPathW(static_cast<DWORD>(temporary.size()), temporary.data());
  std::array<uint8_t, 16> nonce{};
  if (length == 0 || length >= temporary.size() ||
      BCryptGenRandom(nullptr, nonce.data(), static_cast<ULONG>(nonce.size()),
                      BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
    return {};
  }
  constexpr wchar_t kHex[] = L"0123456789abcdef";
  std::wstring suffix;
  for (const uint8_t value : nonce) {
    suffix.push_back(kHex[(value >> 4U) & 0x0FU]);
    suffix.push_back(kHex[value & 0x0FU]);
  }
  return std::filesystem::path(temporary.data()) /
         (L"WinDayFlow-AnalysisEvidence-" + suffix);
}

class ScopedTestRoot final {
 public:
  ScopedTestRoot() : path_(UniqueTestRoot()) {
    std::error_code error;
    std::filesystem::create_directories(path_, error);
  }
  ~ScopedTestRoot() {
    std::error_code ignored;
    std::filesystem::remove_all(path_, ignored);
  }
  const std::filesystem::path& path() const noexcept { return path_; }

 private:
  std::filesystem::path path_;
};

class MediaFoundationTestRuntime final {
 public:
  HRESULT Start() noexcept {
    HRESULT result =
        CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
    if (SUCCEEDED(result)) {
      uninitialize_com_ = true;
    } else if (result != RPC_E_CHANGED_MODE) {
      return result;
    }
    result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(result)) {
      media_foundation_started_ = true;
    }
    return result;
  }
  ~MediaFoundationTestRuntime() {
    if (media_foundation_started_) {
      static_cast<void>(MFShutdown());
    }
    if (uninitialize_com_) {
      CoUninitialize();
    }
  }

 private:
  bool uninitialize_com_ = false;
  bool media_foundation_started_ = false;
};

bool WriteBytes(const std::filesystem::path& path,
                std::span<const uint8_t> bytes) {
  std::ofstream output(path, std::ios::binary | std::ios::trunc);
  output.write(reinterpret_cast<const char*>(bytes.data()),
               static_cast<std::streamsize>(bytes.size()));
  return output.good();
}

std::vector<uint8_t> MakeFrame(uint8_t frame_index) {
  constexpr uint32_t kWidth = 64;
  constexpr uint32_t kHeight = 48;
  std::vector<uint8_t> pixels(kWidth * kHeight * 4U);
  for (uint32_t y = 0; y < kHeight; ++y) {
    for (uint32_t x = 0; x < kWidth; ++x) {
      const size_t offset = (static_cast<size_t>(y) * kWidth + x) * 4U;
      pixels[offset] = static_cast<uint8_t>(x + frame_index * 7U);
      pixels[offset + 1U] = static_cast<uint8_t>(y + frame_index * 11U);
      pixels[offset + 2U] = static_cast<uint8_t>(x + y + frame_index * 13U);
      pixels[offset + 3U] = 0xFFU;
    }
  }
  return pixels;
}

bool CreateRealChunk(const std::filesystem::path& root,
                     std::string_view chunk_id, std::vector<uint8_t>* video) {
  if (video == nullptr) {
    return false;
  }
  MfH264ChunkWriterConfig config;
  config.width = 64;
  config.height = 48;
  config.frame_rate_numerator = 10;
  config.frame_rate_denominator = 1;
  config.average_bitrate = 2'500'000;
  MfH264ChunkWriter writer;
  HRESULT result = writer.Begin(config);
  for (uint32_t index = 0; SUCCEEDED(result) && index < 10U; ++index) {
    const std::vector<uint8_t> frame = MakeFrame(static_cast<uint8_t>(index));
    result = writer.AddFrame(
        frame, static_cast<int64_t>(index) *
                   windayflow::capture::kMediaFoundationTicksPerSecond / 10);
  }
  if (SUCCEEDED(result)) {
    result = writer.Finalize(
        windayflow::capture::kMediaFoundationTicksPerSecond, video);
  }
  const std::filesystem::path directory =
      root / L"chunks" /
      std::wstring(chunk_id.begin(), chunk_id.end());
  std::error_code error;
  std::filesystem::create_directories(directory, error);
  constexpr std::array<uint8_t, 2> kManifest{'{', '}'};
  return SUCCEEDED(result) && !video->empty() && !error &&
         WriteBytes(directory / L"manifest.json", kManifest) &&
         WriteBytes(directory / L"capture.mp4", *video);
}

std::string Fingerprint(const std::filesystem::path& root,
                        std::string_view chunk_id, size_t video_bytes) {
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      value{};
  const auto result = windayflow::capture::ComputeCaptureChunkFingerprint(
      root.wstring(), chunk_id, video_bytes, &value);
  return result == windayflow::capture::CaptureChunkFingerprintResult::kOk
             ? std::string(
                   value.data(),
                   windayflow::capture::kCaptureChunkFingerprintHexLength)
             : std::string();
}

AnalysisEvidenceRequest Request(const std::filesystem::path& root,
                                std::string chunk_id, size_t video_bytes,
                                std::string fingerprint) {
  AnalysisEvidenceRequest request;
  request.data_root = root.wstring();
  request.canonical_chunk_id = std::move(chunk_id);
  request.expected_video_byte_count = video_bytes;
  request.expected_frame_count = 10;
  request.expected_video_width = 64;
  request.expected_video_height = 48;
  request.expected_duration_ms = 1'000;
  request.expected_source_fingerprint = std::move(fingerprint);
  return request;
}

bool TestRealMp4ExtractionAndIdempotentRead() {
  ScopedTestRoot root;
  std::vector<uint8_t> video;
  if (!CreateRealChunk(root.path(), "real", &video)) {
    return Expect(false, "real MP4 setup failed");
  }
  const std::string fingerprint =
      Fingerprint(root.path(), "real", video.size());
  const AnalysisEvidenceRequest request =
      Request(root.path(), "real", video.size(), fingerprint);
  std::string first;
  std::string second;
  const AnalysisEvidenceResult extraction =
      windayflow::capture::ExtractAnalysisEvidence(request, &first);
  if (!Expect(!fingerprint.empty(), "source fingerprint failed") ||
      !Expect(extraction == AnalysisEvidenceResult::kOk,
              "real MP4 extraction failed") ||
      !Expect(first.find("\"policyVersion\":\"evidence-v1\"") !=
                  std::string::npos,
              "evidence manifest omitted policy version") ||
      !Expect(first.find(fingerprint) != std::string::npos,
              "evidence manifest omitted source fingerprint") ||
      !Expect(windayflow::capture::ExtractAnalysisEvidence(request, &second) ==
                  AnalysisEvidenceResult::kOk,
              "identical evidence was not reusable") ||
      !Expect(first == second, "idempotent extraction changed the manifest")) {
    return false;
  }
  std::vector<uint8_t> jpeg;
  return Expect(windayflow::capture::ReadAnalysisEvidenceFrame(
                    root.path().wstring(), "real", fingerprint, 0, &jpeg) ==
                    AnalysisEvidenceResult::kOk,
                "root-bound evidence frame read failed") &&
         Expect(jpeg.size() >= 4U && jpeg[0] == 0xFFU && jpeg[1] == 0xD8U &&
                    jpeg[jpeg.size() - 2U] == 0xFFU &&
                    jpeg[jpeg.size() - 1U] == 0xD9U,
                "evidence frame is not a JPEG") &&
         Expect(jpeg.size() <=
                    windayflow::capture::kMaximumAnalysisEvidenceFrameBytes,
                "evidence JPEG exceeded its bound");
}

bool TestWrongFingerprintAndCorruptVideoFailClosed() {
  ScopedTestRoot root;
  std::vector<uint8_t> video;
  if (!CreateRealChunk(root.path(), "wrong-fingerprint", &video)) {
    return Expect(false, "wrong-fingerprint setup failed");
  }
  AnalysisEvidenceRequest wrong = Request(
      root.path(), "wrong-fingerprint", video.size(), std::string(64, 'A'));
  std::string manifest = "unchanged";
  if (!Expect(windayflow::capture::ExtractAnalysisEvidence(wrong, &manifest) ==
                  AnalysisEvidenceResult::kChangedDuringRead &&
                  manifest.empty(),
              "wrong source fingerprint did not fail closed")) {
    return false;
  }

  const std::filesystem::path corrupt_directory =
      root.path() / L"chunks" / L"corrupt";
  std::error_code error;
  std::filesystem::create_directories(corrupt_directory, error);
  constexpr std::array<uint8_t, 2> kManifest{'{', '}'};
  constexpr std::array<uint8_t, 8> kCorruptVideo{1, 2, 3, 4, 5, 6, 7, 8};
  if (error || !WriteBytes(corrupt_directory / L"manifest.json", kManifest) ||
      !WriteBytes(corrupt_directory / L"capture.mp4", kCorruptVideo)) {
    return Expect(false, "corrupt-video setup failed");
  }
  const std::string fingerprint =
      Fingerprint(root.path(), "corrupt", kCorruptVideo.size());
  AnalysisEvidenceRequest corrupt =
      Request(root.path(), "corrupt", kCorruptVideo.size(), fingerprint);
  return Expect(windayflow::capture::ExtractAnalysisEvidence(
                    corrupt, &manifest) ==
                    AnalysisEvidenceResult::kDecoderFailure &&
                    manifest.empty(),
                "corrupt MP4 did not fail at the decoder boundary");
}

bool TestPublishedCorruptionFailsClosed() {
  ScopedTestRoot root;
  std::vector<uint8_t> video;
  if (!CreateRealChunk(root.path(), "published-corrupt", &video)) {
    return Expect(false, "published-corrupt setup failed");
  }
  const std::string fingerprint =
      Fingerprint(root.path(), "published-corrupt", video.size());
  const AnalysisEvidenceRequest request =
      Request(root.path(), "published-corrupt", video.size(), fingerprint);
  std::string manifest;
  if (windayflow::capture::ExtractAnalysisEvidence(request, &manifest) !=
      AnalysisEvidenceResult::kOk) {
    return Expect(false, "published-corrupt initial extraction failed");
  }
  const std::filesystem::path frame =
      root.path() / L"evidence" / L"evidence-v1" / L"published-corrupt" /
      std::wstring(fingerprint.begin(), fingerprint.end()) / L"frame-0000.jpg";
  std::vector<uint8_t> original;
  if (windayflow::capture::ReadAnalysisEvidenceFrame(
          root.path().wstring(), "published-corrupt", fingerprint, 0,
          &original) != AnalysisEvidenceResult::kOk ||
      original.size() <= 15U || original[6] != 'J' || original[7] != 'F' ||
      original[8] != 'I' || original[9] != 'F') {
    return Expect(false, "published JPEG setup was not canonical JFIF");
  }
  original[15] ^= 1U;
  if (!WriteBytes(frame, original)) {
    return Expect(false, "published evidence corruption failed");
  }
  return Expect(windayflow::capture::ExtractAnalysisEvidence(
                    request, &manifest) == AnalysisEvidenceResult::kConflict &&
                    manifest.empty(),
                "corrupt published evidence was silently reused");
}

bool TestRequestBounds() {
  AnalysisEvidenceRequest request;
  request.data_root = L"C:\\data";
  request.canonical_chunk_id = "chunk";
  request.expected_video_byte_count = 1;
  request.expected_frame_count = 14'401;
  request.expected_video_width = 64;
  request.expected_video_height = 48;
  request.expected_duration_ms = 1'000;
  request.expected_source_fingerprint = std::string(64, 'A');
  std::string manifest;
  return Expect(windayflow::capture::ExtractAnalysisEvidence(
                    request, &manifest) ==
                    AnalysisEvidenceResult::kInvalidArgument,
                "oversized source frame count was accepted") &&
         Expect(!windayflow::capture::IsCanonicalSourceFingerprint(
                    std::string(64, 'a')),
                "lowercase source fingerprint was accepted");
}

}  // namespace

int main() {
  MediaFoundationTestRuntime runtime;
  if (FAILED(runtime.Start()) || !TestRealMp4ExtractionAndIdempotentRead() ||
      !TestWrongFingerprintAndCorruptVideoFailClosed() ||
      !TestPublishedCorruptionFailsClosed() || !TestRequestBounds()) {
    return 1;
  }
  std::cout << "analysis evidence extractor tests passed\n";
  return 0;
}

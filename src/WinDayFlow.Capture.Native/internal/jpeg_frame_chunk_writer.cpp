#include "jpeg_frame_chunk_writer.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "wic_jpeg_encoder.h"

namespace windayflow::capture {
namespace {

constexpr uint32_t kSignatureColumns = 64U;
constexpr uint32_t kSignatureRows = 36U;
constexpr size_t kSignatureChannels = 3U;
using FrameSignature = std::array<
    uint8_t, kSignatureColumns * kSignatureRows * kSignatureChannels>;

bool IsValidConfig(const JpegFrameChunkWriterConfig& config) noexcept {
  const uint64_t frame_bytes =
      static_cast<uint64_t>(config.width) * config.height * 4U;
  return config.width >= 2 && config.height >= 2 &&
         (config.width & 1U) == 0 && (config.height & 1U) == 0 &&
         frame_bytes <= std::numeric_limits<size_t>::max() &&
         config.quality > 0.0F && config.quality <= 1.0F &&
         config.maximum_frame_bytes >= 4U &&
         config.maximum_frame_bytes <= kMaximumChunkFrameFileBytes &&
         config.maximum_chunk_bytes >= config.maximum_frame_bytes &&
         config.maximum_chunk_bytes <= kMaximumChunkFrameBytes;
}

FrameSignature CreateSignature(std::span<const uint8_t> bgra, uint32_t width,
                               uint32_t height) noexcept {
  FrameSignature signature{};
  for (uint32_t row = 0; row < kSignatureRows; ++row) {
    const uint32_t y = std::min(
        height - 1U,
        static_cast<uint32_t>((static_cast<uint64_t>(row) * 2U + 1U) *
                              height / (kSignatureRows * 2U)));
    for (uint32_t column = 0; column < kSignatureColumns; ++column) {
      const uint32_t x = std::min(
          width - 1U,
          static_cast<uint32_t>((static_cast<uint64_t>(column) * 2U + 1U) *
                                width / (kSignatureColumns * 2U)));
      const size_t source = (static_cast<size_t>(y) * width + x) * 4U;
      const size_t destination =
          (static_cast<size_t>(row) * kSignatureColumns + column) *
          kSignatureChannels;
      signature[destination] = bgra[source];
      signature[destination + 1U] = bgra[source + 1U];
      signature[destination + 2U] = bgra[source + 2U];
    }
  }
  return signature;
}

bool IsNearDuplicate(const FrameSignature& previous,
                     const FrameSignature& current) noexcept {
  uint64_t total_difference = 0;
  uint32_t changed_samples = 0;
  uint8_t maximum_difference = 0;
  for (size_t index = 0; index < previous.size(); ++index) {
    const uint8_t difference = static_cast<uint8_t>(
        std::abs(static_cast<int>(previous[index]) -
                 static_cast<int>(current[index])));
    total_difference += difference;
    maximum_difference = std::max(maximum_difference, difference);
    if (difference > 12U) {
      ++changed_samples;
    }
  }
  return maximum_difference <= 40U &&
         static_cast<uint64_t>(changed_samples) * 50U <= previous.size() &&
         total_difference <= previous.size() * 3U;
}

bool ComputeSha256(std::span<const uint8_t> bytes, std::string* sha256) noexcept {
  if (sha256 == nullptr || bytes.empty() ||
      bytes.size() > static_cast<size_t>(std::numeric_limits<ULONG>::max())) {
    return false;
  }
  BCRYPT_ALG_HANDLE algorithm = nullptr;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr,
                                  0) != 0) {
    return false;
  }
  std::array<uint8_t, 32> digest{};
  const NTSTATUS status = BCryptHash(
      algorithm, nullptr, 0, const_cast<PUCHAR>(bytes.data()),
      static_cast<ULONG>(bytes.size()), digest.data(),
      static_cast<ULONG>(digest.size()));
  static_cast<void>(BCryptCloseAlgorithmProvider(algorithm, 0));
  if (status != 0) {
    return false;
  }
  constexpr char kHex[] = "0123456789ABCDEF";
  try {
    sha256->assign(digest.size() * 2U, '0');
    for (size_t index = 0; index < digest.size(); ++index) {
      (*sha256)[index * 2U] = kHex[digest[index] >> 4U];
      (*sha256)[index * 2U + 1U] = kHex[digest[index] & 0x0FU];
    }
    return true;
  } catch (...) {
    sha256->clear();
    return false;
  }
}

void SecureClear(std::vector<uint8_t>* bytes) noexcept {
  if (bytes != nullptr) {
    if (!bytes->empty()) {
      SecureZeroMemory(bytes->data(), bytes->size());
    }
    bytes->clear();
  }
}

struct EncodedFrame {
  uint64_t offset_milliseconds = 0;
  FrameSignature signature{};
  std::vector<uint8_t> jpeg;
  std::string sha256;
};

}  // namespace

class JpegFrameChunkWriter::Impl final {
 public:
  explicit Impl(std::wstring output_root) : store_(std::move(output_root)) {}
  ~Impl() { static_cast<void>(Reset()); }

  JpegFrameChunkWriterResult Begin(
      IWICImagingFactory* factory, std::string_view chunk_id,
      const JpegFrameChunkWriterConfig& config) noexcept {
    if (active_ || factory == nullptr || !IsValidConfig(config) ||
        !IsValidChunkArtifactId(chunk_id)) {
      return JpegFrameChunkWriterResult::kInvalidArgument;
    }
    const AtomicChunkStoreResult result = store_.Begin(chunk_id, &writer_);
    if (result != AtomicChunkStoreResult::kOk) {
      return JpegFrameChunkWriterResult::kStorageFailure;
    }
    factory_ = factory;
    config_ = config;
    active_ = true;
    return JpegFrameChunkWriterResult::kOk;
  }

  JpegFrameChunkWriterResult AddFrame(
      std::span<const uint8_t> top_down_bgra,
      uint64_t offset_milliseconds) noexcept {
    const uint64_t required =
        static_cast<uint64_t>(config_.width) * config_.height * 4U;
    if (!active_ || factory_ == nullptr || top_down_bgra.size() != required ||
        (captured_frame_count_ != 0 &&
         offset_milliseconds <= last_capture_offset_)) {
      return JpegFrameChunkWriterResult::kInvalidArgument;
    }

    EncodedFrame current;
    current.offset_milliseconds = offset_milliseconds;
    current.signature =
        CreateSignature(top_down_bgra, config_.width, config_.height);
    const HRESULT encoded = EncodeBgraFrameAsJpeg(
        factory_, top_down_bgra, config_.width, config_.height,
        config_.quality, config_.maximum_frame_bytes, &current.jpeg);
    if (FAILED(encoded) || !ComputeSha256(current.jpeg, &current.sha256)) {
      SecureClear(&current.jpeg);
      return JpegFrameChunkWriterResult::kEncoderFailure;
    }

    ++captured_frame_count_;
    last_capture_offset_ = offset_milliseconds;
    if (!last_retained_.has_value()) {
      return AppendRetained(std::move(current));
    }

    const bool duplicate =
        IsNearDuplicate(last_retained_->signature, current.signature) ||
        last_retained_->sha256 == current.sha256;
    if (duplicate) {
      if (pending_final_.has_value()) {
        SecureClear(&pending_final_->jpeg);
      }
      pending_final_ = std::move(current);
      return JpegFrameChunkWriterResult::kOk;
    }

    if (pending_final_.has_value()) {
      SecureClear(&pending_final_->jpeg);
      pending_final_.reset();
    }
    return AppendRetained(std::move(current));
  }

  JpegFrameChunkWriterResult Finalize(
      ChunkManifest* manifest,
      AtomicChunkPublication* publication) noexcept {
    if (!active_ || manifest == nullptr || publication == nullptr ||
        manifest->chunk_id != writer_.chunk_id() ||
        manifest->captured_frame_count != captured_frame_count_ ||
        manifest->frame_width != config_.width ||
        manifest->frame_height != config_.height || !manifest->frames.empty() ||
        manifest->frame_byte_count != 0) {
      return JpegFrameChunkWriterResult::kInvalidArgument;
    }
    if (pending_final_.has_value()) {
      const JpegFrameChunkWriterResult appended =
          AppendRetained(std::move(*pending_final_));
      pending_final_.reset();
      if (appended != JpegFrameChunkWriterResult::kOk) {
        return appended;
      }
    }
    manifest->frames = records_;
    manifest->frame_byte_count = total_frame_bytes_;
    const AtomicChunkStoreResult prepared = writer_.Prepare(*manifest, publication);
    if (prepared != AtomicChunkStoreResult::kOk) {
      return JpegFrameChunkWriterResult::kStorageFailure;
    }
    ClearStateAfterPrepare();
    return JpegFrameChunkWriterResult::kOk;
  }

  JpegFrameChunkWriterResult Reset() noexcept {
    if (pending_final_.has_value()) {
      SecureClear(&pending_final_->jpeg);
      pending_final_.reset();
    }
    const AtomicChunkStoreResult result = writer_.Reset();
    ClearStateAfterPrepare();
    return result == AtomicChunkStoreResult::kOk
               ? JpegFrameChunkWriterResult::kOk
               : JpegFrameChunkWriterResult::kStorageFailure;
  }

 private:
  JpegFrameChunkWriterResult AppendRetained(EncodedFrame frame) noexcept {
    if (frame.jpeg.size() > config_.maximum_chunk_bytes -
                                std::min(config_.maximum_chunk_bytes,
                                         total_frame_bytes_)) {
      SecureClear(&frame.jpeg);
      return JpegFrameChunkWriterResult::kStorageFailure;
    }
    const ChunkFrameManifest record{
        static_cast<uint32_t>(records_.size()), frame.offset_milliseconds,
        static_cast<uint32_t>(frame.jpeg.size()), frame.sha256};
    const AtomicChunkStoreResult stored = writer_.AppendFrame(record, frame.jpeg);
    if (stored != AtomicChunkStoreResult::kOk) {
      SecureClear(&frame.jpeg);
      return JpegFrameChunkWriterResult::kStorageFailure;
    }
    total_frame_bytes_ += frame.jpeg.size();
    records_.push_back(record);
    SecureClear(&frame.jpeg);
    last_retained_ = std::move(frame);
    return JpegFrameChunkWriterResult::kOk;
  }

  void ClearStateAfterPrepare() noexcept {
    if (last_retained_.has_value()) {
      SecureClear(&last_retained_->jpeg);
      last_retained_.reset();
    }
    records_.clear();
    captured_frame_count_ = 0;
    total_frame_bytes_ = 0;
    last_capture_offset_ = 0;
    factory_ = nullptr;
    config_ = {};
    active_ = false;
  }

  AtomicChunkStore store_;
  AtomicChunkWriter writer_;
  IWICImagingFactory* factory_ = nullptr;
  JpegFrameChunkWriterConfig config_;
  std::optional<EncodedFrame> last_retained_;
  std::optional<EncodedFrame> pending_final_;
  std::vector<ChunkFrameManifest> records_;
  uint32_t captured_frame_count_ = 0;
  size_t total_frame_bytes_ = 0;
  uint64_t last_capture_offset_ = 0;
  bool active_ = false;
};

JpegFrameChunkWriter::JpegFrameChunkWriter(std::wstring output_root)
    : impl_(std::make_unique<Impl>(std::move(output_root))) {}
JpegFrameChunkWriter::~JpegFrameChunkWriter() = default;
JpegFrameChunkWriterResult JpegFrameChunkWriter::Begin(
    IWICImagingFactory* factory, std::string_view chunk_id,
    const JpegFrameChunkWriterConfig& config) noexcept {
  return impl_->Begin(factory, chunk_id, config);
}
JpegFrameChunkWriterResult JpegFrameChunkWriter::AddFrame(
    std::span<const uint8_t> top_down_bgra,
    uint64_t offset_milliseconds) noexcept {
  return impl_->AddFrame(top_down_bgra, offset_milliseconds);
}
JpegFrameChunkWriterResult JpegFrameChunkWriter::Finalize(
    ChunkManifest* manifest,
    AtomicChunkPublication* publication) noexcept {
  return impl_->Finalize(manifest, publication);
}
JpegFrameChunkWriterResult JpegFrameChunkWriter::Reset() noexcept {
  return impl_->Reset();
}

}  // namespace windayflow::capture

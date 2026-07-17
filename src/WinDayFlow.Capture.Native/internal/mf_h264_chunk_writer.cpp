// Heavily derived from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Derived and
// modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#include "mf_h264_chunk_writer.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <objidl.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <utility>

#include "capture_policy.h"
#include "pixel_buffer.h"

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

constexpr uint32_t kMaximumVideoWidth = 7'680;
constexpr uint32_t kMaximumVideoHeight = 4'320;
constexpr size_t kCopyBufferBytes = 64U * 1024U;

class BoundedMemoryStream final
    : public RuntimeClass<RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                          IStream> {
 public:
  HRESULT Initialize(size_t maximum_bytes) noexcept {
    if (maximum_bytes == 0 || maximum_bytes > kMaximumH264ChunkBytes) {
      return E_INVALIDARG;
    }
    maximum_bytes_ = maximum_bytes;
    return S_OK;
  }

  IFACEMETHODIMP Read(void* destination, ULONG requested,
                      ULONG* bytes_read) noexcept override {
    if (bytes_read != nullptr) {
      *bytes_read = 0;
    }
    if (destination == nullptr && requested != 0) {
      return STG_E_INVALIDPOINTER;
    }

    std::lock_guard lock(mutex_);
    if (FAILED(failure_)) {
      return failure_;
    }
    const size_t available =
        position_ < data_.size() ? data_.size() - position_ : 0;
    const size_t count = std::min<size_t>(available, requested);
    if (count != 0) {
      std::memcpy(destination, data_.data() + position_, count);
      position_ += count;
    }
    if (bytes_read != nullptr) {
      *bytes_read = static_cast<ULONG>(count);
    }
    return count == requested ? S_OK : S_FALSE;
  }

  IFACEMETHODIMP Write(const void* source, ULONG requested,
                       ULONG* bytes_written) noexcept override {
    if (bytes_written != nullptr) {
      *bytes_written = 0;
    }
    if (source == nullptr && requested != 0) {
      return STG_E_INVALIDPOINTER;
    }

    std::lock_guard lock(mutex_);
    if (FAILED(failure_)) {
      return failure_;
    }
    const size_t count = requested;
    if (position_ > maximum_bytes_ || count > maximum_bytes_ - position_) {
      return FailAndDiscardLocked(STG_E_MEDIUMFULL);
    }
    const size_t end = position_ + count;
    if (end > data_.size()) {
      try {
        data_.resize(end, 0);
      } catch (const std::bad_alloc&) {
        return FailAndDiscardLocked(E_OUTOFMEMORY);
      } catch (...) {
        return FailAndDiscardLocked(E_FAIL);
      }
    }
    if (count != 0) {
      std::memcpy(data_.data() + position_, source, count);
      position_ = end;
    }
    if (bytes_written != nullptr) {
      *bytes_written = requested;
    }
    return S_OK;
  }

  IFACEMETHODIMP Seek(LARGE_INTEGER move, DWORD origin,
                      ULARGE_INTEGER* new_position) noexcept override {
    std::lock_guard lock(mutex_);
    if (FAILED(failure_)) {
      return failure_;
    }

    int64_t base = 0;
    switch (origin) {
      case STREAM_SEEK_SET:
        break;
      case STREAM_SEEK_CUR:
        base = static_cast<int64_t>(position_);
        break;
      case STREAM_SEEK_END:
        base = static_cast<int64_t>(data_.size());
        break;
      default:
        return STG_E_INVALIDFUNCTION;
    }

    if ((move.QuadPart > 0 &&
         base > std::numeric_limits<int64_t>::max() - move.QuadPart) ||
        (move.QuadPart < 0 &&
         base < std::numeric_limits<int64_t>::min() - move.QuadPart)) {
      return STG_E_INVALIDFUNCTION;
    }
    const int64_t requested = base + move.QuadPart;
    if (requested < 0) {
      return STG_E_INVALIDFUNCTION;
    }
    if (static_cast<uint64_t>(requested) > maximum_bytes_) {
      return FailAndDiscardLocked(STG_E_MEDIUMFULL);
    }
    position_ = static_cast<size_t>(requested);
    if (new_position != nullptr) {
      new_position->QuadPart = position_;
    }
    return S_OK;
  }

  IFACEMETHODIMP SetSize(ULARGE_INTEGER new_size) noexcept override {
    std::lock_guard lock(mutex_);
    if (FAILED(failure_)) {
      return failure_;
    }
    if (new_size.QuadPart > maximum_bytes_) {
      return FailAndDiscardLocked(STG_E_MEDIUMFULL);
    }
    try {
      data_.resize(static_cast<size_t>(new_size.QuadPart), 0);
    } catch (const std::bad_alloc&) {
      return FailAndDiscardLocked(E_OUTOFMEMORY);
    } catch (...) {
      return FailAndDiscardLocked(E_FAIL);
    }
    return S_OK;
  }

  IFACEMETHODIMP CopyTo(IStream* destination, ULARGE_INTEGER requested,
                        ULARGE_INTEGER* bytes_read,
                        ULARGE_INTEGER* bytes_written) noexcept override {
    if (bytes_read != nullptr) {
      bytes_read->QuadPart = 0;
    }
    if (bytes_written != nullptr) {
      bytes_written->QuadPart = 0;
    }
    if (destination == nullptr) {
      return STG_E_INVALIDPOINTER;
    }

    std::array<uint8_t, kCopyBufferBytes> buffer{};
    uint64_t remaining = requested.QuadPart;
    uint64_t total_read = 0;
    uint64_t total_written = 0;
    while (remaining != 0) {
      const ULONG next = static_cast<ULONG>(
          std::min<uint64_t>(remaining, static_cast<uint64_t>(buffer.size())));
      ULONG read = 0;
      const HRESULT read_result = Read(buffer.data(), next, &read);
      if (FAILED(read_result)) {
        return read_result;
      }
      total_read += read;
      remaining -= read;
      if (read == 0) {
        break;
      }

      ULONG written = 0;
      const HRESULT write_result =
          destination->Write(buffer.data(), read, &written);
      total_written += written;
      if (FAILED(write_result)) {
        if (bytes_read != nullptr) {
          bytes_read->QuadPart = total_read;
        }
        if (bytes_written != nullptr) {
          bytes_written->QuadPart = total_written;
        }
        return write_result;
      }
      if (written != read) {
        break;
      }
    }
    if (bytes_read != nullptr) {
      bytes_read->QuadPart = total_read;
    }
    if (bytes_written != nullptr) {
      bytes_written->QuadPart = total_written;
    }
    return remaining == 0 ? S_OK : S_FALSE;
  }

  IFACEMETHODIMP Commit(DWORD) noexcept override {
    std::lock_guard lock(mutex_);
    return failure_;
  }

  IFACEMETHODIMP Revert() noexcept override { return STG_E_REVERTED; }

  IFACEMETHODIMP LockRegion(ULARGE_INTEGER, ULARGE_INTEGER,
                            DWORD) noexcept override {
    return STG_E_INVALIDFUNCTION;
  }

  IFACEMETHODIMP UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER,
                              DWORD) noexcept override {
    return STG_E_INVALIDFUNCTION;
  }

  IFACEMETHODIMP Stat(STATSTG* statistics, DWORD) noexcept override {
    if (statistics == nullptr) {
      return STG_E_INVALIDPOINTER;
    }
    std::lock_guard lock(mutex_);
    std::memset(statistics, 0, sizeof(*statistics));
    statistics->type = STGTY_STREAM;
    statistics->cbSize.QuadPart = data_.size();
    statistics->grfMode = STGM_READWRITE | STGM_SHARE_EXCLUSIVE;
    return S_OK;
  }

  IFACEMETHODIMP Clone(IStream** clone) noexcept override {
    if (clone != nullptr) {
      *clone = nullptr;
    }
    return E_NOTIMPL;
  }

  HRESULT failure() const noexcept {
    std::lock_guard lock(mutex_);
    return failure_;
  }

  void Abort(HRESULT failure) noexcept {
    std::lock_guard lock(mutex_);
    static_cast<void>(FailAndDiscardLocked(
        FAILED(failure) ? failure : static_cast<HRESULT>(E_FAIL)));
  }

  HRESULT TakeData(std::vector<uint8_t>* destination) noexcept {
    if (destination == nullptr) {
      return E_POINTER;
    }
    destination->clear();
    std::lock_guard lock(mutex_);
    if (FAILED(failure_)) {
      return failure_;
    }
    destination->swap(data_);
    position_ = 0;
    return S_OK;
  }

 private:
  HRESULT FailAndDiscardLocked(HRESULT failure) noexcept {
    if (SUCCEEDED(failure_)) {
      failure_ = FAILED(failure) ? failure : static_cast<HRESULT>(E_FAIL);
    }
    if (!data_.empty()) {
      SecureZeroMemory(data_.data(), data_.size());
    }
    std::vector<uint8_t>().swap(data_);
    position_ = 0;
    return failure_;
  }

  mutable std::mutex mutex_;
  std::vector<uint8_t> data_;
  size_t maximum_bytes_ = 0;
  size_t position_ = 0;
  HRESULT failure_ = S_OK;
};

bool TryCalculateFrameBytes(const MfH264ChunkWriterConfig& config,
                            size_t* frame_bytes) noexcept {
  if (frame_bytes == nullptr || config.width == 0 || config.height == 0 ||
      config.width > kMaximumVideoWidth ||
      config.height > kMaximumVideoHeight || (config.width & 1U) != 0 ||
      (config.height & 1U) != 0 || config.frame_rate_numerator == 0 ||
      config.frame_rate_denominator == 0 || config.average_bitrate == 0 ||
      config.max_output_bytes == 0 ||
      config.max_output_bytes > kMaximumH264ChunkBytes) {
    return false;
  }
  const uint64_t bytes = static_cast<uint64_t>(config.width) *
                         static_cast<uint64_t>(config.height) * 4U;
  if (bytes > std::numeric_limits<DWORD>::max()) {
    return false;
  }
  *frame_bytes = static_cast<size_t>(bytes);
  return true;
}

}  // namespace

class MfH264ChunkWriter::Impl final {
 public:
  ~Impl() { static_cast<void>(Reset()); }

  HRESULT Begin(const MfH264ChunkWriterConfig& config) noexcept {
    if (state_ != MfH264ChunkWriterState::kIdle) {
      return SetResult(MF_E_INVALIDREQUEST);
    }

    size_t frame_bytes = 0;
    if (!TryCalculateFrameBytes(config, &frame_bytes)) {
      return SetResult(E_INVALIDARG);
    }

    owner_thread_id_ = GetCurrentThreadId();
    HRESULT result =
        CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
    if (SUCCEEDED(result)) {
      uninitialize_com_ = true;
    } else if (result != RPC_E_CHANGED_MODE) {
      owner_thread_id_ = 0;
      state_ = MfH264ChunkWriterState::kFailed;
      return SetResult(result);
    }

    result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(result)) {
      CleanupRuntime();
      state_ = MfH264ChunkWriterState::kFailed;
      return SetResult(result);
    }
    media_foundation_started_ = true;

    config_ = config;
    frame_bytes_ = frame_bytes;
    result = CreateSinkWriter();
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }
    state_ = MfH264ChunkWriterState::kWriting;
    frame_count_ = 0;
    return SetResult(S_OK);
  }

  HRESULT AddFrame(std::span<const uint8_t> pixels,
                   int64_t timestamp_ticks) noexcept {
    const HRESULT thread_result = CheckOwnerThread();
    if (FAILED(thread_result)) {
      return SetResult(thread_result);
    }
    if (state_ != MfH264ChunkWriterState::kWriting) {
      return SetResult(MF_E_INVALIDREQUEST);
    }
    if (pixels.size() != frame_bytes_ ||
        (pixels.empty() ? nullptr : pixels.data()) == nullptr) {
      return SetResult(E_INVALIDARG);
    }

    ComPtr<IMFMediaBuffer> buffer;
    HRESULT result = MFCreateMemoryBuffer(static_cast<DWORD>(frame_bytes_),
                                          buffer.GetAddressOf());
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }

    BYTE* destination = nullptr;
    DWORD maximum_length = 0;
    result = buffer->Lock(&destination, &maximum_length, nullptr);
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }
    const bool copied =
        CopyTopDownBgraRows(pixels.data(), pixels.size(), config_.width,
                            config_.height, destination, maximum_length);
    const HRESULT unlock_result = buffer->Unlock();
    if (!copied) {
      return SetResult(E_INVALIDARG);
    }
    if (FAILED(unlock_result)) {
      return TransitionToFailed(unlock_result);
    }
    result = buffer->SetCurrentLength(static_cast<DWORD>(frame_bytes_));
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }

    ComPtr<IMFSample> sample;
    result = MFCreateSample(sample.GetAddressOf());
    if (SUCCEEDED(result)) {
      result = sample->AddBuffer(buffer.Get());
    }
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }

    int64_t normalized_timestamp =
        CalculateMediaSampleTiming(timestamp_ticks, timestamp_ticks)
            .timestamp_ticks;
    if (pending_sample_ != nullptr) {
      int64_t previous_end = 0;
      result = WritePendingFrame(normalized_timestamp, &previous_end);
      if (FAILED(result)) {
        return TransitionToFailed(result);
      }
      normalized_timestamp = previous_end;
      if (normalized_timestamp == std::numeric_limits<int64_t>::max()) {
        return TransitionToFailed(
            HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
      }
    }

    pending_sample_ = std::move(sample);
    pending_timestamp_ticks_ = normalized_timestamp;
    if (frame_count_ < std::numeric_limits<uint32_t>::max()) {
      ++frame_count_;
    }
    return SetResult(S_OK);
  }

  HRESULT Finalize(int64_t end_timestamp_ticks,
                   std::vector<uint8_t>* output) noexcept {
    if (output == nullptr) {
      return SetResult(E_POINTER);
    }
    output->clear();
    const HRESULT thread_result = CheckOwnerThread();
    if (FAILED(thread_result)) {
      return SetResult(thread_result);
    }
    if (state_ != MfH264ChunkWriterState::kWriting) {
      return SetResult(MF_E_INVALIDREQUEST);
    }
    if (pending_sample_ == nullptr || frame_count_ == 0) {
      return SetResult(MF_E_INVALIDREQUEST);
    }

    int64_t actual_end = 0;
    HRESULT result = WritePendingFrame(end_timestamp_ticks, &actual_end);
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }
    result = sink_writer_->Finalize();
    if (SUCCEEDED(result) && bounded_stream_ != nullptr) {
      const HRESULT stream_failure = bounded_stream_->failure();
      if (FAILED(stream_failure)) {
        result = stream_failure;
      }
    }
    if (FAILED(result)) {
      return TransitionToFailed(result);
    }

    sink_writer_.Reset();
    byte_stream_.Reset();
    result = bounded_stream_->TakeData(output);
    bounded_stream_.Reset();
    if (FAILED(result) || output->empty() ||
        output->size() > config_.max_output_bytes) {
      output->clear();
      return TransitionToFailed(FAILED(result) ? result : E_FAIL);
    }

    CleanupRuntime();
    state_ = MfH264ChunkWriterState::kFinalized;
    return SetResult(S_OK);
  }

  HRESULT Reset() noexcept {
    const HRESULT thread_result = CheckOwnerThread();
    if (FAILED(thread_result)) {
      return SetResult(thread_result);
    }
    if (bounded_stream_ != nullptr) {
      bounded_stream_->Abort(E_ABORT);
    }
    pending_sample_.Reset();
    sink_writer_.Reset();
    byte_stream_.Reset();
    bounded_stream_.Reset();
    CleanupRuntime();
    config_ = {};
    frame_bytes_ = 0;
    pending_timestamp_ticks_ = 0;
    frame_count_ = 0;
    state_ = MfH264ChunkWriterState::kIdle;
    return SetResult(S_OK);
  }

  MfH264ChunkWriterState state() const noexcept { return state_; }
  HRESULT last_result() const noexcept { return last_result_; }
  uint32_t frame_count() const noexcept { return frame_count_; }

 private:
  HRESULT CreateSinkWriter() noexcept {
    bounded_stream_ = Make<BoundedMemoryStream>();
    if (bounded_stream_ == nullptr) {
      return E_OUTOFMEMORY;
    }
    HRESULT result = bounded_stream_->Initialize(config_.max_output_bytes);
    if (FAILED(result)) {
      return result;
    }
    result = MFCreateMFByteStreamOnStream(bounded_stream_.Get(),
                                          byte_stream_.GetAddressOf());
    if (FAILED(result)) {
      return result;
    }

    ComPtr<IMFAttributes> attributes;
    result = MFCreateAttributes(attributes.GetAddressOf(), 3);
    if (SUCCEEDED(result)) {
      result =
          attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    }
    if (SUCCEEDED(result)) {
      result = attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    }
    if (SUCCEEDED(result)) {
      result = attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE,
                                   MFTranscodeContainerType_MPEG4);
    }
    if (SUCCEEDED(result)) {
      result = MFCreateSinkWriterFromURL(nullptr, byte_stream_.Get(),
                                         attributes.Get(),
                                         sink_writer_.GetAddressOf());
    }
    if (FAILED(result)) {
      return result;
    }

    ComPtr<IMFMediaType> output_type;
    result = MFCreateMediaType(output_type.GetAddressOf());
    if (SUCCEEDED(result)) {
      result = output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    }
    if (SUCCEEDED(result)) {
      result = output_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    }
    if (SUCCEEDED(result)) {
      result =
          output_type->SetUINT32(MF_MT_AVG_BITRATE, config_.average_bitrate);
    }
    if (SUCCEEDED(result)) {
      result = output_type->SetUINT32(MF_MT_INTERLACE_MODE,
                                      MFVideoInterlace_Progressive);
    }
    if (SUCCEEDED(result)) {
      result = MFSetAttributeSize(output_type.Get(), MF_MT_FRAME_SIZE,
                                  config_.width, config_.height);
    }
    if (SUCCEEDED(result)) {
      result = MFSetAttributeRatio(output_type.Get(), MF_MT_FRAME_RATE,
                                   config_.frame_rate_numerator,
                                   config_.frame_rate_denominator);
    }
    if (SUCCEEDED(result)) {
      result = MFSetAttributeRatio(output_type.Get(), MF_MT_PIXEL_ASPECT_RATIO,
                                   1, 1);
    }
    if (SUCCEEDED(result)) {
      result = sink_writer_->AddStream(output_type.Get(), &stream_index_);
    }
    if (FAILED(result)) {
      return result;
    }

    ComPtr<IMFMediaType> input_type;
    result = MFCreateMediaType(input_type.GetAddressOf());
    if (SUCCEEDED(result)) {
      result = input_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    }
    if (SUCCEEDED(result)) {
      result = input_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    }
    if (SUCCEEDED(result)) {
      result = input_type->SetUINT32(MF_MT_INTERLACE_MODE,
                                     MFVideoInterlace_Progressive);
    }
    if (SUCCEEDED(result)) {
      result = MFSetAttributeSize(input_type.Get(), MF_MT_FRAME_SIZE,
                                  config_.width, config_.height);
    }
    if (SUCCEEDED(result)) {
      result = MFSetAttributeRatio(input_type.Get(), MF_MT_FRAME_RATE,
                                   config_.frame_rate_numerator,
                                   config_.frame_rate_denominator);
    }
    if (SUCCEEDED(result)) {
      result =
          MFSetAttributeRatio(input_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    }
    if (SUCCEEDED(result)) {
      result = input_type->SetUINT32(MF_MT_DEFAULT_STRIDE, config_.width * 4U);
    }
    if (SUCCEEDED(result)) {
      result = input_type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
    }
    if (SUCCEEDED(result)) {
      result = input_type->SetUINT32(MF_MT_SAMPLE_SIZE,
                                     static_cast<UINT32>(frame_bytes_));
    }
    if (SUCCEEDED(result)) {
      result = sink_writer_->SetInputMediaType(stream_index_, input_type.Get(),
                                               nullptr);
    }
    if (SUCCEEDED(result)) {
      result = sink_writer_->BeginWriting();
    }
    if (SUCCEEDED(result)) {
      const HRESULT stream_failure = bounded_stream_->failure();
      if (FAILED(stream_failure)) {
        result = stream_failure;
      }
    }
    return result;
  }

  HRESULT WritePendingFrame(int64_t end_timestamp_ticks,
                            int64_t* actual_end_ticks) noexcept {
    if (sink_writer_ == nullptr || pending_sample_ == nullptr ||
        actual_end_ticks == nullptr) {
      return E_UNEXPECTED;
    }
    const MediaSampleTiming timing = CalculateMediaSampleTiming(
        pending_timestamp_ticks_, end_timestamp_ticks);
    HRESULT result = pending_sample_->SetSampleTime(timing.timestamp_ticks);
    if (SUCCEEDED(result)) {
      result = pending_sample_->SetSampleDuration(timing.duration_ticks);
    }
    if (SUCCEEDED(result)) {
      result = sink_writer_->WriteSample(stream_index_, pending_sample_.Get());
    }
    if (SUCCEEDED(result) && bounded_stream_ != nullptr) {
      const HRESULT stream_failure = bounded_stream_->failure();
      if (FAILED(stream_failure)) {
        result = stream_failure;
      }
    }
    if (SUCCEEDED(result)) {
      pending_sample_.Reset();
      pending_timestamp_ticks_ = 0;
      *actual_end_ticks = timing.end_ticks;
    }
    return result;
  }

  HRESULT TransitionToFailed(HRESULT failure) noexcept {
    const HRESULT actual_failure =
        FAILED(failure) ? failure : static_cast<HRESULT>(E_FAIL);
    if (bounded_stream_ != nullptr) {
      bounded_stream_->Abort(actual_failure);
    }
    pending_sample_.Reset();
    sink_writer_.Reset();
    byte_stream_.Reset();
    bounded_stream_.Reset();
    CleanupRuntime();
    state_ = MfH264ChunkWriterState::kFailed;
    return SetResult(actual_failure);
  }

  HRESULT CheckOwnerThread() const noexcept {
    return owner_thread_id_ == 0 || owner_thread_id_ == GetCurrentThreadId()
               ? S_OK
               : RPC_E_WRONG_THREAD;
  }

  void CleanupRuntime() noexcept {
    if (media_foundation_started_) {
      static_cast<void>(MFShutdown());
      media_foundation_started_ = false;
    }
    if (uninitialize_com_) {
      CoUninitialize();
      uninitialize_com_ = false;
    }
    owner_thread_id_ = 0;
  }

  HRESULT SetResult(HRESULT result) noexcept {
    last_result_ = result;
    return result;
  }

  MfH264ChunkWriterConfig config_;
  MfH264ChunkWriterState state_ = MfH264ChunkWriterState::kIdle;
  HRESULT last_result_ = S_OK;
  DWORD owner_thread_id_ = 0;
  bool uninitialize_com_ = false;
  bool media_foundation_started_ = false;
  size_t frame_bytes_ = 0;
  DWORD stream_index_ = 0;
  uint32_t frame_count_ = 0;
  int64_t pending_timestamp_ticks_ = 0;
  ComPtr<BoundedMemoryStream> bounded_stream_;
  ComPtr<IMFByteStream> byte_stream_;
  ComPtr<IMFSinkWriter> sink_writer_;
  ComPtr<IMFSample> pending_sample_;
};

MfH264ChunkWriter::MfH264ChunkWriter() : impl_(std::make_unique<Impl>()) {}

MfH264ChunkWriter::~MfH264ChunkWriter() = default;

HRESULT
MfH264ChunkWriter::Begin(const MfH264ChunkWriterConfig& config) noexcept {
  return impl_->Begin(config);
}

HRESULT MfH264ChunkWriter::AddFrame(std::span<const uint8_t> top_down_bgra,
                                    int64_t timestamp_ticks) noexcept {
  return impl_->AddFrame(top_down_bgra, timestamp_ticks);
}

HRESULT MfH264ChunkWriter::Finalize(int64_t end_timestamp_ticks,
                                    std::vector<uint8_t>* output_mp4) noexcept {
  return impl_->Finalize(end_timestamp_ticks, output_mp4);
}

HRESULT MfH264ChunkWriter::Reset() noexcept { return impl_->Reset(); }

MfH264ChunkWriterState MfH264ChunkWriter::state() const noexcept {
  return impl_->state();
}

HRESULT MfH264ChunkWriter::last_result() const noexcept {
  return impl_->last_result();
}

uint32_t MfH264ChunkWriter::frame_count() const noexcept {
  return impl_->frame_count();
}

}  // namespace windayflow::capture

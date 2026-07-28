#include "windows_capture_worker_backend.h"

#include <Windows.h>
#include <bcrypt.h>
#include <wrl/client.h>

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "atomic_chunk_store.h"
#include "dxgi_desktop_frame_source.h"
#include "windows_capture_target_observer.h"
#include "windows_graphics_capture_frame_source.h"

namespace windayflow::capture {
namespace {

CaptureWorkerBackendResult MapDxgiResult(
    DxgiDesktopFrameResult result) noexcept {
  switch (result) {
    case DxgiDesktopFrameResult::kOk:
      return CaptureWorkerBackendResult::kOk;
    case DxgiDesktopFrameResult::kTimeout:
      return CaptureWorkerBackendResult::kTimeout;
    case DxgiDesktopFrameResult::kOutputUnavailable:
    case DxgiDesktopFrameResult::kTopologyChanged:
    case DxgiDesktopFrameResult::kAccessLost:
      return CaptureWorkerBackendResult::kRebuildRequired;
    case DxgiDesktopFrameResult::kAccessDenied:
    case DxgiDesktopFrameResult::kInvalidArgument:
    case DxgiDesktopFrameResult::kUnsupportedFormat:
      return CaptureWorkerBackendResult::kInvalidFrame;
    case DxgiDesktopFrameResult::kDeviceFailure:
    case DxgiDesktopFrameResult::kCopyFailure:
    default:
      return CaptureWorkerBackendResult::kDeviceUnavailable;
  }
}

CaptureWorkerBackendResult MapAtomicStoreResult(
    AtomicChunkStoreResult result) noexcept {
  return result == AtomicChunkStoreResult::kOk
             ? CaptureWorkerBackendResult::kOk
             : CaptureWorkerBackendResult::kStorageFailure;
}

class AtomicPublicationAdapter final : public CaptureWorkerPublication {
 public:
  AtomicPublicationAdapter() = default;

  AtomicChunkPublication* mutable_publication() noexcept {
    return &publication_;
  }

  bool committed() const noexcept override { return publication_.committed(); }

  const std::string& artifact_identifier() const noexcept override {
    return publication_.artifact_identifier();
  }

  CaptureWorkerBackendResult Commit() noexcept override {
    return MapAtomicStoreResult(publication_.Commit());
  }

  void Acknowledge() noexcept override { publication_.Acknowledge(); }

  CaptureWorkerBackendResult Rollback() noexcept override {
    return MapAtomicStoreResult(publication_.Rollback());
  }

 private:
  AtomicChunkPublication publication_;
};

class WindowsCaptureWorkerBackend final : public CaptureWorkerBackend {
 public:
  explicit WindowsCaptureWorkerBackend(std::wstring output_root)
      : store_(std::move(output_root)) {}

  ~WindowsCaptureWorkerBackend() override {
    if (owner_thread_id_ == GetCurrentThreadId()) {
      ShutdownThread();
    }
  }

  std::optional<CaptureTargetIdentity> ObserveTarget(
      const CaptureTargetIdentity& expected) noexcept override {
    return ObserveWindowsCaptureAuthorization(expected);
  }

  CaptureWorkerBackendResult InitializeAcquisition(
      const CaptureTargetIdentity& target) noexcept override {
    if (!EnsureThreadRuntime()) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    ResetAcquisition();
    const DxgiDesktopFrameResult dxgi_result =
        frame_source_.Initialize(target);
    if (dxgi_result == DxgiDesktopFrameResult::kOk) {
      frame_source_kind_ = FrameSourceKind::kDxgi;
      return CaptureWorkerBackendResult::kOk;
    }
    if (!ShouldFallbackToWindowsGraphicsCapture(dxgi_result)) {
      return MapDxgiResult(dxgi_result);
    }

    const DxgiDesktopFrameResult graphics_capture_result =
        graphics_capture_frame_source_.Initialize(target);
    if (graphics_capture_result == DxgiDesktopFrameResult::kOk) {
      frame_source_kind_ = FrameSourceKind::kWindowsGraphicsCapture;
    }
    return MapDxgiResult(graphics_capture_result);
  }

  CaptureWorkerBackendResult AcquireFrame(uint32_t timeout_ms,
                                          BgraFrame* frame) noexcept override {
    if (!IsOwnerThread()) {
      if (frame != nullptr) {
        *frame = {};
      }
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    switch (frame_source_kind_) {
      case FrameSourceKind::kDxgi:
        return MapDxgiResult(frame_source_.Acquire(timeout_ms, frame));
      case FrameSourceKind::kWindowsGraphicsCapture:
        return MapDxgiResult(
            graphics_capture_frame_source_.Acquire(timeout_ms, frame));
      case FrameSourceKind::kNone:
      default:
        if (frame != nullptr) {
          *frame = {};
        }
        return CaptureWorkerBackendResult::kInternalFailure;
    }
  }

  void ResetAcquisition() noexcept override {
    if (owner_thread_id_ == 0 || IsOwnerThread()) {
      frame_source_.Reset();
      graphics_capture_frame_source_.Reset();
      frame_source_kind_ = FrameSourceKind::kNone;
    }
  }

  CaptureWorkerBackendResult TransformFrame(
      const BgraFrame& source, uint32_t maximum_width, uint32_t maximum_height,
      BgraFrame* destination) noexcept override {
    if (destination == nullptr || !EnsureThreadRuntime()) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    if (wic_factory_ == nullptr &&
        FAILED(CreateWicImagingFactory(&wic_factory_))) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    const HRESULT result = ScaleBgraFrameWithWic(
        wic_factory_.Get(), source, maximum_width, maximum_height, destination);
    return SUCCEEDED(result) ? CaptureWorkerBackendResult::kOk
                             : CaptureWorkerBackendResult::kInvalidFrame;
  }

  CaptureWorkerBackendResult BeginChunk(
      const MfH264ChunkWriterConfig& config) noexcept override {
    if (!EnsureThreadRuntime()) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    return SUCCEEDED(writer_.Begin(config))
               ? CaptureWorkerBackendResult::kOk
               : CaptureWorkerBackendResult::kEncoderFailure;
  }

  CaptureWorkerBackendResult EncodeFrame(
      std::span<const uint8_t> top_down_bgra,
      int64_t timestamp_ticks) noexcept override {
    if (!IsOwnerThread()) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    return SUCCEEDED(writer_.AddFrame(top_down_bgra, timestamp_ticks))
               ? CaptureWorkerBackendResult::kOk
               : CaptureWorkerBackendResult::kEncoderFailure;
  }

  CaptureWorkerBackendResult FinalizeChunk(
      int64_t end_timestamp_ticks,
      std::vector<uint8_t>* encoded_mp4) noexcept override {
    if (!IsOwnerThread()) {
      if (encoded_mp4 != nullptr) {
        encoded_mp4->clear();
      }
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    return SUCCEEDED(writer_.Finalize(end_timestamp_ticks, encoded_mp4))
               ? CaptureWorkerBackendResult::kOk
               : CaptureWorkerBackendResult::kEncoderFailure;
  }

  void ResetChunk() noexcept override {
    if (owner_thread_id_ == 0 || IsOwnerThread()) {
      static_cast<void>(writer_.Reset());
    }
  }

  bool CreateArtifactId(std::string* artifact_id) noexcept override {
    if (artifact_id == nullptr) {
      return false;
    }
    artifact_id->clear();
    std::array<uint8_t, 16> random{};
    if (BCryptGenRandom(nullptr, random.data(),
                        static_cast<ULONG>(random.size()),
                        BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
      return false;
    }
    static constexpr char kHex[] = "0123456789abcdef";
    try {
      artifact_id->reserve(38);
      artifact_id->append("chunk-");
      for (const uint8_t value : random) {
        artifact_id->push_back(kHex[value >> 4U]);
        artifact_id->push_back(kHex[value & 0x0FU]);
      }
    } catch (...) {
      artifact_id->clear();
      return false;
    }
    return IsValidChunkArtifactId(*artifact_id);
  }

  CaptureWorkerBackendResult PreparePublication(
      std::string_view artifact_id, std::span<const uint8_t> encoded_mp4,
      const ChunkManifest& manifest,
      std::unique_ptr<CaptureWorkerPublication>* publication) noexcept
      override {
    if (publication == nullptr || *publication != nullptr) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    std::unique_ptr<AtomicPublicationAdapter> prepared;
    try {
      prepared = std::make_unique<AtomicPublicationAdapter>();
    } catch (...) {
      return CaptureWorkerBackendResult::kStorageFailure;
    }
    const AtomicChunkStoreResult result = store_.Prepare(
        artifact_id, encoded_mp4, manifest, prepared->mutable_publication());
    if (*prepared->mutable_publication()) {
      *publication = std::move(prepared);
    }
    return MapAtomicStoreResult(result);
  }

  int64_t SteadyNowMilliseconds() noexcept override {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
  }

  int64_t UnixNowMilliseconds() noexcept override {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::system_clock::now().time_since_epoch())
        .count();
  }

  void ShutdownThread() noexcept override {
    if (owner_thread_id_ == 0 || !IsOwnerThread()) {
      return;
    }
    static_cast<void>(writer_.Reset());
    ResetAcquisition();
    wic_factory_.Reset();
    if (uninitialize_com_) {
      CoUninitialize();
    }
    uninitialize_com_ = false;
    owner_thread_id_ = 0;
  }

 private:
  enum class FrameSourceKind {
    kNone,
    kDxgi,
    kWindowsGraphicsCapture,
  };

  bool IsOwnerThread() const noexcept {
    return owner_thread_id_ != 0 && owner_thread_id_ == GetCurrentThreadId();
  }

  bool EnsureThreadRuntime() noexcept {
    const DWORD current_thread = GetCurrentThreadId();
    if (owner_thread_id_ != 0) {
      return owner_thread_id_ == current_thread;
    }

    const HRESULT result =
        CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
    if (FAILED(result) && result != RPC_E_CHANGED_MODE) {
      return false;
    }
    owner_thread_id_ = current_thread;
    uninitialize_com_ = SUCCEEDED(result);
    return true;
  }

  AtomicChunkStore store_;
  DxgiDesktopFrameSource frame_source_;
  WindowsGraphicsCaptureFrameSource graphics_capture_frame_source_;
  Microsoft::WRL::ComPtr<IWICImagingFactory> wic_factory_;
  MfH264ChunkWriter writer_;
  DWORD owner_thread_id_ = 0;
  bool uninitialize_com_ = false;
  FrameSourceKind frame_source_kind_ = FrameSourceKind::kNone;
};

}  // namespace

bool ShouldFallbackToWindowsGraphicsCapture(
    DxgiDesktopFrameResult result) noexcept {
  return result == DxgiDesktopFrameResult::kAccessDenied;
}

bool TryConvertCaptureOutputDirectory(std::string_view utf8,
                                      std::wstring* utf16) noexcept {
  if (utf16 == nullptr || utf8.empty() || utf8.size() > 32'767 ||
      utf8.find('\0') != std::string_view::npos) {
    return false;
  }
  utf16->clear();
  if (utf8.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }
  const int input_length = static_cast<int>(utf8.size());
  const int output_length = MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), input_length, nullptr, 0);
  if (output_length <= 0 || output_length >= 32'767) {
    return false;
  }
  try {
    utf16->resize(static_cast<size_t>(output_length));
  } catch (...) {
    return false;
  }
  if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
                          input_length, utf16->data(),
                          output_length) != output_length) {
    utf16->clear();
    return false;
  }
  if (utf16->size() < 3 || (*utf16)[1] != L':' ||
      ((*utf16)[2] != L'\\' && (*utf16)[2] != L'/') || (*utf16)[0] == L'\\' ||
      (*utf16)[0] == L'/') {
    utf16->clear();
    return false;
  }
  const std::array<wchar_t, 4> root{(*utf16)[0], L':', L'\\', L'\0'};
  const UINT drive_type = GetDriveTypeW(root.data());
  if (drive_type == DRIVE_UNKNOWN || drive_type == DRIVE_NO_ROOT_DIR ||
      drive_type == DRIVE_REMOTE) {
    utf16->clear();
    return false;
  }
  return true;
}

std::unique_ptr<CaptureWorkerBackend> CreateWindowsCaptureWorkerBackend(
    std::wstring output_root) noexcept {
  try {
    return std::make_unique<WindowsCaptureWorkerBackend>(
        std::move(output_root));
  } catch (...) {
    return nullptr;
  }
}

}  // namespace windayflow::capture

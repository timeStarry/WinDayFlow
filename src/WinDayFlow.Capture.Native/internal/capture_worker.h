#ifndef WINDAYFLOW_CAPTURE_WORKER_H_
#define WINDAYFLOW_CAPTURE_WORKER_H_

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "capture_event_queue.h"
#include "capture_policy.h"
#include "capture_runtime_owner.h"
#include "capture_safety_core.h"
#include "chunk_manifest.h"
#include "jpeg_frame_chunk_writer.h"
#include "process_telemetry.h"
#include "wic_bgra_scaler.h"

namespace windayflow::capture {

enum class CaptureWorkerBackendResult {
  kOk,
  kTimeout,
  kRebuildRequired,
  kDeviceUnavailable,
  kInvalidFrame,
  kEncoderFailure,
  kStorageFailure,
  kInternalFailure,
};

enum class CaptureFrameWriteDisposition {
  kRetained,
  kDuplicate,
};

class CaptureWorkerPublication {
 public:
  virtual ~CaptureWorkerPublication() = default;

  virtual bool committed() const noexcept = 0;
  virtual const std::string& artifact_identifier() const noexcept = 0;
  virtual CaptureWorkerBackendResult Commit() noexcept = 0;
  virtual void Acknowledge() noexcept = 0;
  virtual CaptureWorkerBackendResult Rollback() noexcept = 0;
};

class CaptureWorkerBackend {
 public:
  virtual ~CaptureWorkerBackend() = default;

  virtual std::optional<CaptureTargetIdentity> ObserveTarget(
      const CaptureTargetIdentity& expected) noexcept = 0;
  virtual CaptureWorkerBackendResult InitializeAcquisition(
      const CaptureTargetIdentity& target) noexcept = 0;
  virtual CaptureWorkerBackendResult AcquireFrame(
      uint32_t timeout_ms, BgraFrame* frame) noexcept = 0;
  virtual void ResetAcquisition() noexcept = 0;
  virtual CaptureWorkerBackendResult TransformFrame(
      const BgraFrame& source, uint32_t maximum_width, uint32_t maximum_height,
      BgraFrame* destination) noexcept = 0;

  virtual std::optional<ProcessTelemetrySample> ObserveApplicationContext(
      const CaptureTargetIdentity& capture_target) noexcept = 0;

  virtual CaptureWorkerBackendResult BeginChunk(
      std::string_view artifact_id,
      const JpegFrameChunkWriterConfig& config) noexcept = 0;
  virtual CaptureWorkerBackendResult EncodeFrame(
      std::span<const uint8_t> top_down_bgra,
      uint64_t offset_milliseconds,
      CaptureFrameWriteDisposition* disposition) noexcept = 0;
  virtual CaptureWorkerBackendResult FinalizeChunk(
      ChunkManifest* manifest,
      std::unique_ptr<CaptureWorkerPublication>* publication) noexcept = 0;
  virtual void ResetChunk() noexcept = 0;

  virtual bool CreateArtifactId(std::string* artifact_id) noexcept = 0;

  virtual int64_t SteadyNowMilliseconds() noexcept = 0;
  virtual int64_t UnixNowMilliseconds() noexcept = 0;
  virtual void ShutdownThread() noexcept = 0;
};

struct CaptureWorkerConfiguration {
  CapturePolicy policy;
  uint32_t maximum_width = 1'600;
  uint32_t maximum_height = 900;
  uint32_t acquire_timeout_ms = 50;
  uint32_t topology_retry_ms = 100;
  uint32_t topology_retry_limit = 4;
  uint32_t rollback_retry_limit = 8;
  uint32_t rollback_retry_delay_ms = 10;
  float jpeg_quality = 0.82F;
  size_t maximum_frame_bytes = kMaximumChunkFrameFileBytes;
  size_t maximum_chunk_bytes = kMaximumChunkFrameBytes;
};

bool IsValidCaptureWorkerConfiguration(
    const CaptureWorkerConfiguration& configuration) noexcept;

enum class CaptureWorkerExitReason {
  kNotRun,
  kStopped,
  kAuthorizationLost,
  kInvalidConfiguration,
  kDeviceFailure,
  kEncoderFailure,
  kStorageFailure,
  kEventPublicationFailure,
  kCompensationFailure,
  kInternalFailure,
};

struct CaptureWorkerRunResult {
  CaptureWorkerExitReason reason = CaptureWorkerExitReason::kNotRun;
  wdf_capture_error error = WDF_CAPTURE_ERROR_NONE;
  uint64_t committed_chunks = 0;
  uint64_t encoded_frames = 0;
  bool compensation_pending = false;

  bool operator==(const CaptureWorkerRunResult&) const = default;
};

struct CaptureWorkerHealthSnapshot {
  int64_t last_successful_sample_unix_ms = 0;
  int64_t last_retained_frame_unix_ms = 0;
  uint64_t sampled_frame_count = 0;
  uint64_t black_frame_count = 0;
  uint64_t duplicate_frame_count = 0;
  uint64_t retained_frame_count = 0;
  uint64_t revision = 0;
};

enum class CaptureWorkerCheckpointKind {
  kReady,
  kPaused,
};

struct CaptureWorkerCheckpoint {
  CaptureWorkerCheckpointKind kind = CaptureWorkerCheckpointKind::kReady;
  uint64_t pause_epoch = 0;

  bool operator==(const CaptureWorkerCheckpoint&) const = default;
};

using CaptureWorkerCheckpointSink =
    std::function<bool(const CaptureWorkerCheckpoint&)>;

class CaptureWorker final {
 public:
  CaptureWorker(CaptureSafetyCore& safety, CaptureEventQueue& events,
                CaptureWorkerBackend& backend,
                CaptureWorkerConfiguration configuration);
  ~CaptureWorker();

  CaptureWorker(const CaptureWorker&) = delete;
  CaptureWorker& operator=(const CaptureWorker&) = delete;

  void Run(CaptureRuntimeOwner& runtime, PersistenceToken initial_token,
           CaptureWorkerCheckpointSink checkpoint_sink = {}) noexcept;
  bool UpdateTiming(uint32_t capture_interval_ms,
                    uint32_t chunk_duration_ms) noexcept;
  CaptureWorkerRunResult last_result() const;
  CaptureWorkerHealthSnapshot health_snapshot() const noexcept;
  bool RetryPendingCompensation(uint32_t attempts) noexcept;

 private:
  struct AuthorizedStageResult {
    bool authorized = false;
    CaptureWorkerBackendResult backend_result =
        CaptureWorkerBackendResult::kInternalFailure;
  };

  template <typename Operation>
  AuthorizedStageResult ExecuteAuthorizedStage(const PersistenceToken& token,
                                               Operation&& operation) noexcept {
    const std::optional<CaptureTargetIdentity> observed =
        backend_.ObserveTarget(token.target);
    if (!observed.has_value()) {
      return {};
    }
    PersistencePermit permit =
        safety_.AcquirePersistencePermit(token, *observed);
    if (!permit) {
      return {};
    }
    const CaptureWorkerBackendResult result = operation();
    if (!safety_.IsPersistencePermitCurrent(permit)) {
      return {};
    }
    const std::optional<CaptureTargetIdentity> observed_after =
        backend_.ObserveTarget(token.target);
    if (!observed_after.has_value() || *observed_after != *observed ||
        !safety_.IsPersistencePermitCurrent(permit)) {
      return {};
    }
    return {true, result};
  }

  CaptureSafetyCore& safety_;
  CaptureEventQueue& events_;
  CaptureWorkerBackend& backend_;
  CaptureWorkerConfiguration configuration_;
  std::atomic<bool> running_{false};
  mutable std::mutex result_mutex_;
  CaptureWorkerRunResult last_result_;
  std::unique_ptr<CaptureWorkerPublication> pending_compensation_;
  std::atomic<int64_t> last_successful_sample_unix_ms_{0};
  std::atomic<int64_t> last_retained_frame_unix_ms_{0};
  std::atomic<uint64_t> sampled_frame_count_{0};
  std::atomic<uint64_t> black_frame_count_{0};
  std::atomic<uint64_t> duplicate_frame_count_{0};
  std::atomic<uint64_t> retained_frame_count_{0};
  std::atomic<uint64_t> health_revision_{0};
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_WORKER_H_

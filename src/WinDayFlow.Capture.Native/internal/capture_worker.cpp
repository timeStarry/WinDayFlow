#include "capture_worker.h"

#include <algorithm>
#include <chrono>
#include <limits>
#include <thread>
#include <utility>

#include "process_telemetry.h"

namespace windayflow::capture {
namespace {

constexpr uint32_t kMaximumWorkerWaitMs = 60'000;
constexpr uint32_t kMaximumTopologyRetryLimit = 64;
constexpr uint32_t kMaximumRollbackAttempts = 64;
constexpr uint32_t kMaximumRollbackDelayMs = 1'000;
constexpr uint32_t kAuthorizationPauseCoordinationWaitMs = 250;
constexpr uint8_t kMaximumDiscardedBlackChannel = 8;

enum class WorkerStepResult {
  kOk,
  kAuthorizationLost,
  kDeviceFailure,
  kEncoderFailure,
  kStorageFailure,
  kEventFailure,
  kCompensationFailure,
  kInternalFailure,
};

enum class ChunkFinalizationReason {
  kRegular,
  kPause,
  kStop,
};

enum class FinalizationAuthorization {
  kCurrent,
  kSealedPrefix,
};

struct ChunkState {
  bool writer_started = false;
  std::string artifact_id;
  int64_t start_steady_ms = 0;
  int64_t start_unix_ms = 0;
  int64_t latest_frame_offset_ms = 0;
  int64_t latest_sample_unix_ms = 0;
  uint32_t sampled_count = 0;
  uint32_t black_count = 0;
  uint32_t frame_count = 0;
  uint32_t retained_counted = 0;
  uint32_t duplicate_counted = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  std::optional<ProcessTelemetrySample> process_telemetry_start;
  std::optional<ProcessTelemetrySample> previous_context_sample;
  int64_t previous_context_steady_ms = 0;
  std::vector<ChunkContextSampleManifest> context_samples;
};

struct PermitValidationContext {
  const CaptureSafetyCore* safety = nullptr;
  const PersistencePermit* permit = nullptr;
};

struct SealedPermitValidationContext {
  const CaptureSafetyCore* safety = nullptr;
  const SealedPrefixPermit* permit = nullptr;
};

class RequiredEventReservationGuard final {
 public:
  explicit RequiredEventReservationGuard(CaptureEventQueue& queue)
      : queue_(queue), reservation_(queue.ReserveRequiredEvent()) {}
  ~RequiredEventReservationGuard() {
    if (reservation_) {
      static_cast<void>(queue_.CancelReservation(&reservation_));
    }
  }

  RequiredEventReservationGuard(const RequiredEventReservationGuard&) = delete;
  RequiredEventReservationGuard& operator=(
      const RequiredEventReservationGuard&) = delete;

  explicit operator bool() const noexcept {
    return static_cast<bool>(reservation_);
  }
  CaptureEventReservation* get() noexcept { return &reservation_; }
  void Cancel() noexcept {
    if (reservation_) {
      static_cast<void>(queue_.CancelReservation(&reservation_));
    }
  }

 private:
  CaptureEventQueue& queue_;
  CaptureEventReservation reservation_;
};

bool ValidatePersistencePermit(void* context) noexcept {
  const auto* validation = static_cast<const PermitValidationContext*>(context);
  return validation != nullptr && validation->safety != nullptr &&
         validation->permit != nullptr &&
         validation->safety->IsPersistencePermitCurrent(*validation->permit);
}

bool ValidateSealedPrefixPermit(void* context) noexcept {
  const auto* validation =
      static_cast<const SealedPermitValidationContext*>(context);
  return validation != nullptr && validation->safety != nullptr &&
         validation->permit != nullptr &&
         validation->safety->IsSealedPrefixPermitCurrent(
             *validation->permit);
}

void SecureClear(std::vector<uint8_t>* bytes) noexcept {
  if (bytes == nullptr) {
    return;
  }
  if (!bytes->empty()) {
    SecureZeroMemory(bytes->data(), bytes->size());
  }
  bytes->clear();
}

void SecureClear(BgraFrame* frame) noexcept {
  if (frame == nullptr) {
    return;
  }
  SecureClear(&frame->pixels);
  frame->width = 0;
  frame->height = 0;
}

bool HasMeaningfulVisualContent(const BgraFrame& frame) noexcept {
  if (!IsValidBgraFrame(frame)) {
    return false;
  }

  for (size_t offset = 0; offset < frame.pixels.size(); offset += 4U) {
    if (frame.pixels[offset] > kMaximumDiscardedBlackChannel ||
        frame.pixels[offset + 1U] > kMaximumDiscardedBlackChannel ||
        frame.pixels[offset + 2U] > kMaximumDiscardedBlackChannel) {
      return true;
    }
  }
  return false;
}

class ScopedBgraFrame final {
 public:
  ScopedBgraFrame() = default;
  ~ScopedBgraFrame() { SecureClear(&value); }

  ScopedBgraFrame(const ScopedBgraFrame&) = delete;
  ScopedBgraFrame& operator=(const ScopedBgraFrame&) = delete;

  BgraFrame value;
};

int64_t SaturatingAddMilliseconds(int64_t value, int64_t delta) noexcept {
  if (delta <= 0) {
    return value;
  }
  if (value > std::numeric_limits<int64_t>::max() - delta) {
    return std::numeric_limits<int64_t>::max();
  }
  return value + delta;
}

uint32_t BoundedWaitMilliseconds(int64_t delay_ms) noexcept {
  if (delay_ms <= 0) {
    return 0;
  }
  return static_cast<uint32_t>(
      std::min<int64_t>(delay_ms, kMaximumWorkerWaitMs));
}

uint32_t TopologyRetryDelayMilliseconds(uint32_t base_delay_ms,
                                        uint32_t attempt) noexcept {
  uint32_t delay_ms = std::min(base_delay_ms, kMaximumWorkerWaitMs);
  for (uint32_t index = 1; index < attempt &&
                           delay_ms < kMaximumWorkerWaitMs;
       ++index) {
    delay_ms = delay_ms > kMaximumWorkerWaitMs / 2U
                   ? kMaximumWorkerWaitMs
                   : delay_ms * 2U;
  }
  return delay_ms;
}

wdf_capture_state EventStateFor(ChunkFinalizationReason reason) noexcept {
  switch (reason) {
    case ChunkFinalizationReason::kPause:
      return WDF_CAPTURE_STATE_PAUSED;
    case ChunkFinalizationReason::kStop:
      return WDF_CAPTURE_STATE_STOPPING;
    case ChunkFinalizationReason::kRegular:
    default:
      return WDF_CAPTURE_STATE_RECORDING;
  }
}

CaptureWorkerRunResult MakeFailure(WorkerStepResult step,
                                   const CaptureWorkerRunResult& progress) {
  CaptureWorkerRunResult result = progress;
  switch (step) {
    case WorkerStepResult::kAuthorizationLost:
      result.reason = CaptureWorkerExitReason::kAuthorizationLost;
      result.error = WDF_CAPTURE_ERROR_NONE;
      break;
    case WorkerStepResult::kDeviceFailure:
      result.reason = CaptureWorkerExitReason::kDeviceFailure;
      result.error = WDF_CAPTURE_ERROR_DEVICE_UNAVAILABLE;
      break;
    case WorkerStepResult::kEncoderFailure:
      result.reason = CaptureWorkerExitReason::kEncoderFailure;
      result.error = WDF_CAPTURE_ERROR_ENCODER_FAILURE;
      break;
    case WorkerStepResult::kStorageFailure:
      result.reason = CaptureWorkerExitReason::kStorageFailure;
      result.error = WDF_CAPTURE_ERROR_IO_FAILURE;
      break;
    case WorkerStepResult::kEventFailure:
      result.reason = CaptureWorkerExitReason::kEventPublicationFailure;
      result.error = WDF_CAPTURE_ERROR_NATIVE_FAILURE;
      break;
    case WorkerStepResult::kCompensationFailure:
      result.reason = CaptureWorkerExitReason::kCompensationFailure;
      result.error = WDF_CAPTURE_ERROR_IO_FAILURE;
      result.compensation_pending = true;
      break;
    case WorkerStepResult::kInternalFailure:
    case WorkerStepResult::kOk:
    default:
      result.reason = CaptureWorkerExitReason::kInternalFailure;
      result.error = WDF_CAPTURE_ERROR_NATIVE_FAILURE;
      break;
  }
  return result;
}

WorkerStepResult MapBackendFailure(CaptureWorkerBackendResult result) noexcept {
  switch (result) {
    case CaptureWorkerBackendResult::kEncoderFailure:
      return WorkerStepResult::kEncoderFailure;
    case CaptureWorkerBackendResult::kStorageFailure:
      return WorkerStepResult::kStorageFailure;
    case CaptureWorkerBackendResult::kDeviceUnavailable:
    case CaptureWorkerBackendResult::kInvalidFrame:
    case CaptureWorkerBackendResult::kRebuildRequired:
    case CaptureWorkerBackendResult::kTimeout:
      return WorkerStepResult::kDeviceFailure;
    case CaptureWorkerBackendResult::kInternalFailure:
    case CaptureWorkerBackendResult::kOk:
    default:
      return WorkerStepResult::kInternalFailure;
  }
}

}  // namespace

bool IsValidCaptureWorkerConfiguration(
    const CaptureWorkerConfiguration& configuration) noexcept {
  return IsValidCapturePolicy(configuration.policy) &&
         configuration.maximum_width >= 2 &&
         configuration.maximum_width <= 7'680 &&
         configuration.maximum_height >= 2 &&
         configuration.maximum_height <= 4'320 &&
         configuration.acquire_timeout_ms <= kMaximumWorkerWaitMs &&
         configuration.topology_retry_ms <= kMaximumWorkerWaitMs &&
         configuration.topology_retry_limit > 0 &&
         configuration.topology_retry_limit <= kMaximumTopologyRetryLimit &&
         configuration.rollback_retry_limit > 0 &&
         configuration.rollback_retry_limit <= kMaximumRollbackAttempts &&
         configuration.rollback_retry_delay_ms <= kMaximumRollbackDelayMs &&
         configuration.jpeg_quality > 0.0F &&
         configuration.jpeg_quality <= 1.0F &&
         configuration.maximum_frame_bytes >= 4U &&
         configuration.maximum_frame_bytes <= kMaximumChunkFrameFileBytes &&
         configuration.maximum_chunk_bytes >=
             configuration.maximum_frame_bytes &&
         configuration.maximum_chunk_bytes <= kMaximumChunkFrameBytes;
}

CaptureWorker::CaptureWorker(CaptureSafetyCore& safety,
                             CaptureEventQueue& events,
                             CaptureWorkerBackend& backend,
                             CaptureWorkerConfiguration configuration)
    : safety_(safety),
      events_(events),
      backend_(backend),
      configuration_(configuration) {}

bool CaptureWorker::UpdateTiming(uint32_t capture_interval_ms,
                                 uint32_t chunk_duration_ms) noexcept {
  if (running_.load(std::memory_order_acquire)) {
    return false;
  }
  CaptureWorkerConfiguration updated = configuration_;
  updated.policy.capture_interval_ms = capture_interval_ms;
  updated.policy.chunk_duration_ms = chunk_duration_ms;
  if (!IsValidCaptureWorkerConfiguration(updated)) {
    return false;
  }
  configuration_ = updated;
  return true;
}

CaptureWorker::~CaptureWorker() {
  static_cast<void>(RetryPendingCompensation(1));
}

CaptureWorkerRunResult CaptureWorker::last_result() const {
  std::lock_guard lock(result_mutex_);
  return last_result_;
}

CaptureWorkerHealthSnapshot CaptureWorker::health_snapshot() const noexcept {
  for (;;) {
    const uint64_t revision_before =
        health_revision_.load(std::memory_order_acquire);
    if ((revision_before & 1U) != 0) {
      std::this_thread::yield();
      continue;
    }

    CaptureWorkerHealthSnapshot snapshot{
        last_successful_sample_unix_ms_.load(std::memory_order_acquire),
        last_retained_frame_unix_ms_.load(std::memory_order_acquire),
        sampled_frame_count_.load(std::memory_order_acquire),
        black_frame_count_.load(std::memory_order_acquire),
        duplicate_frame_count_.load(std::memory_order_acquire),
        retained_frame_count_.load(std::memory_order_acquire),
        revision_before};
    const uint64_t revision_after =
        health_revision_.load(std::memory_order_acquire);
    if (revision_before == revision_after) {
      return snapshot;
    }
  }
}

void CaptureWorker::BeginHealthUpdate() noexcept {
  health_revision_.fetch_add(1, std::memory_order_acq_rel);
}

void CaptureWorker::EndHealthUpdate() noexcept {
  health_revision_.fetch_add(1, std::memory_order_release);
}

bool CaptureWorker::RetryPendingCompensation(uint32_t attempts) noexcept {
  if (attempts == 0) {
    return false;
  }
  std::lock_guard lock(result_mutex_);
  if (pending_compensation_ == nullptr) {
    return true;
  }
  for (uint32_t attempt = 0; attempt < attempts; ++attempt) {
    if (pending_compensation_->Rollback() == CaptureWorkerBackendResult::kOk) {
      pending_compensation_.reset();
      last_result_.compensation_pending = false;
      return true;
    }
  }
  last_result_.compensation_pending = true;
  return false;
}

void CaptureWorker::Run(CaptureRuntimeOwner& runtime,
                        PersistenceToken initial_token,
                        CaptureWorkerCheckpointSink checkpoint_sink) noexcept {
  bool expected = false;
  if (!running_.compare_exchange_strong(expected, true)) {
    return;
  }

  CaptureWorkerRunResult result;
  auto complete = [this, &result]() noexcept {
    backend_.ResetChunk();
    backend_.ResetAcquisition();
    backend_.ShutdownThread();
    {
      std::lock_guard lock(result_mutex_);
      result.compensation_pending = pending_compensation_ != nullptr;
      last_result_ = result;
    }
    running_.store(false);
  };

  try {
    if (!IsValidCaptureWorkerConfiguration(configuration_) ||
        initial_token.instance_epoch == 0 ||
        initial_token.persistence_generation == 0 ||
        initial_token.target.target_epoch == 0) {
      result.reason = CaptureWorkerExitReason::kInvalidConfiguration;
      result.error = WDF_CAPTURE_ERROR_INVALID_CONFIGURATION;
      complete();
      return;
    }
    if (!RetryPendingCompensation(configuration_.rollback_retry_limit)) {
      result.reason = CaptureWorkerExitReason::kCompensationFailure;
      result.error = WDF_CAPTURE_ERROR_IO_FAILURE;
      result.compensation_pending = true;
      complete();
      return;
    }

    PersistenceToken token = std::move(initial_token);
    CaptureSchedule schedule(configuration_.policy);
    schedule.Reset(backend_.SteadyNowMilliseconds());
    ChunkState chunk;
    bool topology_available = false;
    bool ready_for_token = false;
    uint32_t consecutive_topology_retries = 0;
    uint64_t handled_pause_epoch = 0;
    uint64_t observed_control_sequence = runtime.ReadControlSnapshot().sequence;

    auto discard_chunk = [&]() noexcept {
      backend_.ResetChunk();
      chunk = {};
    };

    auto publish_checkpoint = [&](CaptureWorkerCheckpointKind kind,
                                  uint64_t pause_epoch) noexcept {
      if (!checkpoint_sink) {
        return true;
      }
      try {
        return checkpoint_sink(CaptureWorkerCheckpoint{kind, pause_epoch});
      } catch (...) {
        return false;
      }
    };

    auto retain_failed_compensation =
        [this](
            std::unique_ptr<CaptureWorkerPublication>* publication) noexcept {
          std::lock_guard lock(result_mutex_);
          pending_compensation_ = std::move(*publication);
        };

    auto compensate =
        [&](std::unique_ptr<CaptureWorkerPublication>* publication) noexcept {
          if (publication == nullptr || *publication == nullptr) {
            return true;
          }
          for (uint32_t attempt = 0;
               attempt < configuration_.rollback_retry_limit; ++attempt) {
            if ((*publication)->Rollback() == CaptureWorkerBackendResult::kOk) {
              publication->reset();
              return true;
            }
            if (attempt + 1U < configuration_.rollback_retry_limit &&
                configuration_.rollback_retry_delay_ms > 0) {
              std::this_thread::sleep_for(std::chrono::milliseconds(
                  configuration_.rollback_retry_delay_ms));
            }
          }
          retain_failed_compensation(publication);
          return false;
        };

    auto finalize_chunk =
        [&](ChunkFinalizationReason finalization_reason,
            FinalizationAuthorization authorization) -> WorkerStepResult {
      SealedPrefixPermit sealed_permit;
      if (authorization == FinalizationAuthorization::kSealedPrefix) {
        sealed_permit = safety_.AcquireSealedPrefixPermit(token);
        if (!sealed_permit) {
          discard_chunk();
          return WorkerStepResult::kAuthorizationLost;
        }
      }

      const auto execute_stage = [&](auto&& operation) noexcept {
        if (authorization == FinalizationAuthorization::kCurrent) {
          return ExecuteAuthorizedStage(token,
                                        std::forward<decltype(operation)>(
                                            operation));
        }
        const CaptureWorkerBackendResult backend_result = operation();
        if (!safety_.IsSealedPrefixPermitCurrent(sealed_permit)) {
          return AuthorizedStageResult{};
        }
        return AuthorizedStageResult{true, backend_result};
      };

      if (!chunk.writer_started || chunk.sampled_count == 0) {
        discard_chunk();
        return WorkerStepResult::kOk;
      }

      const int64_t finalization_steady_ms = backend_.SteadyNowMilliseconds();
      RequiredEventReservationGuard reservation(events_);
      if (!reservation) {
        discard_chunk();
        return WorkerStepResult::kEventFailure;
      }

      const int64_t elapsed_ms =
          std::max<int64_t>(0, finalization_steady_ms - chunk.start_steady_ms);
      const int64_t encoded_duration_ms = CalculateEncodedDurationMs(
          chunk.sampled_count, configuration_.policy.capture_interval_ms);
      const int64_t duration_ms = CalculateChunkDurationMs(
          elapsed_ms, encoded_duration_ms, chunk.latest_frame_offset_ms);
      ChunkManifest manifest{
          chunk.artifact_id,
          chunk.start_unix_ms,
          SaturatingAddMilliseconds(chunk.start_unix_ms, duration_ms),
          chunk.sampled_count,
          chunk.width,
          chunk.height,
          0,
          token.persistence_generation,
          token.target.target_epoch,
          token.target.scope == CaptureAuthorizationScope::kDisplayWide,
          {},
      };
      manifest.black_frame_count = chunk.black_count;
      manifest.context_samples = chunk.context_samples;
      if (chunk.process_telemetry_start.has_value() &&
          token.target.scope == CaptureAuthorizationScope::kForegroundTarget) {
        const std::optional<ProcessTelemetrySample> end_sample =
            ReadProcessTelemetrySample(
                token.target.process_id,
                token.target.process_creation_time_100ns);
        if (end_sample.has_value()) {
          const std::optional<ProcessTelemetryInterval> interval =
              BuildProcessTelemetryInterval(
                  *chunk.process_telemetry_start, *end_sample,
                  static_cast<uint64_t>(std::max<int64_t>(1, elapsed_ms)));
          if (interval.has_value()) {
            manifest.application = ChunkApplicationManifest{
                interval->process_name_utf8, interval->process_id,
                interval->cpu_usage_basis_points,
                interval->working_set_bytes,
                interval->private_memory_bytes};
          }
        }
      }

      std::unique_ptr<CaptureWorkerPublication> publication;
      const AuthorizedStageResult finalize = execute_stage([&]() noexcept {
        return backend_.FinalizeChunk(&manifest, &publication);
      });
      if (!finalize.authorized ||
          finalize.backend_result != CaptureWorkerBackendResult::kOk ||
          publication == nullptr) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        if (!compensated) {
          return WorkerStepResult::kCompensationFailure;
        }
        return !finalize.authorized ? WorkerStepResult::kAuthorizationLost
               : finalize.backend_result == CaptureWorkerBackendResult::kOk
                   ? WorkerStepResult::kInternalFailure
                   : MapBackendFailure(finalize.backend_result);
      }

      const uint32_t actual_retained =
          static_cast<uint32_t>(manifest.frames.size());
      const uint32_t actual_duplicate = manifest.duplicate_frame_count;
      const bool health_changed = actual_retained != chunk.retained_counted ||
                                  actual_duplicate != chunk.duplicate_counted;
      if (health_changed) {
        BeginHealthUpdate();
      }
      if (actual_retained > chunk.retained_counted) {
        retained_frame_count_.fetch_add(
            actual_retained - chunk.retained_counted,
            std::memory_order_acq_rel);
        last_retained_frame_unix_ms_.store(
            chunk.latest_sample_unix_ms, std::memory_order_release);
      }
      if (chunk.duplicate_counted > actual_duplicate) {
        duplicate_frame_count_.fetch_sub(
            chunk.duplicate_counted - actual_duplicate,
            std::memory_order_acq_rel);
      }
      if (health_changed) {
        EndHealthUpdate();
      }

      std::string artifact_identifier;
      try {
        artifact_identifier = publication->artifact_identifier();
      } catch (...) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        return compensated ? WorkerStepResult::kInternalFailure
                           : WorkerStepResult::kCompensationFailure;
      }
      if (artifact_identifier.empty()) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        return compensated ? WorkerStepResult::kInternalFailure
                           : WorkerStepResult::kCompensationFailure;
      }

      const AuthorizedStageResult commit =
          execute_stage([&]() noexcept { return publication->Commit(); });
      if (!commit.authorized ||
          commit.backend_result != CaptureWorkerBackendResult::kOk ||
          !publication->committed()) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        if (!compensated) {
          return WorkerStepResult::kCompensationFailure;
        }
        return !commit.authorized ? WorkerStepResult::kAuthorizationLost
               : commit.backend_result == CaptureWorkerBackendResult::kOk
                   ? WorkerStepResult::kInternalFailure
                   : MapBackendFailure(commit.backend_result);
      }

      PersistencePermit event_permit;
      std::optional<CaptureTargetIdentity> observed;
      if (authorization == FinalizationAuthorization::kCurrent) {
        observed = backend_.ObserveTarget(token.target);
        if (observed.has_value()) {
          event_permit = safety_.AcquirePersistencePermit(token, *observed);
        }
        const std::optional<CaptureTargetIdentity>
            observed_before_publication = backend_.ObserveTarget(token.target);
        if (!event_permit || !observed_before_publication.has_value() ||
            *observed_before_publication != *observed ||
            !safety_.IsPersistencePermitCurrent(event_permit)) {
          const bool compensated = compensate(&publication);
          reservation.Cancel();
          discard_chunk();
          return compensated ? WorkerStepResult::kAuthorizationLost
                             : WorkerStepResult::kCompensationFailure;
        }
      }

      PermitValidationContext validation{&safety_, &event_permit};
      SealedPermitValidationContext sealed_validation{&safety_,
                                                       &sealed_permit};
      const uint64_t event_sequence = events_.PushReservedValidated(
          reservation.get(), WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
          EventStateFor(finalization_reason), WDF_CAPTURE_REASON_NONE,
          WDF_CAPTURE_ERROR_NONE, std::move(artifact_identifier),
          backend_.UnixNowMilliseconds(), token.persistence_generation,
          token.target.target_epoch,
          authorization == FinalizationAuthorization::kCurrent
              ? ValidatePersistencePermit
              : ValidateSealedPrefixPermit,
          authorization == FinalizationAuthorization::kCurrent
              ? static_cast<void*>(&validation)
              : static_cast<void*>(&sealed_validation));
      if (event_sequence == 0) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        if (!compensated) {
          return WorkerStepResult::kCompensationFailure;
        }
        const bool permit_current =
            authorization == FinalizationAuthorization::kCurrent
                ? safety_.IsPersistencePermitCurrent(event_permit)
                : safety_.IsSealedPrefixPermitCurrent(sealed_permit);
        return permit_current ? WorkerStepResult::kEventFailure
                              : WorkerStepResult::kAuthorizationLost;
      }

      publication->Acknowledge();
      publication.reset();
      ++result.committed_chunks;
      discard_chunk();
      return WorkerStepResult::kOk;
    };

    auto fail = [&](WorkerStepResult step) noexcept {
      safety_.ConsumeSealedPrefix(token);
      discard_chunk();
      result = MakeFailure(step, result);
      complete();
    };

    auto exit_stopped = [&]() noexcept {
      safety_.ConsumeSealedPrefix(token);
      discard_chunk();
      backend_.ResetAcquisition();
      topology_available = false;
      ready_for_token = false;
      result.reason = CaptureWorkerExitReason::kStopped;
      result.error = WDF_CAPTURE_ERROR_NONE;
      complete();
    };

    auto await_resume = [&](CaptureRuntimeControlSnapshot control) {
      for (;;) {
        observed_control_sequence = control.sequence;
        if (control.stop_requested) {
          exit_stopped();
          return false;
        }
        if (!control.pause_requested) {
          if (!control.replacement_token.has_value()) {
            fail(WorkerStepResult::kInternalFailure);
            return false;
          }
          token = *control.replacement_token;
          ready_for_token = false;
          consecutive_topology_retries = 0;
          schedule.Reset(backend_.SteadyNowMilliseconds());
          return true;
        }

        if (control.pause_epoch != handled_pause_epoch) {
          handled_pause_epoch = control.pause_epoch;
          if (!publish_checkpoint(CaptureWorkerCheckpointKind::kPaused,
                                  handled_pause_epoch)) {
            fail(WorkerStepResult::kEventFailure);
            return false;
          }
        }

        const std::optional<CaptureRuntimeControlSnapshot> changed =
            runtime.WaitForControlChange(observed_control_sequence,
                                         kMaximumWorkerWaitMs);
        if (changed.has_value()) {
          control = *changed;
        }
      }
    };

    auto enter_pause = [&](CaptureRuntimeControlSnapshot control,
                           bool finalize_authorized_chunk) {
      if (finalize_authorized_chunk) {
        const bool sealed_prefix = safety_.HasSealedPrefix(token);
        const WorkerStepResult paused =
            finalize_chunk(ChunkFinalizationReason::kPause,
                           sealed_prefix
                               ? FinalizationAuthorization::kSealedPrefix
                               : FinalizationAuthorization::kCurrent);
        if (sealed_prefix) {
          safety_.ConsumeSealedPrefix(token);
        }
        if (paused == WorkerStepResult::kAuthorizationLost) {
          safety_.ConsumeSealedPrefix(token);
          control = runtime.ReadControlSnapshot();
          observed_control_sequence = control.sequence;
          if (control.stop_requested) {
            exit_stopped();
            return false;
          }
          if (control.pause_epoch == handled_pause_epoch) {
            fail(WorkerStepResult::kAuthorizationLost);
            return false;
          }
        } else if (paused != WorkerStepResult::kOk) {
          fail(paused);
          return false;
        }
      } else {
        safety_.ConsumeSealedPrefix(token);
        discard_chunk();
      }

      backend_.ResetAcquisition();
      topology_available = false;
      ready_for_token = false;
      handled_pause_epoch = control.pause_epoch;
      if (!publish_checkpoint(CaptureWorkerCheckpointKind::kPaused,
                              handled_pause_epoch)) {
        fail(WorkerStepResult::kEventFailure);
        return false;
      }
      return await_resume(std::move(control));
    };

    auto handle_authorization_loss = [&](bool prefix_sealable) {
      CaptureRuntimeControlSnapshot latest = runtime.ReadControlSnapshot();
      observed_control_sequence = latest.sequence;
      bool sealed_prefix = safety_.HasSealedPrefix(token);
      if (!latest.stop_requested &&
          latest.pause_epoch == handled_pause_epoch) {
        const std::optional<CaptureRuntimeControlSnapshot> coordinated =
            runtime.WaitForControlChange(
                observed_control_sequence,
                kAuthorizationPauseCoordinationWaitMs);
        if (coordinated.has_value()) {
          latest = *coordinated;
          observed_control_sequence = latest.sequence;
        }
      }
      if (latest.stop_requested) {
        if (sealed_prefix && prefix_sealable) {
          const WorkerStepResult stopped = finalize_chunk(
              ChunkFinalizationReason::kStop,
              FinalizationAuthorization::kSealedPrefix);
          safety_.ConsumeSealedPrefix(token);
          if (stopped != WorkerStepResult::kOk) {
            fail(stopped);
            return false;
          }
        } else {
          safety_.ConsumeSealedPrefix(token);
        }
        exit_stopped();
        return false;
      }
      if (latest.pause_epoch != handled_pause_epoch) {
        if (sealed_prefix && prefix_sealable) {
          const WorkerStepResult paused = finalize_chunk(
              ChunkFinalizationReason::kPause,
              FinalizationAuthorization::kSealedPrefix);
          safety_.ConsumeSealedPrefix(token);
          if (paused != WorkerStepResult::kOk) {
            fail(paused);
            return false;
          }
        } else {
          safety_.ConsumeSealedPrefix(token);
        }
        return enter_pause(std::move(latest), false);
      }
      safety_.ConsumeSealedPrefix(token);
      fail(WorkerStepResult::kAuthorizationLost);
      return false;
    };

    for (;;) {
      CaptureRuntimeControlSnapshot control = runtime.ReadControlSnapshot();
      observed_control_sequence = control.sequence;

      if (control.stop_requested) {
        const bool sealed_prefix = safety_.HasSealedPrefix(token);
        const WorkerStepResult stopped =
            finalize_chunk(ChunkFinalizationReason::kStop,
                           sealed_prefix
                               ? FinalizationAuthorization::kSealedPrefix
                               : FinalizationAuthorization::kCurrent);
        if (sealed_prefix) {
          safety_.ConsumeSealedPrefix(token);
        }
        if (stopped == WorkerStepResult::kAuthorizationLost) {
          static_cast<void>(handle_authorization_loss(false));
          return;
        }
        if (stopped != WorkerStepResult::kOk) {
          fail(stopped);
          return;
        }
        exit_stopped();
        return;
      }

      if (control.pause_epoch != handled_pause_epoch) {
        if (!enter_pause(std::move(control), true)) {
          return;
        }
        continue;
      }

      if (control.pause_requested) {
        if (!await_resume(std::move(control))) {
          return;
        }
        continue;
      }

      const AuthorizedStageResult current = ExecuteAuthorizedStage(
          token, []() noexcept { return CaptureWorkerBackendResult::kOk; });
      if (!current.authorized) {
        if (!handle_authorization_loss(true)) {
          return;
        }
        continue;
      }

      if (!topology_available) {
        const AuthorizedStageResult initialized =
            ExecuteAuthorizedStage(token, [&]() noexcept {
              return backend_.InitializeAcquisition(token.target);
            });
        if (!initialized.authorized) {
          if (!handle_authorization_loss(true)) {
            return;
          }
          continue;
        }
        if (initialized.backend_result ==
            CaptureWorkerBackendResult::kRebuildRequired) {
          backend_.ResetAcquisition();
          if (consecutive_topology_retries >=
              configuration_.topology_retry_limit) {
            fail(WorkerStepResult::kDeviceFailure);
            return;
          }
          ++consecutive_topology_retries;
          static_cast<void>(runtime.WaitForControlChange(
              observed_control_sequence,
              TopologyRetryDelayMilliseconds(
                  configuration_.topology_retry_ms,
                  consecutive_topology_retries)));
          continue;
        }
        if (initialized.backend_result != CaptureWorkerBackendResult::kOk) {
          fail(MapBackendFailure(initialized.backend_result));
          return;
        }
        topology_available = true;
        schedule.Reset(backend_.SteadyNowMilliseconds());
      }

      const int64_t now_ms = backend_.SteadyNowMilliseconds();
      if (chunk.writer_started &&
          now_ms - chunk.start_steady_ms >=
              static_cast<int64_t>(configuration_.policy.chunk_duration_ms)) {
        const WorkerStepResult finalized =
            finalize_chunk(ChunkFinalizationReason::kRegular,
                           FinalizationAuthorization::kCurrent);
        if (finalized == WorkerStepResult::kAuthorizationLost) {
          if (!handle_authorization_loss(false)) {
            return;
          }
          continue;
        }
        if (finalized != WorkerStepResult::kOk) {
          fail(finalized);
          return;
        }
      }

      const CaptureScheduleDecision schedule_decision = schedule.Poll(now_ms);
      if (!schedule_decision.capture_frame) {
        const uint32_t wait_ms =
            BoundedWaitMilliseconds(schedule.DelayUntilNextMs(now_ms));
        static_cast<void>(
            runtime.WaitForControlChange(observed_control_sequence, wait_ms));
        continue;
      }

      ScopedBgraFrame acquired_storage;
      BgraFrame& acquired_frame = acquired_storage.value;
      const AuthorizedStageResult acquired =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.AcquireFrame(configuration_.acquire_timeout_ms,
                                         &acquired_frame);
          });
      if (!acquired.authorized) {
        SecureClear(&acquired_frame);
        if (!handle_authorization_loss(true)) {
          return;
        }
        continue;
      }
      if (acquired.backend_result == CaptureWorkerBackendResult::kTimeout) {
        SecureClear(&acquired_frame);
        continue;
      }
      if (acquired.backend_result ==
          CaptureWorkerBackendResult::kRebuildRequired) {
        SecureClear(&acquired_frame);
        const WorkerStepResult interrupted =
            finalize_chunk(ChunkFinalizationReason::kRegular,
                           FinalizationAuthorization::kCurrent);
        if (interrupted == WorkerStepResult::kAuthorizationLost) {
          if (!handle_authorization_loss(false)) {
            return;
          }
          continue;
        }
        if (interrupted != WorkerStepResult::kOk) {
          fail(interrupted);
          return;
        }
        backend_.ResetAcquisition();
        topology_available = false;
        if (consecutive_topology_retries >=
            configuration_.topology_retry_limit) {
          fail(WorkerStepResult::kDeviceFailure);
          return;
        }
        ++consecutive_topology_retries;
        static_cast<void>(runtime.WaitForControlChange(
            observed_control_sequence,
            TopologyRetryDelayMilliseconds(
                configuration_.topology_retry_ms,
                consecutive_topology_retries)));
        continue;
      }
      if (acquired.backend_result != CaptureWorkerBackendResult::kOk) {
        SecureClear(&acquired_frame);
        fail(MapBackendFailure(acquired.backend_result));
        return;
      }

      ScopedBgraFrame transformed_storage;
      BgraFrame& transformed_frame = transformed_storage.value;
      const AuthorizedStageResult transformed =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.TransformFrame(
                acquired_frame, configuration_.maximum_width,
                configuration_.maximum_height, &transformed_frame);
          });
      SecureClear(&acquired_frame);
      if (!transformed.authorized) {
        SecureClear(&transformed_frame);
        if (!handle_authorization_loss(true)) {
          return;
        }
        continue;
      }
      if (transformed.backend_result != CaptureWorkerBackendResult::kOk ||
          !IsValidBgraFrame(transformed_frame)) {
        SecureClear(&transformed_frame);
        fail(transformed.backend_result == CaptureWorkerBackendResult::kOk
                 ? WorkerStepResult::kDeviceFailure
                 : MapBackendFailure(transformed.backend_result));
        return;
      }

      const int64_t frame_steady_ms = backend_.SteadyNowMilliseconds();
      const int64_t sample_unix_ms = backend_.UnixNowMilliseconds();
      BeginHealthUpdate();
      last_successful_sample_unix_ms_.store(sample_unix_ms,
                                            std::memory_order_release);
      sampled_frame_count_.fetch_add(1, std::memory_order_acq_rel);
      EndHealthUpdate();

      if (chunk.writer_started && (chunk.width != transformed_frame.width ||
                                   chunk.height != transformed_frame.height)) {
        const WorkerStepResult resized =
            finalize_chunk(ChunkFinalizationReason::kRegular,
                           FinalizationAuthorization::kCurrent);
        if (resized == WorkerStepResult::kAuthorizationLost) {
          SecureClear(&transformed_frame);
          if (!handle_authorization_loss(false)) {
            return;
          }
          continue;
        }
        if (resized != WorkerStepResult::kOk) {
          SecureClear(&transformed_frame);
          fail(resized);
          return;
        }
      }

      if (chunk.writer_started &&
          frame_steady_ms - chunk.start_steady_ms >=
              static_cast<int64_t>(configuration_.policy.chunk_duration_ms)) {
        const WorkerStepResult boundary =
            finalize_chunk(ChunkFinalizationReason::kRegular,
                           FinalizationAuthorization::kCurrent);
        if (boundary == WorkerStepResult::kAuthorizationLost) {
          SecureClear(&transformed_frame);
          if (!handle_authorization_loss(false)) {
            return;
          }
          continue;
        }
        if (boundary != WorkerStepResult::kOk) {
          SecureClear(&transformed_frame);
          fail(boundary);
          return;
        }
      }

      const JpegFrameChunkWriterConfig writer_configuration{
          transformed_frame.width,
          transformed_frame.height,
          configuration_.jpeg_quality,
          configuration_.maximum_frame_bytes,
          configuration_.maximum_chunk_bytes,
      };
      if (!chunk.writer_started) {
        if (token.target.scope == CaptureAuthorizationScope::kForegroundTarget) {
          chunk.process_telemetry_start = ReadProcessTelemetrySample(
              token.target.process_id,
              token.target.process_creation_time_100ns);
        }
        if (!backend_.CreateArtifactId(&chunk.artifact_id)) {
          SecureClear(&transformed_frame);
          fail(WorkerStepResult::kInternalFailure);
          return;
        }
        const AuthorizedStageResult begun =
            ExecuteAuthorizedStage(token, [&]() noexcept {
              return backend_.BeginChunk(chunk.artifact_id,
                                         writer_configuration);
            });
        if (!begun.authorized) {
          SecureClear(&transformed_frame);
          if (!handle_authorization_loss(false)) {
            return;
          }
          continue;
        }
        if (begun.backend_result != CaptureWorkerBackendResult::kOk) {
          SecureClear(&transformed_frame);
          fail(MapBackendFailure(begun.backend_result));
          return;
        }
        chunk.writer_started = true;
        chunk.start_steady_ms = frame_steady_ms;
        chunk.start_unix_ms = sample_unix_ms;
        chunk.width = writer_configuration.width;
        chunk.height = writer_configuration.height;
        schedule.ReanchorFrame(frame_steady_ms);
      }

      int64_t frame_offset_ms =
          std::max<int64_t>(0, frame_steady_ms - chunk.start_steady_ms);
      if (chunk.sampled_count != 0 &&
          frame_offset_ms <= chunk.latest_frame_offset_ms) {
        frame_offset_ms = SaturatingAddMilliseconds(
            chunk.latest_frame_offset_ms, 1);
      }
      chunk.latest_frame_offset_ms =
          std::max(chunk.latest_frame_offset_ms, frame_offset_ms);
      chunk.latest_sample_unix_ms = sample_unix_ms;
      const uint32_t sample_index = chunk.sampled_count;
      if (chunk.sampled_count < std::numeric_limits<uint32_t>::max()) {
        ++chunk.sampled_count;
      }

      try {
        ChunkContextSampleManifest context_sample;
        context_sample.sample_index = sample_index;
        context_sample.offset_milliseconds =
            static_cast<uint64_t>(frame_offset_ms);
        const std::optional<ProcessTelemetrySample> observed_context =
            backend_.ObserveApplicationContext(token.target);
        if (observed_context.has_value()) {
          uint32_t cpu_usage_basis_points = 0;
          if (chunk.previous_context_sample.has_value()) {
            const int64_t elapsed_context_ms = std::max<int64_t>(
                1, frame_steady_ms - chunk.previous_context_steady_ms);
            const std::optional<ProcessTelemetryInterval> interval =
                BuildProcessTelemetryInterval(
                    *chunk.previous_context_sample,
                    *observed_context,
                    static_cast<uint64_t>(elapsed_context_ms));
            if (interval.has_value()) {
              cpu_usage_basis_points = interval->cpu_usage_basis_points;
            }
          }
          context_sample.application = ChunkApplicationManifest{
              observed_context->process_name_utf8,
              observed_context->process_id,
              cpu_usage_basis_points,
              observed_context->working_set_bytes,
              observed_context->private_memory_bytes};
          chunk.previous_context_sample = observed_context;
          chunk.previous_context_steady_ms = frame_steady_ms;
        } else {
          chunk.previous_context_sample.reset();
          chunk.previous_context_steady_ms = 0;
        }
        chunk.context_samples.push_back(std::move(context_sample));
      } catch (...) {
        // Context telemetry is observational and must never interrupt capture.
      }

      // Reject only compositor-empty frames. The context chunk remains so the
      // sampled interval and filter count are not lost.
      if (!HasMeaningfulVisualContent(transformed_frame)) {
        BeginHealthUpdate();
        black_frame_count_.fetch_add(1, std::memory_order_acq_rel);
        EndHealthUpdate();
        ++chunk.black_count;
        SecureClear(&transformed_frame);
        consecutive_topology_retries = 0;
        if (!ready_for_token) {
          if (!publish_checkpoint(CaptureWorkerCheckpointKind::kReady, 0)) {
            fail(WorkerStepResult::kEventFailure);
            return;
          }
          ready_for_token = true;
        }
        continue;
      }

      CaptureFrameWriteDisposition disposition =
          CaptureFrameWriteDisposition::kDuplicate;
      const AuthorizedStageResult encoded =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.EncodeFrame(transformed_frame.pixels,
                                        static_cast<uint64_t>(frame_offset_ms),
                                        &disposition);
          });
      SecureClear(&transformed_frame);
      if (!encoded.authorized) {
        if (!handle_authorization_loss(false)) {
          return;
        }
        continue;
      }
      if (encoded.backend_result != CaptureWorkerBackendResult::kOk) {
        fail(MapBackendFailure(encoded.backend_result));
        return;
      }

      BeginHealthUpdate();
      if (disposition == CaptureFrameWriteDisposition::kRetained) {
        retained_frame_count_.fetch_add(1, std::memory_order_acq_rel);
        last_retained_frame_unix_ms_.store(sample_unix_ms,
                                           std::memory_order_release);
        ++chunk.retained_counted;
      } else {
        duplicate_frame_count_.fetch_add(1, std::memory_order_acq_rel);
        ++chunk.duplicate_counted;
      }
      EndHealthUpdate();

      if (chunk.frame_count < std::numeric_limits<uint32_t>::max()) {
        ++chunk.frame_count;
      }
      ++result.encoded_frames;
      consecutive_topology_retries = 0;
      if (!ready_for_token) {
        if (!publish_checkpoint(CaptureWorkerCheckpointKind::kReady, 0)) {
          fail(WorkerStepResult::kEventFailure);
          return;
        }
        ready_for_token = true;
      }
    }
  } catch (...) {
    result.reason = CaptureWorkerExitReason::kInternalFailure;
    result.error = WDF_CAPTURE_ERROR_NATIVE_FAILURE;
    complete();
  }
}

}  // namespace windayflow::capture

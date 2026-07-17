#include "capture_worker.h"

#include <algorithm>
#include <chrono>
#include <limits>
#include <thread>
#include <utility>

namespace windayflow::capture {
namespace {

constexpr uint32_t kMaximumWorkerWaitMs = 60'000;
constexpr uint32_t kMaximumRollbackAttempts = 64;
constexpr uint32_t kMaximumRollbackDelayMs = 1'000;
constexpr int64_t kMediaFoundationTicksPerMillisecond = 10'000;

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

struct ChunkState {
  bool writer_started = false;
  int64_t start_steady_ms = 0;
  int64_t start_unix_ms = 0;
  int64_t latest_frame_offset_ms = 0;
  int64_t last_frame_timestamp_ticks = 0;
  int64_t end_timestamp_ticks = 1;
  uint32_t frame_count = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  CaptureVideoTiming timing;
};

struct PermitValidationContext {
  const CaptureSafetyCore* safety = nullptr;
  const PersistencePermit* permit = nullptr;
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

class ScopedBgraFrame final {
 public:
  ScopedBgraFrame() = default;
  ~ScopedBgraFrame() { SecureClear(&value); }

  ScopedBgraFrame(const ScopedBgraFrame&) = delete;
  ScopedBgraFrame& operator=(const ScopedBgraFrame&) = delete;

  BgraFrame value;
};

class ScopedSensitiveBytes final {
 public:
  ScopedSensitiveBytes() = default;
  ~ScopedSensitiveBytes() { SecureClear(&value); }

  ScopedSensitiveBytes(const ScopedSensitiveBytes&) = delete;
  ScopedSensitiveBytes& operator=(const ScopedSensitiveBytes&) = delete;

  std::vector<uint8_t> value;
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

int64_t MillisecondsToTicks(int64_t milliseconds) noexcept {
  if (milliseconds <= 0) {
    return 0;
  }
  if (milliseconds > std::numeric_limits<int64_t>::max() /
                         kMediaFoundationTicksPerMillisecond) {
    return std::numeric_limits<int64_t>::max();
  }
  return milliseconds * kMediaFoundationTicksPerMillisecond;
}

int64_t SaturatingAddTicks(int64_t value, int64_t delta) noexcept {
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
         configuration.rollback_retry_limit > 0 &&
         configuration.rollback_retry_limit <= kMaximumRollbackAttempts &&
         configuration.rollback_retry_delay_ms <= kMaximumRollbackDelayMs &&
         configuration.average_bitrate > 0 &&
         configuration.maximum_encoded_chunk_bytes > 0 &&
         configuration.maximum_encoded_chunk_bytes <= kMaximumH264ChunkBytes;
}

CaptureWorker::CaptureWorker(CaptureSafetyCore& safety,
                             CaptureEventQueue& events,
                             CaptureWorkerBackend& backend,
                             CaptureWorkerConfiguration configuration)
    : safety_(safety),
      events_(events),
      backend_(backend),
      configuration_(configuration) {}

CaptureWorker::~CaptureWorker() {
  static_cast<void>(RetryPendingCompensation(1));
}

CaptureWorkerRunResult CaptureWorker::last_result() const {
  std::lock_guard lock(result_mutex_);
  return last_result_;
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
                        PersistenceToken initial_token) noexcept {
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
    uint64_t handled_pause_epoch = 0;
    uint64_t observed_control_sequence = runtime.ReadControlSnapshot().sequence;

    auto discard_chunk = [&]() noexcept {
      backend_.ResetChunk();
      chunk = {};
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
        [&](ChunkFinalizationReason finalization_reason) -> WorkerStepResult {
      if (!chunk.writer_started || chunk.frame_count == 0) {
        discard_chunk();
        return WorkerStepResult::kOk;
      }

      ScopedSensitiveBytes encoded_storage;
      std::vector<uint8_t>& encoded_mp4 = encoded_storage.value;
      const AuthorizedStageResult finalize =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.FinalizeChunk(chunk.end_timestamp_ticks,
                                          &encoded_mp4);
          });
      if (!finalize.authorized) {
        SecureClear(&encoded_mp4);
        discard_chunk();
        return WorkerStepResult::kAuthorizationLost;
      }
      if (finalize.backend_result != CaptureWorkerBackendResult::kOk ||
          encoded_mp4.empty() ||
          encoded_mp4.size() > configuration_.maximum_encoded_chunk_bytes) {
        SecureClear(&encoded_mp4);
        discard_chunk();
        return finalize.backend_result == CaptureWorkerBackendResult::kOk
                   ? WorkerStepResult::kEncoderFailure
                   : MapBackendFailure(finalize.backend_result);
      }

      RequiredEventReservationGuard reservation(events_);
      if (!reservation) {
        SecureClear(&encoded_mp4);
        discard_chunk();
        return WorkerStepResult::kEventFailure;
      }

      std::string artifact_id;
      if (!backend_.CreateArtifactId(&artifact_id)) {
        reservation.Cancel();
        SecureClear(&encoded_mp4);
        discard_chunk();
        return WorkerStepResult::kInternalFailure;
      }

      const int64_t now_steady_ms = backend_.SteadyNowMilliseconds();
      const int64_t elapsed_ms =
          std::max<int64_t>(0, now_steady_ms - chunk.start_steady_ms);
      const int64_t encoded_duration_ms = CalculateEncodedDurationMs(
          chunk.frame_count, configuration_.policy.capture_interval_ms);
      const int64_t duration_ms = CalculateChunkDurationMs(
          elapsed_ms, encoded_duration_ms, chunk.latest_frame_offset_ms);
      const ChunkManifest manifest{
          artifact_id,
          chunk.start_unix_ms,
          SaturatingAddMilliseconds(chunk.start_unix_ms, duration_ms),
          chunk.frame_count,
          chunk.width,
          chunk.height,
          chunk.timing.frame_rate_numerator,
          chunk.timing.frame_rate_denominator,
          token.persistence_generation,
          token.target.target_epoch,
      };
      if (!IsValidChunkManifest(manifest)) {
        reservation.Cancel();
        SecureClear(&encoded_mp4);
        discard_chunk();
        return WorkerStepResult::kInternalFailure;
      }

      std::unique_ptr<CaptureWorkerPublication> publication;
      const AuthorizedStageResult prepare =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.PreparePublication(artifact_id, encoded_mp4,
                                               manifest, &publication);
          });
      SecureClear(&encoded_mp4);
      if (!prepare.authorized ||
          prepare.backend_result != CaptureWorkerBackendResult::kOk ||
          publication == nullptr) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        if (!compensated) {
          return WorkerStepResult::kCompensationFailure;
        }
        return !prepare.authorized ? WorkerStepResult::kAuthorizationLost
               : prepare.backend_result == CaptureWorkerBackendResult::kOk
                   ? WorkerStepResult::kInternalFailure
                   : MapBackendFailure(prepare.backend_result);
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

      const AuthorizedStageResult commit = ExecuteAuthorizedStage(
          token, [&]() noexcept { return publication->Commit(); });
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

      const std::optional<CaptureTargetIdentity> observed =
          backend_.ObserveTarget(token.target);
      PersistencePermit event_permit;
      if (observed.has_value()) {
        event_permit = safety_.AcquirePersistencePermit(token, *observed);
      }
      if (!event_permit) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        return compensated ? WorkerStepResult::kAuthorizationLost
                           : WorkerStepResult::kCompensationFailure;
      }
      const std::optional<CaptureTargetIdentity> observed_before_publication =
          backend_.ObserveTarget(token.target);
      if (!observed_before_publication.has_value() ||
          *observed_before_publication != *observed ||
          !safety_.IsPersistencePermitCurrent(event_permit)) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        return compensated ? WorkerStepResult::kAuthorizationLost
                           : WorkerStepResult::kCompensationFailure;
      }

      PermitValidationContext validation{&safety_, &event_permit};
      const uint64_t event_sequence = events_.PushReservedValidated(
          reservation.get(), WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
          EventStateFor(finalization_reason), WDF_CAPTURE_REASON_NONE,
          WDF_CAPTURE_ERROR_NONE, std::move(artifact_identifier),
          backend_.UnixNowMilliseconds(), token.persistence_generation,
          token.target.target_epoch, ValidatePersistencePermit, &validation);
      if (event_sequence == 0) {
        const bool compensated = compensate(&publication);
        reservation.Cancel();
        discard_chunk();
        if (!compensated) {
          return WorkerStepResult::kCompensationFailure;
        }
        return safety_.IsPersistencePermitCurrent(event_permit)
                   ? WorkerStepResult::kEventFailure
                   : WorkerStepResult::kAuthorizationLost;
      }

      publication->Acknowledge();
      publication.reset();
      ++result.committed_chunks;
      discard_chunk();
      return WorkerStepResult::kOk;
    };

    auto fail = [&](WorkerStepResult step) noexcept {
      discard_chunk();
      result = MakeFailure(step, result);
      complete();
    };

    for (;;) {
      CaptureRuntimeControlSnapshot control = runtime.ReadControlSnapshot();
      observed_control_sequence = control.sequence;

      if (control.stop_requested) {
        const WorkerStepResult stopped =
            finalize_chunk(ChunkFinalizationReason::kStop);
        if (stopped != WorkerStepResult::kOk) {
          fail(stopped);
          return;
        }
        result.reason = CaptureWorkerExitReason::kStopped;
        result.error = WDF_CAPTURE_ERROR_NONE;
        complete();
        return;
      }

      if (control.pause_epoch != handled_pause_epoch) {
        const WorkerStepResult paused =
            finalize_chunk(ChunkFinalizationReason::kPause);
        if (paused != WorkerStepResult::kOk &&
            paused != WorkerStepResult::kAuthorizationLost) {
          fail(paused);
          return;
        }
        backend_.ResetAcquisition();
        topology_available = false;
        handled_pause_epoch = control.pause_epoch;
        // A folded Resume still advances the token while the latest state is
        // paused.
        if (control.replacement_token.has_value()) {
          token = *control.replacement_token;
        } else if (!control.pause_requested) {
          fail(WorkerStepResult::kInternalFailure);
          return;
        }
        if (!control.pause_requested) {
          schedule.Reset(backend_.SteadyNowMilliseconds());
          continue;
        }
      }

      if (control.pause_requested) {
        for (;;) {
          const std::optional<CaptureRuntimeControlSnapshot> changed =
              runtime.WaitForControlChange(observed_control_sequence,
                                           kMaximumWorkerWaitMs);
          if (!changed.has_value()) {
            continue;
          }
          control = *changed;
          observed_control_sequence = control.sequence;
          if (control.stop_requested) {
            result.reason = CaptureWorkerExitReason::kStopped;
            result.error = WDF_CAPTURE_ERROR_NONE;
            complete();
            return;
          }
          if (control.pause_requested) {
            continue;
          }
          if (!control.replacement_token.has_value()) {
            fail(WorkerStepResult::kInternalFailure);
            return;
          }
          token = *control.replacement_token;
          schedule.Reset(backend_.SteadyNowMilliseconds());
          break;
        }
        continue;
      }

      const AuthorizedStageResult current = ExecuteAuthorizedStage(
          token, []() noexcept { return CaptureWorkerBackendResult::kOk; });
      if (!current.authorized) {
        fail(WorkerStepResult::kAuthorizationLost);
        return;
      }

      if (!topology_available) {
        const AuthorizedStageResult initialized =
            ExecuteAuthorizedStage(token, [&]() noexcept {
              return backend_.InitializeAcquisition(token.target);
            });
        if (!initialized.authorized) {
          fail(WorkerStepResult::kAuthorizationLost);
          return;
        }
        if (initialized.backend_result ==
            CaptureWorkerBackendResult::kRebuildRequired) {
          backend_.ResetAcquisition();
          static_cast<void>(runtime.WaitForControlChange(
              observed_control_sequence, configuration_.topology_retry_ms));
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
            finalize_chunk(ChunkFinalizationReason::kRegular);
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
        fail(WorkerStepResult::kAuthorizationLost);
        return;
      }
      if (acquired.backend_result == CaptureWorkerBackendResult::kTimeout) {
        continue;
      }
      if (acquired.backend_result ==
          CaptureWorkerBackendResult::kRebuildRequired) {
        const WorkerStepResult interrupted =
            finalize_chunk(ChunkFinalizationReason::kRegular);
        if (interrupted != WorkerStepResult::kOk) {
          fail(interrupted);
          return;
        }
        backend_.ResetAcquisition();
        topology_available = false;
        static_cast<void>(runtime.WaitForControlChange(
            observed_control_sequence, configuration_.topology_retry_ms));
        continue;
      }
      if (acquired.backend_result != CaptureWorkerBackendResult::kOk) {
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
        fail(WorkerStepResult::kAuthorizationLost);
        return;
      }
      if (transformed.backend_result != CaptureWorkerBackendResult::kOk ||
          !IsValidBgraFrame(transformed_frame)) {
        fail(transformed.backend_result == CaptureWorkerBackendResult::kOk
                 ? WorkerStepResult::kDeviceFailure
                 : MapBackendFailure(transformed.backend_result));
        return;
      }

      if (chunk.writer_started && (chunk.width != transformed_frame.width ||
                                   chunk.height != transformed_frame.height)) {
        const WorkerStepResult resized =
            finalize_chunk(ChunkFinalizationReason::kRegular);
        if (resized != WorkerStepResult::kOk) {
          fail(resized);
          return;
        }
      }

      const int64_t frame_steady_ms = backend_.SteadyNowMilliseconds();
      const int64_t frame_unix_ms = backend_.UnixNowMilliseconds();
      int64_t frame_offset_ms = 0;
      int64_t frame_timestamp_ticks = 0;
      bool begin_writer = !chunk.writer_started;
      CaptureVideoTiming timing =
          VideoTimingForIntervalMs(configuration_.policy.capture_interval_ms);
      if (!begin_writer) {
        frame_offset_ms =
            std::max<int64_t>(0, frame_steady_ms - chunk.start_steady_ms);
        frame_timestamp_ticks = MillisecondsToTicks(frame_offset_ms);
        if (frame_timestamp_ticks <= chunk.last_frame_timestamp_ticks) {
          frame_timestamp_ticks =
              SaturatingAddTicks(chunk.last_frame_timestamp_ticks, 1);
        }
        timing = chunk.timing;
      }

      const MfH264ChunkWriterConfig writer_configuration{
          transformed_frame.width,
          transformed_frame.height,
          timing.frame_rate_numerator,
          timing.frame_rate_denominator,
          configuration_.average_bitrate,
          configuration_.maximum_encoded_chunk_bytes,
      };
      if (begin_writer) {
        const AuthorizedStageResult begun =
            ExecuteAuthorizedStage(token, [&]() noexcept {
              return backend_.BeginChunk(writer_configuration);
            });
        if (!begun.authorized) {
          fail(WorkerStepResult::kAuthorizationLost);
          return;
        }
        if (begun.backend_result != CaptureWorkerBackendResult::kOk) {
          fail(MapBackendFailure(begun.backend_result));
          return;
        }
      }
      const AuthorizedStageResult encoded =
          ExecuteAuthorizedStage(token, [&]() noexcept {
            return backend_.EncodeFrame(transformed_frame.pixels,
                                        frame_timestamp_ticks);
          });
      SecureClear(&transformed_frame);
      if (!encoded.authorized) {
        fail(WorkerStepResult::kAuthorizationLost);
        return;
      }
      if (encoded.backend_result != CaptureWorkerBackendResult::kOk) {
        fail(MapBackendFailure(encoded.backend_result));
        return;
      }

      if (begin_writer) {
        chunk.writer_started = true;
        chunk.start_steady_ms = frame_steady_ms;
        chunk.start_unix_ms = frame_unix_ms;
        chunk.width = writer_configuration.width;
        chunk.height = writer_configuration.height;
        chunk.timing = timing;
      }
      chunk.latest_frame_offset_ms =
          std::max(chunk.latest_frame_offset_ms, frame_offset_ms);
      chunk.last_frame_timestamp_ticks = frame_timestamp_ticks;
      chunk.end_timestamp_ticks = SaturatingAddTicks(
          frame_timestamp_ticks, chunk.timing.frame_duration_ticks);
      if (chunk.frame_count < std::numeric_limits<uint32_t>::max()) {
        ++chunk.frame_count;
      }
      ++result.encoded_frames;
    }
  } catch (...) {
    result.reason = CaptureWorkerExitReason::kInternalFailure;
    result.error = WDF_CAPTURE_ERROR_NATIVE_FAILURE;
    complete();
  }
}

}  // namespace windayflow::capture

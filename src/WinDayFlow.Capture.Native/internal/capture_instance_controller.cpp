#include "capture_instance_controller.h"

#include <chrono>
#include <condition_variable>
#include <limits>
#include <optional>
#include <stdexcept>
#include <utility>

namespace windayflow::capture {
namespace {

constexpr size_t kEnabledControlReservationCount = 3;

int64_t CurrentUnixMilliseconds() noexcept {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::system_clock::now().time_since_epoch())
      .count();
}

uint32_t RemainingMilliseconds(
    std::chrono::steady_clock::time_point deadline) noexcept {
  const auto now = std::chrono::steady_clock::now();
  if (now >= deadline) {
    return 0;
  }
  const auto remaining =
      std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now)
          .count();
  return remaining >= std::numeric_limits<uint32_t>::max()
             ? std::numeric_limits<uint32_t>::max()
             : static_cast<uint32_t>(remaining);
}

wdf_capture_result MapAdmissionResult(
    CaptureCommandAdmissionResult result) noexcept {
  switch (result) {
    case CaptureCommandAdmissionResult::kOk:
      return WDF_CAPTURE_RESULT_OK;
    case CaptureCommandAdmissionResult::kInvalidArgument:
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    case CaptureCommandAdmissionResult::kPolicyBlocked:
      return WDF_CAPTURE_RESULT_POLICY_BLOCKED;
    case CaptureCommandAdmissionResult::kAdmissionRejected:
      return WDF_CAPTURE_RESULT_ADMISSION_REJECTED;
    case CaptureCommandAdmissionResult::kGenerationExhausted:
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    case CaptureCommandAdmissionResult::kInternalError:
    default:
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

const char* WorkerExitDetail(CaptureWorkerExitReason reason) noexcept {
  switch (reason) {
    case CaptureWorkerExitReason::kStopped:
      return "Capture worker stopped and joined.";
    case CaptureWorkerExitReason::kAuthorizationLost:
      return "Capture worker exited because runtime authorization was lost.";
    case CaptureWorkerExitReason::kInvalidConfiguration:
      return "Capture worker exited because its configuration was invalid.";
    case CaptureWorkerExitReason::kDeviceFailure:
      return "Capture worker exited because display acquisition failed.";
    case CaptureWorkerExitReason::kEncoderFailure:
      return "Capture worker exited because video encoding failed.";
    case CaptureWorkerExitReason::kStorageFailure:
      return "Capture worker exited because chunk storage failed.";
    case CaptureWorkerExitReason::kEventPublicationFailure:
      return "Capture worker exited because status publication failed.";
    case CaptureWorkerExitReason::kCompensationFailure:
      return "Capture worker exited because chunk rollback failed.";
    case CaptureWorkerExitReason::kNotRun:
    case CaptureWorkerExitReason::kInternalFailure:
    default:
      return "Capture worker exited because an internal failure occurred.";
  }
}

}  // namespace

struct CaptureInstanceController::RunRecord {
  explicit RunRecord(uint64_t run_identifier) : run_id(run_identifier) {}

  std::mutex mutex;
  std::condition_variable changed;
  const uint64_t run_id;
  std::optional<CaptureEventReservation> stopping_reservation;
  std::optional<CaptureEventReservation> stopped_reservation;
  std::optional<CaptureEventReservation> error_reservation;
  bool start_released = false;
  bool start_cancelled = false;
  bool stop_requested = false;
  bool wait_leader = false;
  bool completed = false;
  bool worker_exited = false;
  bool control_event_failed = false;
  bool error_published = false;
  bool revoke_finalized = false;
  bool fatal_exit = false;
  uint64_t expected_pause_epoch = 0;
  wdf_capture_reason pause_reason = WDF_CAPTURE_REASON_USER_PAUSED;
  wdf_capture_reason stop_reason = WDF_CAPTURE_REASON_USER_STOPPED;
  wdf_capture_result cached_result = WDF_CAPTURE_RESULT_INVALID_STATE;
  CaptureWorkerRunResult worker_result;
};

CaptureInstanceController::CaptureInstanceController(
    CaptureInstanceControllerConfiguration configuration,
    std::unique_ptr<CaptureWorkerBackend> backend,
    CaptureEventAppendHook event_append_hook,
    CaptureRuntimeOwner::WaitStoppedExitHook wait_stopped_exit_hook)
    : events_(configuration.event_queue_capacity, event_append_hook),
      backend_(std::move(backend)),
      worker_(configuration.activation_mode == CaptureActivationMode::kEnabled &&
                      backend_ != nullptr &&
                      IsValidCaptureWorkerConfiguration(configuration.worker)
                  ? std::make_unique<CaptureWorker>(
                        safety_, events_, *backend_, configuration.worker)
                  : nullptr),
      activation_mode_(configuration.activation_mode),
      state_(configuration.activation_mode == CaptureActivationMode::kEnabled
                 ? WDF_CAPTURE_STATE_STOPPED
                 : WDF_CAPTURE_STATE_UNAVAILABLE),
      runtime_(std::move(wait_stopped_exit_hook)) {
  if (activation_mode_ == CaptureActivationMode::kEnabled && worker_ == nullptr) {
    throw std::invalid_argument(
        "Enabled capture controller requires a valid worker backend and configuration.");
  }
  const bool published =
      activation_mode_ == CaptureActivationMode::kEnabled
          ? PublishStateUnderLock(WDF_CAPTURE_STATE_STOPPED,
                                  WDF_CAPTURE_REASON_NONE,
                                  "Capture controller initialized.")
          : PublishStateUnderLock(
                WDF_CAPTURE_STATE_UNAVAILABLE,
                WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE,
                "Capture controller initialized with live capture disabled.");
  if (!published) {
    throw std::runtime_error("Capture controller initial event failed.");
  }
}

CaptureInstanceController::~CaptureInstanceController() { Shutdown(); }

wdf_capture_result CaptureInstanceController::UpdateTiming(
    uint32_t capture_interval_ms, uint32_t chunk_duration_ms) noexcept {
  try {
    std::lock_guard lock(mutex_);
    if (activation_mode_ != CaptureActivationMode::kEnabled ||
        worker_ == nullptr) {
      return WDF_CAPTURE_RESULT_NOT_IMPLEMENTED;
    }
    if (shutting_down_ || state_ != WDF_CAPTURE_STATE_STOPPED) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    return worker_->UpdateTiming(capture_interval_ms, chunk_duration_ms)
               ? WDF_CAPTURE_RESULT_OK
               : WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

CaptureSafetyUpdateResult
CaptureInstanceController::UpdateRuntimeAuthorization(
    const RuntimeAuthorization& authorization,
    uint64_t* persistence_generation) {
  std::lock_guard lock(mutex_);
  const PrivacyDecision decision =
      EvaluatePrivacyContext(authorization.privacy);
  const wdf_capture_reason pause_reason =
      decision.allowed
          ? state_ == WDF_CAPTURE_STATE_PAUSING
                ? WDF_CAPTURE_REASON_NONE
                : WDF_CAPTURE_REASON_POLICY_BLOCKED
          : decision.reason;
  if (shutting_down_ || state_ == WDF_CAPTURE_STATE_STOPPING) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  const CaptureSafetyUpdateTicket ticket =
      active_run_ != nullptr && state_ != WDF_CAPTURE_STATE_PAUSED
          ? safety_.BeginSealedAuthorizationUpdate()
          : safety_.BeginAuthorizationUpdate();
  if (!PauseForAuthorizationChangeUnderLock(pause_reason)) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  runtime_.NotifyAuthorizationChanged();
  return safety_.CompleteRuntimeAuthorization(ticket, authorization,
                                               persistence_generation);
}

CaptureSafetyUpdateResult CaptureInstanceController::UpdatePrivacyContext(
    const PrivacyContext& privacy, uint64_t* persistence_generation) {
  std::lock_guard lock(mutex_);
  const PrivacyDecision decision = EvaluatePrivacyContext(privacy);
  const wdf_capture_reason pause_reason =
      decision.allowed
          ? state_ == WDF_CAPTURE_STATE_PAUSING
                ? WDF_CAPTURE_REASON_NONE
                : WDF_CAPTURE_REASON_POLICY_BLOCKED
          : decision.reason;
  if (shutting_down_ || state_ == WDF_CAPTURE_STATE_STOPPING) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  const CaptureSafetyUpdateTicket ticket =
      active_run_ != nullptr && state_ != WDF_CAPTURE_STATE_PAUSED
          ? safety_.BeginSealedAuthorizationUpdate()
          : safety_.BeginAuthorizationUpdate();
  if (!PauseForAuthorizationChangeUnderLock(pause_reason)) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  runtime_.NotifyAuthorizationChanged();
  return safety_.CompleteLegacyPrivacyContext(ticket, privacy,
                                               persistence_generation);
}

wdf_capture_result CaptureInstanceController::InvalidateRuntimeAuthorization(
    uint64_t* authorization_epoch) noexcept {
  try {
    if (authorization_epoch == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *authorization_epoch = 0;
    std::lock_guard lock(mutex_);
    if (shutting_down_ || state_ == WDF_CAPTURE_STATE_STOPPING) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    const uint64_t epoch = safety_.InvalidateAuthorizationAdmission();
    if (!PauseForAuthorizationChangeUnderLock(
            WDF_CAPTURE_REASON_POLICY_BLOCKED)) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    runtime_.NotifyAuthorizationChanged();
    if (epoch == 0) {
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    }
    *authorization_epoch = epoch;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

CaptureSafetyUpdateResult
CaptureInstanceController::RevokeRuntimeAuthorization(
    uint64_t* persistence_generation) {
  std::unique_lock lock(mutex_);
  if (shutting_down_ || state_ == WDF_CAPTURE_STATE_STOPPING) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  if (active_run_ != nullptr && state_ != WDF_CAPTURE_STATE_PAUSED) {
    static_cast<void>(safety_.BeginSealedAuthorizationUpdate());
  }
  if (!PauseForAuthorizationChangeUnderLock(
          WDF_CAPTURE_REASON_POLICY_BLOCKED)) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  const uint64_t run_id =
      active_run_ == nullptr ? 0 : active_run_->run_id;
  const CaptureSafetyUpdateResult result = safety_.Revoke(persistence_generation);
  runtime_.NotifyAuthorizationChanged();
  lock.unlock();
  if (result == CaptureSafetyUpdateResult::kOk && run_id != 0) {
    static_cast<void>(
        RequestStopCore(WDF_CAPTURE_REASON_POLICY_BLOCKED, run_id));
  }
  return result;
}

wdf_capture_result CaptureInstanceController::IssueAdmission(
    CaptureCommand command, uint64_t expected_persistence_generation,
    uint64_t expected_target_epoch, CaptureCommandAdmission* admission) {
  std::lock_guard lock(mutex_);
  if (shutting_down_) {
    return WDF_CAPTURE_RESULT_INVALID_STATE;
  }
  const bool valid_state =
      command == CaptureCommand::kStart
          ? state_ == WDF_CAPTURE_STATE_STOPPED ||
                state_ == WDF_CAPTURE_STATE_UNAVAILABLE
          : state_ == WDF_CAPTURE_STATE_PAUSED;
  if (!valid_state) {
    return WDF_CAPTURE_RESULT_INVALID_STATE;
  }
  const uint64_t owner_epoch = runtime_.owner_epoch();
  if (owner_epoch == 0) {
    return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
  }
  return MapAdmissionResult(safety_.IssueCommandAdmission(
      command, expected_persistence_generation, expected_target_epoch,
      owner_epoch, admission));
}

wdf_capture_result CaptureInstanceController::StartAuthorized(
    const CaptureCommandAdmission& admission) noexcept {
  return StartOrResumeAuthorized(admission, CaptureCommand::kStart);
}

wdf_capture_result CaptureInstanceController::ResumeAuthorized(
    const CaptureCommandAdmission& admission) noexcept {
  return StartOrResumeAuthorized(admission, CaptureCommand::kResume);
}

wdf_capture_result CaptureInstanceController::StartOrResumeAuthorized(
    const CaptureCommandAdmission& admission,
    CaptureCommand command) noexcept {
  try {
    std::unique_lock lock(mutex_);
    if (shutting_down_) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    const uint64_t owner_epoch = runtime_.owner_epoch();
    if (owner_epoch == 0) {
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    }
    CaptureCommandAdmissionPermit permit;
    const CaptureCommandAdmissionResult admission_result =
        safety_.AcquireCommandAdmissionPermit(admission, command, owner_epoch,
                                              &permit);
    if (admission_result != CaptureCommandAdmissionResult::kOk) {
      return MapAdmissionResult(admission_result);
    }

    const bool valid_state =
        command == CaptureCommand::kStart
            ? state_ == WDF_CAPTURE_STATE_STOPPED ||
                  state_ == WDF_CAPTURE_STATE_UNAVAILABLE
            : state_ == WDF_CAPTURE_STATE_PAUSED;
    if (!valid_state) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    if (activation_mode_ == CaptureActivationMode::kDisabled) {
      return WDF_CAPTURE_RESULT_NOT_IMPLEMENTED;
    }
    if (worker_ == nullptr) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }

    if (command == CaptureCommand::kResume) {
      if (!runtime_.Resume(std::move(permit))) {
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      if (!PublishStateUnderLock(WDF_CAPTURE_STATE_RESUMING,
                                 WDF_CAPTURE_REASON_NONE,
                                 "Capture worker resuming.")) {
        const uint64_t run_id = active_run_->run_id;
        lock.unlock();
        static_cast<void>(
            RequestStopCore(WDF_CAPTURE_REASON_BACKEND_FAULT, run_id));
        return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
      }
      return WDF_CAPTURE_RESULT_OK;
    }

    if (events_.capacity() < kEnabledControlReservationCount + 1U) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    const uint64_t run_id = AllocateRunIdUnderLock();
    if (run_id == 0) {
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    }
    auto run = std::make_shared<RunRecord>(run_id);
    CaptureEventReservation stopping = events_.ReserveRequiredEvent();
    CaptureEventReservation stopped = events_.ReserveRequiredEvent();
    CaptureEventReservation error = events_.ReserveRequiredEvent();
    if (!stopping || !stopped || !error) {
      if (stopping) {
        static_cast<void>(events_.CancelReservation(&stopping));
      }
      if (stopped) {
        static_cast<void>(events_.CancelReservation(&stopped));
      }
      if (error) {
        static_cast<void>(events_.CancelReservation(&error));
      }
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    run->stopping_reservation.emplace(std::move(stopping));
    run->stopped_reservation.emplace(std::move(stopped));
    run->error_reservation.emplace(std::move(error));
    active_run_ = run;
    state_ = WDF_CAPTURE_STATE_STARTING;
    bool started = false;
    try {
      started = runtime_.Start(
          std::move(permit),
          [this, run, run_id](CaptureRuntimeOwner& runtime,
                              PersistenceToken token) noexcept {
            {
              std::unique_lock run_lock(run->mutex);
              run->changed.wait(run_lock,
                                [&run] { return run->start_released; });
              if (run->start_cancelled) {
                return;
              }
            }
            worker_->Run(
                runtime, std::move(token),
                [this, run_id](const CaptureWorkerCheckpoint& checkpoint) {
                  return OnWorkerCheckpoint(run_id, checkpoint);
                });
          },
          [this, run, run_id]() noexcept {
            CaptureWorkerRunResult result;
            {
              std::lock_guard run_lock(run->mutex);
              if (run->start_cancelled) {
                result.reason = CaptureWorkerExitReason::kStopped;
              } else {
                result = worker_->last_result();
              }
            }
            OnWorkerExited(run_id, result);
          });
    } catch (...) {
      active_run_.reset();
      state_ = WDF_CAPTURE_STATE_STOPPED;
      lock.unlock();
      CancelRunReservations(run);
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    if (!started) {
      active_run_.reset();
      state_ = WDF_CAPTURE_STATE_STOPPED;
      lock.unlock();
      CancelRunReservations(run);
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    if (!PublishStateUnderLock(WDF_CAPTURE_STATE_STARTING,
                               WDF_CAPTURE_REASON_NONE,
                               "Capture worker starting.")) {
      {
        std::lock_guard run_lock(run->mutex);
        run->start_cancelled = true;
        run->start_released = true;
      }
      run->changed.notify_all();
      lock.unlock();
      static_cast<void>(
          RequestStopCore(WDF_CAPTURE_REASON_BACKEND_FAULT, run_id));
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    {
      std::lock_guard run_lock(run->mutex);
      run->start_released = true;
    }
    run->changed.notify_all();
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

wdf_capture_result CaptureInstanceController::Pause() noexcept {
  try {
    std::unique_lock lock(mutex_);
    if (shutting_down_ || active_run_ == nullptr ||
        (state_ != WDF_CAPTURE_STATE_STARTING &&
         state_ != WDF_CAPTURE_STATE_RECORDING &&
         state_ != WDF_CAPTURE_STATE_RESUMING)) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    const CaptureRuntimePauseResult result = runtime_.RequestPause();
    if (result == CaptureRuntimePauseResult::kNotRunning) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    if (result == CaptureRuntimePauseResult::kAlreadyPaused) {
      return WDF_CAPTURE_RESULT_OK;
    }
    const uint64_t pause_epoch = runtime_.ReadControlSnapshot().pause_epoch;
    {
      std::lock_guard run_lock(active_run_->mutex);
      active_run_->expected_pause_epoch = pause_epoch;
      active_run_->pause_reason = WDF_CAPTURE_REASON_USER_PAUSED;
    }
    if (PublishStateUnderLock(WDF_CAPTURE_STATE_PAUSING,
                              WDF_CAPTURE_REASON_USER_PAUSED,
                              "Capture worker pausing.")) {
      return WDF_CAPTURE_RESULT_OK;
    }
    const uint64_t run_id = active_run_->run_id;
    lock.unlock();
    static_cast<void>(
        RequestStopCore(WDF_CAPTURE_REASON_BACKEND_FAULT, run_id));
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

wdf_capture_result CaptureInstanceController::RequestStop(
    wdf_capture_reason reason) noexcept {
  return RequestStopCore(reason);
}

wdf_capture_result CaptureInstanceController::RequestStopCore(
    wdf_capture_reason reason, uint64_t expected_run_id) noexcept {
  try {
    std::shared_ptr<RunRecord> run;
    {
      std::lock_guard lock(mutex_);
      if (expected_run_id != 0 &&
          (active_run_ == nullptr ||
           active_run_->run_id != expected_run_id)) {
        return WDF_CAPTURE_RESULT_OK;
      }
      run = active_run_;
      if (run == nullptr) {
        if (state_ == WDF_CAPTURE_STATE_STOPPED && safety_.revoked()) {
          return WDF_CAPTURE_RESULT_OK;
        }
        run = std::make_shared<RunRecord>(0);
        CaptureEventReservation stopping = events_.ReserveRequiredEvent();
        CaptureEventReservation stopped = events_.ReserveRequiredEvent();
        if (stopping) {
          run->stopping_reservation.emplace(std::move(stopping));
        } else {
          run->control_event_failed = true;
        }
        if (stopped) {
          run->stopped_reservation.emplace(std::move(stopped));
        } else {
          run->control_event_failed = true;
        }
        run->worker_exited = true;
        run->worker_result.reason = CaptureWorkerExitReason::kStopped;
        active_run_ = run;
      }
      safety_.InvalidatePendingCommandAdmission();
      static_cast<void>(runtime_.RequestStop());
      std::lock_guard run_lock(run->mutex);
      if (run->stop_requested) {
        return WDF_CAPTURE_RESULT_OK;
      }
      run->stop_requested = true;
      run->stop_reason = reason;
      state_ = WDF_CAPTURE_STATE_STOPPING;
      if (!run->stopping_reservation.has_value() ||
          !PublishReservedStateUnderLock(&*run->stopping_reservation,
                                         WDF_CAPTURE_STATE_STOPPING, reason,
                                         "Capture worker stopping.")) {
        run->control_event_failed = true;
      }
    }
    run->changed.notify_all();
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

wdf_capture_result CaptureInstanceController::WaitStopped(
    uint32_t timeout_ms) noexcept {
  std::shared_ptr<RunRecord> run;
  bool owns_wait_leadership = false;
  try {
    {
      std::lock_guard lock(mutex_);
      run = active_run_;
      if (run == nullptr) {
        if (state_ == WDF_CAPTURE_STATE_UNAVAILABLE) {
          return WDF_CAPTURE_RESULT_OK;
        }
        return state_ == WDF_CAPTURE_STATE_STOPPED
                   ? last_stop_result_
                   : WDF_CAPTURE_RESULT_INVALID_STATE;
      }
    }

    const auto deadline = std::chrono::steady_clock::now() +
                          std::chrono::milliseconds(timeout_ms);
    for (;;) {
      std::unique_lock run_lock(run->mutex);
      if (!run->stop_requested) {
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      if (run->completed) {
        return run->cached_result;
      }
      if (!run->wait_leader) {
        run->wait_leader = true;
        owns_wait_leadership = true;
        run_lock.unlock();
        break;
      }
      if (!run->changed.wait_until(run_lock, deadline, [&run] {
            return run->completed || !run->wait_leader;
          })) {
        return WDF_CAPTURE_RESULT_TIMEOUT;
      }
    }

    auto relinquish_leadership =
        [&run, &owns_wait_leadership]() noexcept {
      {
        std::lock_guard lock(run->mutex);
        run->wait_leader = false;
      }
      owns_wait_leadership = false;
      run->changed.notify_all();
    };

    const CaptureRuntimeWaitResult runtime_result =
        runtime_.WaitStopped(RemainingMilliseconds(deadline));
    if (runtime_result == CaptureRuntimeWaitResult::kTimeout) {
      relinquish_leadership();
      return WDF_CAPTURE_RESULT_TIMEOUT;
    }

    uint64_t persistence_generation = 0;
    if (!safety_.FinalizeRevoke(RemainingMilliseconds(deadline),
                                &persistence_generation)) {
      relinquish_leadership();
      return WDF_CAPTURE_RESULT_TIMEOUT;
    }
    static_cast<void>(persistence_generation);

    wdf_capture_result final_result = WDF_CAPTURE_RESULT_OK;
    {
      std::lock_guard lock(mutex_);
      if (active_run_ != nullptr && active_run_->run_id == run->run_id) {
        std::lock_guard run_lock(run->mutex);
        if (runtime_result == CaptureRuntimeWaitResult::kWorkerFailed ||
            run->fatal_exit) {
          final_result = WDF_CAPTURE_RESULT_INTERNAL_ERROR;
        }
        run->revoke_finalized = true;
        if (!run->stopped_reservation.has_value() ||
            !PublishReservedStateUnderLock(
                &*run->stopped_reservation, WDF_CAPTURE_STATE_STOPPED,
                run->stop_reason,
                WorkerExitDetail(run->worker_result.reason))) {
          run->control_event_failed = true;
        }
        if (run->control_event_failed) {
          final_result = WDF_CAPTURE_RESULT_INTERNAL_ERROR;
        }
        CancelRunReservationsUnderRunLock(run);
        if (terminal_finalization_hook_ != nullptr) {
          terminal_finalization_hook_();
        }
        run->cached_result = final_result;
        run->completed = true;
        run->wait_leader = false;
        owns_wait_leadership = false;
        last_stop_result_ = final_result;
        state_ = WDF_CAPTURE_STATE_STOPPED;
        active_run_.reset();
      }
    }
    run->changed.notify_all();
    return final_result;
  } catch (...) {
    if (owns_wait_leadership && run != nullptr) {
      try {
        std::lock_guard run_lock(run->mutex);
        if (!run->completed) {
          run->wait_leader = false;
        }
      } catch (...) {
      }
      run->changed.notify_all();
    }
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

CaptureEventReadResult CaptureInstanceController::Poll(
    uint32_t timeout_ms, wdf_capture_event_v1* event, char* detail_utf8,
    uint32_t detail_utf8_capacity, uint32_t* detail_utf8_required) {
  return events_.Read(timeout_ms, event, detail_utf8, detail_utf8_capacity,
                      detail_utf8_required);
}

void CaptureInstanceController::Shutdown() noexcept {
  try {
    {
      std::lock_guard lock(mutex_);
      if (events_closed_) {
        return;
      }
      shutting_down_ = true;
    }
    static_cast<void>(RequestStopCore(WDF_CAPTURE_REASON_SHUTDOWN));
    while (WaitStopped(std::numeric_limits<uint32_t>::max()) ==
           WDF_CAPTURE_RESULT_TIMEOUT) {
    }
    if (!safety_.revoked()) {
      uint64_t persistence_generation = 0;
      static_cast<void>(safety_.FinalizeRevoke(
          std::numeric_limits<uint32_t>::max(), &persistence_generation));
    }
    events_.Close();
    std::lock_guard lock(mutex_);
    events_closed_ = true;
  } catch (...) {
    runtime_.Shutdown();
    events_.Close();
    std::lock_guard lock(mutex_);
    events_closed_ = true;
  }
}

wdf_capture_state CaptureInstanceController::state() const noexcept {
  std::lock_guard lock(mutex_);
  return state_;
}

uint64_t CaptureInstanceController::active_run_id() const noexcept {
  std::lock_guard lock(mutex_);
  return active_run_ == nullptr ? 0 : active_run_->run_id;
}

uint64_t CaptureInstanceController::join_count() const {
  return runtime_.join_count();
}

size_t CaptureInstanceController::reserved_event_count() const {
  return events_.reserved_size();
}

CaptureSafetyObservableSnapshot
CaptureInstanceController::safety_snapshot() const {
  return safety_.observable_snapshot();
}

bool CaptureInstanceController::OnWorkerCheckpoint(
    uint64_t run_id, const CaptureWorkerCheckpoint& checkpoint) noexcept {
  try {
    if (worker_checkpoint_hook_ != nullptr) {
      worker_checkpoint_hook_(checkpoint);
    }
    std::lock_guard lock(mutex_);
    if (active_run_ == nullptr || active_run_->run_id != run_id) {
      return false;
    }
    if (shutting_down_ || state_ == WDF_CAPTURE_STATE_STOPPING) {
      return true;
    }
    switch (checkpoint.kind) {
      case CaptureWorkerCheckpointKind::kReady:
        if (state_ == WDF_CAPTURE_STATE_STARTING ||
            state_ == WDF_CAPTURE_STATE_RESUMING) {
          return PublishStateUnderLock(WDF_CAPTURE_STATE_RECORDING,
                                       WDF_CAPTURE_REASON_NONE,
                                       "Capture worker ready.");
        }
        return state_ == WDF_CAPTURE_STATE_RECORDING ||
               state_ == WDF_CAPTURE_STATE_PAUSING ||
               state_ == WDF_CAPTURE_STATE_PAUSED;
      case CaptureWorkerCheckpointKind::kPaused: {
        wdf_capture_reason pause_reason = WDF_CAPTURE_REASON_USER_PAUSED;
        {
          std::lock_guard run_lock(active_run_->mutex);
          if (checkpoint.pause_epoch == 0 ||
              checkpoint.pause_epoch != active_run_->expected_pause_epoch) {
            return false;
          }
          pause_reason = active_run_->pause_reason;
        }
        if (state_ == WDF_CAPTURE_STATE_PAUSING) {
          return PublishStateUnderLock(WDF_CAPTURE_STATE_PAUSED,
                                       pause_reason,
                                       "Capture worker paused.");
        }
        return state_ == WDF_CAPTURE_STATE_PAUSED;
      }
    }
    return false;
  } catch (...) {
    return false;
  }
}

void CaptureInstanceController::OnWorkerExited(
    uint64_t run_id, CaptureWorkerRunResult result) noexcept {
  try {
    std::shared_ptr<RunRecord> run;
    bool request_stop = false;
    {
      std::lock_guard lock(mutex_);
      if (active_run_ == nullptr || active_run_->run_id != run_id) {
        return;
      }
      run = active_run_;
      std::lock_guard run_lock(run->mutex);
      run->worker_exited = true;
      run->worker_result = result;
      request_stop = !run->stop_requested;
      run->fatal_exit = result.reason != CaptureWorkerExitReason::kStopped;
      if (run->fatal_exit && !run->error_published) {
        const wdf_capture_error error =
            result.error == WDF_CAPTURE_ERROR_NONE
                ? WDF_CAPTURE_ERROR_NATIVE_FAILURE
                : result.error;
        if (!PublishReservedErrorUnderLock(
                &*run->error_reservation, error,
                WorkerExitDetail(result.reason))) {
          run->control_event_failed = true;
        }
        run->error_published = true;
        run->stop_requested = true;
        run->stop_reason = WDF_CAPTURE_REASON_BACKEND_FAULT;
        state_ = WDF_CAPTURE_STATE_FAULTED;
        request_stop = false;
      }
    }
    run->changed.notify_all();
    if (request_stop) {
      static_cast<void>(
          RequestStopCore(WDF_CAPTURE_REASON_BACKEND_FAULT, run_id));
    }
  } catch (...) {
  }
}

bool CaptureInstanceController::PublishStateUnderLock(
    wdf_capture_state state, wdf_capture_reason reason,
    const char* detail) noexcept {
  try {
    const CaptureSafetyObservableSnapshot snapshot =
        safety_.observable_snapshot();
    if (events_.Push(WDF_CAPTURE_EVENT_STATE_CHANGED, state, reason,
                     WDF_CAPTURE_ERROR_NONE, detail, CurrentUnixMilliseconds(),
                     snapshot.persistence_generation, snapshot.target_epoch) ==
        0) {
      return false;
    }
    state_ = state;
    return true;
  } catch (...) {
    return false;
  }
}

bool CaptureInstanceController::PublishReservedStateUnderLock(
    CaptureEventReservation* reservation, wdf_capture_state state,
    wdf_capture_reason reason, const char* detail) noexcept {
  try {
    const CaptureSafetyObservableSnapshot snapshot =
        safety_.observable_snapshot();
    return events_.PushReserved(
               reservation, WDF_CAPTURE_EVENT_STATE_CHANGED, state, reason,
               WDF_CAPTURE_ERROR_NONE, detail, CurrentUnixMilliseconds(),
               snapshot.persistence_generation, snapshot.target_epoch) != 0;
  } catch (...) {
    return false;
  }
}

bool CaptureInstanceController::PublishReservedErrorUnderLock(
    CaptureEventReservation* reservation, wdf_capture_error error,
    const char* detail) noexcept {
  try {
    const CaptureSafetyObservableSnapshot snapshot =
        safety_.observable_snapshot();
    return events_.PushReserved(
               reservation, WDF_CAPTURE_EVENT_ERROR,
               WDF_CAPTURE_STATE_FAULTED, WDF_CAPTURE_REASON_BACKEND_FAULT,
               error, detail, CurrentUnixMilliseconds(),
               snapshot.persistence_generation, snapshot.target_epoch) != 0;
  } catch (...) {
    return false;
  }
}

bool CaptureInstanceController::PauseForAuthorizationChangeUnderLock(
    wdf_capture_reason reason) noexcept {
  if (active_run_ == nullptr || state_ == WDF_CAPTURE_STATE_PAUSED) {
    return true;
  }
  if (state_ == WDF_CAPTURE_STATE_PAUSING) {
    std::lock_guard run_lock(active_run_->mutex);
    if (active_run_->pause_reason != WDF_CAPTURE_REASON_USER_PAUSED &&
        reason != WDF_CAPTURE_REASON_NONE) {
      active_run_->pause_reason = reason;
    }
    return true;
  }
  if (state_ != WDF_CAPTURE_STATE_STARTING &&
      state_ != WDF_CAPTURE_STATE_RECORDING &&
      state_ != WDF_CAPTURE_STATE_RESUMING) {
    return false;
  }
  const CaptureRuntimePauseResult pause = runtime_.RequestPause();
  if (pause == CaptureRuntimePauseResult::kNotRunning) {
    return false;
  }
  const uint64_t pause_epoch = runtime_.ReadControlSnapshot().pause_epoch;
  {
    std::lock_guard run_lock(active_run_->mutex);
    active_run_->expected_pause_epoch = pause_epoch;
    active_run_->pause_reason = reason;
  }
  if (pause == CaptureRuntimePauseResult::kAlreadyPaused ||
      PublishStateUnderLock(WDF_CAPTURE_STATE_PAUSING, reason,
                            "Capture worker pausing for authorization change.")) {
    return true;
  }
  safety_.InvalidatePendingCommandAdmission();
  static_cast<void>(runtime_.RequestStop());
  {
    std::lock_guard run_lock(active_run_->mutex);
    active_run_->stop_requested = true;
    active_run_->stop_reason = WDF_CAPTURE_REASON_BACKEND_FAULT;
    if (!PublishReservedStateUnderLock(
            &*active_run_->stopping_reservation,
            WDF_CAPTURE_STATE_STOPPING,
            WDF_CAPTURE_REASON_BACKEND_FAULT,
            "Capture worker stopping after control event failure.")) {
      active_run_->control_event_failed = true;
    }
  }
  state_ = WDF_CAPTURE_STATE_STOPPING;
  active_run_->changed.notify_all();
  return false;
}

uint64_t CaptureInstanceController::AllocateRunIdUnderLock() noexcept {
  if (run_id_exhausted_ || next_run_id_ == 0) {
    return 0;
  }
  const uint64_t run_id = next_run_id_;
  if (next_run_id_ == std::numeric_limits<uint64_t>::max()) {
    next_run_id_ = 0;
    run_id_exhausted_ = true;
  } else {
    ++next_run_id_;
  }
  return run_id;
}

void CaptureInstanceController::CancelRunReservations(
    const std::shared_ptr<RunRecord>& run) noexcept {
  if (run == nullptr) {
    return;
  }
  std::lock_guard lock(run->mutex);
  CancelRunReservationsUnderRunLock(run);
}

void CaptureInstanceController::CancelRunReservationsUnderRunLock(
    const std::shared_ptr<RunRecord>& run) noexcept {
  if (run->stopping_reservation.has_value() &&
      *run->stopping_reservation) {
    static_cast<void>(
        events_.CancelReservation(&*run->stopping_reservation));
  }
  if (run->stopped_reservation.has_value() && *run->stopped_reservation) {
    static_cast<void>(events_.CancelReservation(&*run->stopped_reservation));
  }
  if (run->error_reservation.has_value() && *run->error_reservation) {
    static_cast<void>(events_.CancelReservation(&*run->error_reservation));
  }
}

}  // namespace windayflow::capture

#ifndef WINDAYFLOW_CAPTURE_INSTANCE_CONTROLLER_H_
#define WINDAYFLOW_CAPTURE_INSTANCE_CONTROLLER_H_

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>

#include "capture_event_queue.h"
#include "capture_runtime_owner.h"
#include "capture_safety_core.h"
#include "capture_worker.h"

namespace windayflow::capture {

enum class CaptureActivationMode {
  kDisabled,
  kEnabled,
};

struct CaptureInstanceControllerConfiguration {
  CaptureActivationMode activation_mode = CaptureActivationMode::kDisabled;
  size_t event_queue_capacity = 64;
  CaptureWorkerConfiguration worker;
};

class CaptureInstanceControllerTestPeer;

class CaptureInstanceController final {
 public:
  CaptureInstanceController(
      CaptureInstanceControllerConfiguration configuration,
      std::unique_ptr<CaptureWorkerBackend> backend,
      CaptureEventAppendHook event_append_hook = nullptr,
      CaptureRuntimeOwner::WaitStoppedExitHook wait_stopped_exit_hook = {});
  ~CaptureInstanceController();

  CaptureInstanceController(const CaptureInstanceController&) = delete;
  CaptureInstanceController& operator=(const CaptureInstanceController&) =
      delete;

  CaptureSafetyUpdateResult UpdateRuntimeAuthorization(
      const RuntimeAuthorization& authorization,
      uint64_t* persistence_generation);
  CaptureSafetyUpdateResult UpdatePrivacyContext(
      const PrivacyContext& privacy, uint64_t* persistence_generation);
  wdf_capture_result InvalidateRuntimeAuthorization(
      uint64_t* authorization_epoch) noexcept;
  CaptureSafetyUpdateResult RevokeRuntimeAuthorization(
      uint64_t* persistence_generation);

  wdf_capture_result UpdateTiming(uint32_t capture_interval_ms,
                                  uint32_t chunk_duration_ms) noexcept;

  wdf_capture_result IssueAdmission(
      CaptureCommand command, uint64_t expected_persistence_generation,
      uint64_t expected_target_epoch, CaptureCommandAdmission* admission);
  wdf_capture_result StartAuthorized(
      const CaptureCommandAdmission& admission) noexcept;
  wdf_capture_result Pause() noexcept;
  wdf_capture_result ResumeAuthorized(
      const CaptureCommandAdmission& admission) noexcept;
  wdf_capture_result RequestStop(
      wdf_capture_reason reason = WDF_CAPTURE_REASON_USER_STOPPED) noexcept;
  wdf_capture_result WaitStopped(uint32_t timeout_ms) noexcept;

  CaptureEventReadResult Poll(uint32_t timeout_ms, wdf_capture_event_v1* event,
                              char* detail_utf8,
                              uint32_t detail_utf8_capacity,
                              uint32_t* detail_utf8_required);
  wdf_capture_result GetHealthSnapshot(
      wdf_capture_health_snapshot_v2* snapshot) const noexcept;
  void Shutdown() noexcept;

  wdf_capture_state state() const noexcept;
  uint64_t active_run_id() const noexcept;
  uint64_t join_count() const;
  size_t reserved_event_count() const;
  CaptureSafetyObservableSnapshot safety_snapshot() const;

 private:
  struct RunRecord;

  friend class CaptureInstanceControllerTestPeer;

  wdf_capture_result StartOrResumeAuthorized(
      const CaptureCommandAdmission& admission,
      CaptureCommand command) noexcept;
  wdf_capture_result RequestStopCore(
      wdf_capture_reason reason,
      uint64_t expected_run_id = 0) noexcept;
  bool OnWorkerCheckpoint(uint64_t run_id,
                          const CaptureWorkerCheckpoint& checkpoint) noexcept;
  void OnWorkerExited(uint64_t run_id,
                      CaptureWorkerRunResult result) noexcept;
  bool PublishStateUnderLock(wdf_capture_state state,
                             wdf_capture_reason reason,
                             const char* detail) noexcept;
  bool PublishReservedStateUnderLock(CaptureEventReservation* reservation,
                                     wdf_capture_state state,
                                     wdf_capture_reason reason,
                                     const char* detail) noexcept;
  bool PublishReservedErrorUnderLock(CaptureEventReservation* reservation,
                                     wdf_capture_error error,
                                     const char* detail) noexcept;
  bool PauseForAuthorizationChangeUnderLock(
      wdf_capture_reason reason) noexcept;
  uint64_t AllocateRunIdUnderLock() noexcept;
  void CancelRunReservations(const std::shared_ptr<RunRecord>& run) noexcept;
  void CancelRunReservationsUnderRunLock(
      const std::shared_ptr<RunRecord>& run) noexcept;

  // Declaration order is intentional: runtime joins before worker/backend
  // destruction and before callback state is released.
  CaptureEventQueue events_;
  CaptureSafetyCore safety_;
  std::unique_ptr<CaptureWorkerBackend> backend_;
  std::unique_ptr<CaptureWorker> worker_;
  const CaptureActivationMode activation_mode_;
  mutable std::mutex mutex_;
  wdf_capture_state state_ = WDF_CAPTURE_STATE_UNAVAILABLE;
  wdf_capture_reason reason_ = WDF_CAPTURE_REASON_NONE;
  std::atomic<uint64_t> state_revision_{1};
  std::shared_ptr<RunRecord> active_run_;
  uint64_t next_run_id_ = 1;
  bool run_id_exhausted_ = false;
  wdf_capture_result last_stop_result_ = WDF_CAPTURE_RESULT_OK;
  bool shutting_down_ = false;
  bool events_closed_ = false;
  void (*worker_checkpoint_hook_)(const CaptureWorkerCheckpoint&) = nullptr;
  void (*terminal_finalization_hook_)() = nullptr;
  CaptureRuntimeOwner runtime_;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_INSTANCE_CONTROLLER_H_

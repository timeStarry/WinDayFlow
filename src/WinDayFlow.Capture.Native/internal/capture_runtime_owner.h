#ifndef WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_
#define WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>
#include <utility>

#include "capture_safety_core.h"

namespace windayflow::capture {

enum class CaptureRuntimeStopResult {
  kAlreadyStopped,
  kStopRequested,
};

enum class CaptureRuntimePauseResult {
  kNotRunning,
  kAlreadyPaused,
  kPauseRequested,
};

enum class CaptureRuntimeWaitResult {
  kStopped,
  kTimeout,
  kWorkerFailed,
};

struct CaptureRuntimeControlSnapshot {
  uint64_t sequence = 0;
  uint64_t pause_epoch = 0;
  bool stop_requested = false;
  bool pause_requested = false;
  std::optional<PersistenceToken> replacement_token;
};

class CaptureRuntimeOwner {
 public:
  using Worker = std::function<void(CaptureRuntimeOwner&, PersistenceToken)>;
  using WaitStoppedExitHook = std::function<void()>;

  explicit CaptureRuntimeOwner(WaitStoppedExitHook wait_stopped_exit_hook = {})
      : wait_stopped_exit_hook_(std::move(wait_stopped_exit_hook)) {}
  ~CaptureRuntimeOwner();

  CaptureRuntimeOwner(const CaptureRuntimeOwner&) = delete;
  CaptureRuntimeOwner& operator=(const CaptureRuntimeOwner&) = delete;

  bool Start(CaptureCommandAdmissionPermit permit, Worker worker);
  CaptureRuntimePauseResult RequestPause();
  bool Resume(CaptureCommandAdmissionPermit permit);
  void NotifyAuthorizationChanged();
  CaptureRuntimeStopResult RequestStop();
  CaptureRuntimeWaitResult WaitStopped(uint32_t timeout_ms);
  void Shutdown();

  CaptureRuntimeControlSnapshot ReadControlSnapshot() const;
  std::optional<CaptureRuntimeControlSnapshot> WaitForControlChange(
      uint64_t observed_sequence, uint32_t timeout_ms);
  bool StopRequested() const;
  bool WaitForStop(uint32_t timeout_ms);
  bool worker_failed() const;
  uint64_t join_count() const;
  uint64_t owner_epoch() const;

 private:
  CaptureRuntimeControlSnapshot ControlSnapshotUnderLock() const;
  void WorkerMain(Worker worker, PersistenceToken initial_token) noexcept;
  CaptureRuntimeWaitResult FinishWaitStoppedUnderLock(
      std::unique_lock<std::mutex>& lock, CaptureRuntimeWaitResult result);
  bool AdvanceOwnerEpochUnderLock();
  void AdvanceControlSequenceUnderLock();

  mutable std::mutex mutex_;
  std::condition_variable state_changed_;
  std::thread worker_;
  bool stop_requested_ = false;
  bool pause_requested_ = false;
  std::optional<PersistenceToken> replacement_token_;
  uint64_t control_sequence_ = 0;
  uint64_t pause_epoch_ = 0;
  bool worker_exited_ = true;
  bool join_in_progress_ = false;
  bool joined_ = true;
  uint64_t wait_stopped_waiters_ = 0;
  bool worker_failed_ = false;
  uint64_t join_count_ = 0;
  uint64_t owner_epoch_ = 1;
  bool owner_epoch_exhausted_ = false;
  WaitStoppedExitHook wait_stopped_exit_hook_;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_

#include "capture_runtime_owner.h"

#include <chrono>
#include <limits>
#include <utility>

namespace windayflow::capture {

CaptureRuntimeOwner::~CaptureRuntimeOwner() { Shutdown(); }

bool CaptureRuntimeOwner::Start(CaptureCommandAdmissionPermit permit,
                                Worker worker) {
  if (!permit || permit.command() != CaptureCommand::kStart || !worker) {
    return false;
  }
  PersistenceToken initial_token = permit.persistence_token();

  std::lock_guard lock(mutex_);
  if (owner_epoch_exhausted_ || permit.runtime_owner_epoch() != owner_epoch_ ||
      !joined_ || wait_stopped_waiters_ != 0 || worker_.joinable() ||
      !AdvanceOwnerEpochUnderLock()) {
    return false;
  }

  stop_requested_ = false;
  pause_requested_ = false;
  pause_epoch_ = 0;
  replacement_token_.reset();
  AdvanceControlSequenceUnderLock();
  worker_exited_ = false;
  join_in_progress_ = false;
  joined_ = false;
  worker_failed_ = false;
  try {
    worker_ = std::thread([this, worker = std::move(worker),
                           initial_token = std::move(initial_token)]() mutable {
      WorkerMain(std::move(worker), std::move(initial_token));
    });
  } catch (...) {
    stop_requested_ = false;
    worker_exited_ = true;
    joined_ = true;
    throw;
  }
  return true;
}

CaptureRuntimePauseResult CaptureRuntimeOwner::RequestPause() {
  CaptureRuntimePauseResult result = CaptureRuntimePauseResult::kNotRunning;
  {
    std::lock_guard lock(mutex_);
    if (!joined_ && !worker_exited_ && !stop_requested_) {
      if (pause_requested_) {
        result = CaptureRuntimePauseResult::kAlreadyPaused;
      } else {
        pause_requested_ = true;
        // Preserve an unconsumed Resume token across a folded second Pause.
        if (pause_epoch_ != std::numeric_limits<uint64_t>::max()) {
          ++pause_epoch_;
        }
        static_cast<void>(AdvanceOwnerEpochUnderLock());
        AdvanceControlSequenceUnderLock();
        result = CaptureRuntimePauseResult::kPauseRequested;
      }
    }
  }
  if (result == CaptureRuntimePauseResult::kPauseRequested) {
    state_changed_.notify_all();
  }
  return result;
}

bool CaptureRuntimeOwner::Resume(CaptureCommandAdmissionPermit permit) {
  if (!permit || permit.command() != CaptureCommand::kResume) {
    return false;
  }
  PersistenceToken replacement_token = permit.persistence_token();

  {
    std::lock_guard lock(mutex_);
    if (owner_epoch_exhausted_ ||
        permit.runtime_owner_epoch() != owner_epoch_ || joined_ ||
        stop_requested_ || !pause_requested_ || worker_exited_ ||
        !worker_.joinable() || !AdvanceOwnerEpochUnderLock()) {
      return false;
    }
    pause_requested_ = false;
    replacement_token_ = std::move(replacement_token);
    AdvanceControlSequenceUnderLock();
  }
  state_changed_.notify_all();
  return true;
}

void CaptureRuntimeOwner::NotifyAuthorizationChanged() {
  {
    std::lock_guard lock(mutex_);
    if (joined_ || worker_exited_) {
      return;
    }
    AdvanceControlSequenceUnderLock();
  }
  state_changed_.notify_all();
}

CaptureRuntimeStopResult CaptureRuntimeOwner::RequestStop() {
  CaptureRuntimeStopResult result = CaptureRuntimeStopResult::kAlreadyStopped;
  {
    std::lock_guard lock(mutex_);
    if (!joined_ && !stop_requested_) {
      stop_requested_ = true;
      pause_requested_ = false;
      replacement_token_.reset();
      static_cast<void>(AdvanceOwnerEpochUnderLock());
      AdvanceControlSequenceUnderLock();
      result = CaptureRuntimeStopResult::kStopRequested;
    }
  }
  state_changed_.notify_all();
  return result;
}

CaptureRuntimeWaitResult CaptureRuntimeOwner::WaitStopped(uint32_t timeout_ms) {
  const auto deadline =
      std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
  std::thread thread_to_join;
  bool worker_failed = false;

  std::unique_lock lock(mutex_);
  if (wait_stopped_waiters_ == std::numeric_limits<uint64_t>::max() &&
      !state_changed_.wait_until(lock, deadline, [this] {
        return wait_stopped_waiters_ != std::numeric_limits<uint64_t>::max();
      })) {
    return CaptureRuntimeWaitResult::kTimeout;
  }
  ++wait_stopped_waiters_;
  while (!joined_) {
    if (!worker_exited_) {
      if (!state_changed_.wait_until(
              lock, deadline, [this] { return worker_exited_ || joined_; })) {
        return FinishWaitStoppedUnderLock(lock,
                                          CaptureRuntimeWaitResult::kTimeout);
      }
      continue;
    }

    if (join_in_progress_) {
      if (!state_changed_.wait_until(lock, deadline,
                                     [this] { return joined_; })) {
        return FinishWaitStoppedUnderLock(lock,
                                          CaptureRuntimeWaitResult::kTimeout);
      }
      continue;
    }

    join_in_progress_ = true;
    thread_to_join = std::move(worker_);
    worker_failed = worker_failed_;
    lock.unlock();
    if (thread_to_join.joinable()) {
      thread_to_join.join();
    }
    lock.lock();
    ++join_count_;
    joined_ = true;
    join_in_progress_ = false;
    state_changed_.notify_all();
  }

  worker_failed = worker_failed || worker_failed_;
  return FinishWaitStoppedUnderLock(
      lock, worker_failed ? CaptureRuntimeWaitResult::kWorkerFailed
                          : CaptureRuntimeWaitResult::kStopped);
}

void CaptureRuntimeOwner::Shutdown() {
  RequestStop();
  while (WaitStopped(std::numeric_limits<uint32_t>::max()) ==
         CaptureRuntimeWaitResult::kTimeout) {
  }
}

CaptureRuntimeControlSnapshot CaptureRuntimeOwner::ReadControlSnapshot() const {
  std::lock_guard lock(mutex_);
  return ControlSnapshotUnderLock();
}

std::optional<CaptureRuntimeControlSnapshot>
CaptureRuntimeOwner::WaitForControlChange(uint64_t observed_sequence,
                                          uint32_t timeout_ms) {
  std::unique_lock lock(mutex_);
  if (control_sequence_ == observed_sequence &&
      !state_changed_.wait_for(
          lock, std::chrono::milliseconds(timeout_ms),
          [this, observed_sequence] {
            // Stop remains observable after the monotonic
            // sequence saturates.
            return control_sequence_ != observed_sequence || stop_requested_;
          })) {
    return std::nullopt;
  }
  return ControlSnapshotUnderLock();
}

bool CaptureRuntimeOwner::StopRequested() const {
  std::lock_guard lock(mutex_);
  return stop_requested_;
}

bool CaptureRuntimeOwner::WaitForStop(uint32_t timeout_ms) {
  std::unique_lock lock(mutex_);
  if (stop_requested_) {
    return true;
  }
  return state_changed_.wait_for(lock, std::chrono::milliseconds(timeout_ms),
                                 [this] { return stop_requested_; });
}

bool CaptureRuntimeOwner::worker_failed() const {
  std::lock_guard lock(mutex_);
  return worker_failed_;
}

uint64_t CaptureRuntimeOwner::join_count() const {
  std::lock_guard lock(mutex_);
  return join_count_;
}

uint64_t CaptureRuntimeOwner::owner_epoch() const {
  std::lock_guard lock(mutex_);
  return owner_epoch_exhausted_ ? 0 : owner_epoch_;
}

CaptureRuntimeControlSnapshot CaptureRuntimeOwner::ControlSnapshotUnderLock()
    const {
  return CaptureRuntimeControlSnapshot{control_sequence_, pause_epoch_,
                                       stop_requested_, pause_requested_,
                                       replacement_token_};
}

void CaptureRuntimeOwner::WorkerMain(Worker worker,
                                     PersistenceToken initial_token) noexcept {
  bool failed = false;
  try {
    worker(*this, std::move(initial_token));
  } catch (...) {
    failed = true;
  }

  {
    std::lock_guard lock(mutex_);
    worker_failed_ = worker_failed_ || failed;
    worker_exited_ = true;
    static_cast<void>(AdvanceOwnerEpochUnderLock());
  }
  state_changed_.notify_all();
}

CaptureRuntimeWaitResult CaptureRuntimeOwner::FinishWaitStoppedUnderLock(
    std::unique_lock<std::mutex>& lock, CaptureRuntimeWaitResult result) {
  if (wait_stopped_exit_hook_) {
    lock.unlock();
    try {
      wait_stopped_exit_hook_();
    } catch (...) {
      lock.lock();
      --wait_stopped_waiters_;
      state_changed_.notify_all();
      return result;
    }
    lock.lock();
  }
  --wait_stopped_waiters_;
  state_changed_.notify_all();
  return result;
}

bool CaptureRuntimeOwner::AdvanceOwnerEpochUnderLock() {
  if (owner_epoch_exhausted_ ||
      owner_epoch_ == std::numeric_limits<uint64_t>::max()) {
    owner_epoch_exhausted_ = true;
    return false;
  }
  ++owner_epoch_;
  return true;
}

void CaptureRuntimeOwner::AdvanceControlSequenceUnderLock() {
  if (control_sequence_ != std::numeric_limits<uint64_t>::max()) {
    ++control_sequence_;
  }
}

}  // namespace windayflow::capture

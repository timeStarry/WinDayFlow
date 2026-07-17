#include "capture_runtime_owner.h"

#include <chrono>
#include <limits>
#include <utility>

namespace windayflow::capture {

CaptureRuntimeOwner::~CaptureRuntimeOwner() {
  Shutdown();
}

bool CaptureRuntimeOwner::Start(CaptureCommandAdmissionPermit permit,
                                Worker worker) {
  if (!permit || permit.command() != CaptureCommand::kStart || !worker) {
    return false;
  }

  std::lock_guard lock(mutex_);
  if (owner_epoch_exhausted_ ||
      permit.runtime_owner_epoch() != owner_epoch_ || !joined_ ||
      worker_.joinable() || !AdvanceOwnerEpochUnderLock()) {
    return false;
  }

  stop_requested_ = false;
  worker_exited_ = false;
  join_in_progress_ = false;
  joined_ = false;
  worker_failed_ = false;
  try {
    worker_ = std::thread(
        [this, worker = std::move(worker)]() mutable {
          WorkerMain(std::move(worker));
        });
  } catch (...) {
    stop_requested_ = false;
    worker_exited_ = true;
    joined_ = true;
    throw;
  }
  return true;
}

bool CaptureRuntimeOwner::Resume(CaptureCommandAdmissionPermit permit) {
  if (!permit || permit.command() != CaptureCommand::kResume) {
    return false;
  }

  std::lock_guard lock(mutex_);
  if (owner_epoch_exhausted_ ||
      permit.runtime_owner_epoch() != owner_epoch_ || joined_ ||
      stop_requested_ || worker_exited_ || !worker_.joinable()) {
    return false;
  }
  return AdvanceOwnerEpochUnderLock();
}

CaptureRuntimeStopResult CaptureRuntimeOwner::RequestStop() {
  CaptureRuntimeStopResult result = CaptureRuntimeStopResult::kAlreadyStopped;
  {
    std::lock_guard lock(mutex_);
    if (!joined_ && !stop_requested_) {
      stop_requested_ = true;
      static_cast<void>(AdvanceOwnerEpochUnderLock());
      result = CaptureRuntimeStopResult::kStopRequested;
    }
  }
  state_changed_.notify_all();
  return result;
}

CaptureRuntimeWaitResult CaptureRuntimeOwner::WaitStopped(
    uint32_t timeout_ms) {
  const auto deadline = std::chrono::steady_clock::now() +
                        std::chrono::milliseconds(timeout_ms);
  std::thread thread_to_join;
  bool worker_failed = false;

  std::unique_lock lock(mutex_);
  while (!joined_) {
    if (!worker_exited_) {
      if (!state_changed_.wait_until(
              lock, deadline, [this] { return worker_exited_ || joined_; })) {
        return CaptureRuntimeWaitResult::kTimeout;
      }
      continue;
    }

    if (join_in_progress_) {
      if (!state_changed_.wait_until(
              lock, deadline, [this] { return joined_; })) {
        return CaptureRuntimeWaitResult::kTimeout;
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
  return worker_failed ? CaptureRuntimeWaitResult::kWorkerFailed
                       : CaptureRuntimeWaitResult::kStopped;
}

void CaptureRuntimeOwner::Shutdown() {
  RequestStop();
  while (WaitStopped(std::numeric_limits<uint32_t>::max()) ==
         CaptureRuntimeWaitResult::kTimeout) {
  }
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
  return state_changed_.wait_for(
      lock,
      std::chrono::milliseconds(timeout_ms),
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

void CaptureRuntimeOwner::WorkerMain(Worker worker) noexcept {
  bool failed = false;
  try {
    worker(*this);
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

bool CaptureRuntimeOwner::AdvanceOwnerEpochUnderLock() {
  if (owner_epoch_exhausted_ ||
      owner_epoch_ == std::numeric_limits<uint64_t>::max()) {
    owner_epoch_exhausted_ = true;
    return false;
  }
  ++owner_epoch_;
  return true;
}

}  // namespace windayflow::capture

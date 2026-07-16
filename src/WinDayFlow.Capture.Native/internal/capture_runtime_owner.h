#ifndef WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_
#define WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <thread>

namespace windayflow::capture {

enum class CaptureRuntimeStopResult {
  kAlreadyStopped,
  kStopRequested,
};

enum class CaptureRuntimeWaitResult {
  kStopped,
  kTimeout,
  kWorkerFailed,
};

class CaptureRuntimeOwner {
 public:
  using Worker = std::function<void(CaptureRuntimeOwner&)>;

  CaptureRuntimeOwner() = default;
  ~CaptureRuntimeOwner();

  CaptureRuntimeOwner(const CaptureRuntimeOwner&) = delete;
  CaptureRuntimeOwner& operator=(const CaptureRuntimeOwner&) = delete;

  bool Start(Worker worker);
  CaptureRuntimeStopResult RequestStop();
  CaptureRuntimeWaitResult WaitStopped(uint32_t timeout_ms);
  void Shutdown();

  bool StopRequested() const;
  bool WaitForStop(uint32_t timeout_ms);
  bool worker_failed() const;
  uint64_t join_count() const;

 private:
  void WorkerMain(Worker worker) noexcept;

  mutable std::mutex mutex_;
  std::condition_variable state_changed_;
  std::thread worker_;
  bool stop_requested_ = false;
  bool worker_exited_ = true;
  bool join_in_progress_ = false;
  bool joined_ = true;
  bool worker_failed_ = false;
  uint64_t join_count_ = 0;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_RUNTIME_OWNER_H_

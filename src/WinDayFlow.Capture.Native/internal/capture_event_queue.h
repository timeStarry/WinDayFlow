#ifndef WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_
#define WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_

#include <cstddef>
#include <cstdint>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <string>

#include "windayflow_capture.h"

namespace windayflow::capture {

struct CaptureEvent {
  uint64_t sequence = 0;
  int64_t timestamp_unix_ms = 0;
  wdf_capture_event_kind kind = WDF_CAPTURE_EVENT_STATE_CHANGED;
  wdf_capture_state state = WDF_CAPTURE_STATE_UNAVAILABLE;
  wdf_capture_reason reason = WDF_CAPTURE_REASON_NONE;
  wdf_capture_error error = WDF_CAPTURE_ERROR_NONE;
  uint32_t dropped_before = 0;
  std::string detail;
};

enum class CaptureEventReadResult {
  kEmpty,
  kBufferTooSmall,
  kSuccess,
  kClosed,
  kInternalError,
};

class CaptureEventQueue {
 public:
  explicit CaptureEventQueue(size_t capacity);

  uint64_t Push(wdf_capture_event_kind kind,
                wdf_capture_state state,
                wdf_capture_reason reason,
                wdf_capture_error error,
                std::string detail,
                int64_t timestamp_unix_ms);

  CaptureEventReadResult Read(uint32_t timeout_ms,
                              wdf_capture_event_v1* event,
                              char* detail_utf8,
                              uint32_t detail_utf8_capacity,
                              uint32_t* detail_utf8_required);
  void Close();
  size_t size() const;
  size_t capacity() const;

 private:
  mutable std::mutex mutex_;
  std::condition_variable event_available_;
  std::deque<CaptureEvent> events_;
  size_t capacity_;
  uint64_t next_sequence_ = 1;
  uint64_t pending_dropped_ = 0;
  bool sequence_exhausted_ = false;
  bool closed_ = false;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_

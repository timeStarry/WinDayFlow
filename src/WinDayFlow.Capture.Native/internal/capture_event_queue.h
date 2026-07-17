#ifndef WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_
#define WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_

#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_set>
#include <utility>

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
  uint64_t persistence_generation = 0;
  uint64_t target_epoch = 0;
  std::string detail;
};

enum class CaptureEventReadResult {
  kEmpty,
  kBufferTooSmall,
  kSuccess,
  kClosed,
  kInternalError,
};

class CaptureEventReservation {
 public:
  CaptureEventReservation() = default;
  CaptureEventReservation(const CaptureEventReservation&) = delete;
  CaptureEventReservation& operator=(const CaptureEventReservation&) = delete;
  CaptureEventReservation(CaptureEventReservation&& other) noexcept
      : issuer_id_(std::exchange(other.issuer_id_, 0)),
        reservation_id_(std::exchange(other.reservation_id_, 0)) {}
  CaptureEventReservation& operator=(CaptureEventReservation&&) = delete;

  explicit operator bool() const noexcept {
    return issuer_id_ != 0 && reservation_id_ != 0;
  }

 private:
  friend class CaptureEventQueue;

  CaptureEventReservation(uint64_t issuer_id, uint64_t reservation_id)
      : issuer_id_(issuer_id), reservation_id_(reservation_id) {}

  void Reset() noexcept {
    issuer_id_ = 0;
    reservation_id_ = 0;
  }

  uint64_t issuer_id_ = 0;
  uint64_t reservation_id_ = 0;
};

using CaptureEventAppendHook = void (*)();

class CaptureEventQueue {
 public:
  explicit CaptureEventQueue(size_t capacity,
                             CaptureEventAppendHook before_append = nullptr);

  uint64_t Push(wdf_capture_event_kind kind, wdf_capture_state state,
                wdf_capture_reason reason, wdf_capture_error error,
                std::string detail, int64_t timestamp_unix_ms,
                uint64_t persistence_generation = 0, uint64_t target_epoch = 0);

  CaptureEventReservation ReserveRequiredEvent();
  uint64_t PushReserved(CaptureEventReservation* reservation,
                        wdf_capture_event_kind kind, wdf_capture_state state,
                        wdf_capture_reason reason, wdf_capture_error error,
                        std::string detail, int64_t timestamp_unix_ms,
                        uint64_t persistence_generation = 0,
                        uint64_t target_epoch = 0);
  bool CancelReservation(CaptureEventReservation* reservation);

  CaptureEventReadResult Read(uint32_t timeout_ms, wdf_capture_event_v1* event,
                              char* detail_utf8, uint32_t detail_utf8_capacity,
                              uint32_t* detail_utf8_required);
  void Close();
  size_t size() const;
  size_t reserved_size() const;
  size_t capacity() const;

 private:
  size_t FindRemovableEventIndexUnderLock() const;
  void RemoveEventUnderLock(size_t index) noexcept;
  uint64_t AppendUnderLock(wdf_capture_event_kind kind, wdf_capture_state state,
                           wdf_capture_reason reason, wdf_capture_error error,
                           std::string detail, int64_t timestamp_unix_ms,
                           uint64_t persistence_generation,
                           uint64_t target_epoch) noexcept;

  mutable std::mutex mutex_;
  std::condition_variable event_available_;
  std::deque<CaptureEvent> events_;
  const uint64_t instance_id_;
  const size_t capacity_;
  const CaptureEventAppendHook before_append_;
  uint64_t next_sequence_ = 1;
  uint64_t next_reservation_id_ = 1;
  uint64_t pending_dropped_ = 0;
  bool sequence_exhausted_ = false;
  bool reservation_exhausted_ = false;
  bool closed_ = false;
  std::unordered_set<uint64_t> reservations_;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_EVENT_QUEUE_H_

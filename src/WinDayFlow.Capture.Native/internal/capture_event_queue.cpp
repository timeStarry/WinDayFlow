#include "capture_event_queue.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <limits>
#include <type_traits>
#include <utility>

namespace windayflow::capture {
namespace {

std::atomic<uint64_t> g_next_queue_instance_id{1};
static_assert(std::atomic<uint64_t>::is_always_lock_free);
static_assert(std::is_nothrow_move_assignable_v<CaptureEvent>);

uint64_t AllocateQueueInstanceId() noexcept {
  uint64_t current = g_next_queue_instance_id.load(std::memory_order_relaxed);
  while (current != 0) {
    const uint64_t next =
        current == std::numeric_limits<uint64_t>::max() ? 0 : current + 1U;
    if (g_next_queue_instance_id.compare_exchange_weak(
            current, next, std::memory_order_relaxed,
            std::memory_order_relaxed)) {
      return current;
    }
  }
  return 0;
}

uint32_t SaturateToUint32(uint64_t value) {
  return value > std::numeric_limits<uint32_t>::max()
             ? std::numeric_limits<uint32_t>::max()
             : static_cast<uint32_t>(value);
}

bool IsRequiredEvent(const CaptureEvent& event) {
  if (event.kind == WDF_CAPTURE_EVENT_CHUNK_COMMITTED ||
      event.kind == WDF_CAPTURE_EVENT_ERROR) {
    return true;
  }
  if (event.kind != WDF_CAPTURE_EVENT_STATE_CHANGED) {
    return false;
  }
  return event.state == WDF_CAPTURE_STATE_STOPPING ||
         event.state == WDF_CAPTURE_STATE_STOPPED ||
         event.state == WDF_CAPTURE_STATE_FAULTED ||
         event.state == WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT ||
         event.reason == WDF_CAPTURE_REASON_EXCLUDED_APPLICATION ||
         event.reason == WDF_CAPTURE_REASON_EXCLUDED_WINDOW ||
         event.reason == WDF_CAPTURE_REASON_SESSION_LOCKED ||
         event.reason == WDF_CAPTURE_REASON_SECURE_DESKTOP ||
         event.reason == WDF_CAPTURE_REASON_REMOTE_SESSION ||
         event.reason == WDF_CAPTURE_REASON_PRESENTATION_MODE ||
         event.reason == WDF_CAPTURE_REASON_STORAGE_CONSTRAINED ||
         event.reason == WDF_CAPTURE_REASON_POLICY_BLOCKED;
}

}  // namespace

CaptureEventQueue::CaptureEventQueue(size_t capacity,
                                     CaptureEventAppendHook before_append)
    : instance_id_(AllocateQueueInstanceId()),
      capacity_(std::max<size_t>(1U, capacity)),
      before_append_(before_append) {}

uint64_t CaptureEventQueue::Push(wdf_capture_event_kind kind,
                                 wdf_capture_state state,
                                 wdf_capture_reason reason,
                                 wdf_capture_error error, std::string detail,
                                 int64_t timestamp_unix_ms,
                                 uint64_t persistence_generation,
                                 uint64_t target_epoch) {
  std::unique_lock lock(mutex_);

  if (closed_ || sequence_exhausted_) {
    return 0;
  }

  const bool needs_removal = events_.size() + reservations_.size() >= capacity_;
  size_t removable_index = 0;
  if (needs_removal) {
    removable_index = FindRemovableEventIndexUnderLock();
    if (removable_index == events_.size()) {
      pending_dropped_ = std::min<uint64_t>(
          std::numeric_limits<uint32_t>::max(), pending_dropped_ + 1U);
      return 0;
    }
  }

  const uint64_t sequence =
      AppendUnderLock(kind, state, reason, error, std::move(detail),
                      timestamp_unix_ms, persistence_generation, target_epoch);
  if (sequence == 0) {
    return 0;
  }
  if (needs_removal) {
    RemoveEventUnderLock(removable_index);
  }
  lock.unlock();
  event_available_.notify_one();
  return sequence;
}

CaptureEventReservation CaptureEventQueue::ReserveRequiredEvent() {
  std::lock_guard lock(mutex_);
  if (closed_ || sequence_exhausted_ || reservation_exhausted_ ||
      instance_id_ == 0) {
    return {};
  }

  const bool needs_removal = events_.size() + reservations_.size() >= capacity_;
  size_t removable_index = 0;
  if (needs_removal) {
    removable_index = FindRemovableEventIndexUnderLock();
    if (removable_index == events_.size()) {
      return {};
    }
  }

  const uint64_t id = next_reservation_id_;
  try {
    const auto [position, inserted] = reservations_.insert(id);
    static_cast<void>(position);
    if (!inserted) {
      reservation_exhausted_ = true;
      return {};
    }
  } catch (...) {
    return {};
  }
  if (next_reservation_id_ == std::numeric_limits<uint64_t>::max()) {
    reservation_exhausted_ = true;
  } else {
    ++next_reservation_id_;
  }
  if (needs_removal) {
    RemoveEventUnderLock(removable_index);
  }
  return CaptureEventReservation{instance_id_, id};
}

uint64_t CaptureEventQueue::PushReserved(
    CaptureEventReservation* reservation, wdf_capture_event_kind kind,
    wdf_capture_state state, wdf_capture_reason reason, wdf_capture_error error,
    std::string detail, int64_t timestamp_unix_ms,
    uint64_t persistence_generation, uint64_t target_epoch) {
  if (reservation == nullptr || !*reservation ||
      reservation->issuer_id_ != instance_id_) {
    return 0;
  }

  std::unique_lock lock(mutex_);
  if (closed_ || sequence_exhausted_ ||
      !IsRequiredEvent(CaptureEvent{0,
                                    timestamp_unix_ms,
                                    kind,
                                    state,
                                    reason,
                                    error,
                                    0,
                                    persistence_generation,
                                    target_epoch,
                                    {}})) {
    return 0;
  }

  const auto match = reservations_.find(reservation->reservation_id_);
  if (match == reservations_.end()) {
    return 0;
  }

  const uint64_t sequence =
      AppendUnderLock(kind, state, reason, error, std::move(detail),
                      timestamp_unix_ms, persistence_generation, target_epoch);
  if (sequence == 0) {
    return 0;
  }
  reservations_.erase(match);
  reservation->Reset();
  lock.unlock();
  event_available_.notify_one();
  return sequence;
}

bool CaptureEventQueue::CancelReservation(
    CaptureEventReservation* reservation) {
  if (reservation == nullptr || !*reservation ||
      reservation->issuer_id_ != instance_id_) {
    return false;
  }

  std::lock_guard lock(mutex_);
  const size_t removed = reservations_.erase(reservation->reservation_id_);
  if (removed == 0) {
    return false;
  }
  reservation->Reset();
  return true;
}

size_t CaptureEventQueue::FindRemovableEventIndexUnderLock() const {
  for (size_t index = 0; index < events_.size(); ++index) {
    if (!IsRequiredEvent(events_[index])) {
      return index;
    }
  }
  return events_.size();
}

void CaptureEventQueue::RemoveEventUnderLock(size_t index) noexcept {
  const auto removable =
      events_.begin() +
      static_cast<std::deque<CaptureEvent>::difference_type>(index);
  const uint64_t dropped_count =
      static_cast<uint64_t>(removable->dropped_before) + 1U;
  const auto after_removed = events_.erase(removable);
  if (after_removed == events_.end()) {
    pending_dropped_ = std::min<uint64_t>(std::numeric_limits<uint32_t>::max(),
                                          pending_dropped_ + dropped_count);
  } else {
    after_removed->dropped_before = SaturateToUint32(
        static_cast<uint64_t>(after_removed->dropped_before) + dropped_count);
  }
}

uint64_t CaptureEventQueue::AppendUnderLock(
    wdf_capture_event_kind kind, wdf_capture_state state,
    wdf_capture_reason reason, wdf_capture_error error, std::string detail,
    int64_t timestamp_unix_ms, uint64_t persistence_generation,
    uint64_t target_epoch) noexcept {
  if (closed_ || sequence_exhausted_) {
    return 0;
  }

  CaptureEvent event;
  event.sequence = next_sequence_;
  event.timestamp_unix_ms = timestamp_unix_ms;
  event.kind = kind;
  event.state = state;
  event.reason = reason;
  event.error = error;
  event.detail = std::move(detail);
  event.persistence_generation = persistence_generation;
  event.target_epoch = target_epoch;

  event.dropped_before = SaturateToUint32(pending_dropped_);
  try {
    if (before_append_ != nullptr) {
      before_append_();
    }
    events_.push_back(std::move(event));
  } catch (...) {
    return 0;
  }

  const uint64_t sequence = next_sequence_;
  if (next_sequence_ == std::numeric_limits<uint64_t>::max()) {
    sequence_exhausted_ = true;
  } else {
    ++next_sequence_;
  }
  pending_dropped_ = 0;
  return sequence;
}

CaptureEventReadResult CaptureEventQueue::Read(uint32_t timeout_ms,
                                               wdf_capture_event_v1* event,
                                               char* detail_utf8,
                                               uint32_t detail_utf8_capacity,
                                               uint32_t* detail_utf8_required) {
  if (event == nullptr || detail_utf8_required == nullptr ||
      (detail_utf8 == nullptr && detail_utf8_capacity != 0)) {
    return CaptureEventReadResult::kInternalError;
  }

  std::unique_lock lock(mutex_);
  if (events_.empty()) {
    if (timeout_ms > 0 && !closed_) {
      event_available_.wait_for(lock, std::chrono::milliseconds(timeout_ms),
                                [this] { return closed_ || !events_.empty(); });
    }
    if (events_.empty()) {
      *detail_utf8_required = 0;
      return closed_ ? CaptureEventReadResult::kClosed
                     : CaptureEventReadResult::kEmpty;
    }
  }

  const CaptureEvent& value = events_.front();
  if (value.detail.size() >= std::numeric_limits<uint32_t>::max()) {
    return CaptureEventReadResult::kInternalError;
  }
  const size_t required_size = value.detail.size() + 1U;

  *detail_utf8_required = static_cast<uint32_t>(required_size);
  wdf_capture_event_v1 output{};
  output.struct_size = sizeof(output);
  output.abi_version = WDF_CAPTURE_ABI_VERSION;
  output.sequence = value.sequence;
  output.timestamp_unix_ms = value.timestamp_unix_ms;
  output.kind = value.kind;
  output.state = value.state;
  output.reason = value.reason;
  output.error = value.error;
  output.dropped_before = value.dropped_before;
  output.detail_utf8_length = static_cast<uint32_t>(value.detail.size());
  output.persistence_generation = value.persistence_generation;
  output.target_epoch = value.target_epoch;
  *event = output;

  if (detail_utf8 == nullptr || detail_utf8_capacity < required_size) {
    return CaptureEventReadResult::kBufferTooSmall;
  }

  std::memcpy(detail_utf8, value.detail.data(), value.detail.size());
  detail_utf8[value.detail.size()] = '\0';
  events_.pop_front();
  return CaptureEventReadResult::kSuccess;
}

void CaptureEventQueue::Close() {
  {
    std::lock_guard lock(mutex_);
    closed_ = true;
  }
  event_available_.notify_all();
}

size_t CaptureEventQueue::size() const {
  std::lock_guard lock(mutex_);
  return events_.size();
}

size_t CaptureEventQueue::reserved_size() const {
  std::lock_guard lock(mutex_);
  return reservations_.size();
}

size_t CaptureEventQueue::capacity() const { return capacity_; }

}  // namespace windayflow::capture

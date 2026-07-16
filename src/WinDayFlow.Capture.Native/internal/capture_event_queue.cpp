#include "capture_event_queue.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <limits>
#include <utility>

namespace windayflow::capture {
namespace {

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
  return event.state == WDF_CAPTURE_STATE_STOPPED ||
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

CaptureEventQueue::CaptureEventQueue(size_t capacity)
    : capacity_(std::max<size_t>(1U, capacity)) {}

uint64_t CaptureEventQueue::Push(wdf_capture_event_kind kind,
                                 wdf_capture_state state,
                                 wdf_capture_reason reason,
                                 wdf_capture_error error,
                                 std::string detail,
                                 int64_t timestamp_unix_ms) {
  std::unique_lock lock(mutex_);

  if (closed_ || sequence_exhausted_) {
    return 0;
  }

  CaptureEvent event;
  event.sequence = next_sequence_;
  if (next_sequence_ == std::numeric_limits<uint64_t>::max()) {
    sequence_exhausted_ = true;
  } else {
    ++next_sequence_;
  }
  event.timestamp_unix_ms = timestamp_unix_ms;
  event.kind = kind;
  event.state = state;
  event.reason = reason;
  event.error = error;
  event.detail = std::move(detail);

  if (events_.size() == capacity_) {
    const auto removable = std::find_if(
        events_.begin(), events_.end(),
        [](const CaptureEvent& queued) { return !IsRequiredEvent(queued); });
    if (removable == events_.end()) {
      pending_dropped_ = std::min<uint64_t>(
          std::numeric_limits<uint32_t>::max(), pending_dropped_ + 1U);
      return 0;
    }

    const uint64_t dropped_count =
        static_cast<uint64_t>(removable->dropped_before) + 1U;
    const auto after_removed = events_.erase(removable);
    if (after_removed == events_.end()) {
      pending_dropped_ = std::min<uint64_t>(
          std::numeric_limits<uint32_t>::max(),
          pending_dropped_ + dropped_count);
    } else {
      after_removed->dropped_before = SaturateToUint32(
          static_cast<uint64_t>(after_removed->dropped_before) +
          dropped_count);
    }
  }

  event.dropped_before = SaturateToUint32(pending_dropped_);
  pending_dropped_ = 0;
  events_.push_back(std::move(event));
  const uint64_t sequence = events_.back().sequence;
  lock.unlock();
  event_available_.notify_one();
  return sequence;
}

CaptureEventReadResult CaptureEventQueue::Read(
    uint32_t timeout_ms,
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
      event_available_.wait_for(
          lock,
          std::chrono::milliseconds(timeout_ms),
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

size_t CaptureEventQueue::capacity() const {
  return capacity_;
}

}  // namespace windayflow::capture

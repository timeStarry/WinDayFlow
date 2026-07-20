// Derived from QiDayflow windows/runner/capture_runtime.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: 459928A5744C9AD2E1D30434FA41EBF5881E70D6D8AA6608CBA4C06400595DE3.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#include "capture_policy.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <numeric>

namespace windayflow::capture {
namespace {

constexpr int64_t kMediaFoundationTicksPerMillisecond = 10'000;

struct DeadlineAdvance {
  int64_t deadline_ms;
  bool exhausted;
};

DeadlineAdvance AdvanceDeadlinePast(int64_t deadline_ms,
                                    int64_t now_ms,
                                    int64_t period_ms) {
  if (now_ms < deadline_ms) {
    return {deadline_ms, false};
  }

  const uint64_t period = static_cast<uint64_t>(period_ms);
  const uint64_t elapsed = static_cast<uint64_t>(now_ms) -
                           static_cast<uint64_t>(deadline_ms);
  const uint64_t completed_periods = elapsed / period;
  const uint64_t room =
      static_cast<uint64_t>(std::numeric_limits<int64_t>::max()) -
      static_cast<uint64_t>(deadline_ms);
  const uint64_t maximum_periods = room / period;
  if (completed_periods >= maximum_periods) {
    return {std::numeric_limits<int64_t>::max(), true};
  }

  const uint64_t advance = (completed_periods + 1U) * period;
  if (deadline_ms >= 0) {
    return {deadline_ms + static_cast<int64_t>(advance), false};
  }

  const uint64_t distance_to_zero =
      static_cast<uint64_t>(-(deadline_ms + 1)) + 1U;
  if (advance >= distance_to_zero) {
    return {static_cast<int64_t>(advance - distance_to_zero), false};
  }
  return {deadline_ms + static_cast<int64_t>(advance), false};
}

bool IsWithin(uint32_t value, uint32_t minimum, uint32_t maximum) {
  return value >= minimum && value <= maximum;
}

}  // namespace

bool IsValidCapturePolicy(const CapturePolicy& policy) {
  return IsWithin(policy.capture_interval_ms,
                  kMinimumCaptureIntervalMs,
                  kMaximumCaptureIntervalMs) &&
         IsWithin(policy.context_interval_ms,
                  kMinimumContextIntervalMs,
                  kMaximumContextIntervalMs) &&
         IsWithin(policy.chunk_duration_ms,
                  kMinimumChunkDurationMs,
                  kMaximumChunkDurationMs) &&
         policy.capture_interval_ms <= policy.chunk_duration_ms;
}

CaptureWorkerAction DecideCaptureWorkerAction(
    bool stop_requested,
    bool manual_paused,
    bool system_paused,
    bool idle_paused,
    bool chunk_has_frames,
    int64_t chunk_elapsed_ms,
    bool topology_available,
    const CapturePolicy& policy) {
  if (stop_requested) {
    return CaptureWorkerAction::kStop;
  }
  if (manual_paused || system_paused || idle_paused) {
    return CaptureWorkerAction::kPause;
  }
  if (chunk_has_frames &&
      chunk_elapsed_ms >= static_cast<int64_t>(policy.chunk_duration_ms)) {
    return CaptureWorkerAction::kFinalizeChunk;
  }
  if (!topology_available) {
    return CaptureWorkerAction::kInitializeTopology;
  }
  return CaptureWorkerAction::kPollSchedule;
}

bool ShouldWakeCaptureRetryWait(bool stop_requested,
                                bool manual_paused,
                                bool system_paused) {
  return stop_requested || manual_paused || system_paused;
}

uint32_t CalculateRegularChunkFrameCount(uint32_t capture_interval_ms,
                                         uint32_t duration_ms) {
  if (capture_interval_ms == 0 || duration_ms == 0) {
    return 0;
  }
  const uint64_t frames =
      (static_cast<uint64_t>(duration_ms) + capture_interval_ms - 1U) /
      capture_interval_ms;
  return frames > std::numeric_limits<uint32_t>::max()
             ? std::numeric_limits<uint32_t>::max()
             : static_cast<uint32_t>(frames);
}

CaptureVideoTiming VideoTimingForIntervalMs(uint32_t capture_interval_ms) {
  const uint32_t safe_interval = std::max<uint32_t>(1U, capture_interval_ms);
  const uint32_t divisor = std::gcd<uint32_t>(1'000U, safe_interval);
  return CaptureVideoTiming{
      1'000U / divisor,
      safe_interval / divisor,
      static_cast<int64_t>(safe_interval) *
          kMediaFoundationTicksPerMillisecond,
  };
}

MediaSampleTiming CalculateMediaSampleTiming(int64_t sample_offset_ticks,
                                             int64_t end_offset_ticks) {
  constexpr int64_t kMaximumTimestamp =
      std::numeric_limits<int64_t>::max() - 1;
  const int64_t timestamp_ticks =
      std::clamp<int64_t>(sample_offset_ticks, 0, kMaximumTimestamp);
  const int64_t requested_end_ticks = std::clamp<int64_t>(
      end_offset_ticks, 0, std::numeric_limits<int64_t>::max());
  const int64_t actual_end_ticks =
      requested_end_ticks > timestamp_ticks ? requested_end_ticks
                                            : timestamp_ticks + 1;
  return MediaSampleTiming{
      timestamp_ticks,
      actual_end_ticks - timestamp_ticks,
      actual_end_ticks,
  };
}

int64_t MediaFoundationTicksToDurationMs(int64_t duration_ticks) {
  if (duration_ticks <= 0) {
    return 1;
  }
  const int64_t whole_milliseconds =
      duration_ticks / kMediaFoundationTicksPerMillisecond;
  return std::max<int64_t>(
      1, whole_milliseconds +
             (duration_ticks % kMediaFoundationTicksPerMillisecond == 0
                  ? 0
                  : 1));
}

int64_t CalculateEncodedDurationMs(uint32_t frame_count,
                                   uint32_t capture_interval_ms) {
  const uint64_t duration_ms =
      static_cast<uint64_t>(frame_count) * capture_interval_ms;
  return duration_ms >
                 static_cast<uint64_t>(std::numeric_limits<int64_t>::max())
             ? std::numeric_limits<int64_t>::max()
             : static_cast<int64_t>(duration_ms);
}

CaptureSchedule::CaptureSchedule(const CapturePolicy& policy) {
  Configure(policy);
}

void CaptureSchedule::Configure(const CapturePolicy& policy) {
  frame_interval_ms_ =
      static_cast<int64_t>(std::max<uint32_t>(1U, policy.capture_interval_ms));
  context_interval_ms_ =
      static_cast<int64_t>(std::max<uint32_t>(1U, policy.context_interval_ms));
  next_frame_ms_ = 0;
  next_context_ms_ = 0;
  frame_schedule_exhausted_ = false;
  context_schedule_exhausted_ = false;
}

void CaptureSchedule::Reset(int64_t now_ms) {
  next_frame_ms_ = now_ms;
  next_context_ms_ = now_ms;
  frame_schedule_exhausted_ = false;
  context_schedule_exhausted_ = false;
}

CaptureScheduleDecision CaptureSchedule::Poll(int64_t now_ms) {
  CaptureScheduleDecision decision;
  if (!frame_schedule_exhausted_ && now_ms >= next_frame_ms_) {
    decision.capture_frame = true;
    const DeadlineAdvance next =
        AdvanceDeadlinePast(next_frame_ms_, now_ms, frame_interval_ms_);
    next_frame_ms_ = next.deadline_ms;
    frame_schedule_exhausted_ = next.exhausted;
  }
  if (!context_schedule_exhausted_ && now_ms >= next_context_ms_) {
    decision.sample_context = true;
    const DeadlineAdvance next =
        AdvanceDeadlinePast(next_context_ms_, now_ms, context_interval_ms_);
    next_context_ms_ = next.deadline_ms;
    context_schedule_exhausted_ = next.exhausted;
  }
  return decision;
}

int64_t CaptureSchedule::DelayUntilNextMs(int64_t now_ms) const {
  if (frame_schedule_exhausted_ && context_schedule_exhausted_) {
    return std::numeric_limits<int64_t>::max();
  }
  const int64_t next_ms = frame_schedule_exhausted_
                              ? next_context_ms_
                              : context_schedule_exhausted_
                                  ? next_frame_ms_
                                  : std::min(next_frame_ms_, next_context_ms_);
  if (next_ms <= now_ms) {
    return 0;
  }
  const uint64_t delay = static_cast<uint64_t>(next_ms) -
                         static_cast<uint64_t>(now_ms);
  return delay >
                 static_cast<uint64_t>(std::numeric_limits<int64_t>::max())
             ? std::numeric_limits<int64_t>::max()
             : static_cast<int64_t>(delay);
}

CaptureChunkProgress::CaptureChunkProgress(
    uint32_t regular_chunk_duration_ms) {
  Configure(regular_chunk_duration_ms);
}

void CaptureChunkProgress::Configure(uint32_t regular_chunk_duration_ms) {
  regular_chunk_duration_ms_ =
      static_cast<int64_t>(std::max<uint32_t>(1U, regular_chunk_duration_ms));
  frame_count_ = 0;
  latest_frame_offset_ms_ = 0;
}

void CaptureChunkProgress::Reset() {
  frame_count_ = 0;
  latest_frame_offset_ms_ = 0;
}

uint32_t CaptureChunkProgress::frame_count() const {
  return frame_count_;
}

int64_t CaptureChunkProgress::latest_frame_offset_ms() const {
  return latest_frame_offset_ms_;
}

bool CaptureChunkProgress::ShouldFinalizeBeforeSample(int64_t elapsed_ms) const {
  return frame_count_ > 0 && elapsed_ms >= regular_chunk_duration_ms_;
}

CaptureLoopDecision CaptureChunkProgress::OnTopologyChanged() const {
  return CaptureLoopDecision{true, false};
}

CaptureLoopDecision CaptureChunkProgress::OnTopologyCheckUnavailable() const {
  return CaptureLoopDecision{true, false};
}

CaptureLoopDecision CaptureChunkProgress::OnRecoverableCaptureError() const {
  return CaptureLoopDecision{true, false};
}

CaptureLoopDecision CaptureChunkProgress::OnFrameWritten(int64_t offset_ms) {
  latest_frame_offset_ms_ =
      std::max(latest_frame_offset_ms_, std::max<int64_t>(0, offset_ms));
  if (frame_count_ < std::numeric_limits<uint32_t>::max()) {
    ++frame_count_;
  }
  return CaptureLoopDecision{false, false};
}

int64_t CalculateChunkDurationMs(int64_t elapsed_ms,
                                 int64_t encoded_duration_ms,
                                 int64_t latest_frame_offset_ms) {
  const int64_t latest_frame_end_ms =
      latest_frame_offset_ms >= std::numeric_limits<int64_t>::max()
          ? std::numeric_limits<int64_t>::max()
          : std::max<int64_t>(0, latest_frame_offset_ms) + 1;
  return std::max<int64_t>(
      1, std::max(elapsed_ms,
                  std::max(encoded_duration_ms, latest_frame_end_ms)));
}

std::optional<double> CalculateCpuUsagePercent(
    const std::optional<ProcessCpuSample>& previous,
    const ProcessCpuSample& current,
    uint32_t logical_processor_count) {
  if (!previous.has_value() || logical_processor_count == 0 ||
      previous->process_id != current.process_id ||
      previous->creation_time_100ns != current.creation_time_100ns ||
      current.wall_time_100ns <= previous->wall_time_100ns ||
      current.process_time_100ns < previous->process_time_100ns) {
    return std::nullopt;
  }

  const uint64_t process_delta =
      current.process_time_100ns - previous->process_time_100ns;
  const uint64_t wall_delta =
      current.wall_time_100ns - previous->wall_time_100ns;
  const long double percentage =
      static_cast<long double>(process_delta) * 100.0L /
      (static_cast<long double>(wall_delta) * logical_processor_count);
  if (!std::isfinite(percentage)) {
    return std::nullopt;
  }
  return static_cast<double>(
      std::clamp<long double>(percentage, 0.0L, 100.0L));
}

std::optional<uint64_t> PrivateUsageToMemoryCommitBytes(
    bool query_succeeded,
    uint64_t private_usage_bytes) {
  if (!query_succeeded) {
    return std::nullopt;
  }
  return private_usage_bytes;
}

}  // namespace windayflow::capture

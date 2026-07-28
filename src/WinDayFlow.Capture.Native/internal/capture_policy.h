// Derived from QiDayflow windows/runner/capture_runtime.h at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: C6269DD460B0461E0C962944648C075EC0B7D7DA544A3E3AC352C6EDAE221DF0.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_CAPTURE_POLICY_H_
#define WINDAYFLOW_CAPTURE_POLICY_H_

#include <cstdint>
#include <optional>

namespace windayflow::capture {

inline constexpr uint32_t kMinimumCaptureIntervalMs = 250;
inline constexpr uint32_t kMaximumCaptureIntervalMs = 300'000;
inline constexpr uint32_t kMinimumContextIntervalMs = 250;
inline constexpr uint32_t kMaximumContextIntervalMs = 60'000;
inline constexpr uint32_t kMinimumChunkDurationMs = 10'000;
inline constexpr uint32_t kMaximumChunkDurationMs = 3'600'000;

struct CapturePolicy {
  uint32_t capture_interval_ms = 10'000;
  uint32_t context_interval_ms = 1'000;
  uint32_t chunk_duration_ms = 60'000;
};

bool IsValidCapturePolicy(const CapturePolicy& policy);

struct CaptureLoopDecision {
  bool rebuild_topology = false;
  bool finalize_chunk = false;
};

enum class CaptureWorkerAction {
  kStop,
  kPause,
  kFinalizeChunk,
  kInitializeTopology,
  kPollSchedule,
};

CaptureWorkerAction DecideCaptureWorkerAction(
    bool stop_requested,
    bool manual_paused,
    bool system_paused,
    bool idle_paused,
    bool chunk_has_frames,
    int64_t chunk_elapsed_ms,
    bool topology_available,
    const CapturePolicy& policy);

bool ShouldWakeCaptureRetryWait(bool stop_requested,
                                bool manual_paused,
                                bool system_paused);

uint32_t CalculateRegularChunkFrameCount(uint32_t capture_interval_ms,
                                         uint32_t duration_ms);

struct CaptureVideoTiming {
  uint32_t frame_rate_numerator = 1;
  uint32_t frame_rate_denominator = 1;
  int64_t frame_duration_ticks = 10'000'000;
};

CaptureVideoTiming VideoTimingForIntervalMs(uint32_t capture_interval_ms);

struct MediaSampleTiming {
  int64_t timestamp_ticks = 0;
  int64_t duration_ticks = 1;
  int64_t end_ticks = 1;
};

MediaSampleTiming CalculateMediaSampleTiming(int64_t sample_offset_ticks,
                                             int64_t end_offset_ticks);

int64_t MediaFoundationTicksToDurationMs(int64_t duration_ticks);

int64_t CalculateEncodedDurationMs(uint32_t frame_count,
                                   uint32_t capture_interval_ms);

struct CaptureScheduleDecision {
  bool capture_frame = false;
  bool sample_context = false;
};

class CaptureSchedule {
 public:
  explicit CaptureSchedule(const CapturePolicy& policy);

  void Configure(const CapturePolicy& policy);
  void Reset(int64_t now_ms);
  void ReanchorFrame(int64_t anchor_ms);
  CaptureScheduleDecision Poll(int64_t now_ms);
  int64_t DelayUntilNextMs(int64_t now_ms) const;

 private:
  int64_t frame_interval_ms_ = 10'000;
  int64_t context_interval_ms_ = 1'000;
  int64_t next_frame_ms_ = 0;
  int64_t next_context_ms_ = 0;
  bool frame_schedule_exhausted_ = false;
  bool context_schedule_exhausted_ = false;
};

class CaptureChunkProgress {
 public:
  explicit CaptureChunkProgress(uint32_t regular_chunk_duration_ms);

  void Configure(uint32_t regular_chunk_duration_ms);
  void Reset();
  uint32_t frame_count() const;
  int64_t latest_frame_offset_ms() const;
  bool ShouldFinalizeBeforeSample(int64_t elapsed_ms) const;

  CaptureLoopDecision OnTopologyChanged() const;
  CaptureLoopDecision OnTopologyCheckUnavailable() const;
  CaptureLoopDecision OnRecoverableCaptureError() const;
  CaptureLoopDecision OnFrameWritten(int64_t offset_ms);

 private:
  int64_t regular_chunk_duration_ms_ = 60'000;
  uint32_t frame_count_ = 0;
  int64_t latest_frame_offset_ms_ = 0;
};

int64_t CalculateChunkDurationMs(int64_t elapsed_ms,
                                 int64_t encoded_duration_ms,
                                 int64_t latest_frame_offset_ms);

struct ProcessCpuSample {
  uint32_t process_id = 0;
  uint64_t creation_time_100ns = 0;
  uint64_t process_time_100ns = 0;
  uint64_t wall_time_100ns = 0;
};

std::optional<double> CalculateCpuUsagePercent(
    const std::optional<ProcessCpuSample>& previous,
    const ProcessCpuSample& current,
    uint32_t logical_processor_count);

std::optional<uint64_t> PrivateUsageToMemoryCommitBytes(
    bool query_succeeded,
    uint64_t private_usage_bytes);

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_POLICY_H_

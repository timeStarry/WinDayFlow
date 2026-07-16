// Derived from QiDayflow windows/runner/capture_runtime_test.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: 541193A401AC7CBD039DB9AB86C9B0AAD5CF41F3633619B6643CAC5EBECD1E0E.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#include "capture_policy.h"
#include "privacy_guard.h"

#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <optional>

namespace {

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

bool TestPolicyValidationAndWorkerOrder() {
  using windayflow::capture::CapturePolicy;
  using windayflow::capture::CaptureWorkerAction;
  const CapturePolicy policy{2'500, 750, 120'000};
  return Expect(windayflow::capture::IsValidCapturePolicy(policy),
                "valid configurable policy was rejected") &&
         Expect(!windayflow::capture::IsValidCapturePolicy(
                    CapturePolicy{0, 750, 120'000}),
                "zero capture cadence was accepted") &&
         Expect(!windayflow::capture::IsValidCapturePolicy(
                    CapturePolicy{20'000, 750, 10'000}),
                "capture cadence longer than chunk was accepted") &&
         Expect(windayflow::capture::DecideCaptureWorkerAction(
                    true, true, true, true, true, 120'000, false, policy) ==
                    CaptureWorkerAction::kStop,
                "stop did not take priority") &&
         Expect(windayflow::capture::DecideCaptureWorkerAction(
                    false, true, false, false, true, 120'000, false, policy) ==
                    CaptureWorkerAction::kPause,
                "pause did not take priority") &&
         Expect(windayflow::capture::DecideCaptureWorkerAction(
                    false, false, false, false, true, 119'999, true, policy) ==
                    CaptureWorkerAction::kPollSchedule,
                "custom chunk duration was ignored") &&
         Expect(windayflow::capture::DecideCaptureWorkerAction(
                    false, false, false, false, true, 120'000, true, policy) ==
                    CaptureWorkerAction::kFinalizeChunk,
                "configured chunk boundary did not finalize");
}

bool TestTiming() {
  const auto quarter_second =
      windayflow::capture::VideoTimingForIntervalMs(250);
  const auto one_and_half_seconds =
      windayflow::capture::VideoTimingForIntervalMs(1'500);
  const auto sample =
      windayflow::capture::CalculateMediaSampleTiming(50, 50);
  const auto saturated = windayflow::capture::CalculateMediaSampleTiming(
      std::numeric_limits<int64_t>::max(),
      std::numeric_limits<int64_t>::max());
  return Expect(quarter_second.frame_rate_numerator == 4 &&
                    quarter_second.frame_rate_denominator == 1 &&
                    quarter_second.frame_duration_ticks == 2'500'000,
                "250ms video timing was incorrect") &&
         Expect(one_and_half_seconds.frame_rate_numerator == 2 &&
                    one_and_half_seconds.frame_rate_denominator == 3 &&
                    one_and_half_seconds.frame_duration_ticks == 15'000'000,
                "1500ms video timing was incorrect") &&
         Expect(sample.timestamp_ticks == 50 && sample.duration_ticks == 1 &&
                    sample.end_ticks == 51,
                "minimum sample duration was not enforced") &&
         Expect(saturated.timestamp_ticks ==
                        std::numeric_limits<int64_t>::max() - 1 &&
                    saturated.duration_ticks == 1,
                "sample timestamp saturation failed") &&
         Expect(windayflow::capture::CalculateRegularChunkFrameCount(
                    2'500, 60'000) == 24,
                "regular chunk frame count was incorrect") &&
         Expect(windayflow::capture::CalculateRegularChunkFrameCount(
                    7'000, 60'000) == 9,
                "partial interval frame count did not round up");
}

bool TestIndependentSchedule() {
  const windayflow::capture::CapturePolicy policy{2'500, 750, 120'000};
  windayflow::capture::CaptureSchedule schedule(policy);
  schedule.Reset(0);
  const auto initial = schedule.Poll(0);
  const auto context_only = schedule.Poll(800);
  const auto both = schedule.Poll(2'600);
  const auto no_burst = schedule.Poll(2'600);
  return Expect(initial.capture_frame && initial.sample_context,
                "initial schedule did not run both cadences") &&
         Expect(!context_only.capture_frame && context_only.sample_context,
                "context cadence was coupled to frame cadence") &&
         Expect(both.capture_frame && both.sample_context,
                "late poll did not advance both due cadences") &&
         Expect(!no_burst.capture_frame && !no_burst.sample_context,
                "late poll replayed missed work in a burst") &&
         Expect(schedule.DelayUntilNextMs(2'600) == 400,
                "next schedule delay was incorrect");
}

bool TestChunkAndResourceCalculations() {
  windayflow::capture::CaptureChunkProgress progress(120'000);
  progress.OnFrameWritten(1'000);
  const std::optional<windayflow::capture::ProcessCpuSample> previous =
      windayflow::capture::ProcessCpuSample{7, 11, 1'000, 10'000};
  const windayflow::capture::ProcessCpuSample current{
      7, 11, 3'000, 20'000};
  const auto usage =
      windayflow::capture::CalculateCpuUsagePercent(previous, current, 4);
  return Expect(!progress.ShouldFinalizeBeforeSample(119'999),
                "chunk finalized before configured duration") &&
         Expect(progress.ShouldFinalizeBeforeSample(120'000),
                "chunk did not finalize at configured duration") &&
         Expect(windayflow::capture::CalculateChunkDurationMs(0, 0, 1'000) ==
                    1'001,
                "latest frame was not covered by chunk duration") &&
         Expect(usage.has_value() && std::abs(*usage - 5.0) < 0.0001,
                "CPU usage calculation was incorrect") &&
         Expect(!windayflow::capture::PrivateUsageToMemoryCommitBytes(
                    false, 123).has_value(),
                "failed memory query produced a value") &&
         Expect(windayflow::capture::PrivateUsageToMemoryCommitBytes(
                    true, 123) == std::optional<uint64_t>{123},
                "memory commit value was not preserved");
}

bool TestPrivacyGuardFailsClosed() {
  windayflow::capture::PrivacyContext context;
  context.policy_revision = 1;
  auto decision = windayflow::capture::EvaluatePrivacyContext(context);
  if (!Expect(!decision.allowed &&
                  decision.reason == WDF_CAPTURE_REASON_CONSENT_REQUIRED,
              "unknown consent did not fail closed")) {
    return false;
  }

  context.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
  context.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
  context.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
  context.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.storage_available = WDF_CAPTURE_POLICY_ALLOW;
  decision = windayflow::capture::EvaluatePrivacyContext(context);
  if (!Expect(windayflow::capture::IsValidPrivacyContext(context) &&
                  decision.allowed &&
                  decision.reason == WDF_CAPTURE_REASON_NONE,
              "fully allowed privacy context was rejected")) {
    return false;
  }

  context.session_unlocked = WDF_CAPTURE_POLICY_UNKNOWN;
  decision = windayflow::capture::EvaluatePrivacyContext(context);
  return Expect(!decision.allowed &&
                    decision.reason == WDF_CAPTURE_REASON_SESSION_LOCKED,
                "unknown session state did not fail closed");
}

}  // namespace

int main() {
  if (!TestPolicyValidationAndWorkerOrder() || !TestTiming() ||
      !TestIndependentSchedule() || !TestChunkAndResourceCalculations() ||
      !TestPrivacyGuardFailsClosed()) {
    return 1;
  }
  std::cout << "capture policy tests passed\n";
  return 0;
}

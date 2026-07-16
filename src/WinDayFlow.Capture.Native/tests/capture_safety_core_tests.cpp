#include "capture_runtime_owner.h"
#include "capture_safety_core.h"

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iostream>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {

using windayflow::capture::CaptureRuntimeOwner;
using windayflow::capture::CaptureRuntimeStopResult;
using windayflow::capture::CaptureRuntimeWaitResult;
using windayflow::capture::CaptureSafetyCore;
using windayflow::capture::CaptureSafetyUpdateResult;
using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::PrivacyContext;
using windayflow::capture::RuntimeAuthorization;

class Gate {
 public:
  void Open() {
    {
      std::lock_guard lock(mutex_);
      open_ = true;
    }
    changed_.notify_all();
  }

  void Wait() {
    std::unique_lock lock(mutex_);
    changed_.wait(lock, [this] { return open_; });
  }

 private:
  std::mutex mutex_;
  std::condition_variable changed_;
  bool open_ = false;
};

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

PrivacyContext AllowedPrivacy(uint64_t revision) {
  PrivacyContext context;
  context.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
  context.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
  context.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
  context.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.storage_available = WDF_CAPTURE_POLICY_ALLOW;
  context.policy_revision = revision;
  return context;
}

PrivacyContext BlockedPrivacy(uint64_t revision) {
  PrivacyContext context = AllowedPrivacy(revision);
  context.application_allowed = WDF_CAPTURE_POLICY_BLOCK;
  return context;
}

CaptureTargetIdentity Target(uint64_t window_handle,
                             uint32_t process_id,
                             uint64_t creation_time,
                             uint64_t target_epoch) {
  return CaptureTargetIdentity{
      window_handle, process_id, creation_time, target_epoch};
}

RuntimeAuthorization AllowedAuthorization(
    uint64_t revision,
    const CaptureTargetIdentity& target) {
  return RuntimeAuthorization{AllowedPrivacy(revision), target};
}

bool TestTargetTupleAndInstanceEpoch() {
  CaptureSafetyCore core(11, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "initial target authorization was rejected")) {
    return false;
  }

  const auto token = core.MintPersistenceToken(target);
  if (!Expect(token.has_value() && token->instance_epoch == 11 &&
                  token->persistence_generation == 2,
              "valid target did not mint an instance-scoped token")) {
    return false;
  }

  const std::array<CaptureTargetIdentity, 4> mismatches{
      Target(101, 200, 300, 10),
      Target(100, 201, 300, 10),
      Target(100, 200, 301, 10),
      Target(100, 200, 300, 11),
  };
  for (const CaptureTargetIdentity& mismatch : mismatches) {
    if (!Expect(!core.MintPersistenceToken(mismatch).has_value(),
                "a mismatched target minted a token") ||
        !Expect(!core.AcquirePersistencePermit(*token, mismatch),
                "a mismatched target acquired a persistence permit")) {
      return false;
    }
  }

  CaptureSafetyCore recreated(12, 1);
  uint64_t recreated_generation = 0;
  if (!Expect(recreated.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &recreated_generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "recreated instance could not be authorized") ||
      !Expect(!recreated.AcquirePersistencePermit(*token, target),
              "a token crossed the native instance epoch")) {
    return false;
  }

  const CaptureTargetIdentity reused_epoch = Target(101, 200, 300, 10);
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, reused_epoch), &generation) ==
                  CaptureSafetyUpdateResult::kTargetMismatch &&
                  generation == 2,
              "a target tuple changed without an epoch advance")) {
    return false;
  }

  const CaptureTargetIdentity next_target = Target(101, 200, 300, 11);
  return Expect(core.UpdateRuntimeAuthorization(
                    AllowedAuthorization(2, next_target), &generation) ==
                    CaptureSafetyUpdateResult::kOk &&
                    generation == 3,
                "an epoch-advanced target was rejected") &&
         Expect(!core.AcquirePersistencePermit(*token, target),
                "a prior target token survived an authorization update");
}

bool TestRevisionAndGenerationRules() {
  CaptureSafetyCore core(21, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, target), &generation) ==
                  CaptureSafetyUpdateResult::kPolicyRevisionGap &&
                  generation == 1,
              "runtime authorization accepted a non-one initial revision") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "runtime revision one was rejected") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "idempotent runtime authorization advanced generation") ||
      !Expect(core.admission_open(),
              "ordinary idempotent update closed authorization")) {
    return false;
  }

  core.BeginRevoke();
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2 && !core.admission_open() &&
                  !core.MintPersistenceToken(target).has_value(),
              "idempotent update reopened an externally revoked admission") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  RuntimeAuthorization{BlockedPrivacy(1), std::nullopt},
                  &generation) ==
                  CaptureSafetyUpdateResult::kPolicyRevisionConflict,
              "same-revision conflict was accepted") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
                  &generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "contiguous restrictive revision was rejected") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kStalePolicy,
              "stale runtime revision was accepted") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(4, target), &generation) ==
                  CaptureSafetyUpdateResult::kPolicyRevisionGap,
              "runtime revision gap was accepted")) {
    return false;
  }

  const CaptureTargetIdentity refreshed_target = Target(100, 200, 300, 11);
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(3, refreshed_target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 4,
              "authorization after a restrictive snapshot was rejected") ||
      !Expect(core.Revoke(&generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 5 && core.revoked(),
              "revoke did not invalidate the active generation") ||
      !Expect(core.privacy_context().consent_granted ==
                      WDF_CAPTURE_POLICY_BLOCK &&
                  core.privacy_context().policy_revision == 3,
              "post-revoke privacy snapshot exposed stale allow") ||
      !Expect(core.Revoke(&generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 5,
              "idempotent revoke advanced generation")) {
    return false;
  }

  CaptureSafetyCore legacy(22, 1);
  if (!Expect(legacy.UpdateLegacyPrivacyContext(
                  AllowedPrivacy(7), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "legacy initial revision compatibility regressed") ||
      !Expect(!legacy.MintPersistenceToken(target).has_value(),
              "legacy allow minted a target-scoped token") ||
      !Expect(legacy.UpdateLegacyPrivacyContext(
                  BlockedPrivacy(9), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "legacy revision skip compatibility regressed") ||
      !Expect(legacy.UpdateLegacyPrivacyContext(
                  BlockedPrivacy(8), &generation) ==
                  CaptureSafetyUpdateResult::kStalePolicy,
              "legacy stale revision was accepted") ||
      !Expect(legacy.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kRevokedDuringUpdate &&
                  generation == 3,
              "legacy-tainted handle accepted target authorization")) {
    return false;
  }

  CaptureSafetyCore runtime_then_legacy(23, 1);
  if (!Expect(runtime_then_legacy.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "mixed-mode runtime authorization was rejected")) {
    return false;
  }
  const auto runtime_token =
      runtime_then_legacy.MintPersistenceToken(target);
  if (!Expect(runtime_token.has_value(),
              "mixed-mode runtime token was not minted") ||
      !Expect(runtime_then_legacy.UpdateLegacyPrivacyContext(
                  AllowedPrivacy(1), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 3 && runtime_then_legacy.revoked(),
              "legacy update did not synchronously taint and revoke") ||
      !Expect(!runtime_then_legacy.AcquirePersistencePermit(
                  *runtime_token, target),
              "legacy update left the runtime token usable") ||
      !Expect(runtime_then_legacy.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, Target(100, 200, 300, 11)),
                  &generation) ==
                  CaptureSafetyUpdateResult::kRevokedDuringUpdate &&
                  generation == 3,
              "runtime authorization resumed after legacy taint")) {
    return false;
  }

  CaptureSafetyCore exhausted(
      24, std::numeric_limits<uint64_t>::max());
  return Expect(exhausted.UpdateRuntimeAuthorization(
                    AllowedAuthorization(1, target), &generation) ==
                    CaptureSafetyUpdateResult::kGenerationExhausted &&
                    generation == std::numeric_limits<uint64_t>::max() &&
                    exhausted.revoked(),
                "persistence generation exhaustion did not fail closed");
}

bool TestPermitLinearizationAndPersistenceStages() {
  CaptureSafetyCore core(31, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (core.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "linearization core could not be authorized");
  }
  const auto token = core.MintPersistenceToken(target);
  if (!token.has_value()) {
    return Expect(false, "linearization token was not minted");
  }

  Gate permit_acquired;
  Gate release_permit;
  Gate admission_closed;
  std::atomic<uint32_t> persisted{0};
  std::atomic<bool> update_finished{false};
  std::atomic<CaptureSafetyUpdateResult> update_result{
      CaptureSafetyUpdateResult::kInvalidArgument};

  std::thread writer([&] {
    auto permit = core.AcquirePersistencePermit(*token, target);
    permit_acquired.Open();
    release_permit.Wait();
    if (permit) {
      persisted.fetch_add(1, std::memory_order_relaxed);
    }
  });
  permit_acquired.Wait();

  std::thread updater([&] {
    const auto ticket = core.BeginAuthorizationUpdate();
    admission_closed.Open();
    uint64_t applied_generation = 0;
    update_result.store(
        core.CompleteRuntimeAuthorization(
            ticket,
            RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
            &applied_generation),
        std::memory_order_relaxed);
    generation = applied_generation;
    update_finished.store(true, std::memory_order_release);
  });
  admission_closed.Wait();
  if (!Expect(!core.admission_open(),
              "authorization update did not close permit admission") ||
      !Expect(!core.AcquirePersistencePermit(*token, target),
              "a new permit crossed the closed admission gate") ||
      !Expect(!update_finished.load(std::memory_order_acquire),
              "authorization update crossed an active persistence permit")) {
    release_permit.Open();
    writer.join();
    updater.join();
    return false;
  }

  core.BeginRevoke();
  release_permit.Open();
  writer.join();
  updater.join();
  if (!Expect(update_result.load(std::memory_order_relaxed) ==
                  CaptureSafetyUpdateResult::kRevokedDuringUpdate &&
                  generation == 2 && persisted.load(std::memory_order_relaxed) ==
                                         1,
              "stop did not supersede the in-flight authorization update") ||
      !Expect(core.FinalizeRevoke(5'000, &generation) && generation == 3,
              "stop did not finalize after the persistence permit drained")) {
    return false;
  }

  std::array<uint32_t, 4> stage_side_effects{};
  for (uint32_t& side_effect : stage_side_effects) {
    auto permit = core.AcquirePersistencePermit(*token, target);
    if (permit) {
      ++side_effect;
    }
  }
  if (!Expect(stage_side_effects == std::array<uint32_t, 4>{},
              "stale token reached an acquire/write/metadata/rename stage")) {
    return false;
  }

  CaptureSafetyCore stop_core(32, 1);
  uint64_t stop_generation = 0;
  if (stop_core.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &stop_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "stop core could not be authorized");
  }
  const auto stop_token = stop_core.MintPersistenceToken(target);
  if (!stop_token.has_value()) {
    return Expect(false, "stop token was not minted");
  }
  auto held_permit =
      stop_core.AcquirePersistencePermit(*stop_token, target);
  stop_core.BeginRevoke();
  if (!Expect(!stop_core.admission_open(),
              "begin revoke did not synchronously close admission") ||
      !Expect(!stop_core.FinalizeRevoke(0, &stop_generation),
              "zero-time finalization waited for a held permit")) {
    return false;
  }
  held_permit = {};
  return Expect(stop_core.FinalizeRevoke(5'000, &stop_generation) &&
                    stop_generation == 3 && stop_core.revoked(),
                "revoke did not finalize after the permit drained");
}

bool TestRuntimeOwnerTimeoutAndSingleJoin() {
  CaptureRuntimeOwner owner;
  Gate worker_started;
  Gate stop_observed;
  Gate release_worker;
  if (!Expect(owner.Start([&](CaptureRuntimeOwner& runtime) {
                worker_started.Open();
                if (runtime.WaitForStop(5'000)) {
                  stop_observed.Open();
                }
                release_worker.Wait();
              }),
              "runtime worker did not start")) {
    return false;
  }
  worker_started.Wait();

  if (!Expect(owner.RequestStop() ==
                  CaptureRuntimeStopResult::kStopRequested,
              "first stop request was not observed") ||
      !Expect(owner.RequestStop() ==
                  CaptureRuntimeStopResult::kAlreadyStopped,
              "repeated stop request was not idempotent") ||
      !Expect(owner.WaitStopped(0) == CaptureRuntimeWaitResult::kTimeout,
              "wait did not time out while the worker was active")) {
    release_worker.Open();
    owner.Shutdown();
    return false;
  }
  stop_observed.Wait();

  constexpr size_t kWaiterCount = 8;
  std::array<CaptureRuntimeWaitResult, kWaiterCount> results{};
  std::vector<std::thread> waiters;
  waiters.reserve(kWaiterCount);
  for (size_t index = 0; index < kWaiterCount; ++index) {
    waiters.emplace_back([&, index] { results[index] = owner.WaitStopped(5'000); });
  }
  release_worker.Open();
  for (std::thread& waiter : waiters) {
    waiter.join();
  }

  for (const CaptureRuntimeWaitResult result : results) {
    if (!Expect(result == CaptureRuntimeWaitResult::kStopped,
                "a concurrent waiter did not observe stopped")) {
      return false;
    }
  }
  if (!Expect(owner.join_count() == 1,
              "concurrent waiters joined the worker more than once")) {
    return false;
  }

  if (!Expect(owner.Start([](CaptureRuntimeOwner&) {
                throw std::runtime_error("injected worker failure");
              }),
              "runtime owner could not restart for fault injection") ||
      !Expect(owner.WaitStopped(5'000) ==
                  CaptureRuntimeWaitResult::kWorkerFailed,
              "worker exception did not produce kWorkerFailed") ||
      !Expect(owner.join_count() == 2,
              "faulted worker was not joined exactly once")) {
    return false;
  }

  return Expect(owner.Start([](CaptureRuntimeOwner&) {}),
                "runtime owner could not restart after a fault") &&
         Expect(owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kStopped,
                "naturally exited worker was not joined") &&
         Expect(owner.join_count() == 3,
                "naturally exited worker join count was incorrect");
}

}  // namespace

int main() {
  if (!TestTargetTupleAndInstanceEpoch() ||
      !TestRevisionAndGenerationRules() ||
      !TestPermitLinearizationAndPersistenceStages() ||
      !TestRuntimeOwnerTimeoutAndSingleJoin()) {
    return 1;
  }
  std::cout << "capture safety core tests passed\n";
  return 0;
}

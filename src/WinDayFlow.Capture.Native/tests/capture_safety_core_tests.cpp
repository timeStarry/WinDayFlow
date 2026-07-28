#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iostream>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "capture_runtime_owner.h"
#include "capture_safety_core.h"

namespace {

using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureCommandAdmission;
using windayflow::capture::CaptureCommandAdmissionPermit;
using windayflow::capture::CaptureCommandAdmissionResult;
using windayflow::capture::CaptureAuthorizationScope;
using windayflow::capture::CaptureRuntimeOwner;
using windayflow::capture::CaptureRuntimePauseResult;
using windayflow::capture::CaptureRuntimeStopResult;
using windayflow::capture::CaptureRuntimeWaitResult;
using windayflow::capture::CaptureSafetyCore;
using windayflow::capture::CaptureSafetyUpdateResult;
using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::PersistenceToken;
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

CaptureTargetIdentity Target(
    uint64_t window_handle, uint32_t process_id, uint64_t creation_time,
    uint64_t target_epoch, uint64_t display_monitor_handle = 400,
    std::wstring_view display_device_key = L"\\\\.\\DISPLAY1") {
  return CaptureTargetIdentity{
      window_handle,          process_id,
      creation_time,          target_epoch,
      display_monitor_handle, std::wstring(display_device_key)};
}

CaptureTargetIdentity DisplayWideTarget(
    uint64_t target_epoch, uint64_t display_monitor_handle = 400,
    std::wstring_view display_device_key = L"\\\\.\\DISPLAY1") {
  return CaptureTargetIdentity{
      0,
      0,
      0,
      target_epoch,
      display_monitor_handle,
      std::wstring(display_device_key),
      CaptureAuthorizationScope::kDisplayWide,
  };
}

RuntimeAuthorization AllowedAuthorization(uint64_t revision,
                                          const CaptureTargetIdentity& target) {
  return RuntimeAuthorization{AllowedPrivacy(revision), target};
}

bool AcquireOwnerPermit(CaptureSafetyCore& core, CaptureRuntimeOwner& owner,
                        const CaptureTargetIdentity& target,
                        CaptureCommand command,
                        CaptureCommandAdmissionPermit* permit) {
  const uint64_t owner_epoch = owner.owner_epoch();
  CaptureCommandAdmission admission;
  return owner_epoch != 0 &&
         core.IssueCommandAdmission(
             command, core.persistence_generation(), target.target_epoch,
             owner_epoch, &admission) == CaptureCommandAdmissionResult::kOk &&
         core.AcquireCommandAdmissionPermit(admission, command, owner_epoch,
                                            permit) ==
             CaptureCommandAdmissionResult::kOk;
}

bool TestTargetTupleAndInstanceEpoch() {
  CaptureSafetyCore core(11, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  CaptureTargetIdentity incomplete_display = target;
  incomplete_display.display_monitor_handle = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, incomplete_display), &generation) ==
                      CaptureSafetyUpdateResult::kInvalidArgument &&
                  generation == 1,
              "an incomplete display target was authorized") ||
      !Expect(
          core.UpdateRuntimeAuthorization(
              AllowedAuthorization(1, Target(100, 200, 300, 10, 400, L"   ")),
              &generation) == CaptureSafetyUpdateResult::kInvalidArgument &&
              generation == 1,
          "a whitespace-only display target was authorized") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, Target(100, 200, 300, 10, 400,
                                                 std::wstring(32, L'A'))),
                  &generation) == CaptureSafetyUpdateResult::kInvalidArgument &&
                  generation == 1,
              "an overlength display target was authorized") ||
      !Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, Target(100, 200, 300, 10, 400,
                                                 std::wstring(L"\\\\.\\DIS"
                                                              L"\x0001"
                                                              L"PLAY1"))),
                  &generation) == CaptureSafetyUpdateResult::kInvalidArgument &&
                  generation == 1,
              "a control-character display target was authorized") ||
      !Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
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

  const CaptureTargetIdentity case_only_display_key =
      Target(100, 200, 300, 10, 400, L"\\\\.\\display1");
  if (!Expect(core.MintPersistenceToken(case_only_display_key).has_value(),
              "case-only display key change invalidated the target") ||
      !Expect(static_cast<bool>(
                  core.AcquirePersistencePermit(*token, case_only_display_key)),
              "case-only display key change rejected a permit")) {
    return false;
  }

  const std::array<CaptureTargetIdentity, 6> mismatches{
      Target(101, 200, 300, 10),
      Target(100, 201, 300, 10),
      Target(100, 200, 301, 10),
      Target(100, 200, 300, 11),
      Target(100, 200, 300, 10, 401),
      Target(100, 200, 300, 10, 400, L"\\\\.\\DISPLAY2"),
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

  const CaptureTargetIdentity reused_display_epoch =
      Target(100, 200, 300, 10, 401);
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, reused_display_epoch), &generation) ==
                      CaptureSafetyUpdateResult::kTargetMismatch &&
                  generation == 2,
              "a display tuple changed without an epoch advance")) {
    return false;
  }

  const CaptureTargetIdentity next_target =
      Target(101, 200, 300, 11, 401, L"\\\\.\\DISPLAY2");
  return Expect(core.UpdateRuntimeAuthorization(
                    AllowedAuthorization(2, next_target), &generation) ==
                        CaptureSafetyUpdateResult::kOk &&
                    generation == 3,
                "an epoch-advanced target was rejected") &&
         Expect(!core.AcquirePersistencePermit(*token, target),
                "a prior target token survived an authorization update");
}

bool TestCommandAdmissionRetainsDisplayBinding() {
  CaptureSafetyCore core(44, 1, [](uint64_t* low, uint64_t* high) {
    *low = 4'401;
    *high = 4'402;
    return true;
  });
  const CaptureTargetIdentity target_a = Target(100, 200, 300, 10);
  const CaptureTargetIdentity target_b =
      Target(100, 200, 300, 11, 401, L"\\\\.\\DISPLAY2");
  uint64_t generation = 0;
  if (core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                      &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "display-bound command core could not be authorized");
  }

  CaptureCommandAdmission stale_admission;
  if (core.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                 target_a.target_epoch, 7, &stale_admission) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "display-bound command admission was not issued");
  }
  if (!Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(2, target_b),
                                              &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "display-bound target replacement was rejected")) {
    return false;
  }

  CaptureCommandAdmissionPermit permit;
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  stale_admission, CaptureCommand::kStart, 7, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "a command admission survived a display-bound target change")) {
    return false;
  }

  CaptureCommandAdmission current_admission;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target_b.target_epoch, 7,
          &current_admission) != CaptureCommandAdmissionResult::kOk ||
      core.AcquireCommandAdmissionPermit(current_admission,
                                         CaptureCommand::kStart, 7, &permit) !=
          CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "current display-bound command was not admitted");
  }

  const PersistenceToken& token = permit.persistence_token();
  return Expect(token.target == target_b &&
                    token.target.display_monitor_handle == 401 &&
                    token.target.display_device_key == L"\\\\.\\DISPLAY2",
                "command permit did not retain the complete display target");
}

bool TestDisplayWideTargetTupleAndPermits() {
  CaptureSafetyCore core(45, 1);
  const CaptureTargetIdentity display_wide = DisplayWideTarget(10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, display_wide), &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "display-wide target authorization was rejected")) {
    return false;
  }

  const auto token = core.MintPersistenceToken(display_wide);
  if (!Expect(token.has_value() &&
                  token->target.scope == CaptureAuthorizationScope::kDisplayWide &&
                  token->target.window_handle == 0 &&
                  token->target.process_id == 0 &&
                  token->target.process_creation_time_100ns == 0,
              "display-wide token retained an application identity")) {
    return false;
  }

  CaptureTargetIdentity invalid = display_wide;
  invalid.window_handle = 100;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, invalid), &generation) ==
                      CaptureSafetyUpdateResult::kInvalidArgument &&
                  generation == 2,
              "display-wide authorization accepted a window identity")) {
    return false;
  }

  const CaptureTargetIdentity foreground = Target(100, 200, 300, 10);
  if (!Expect(!core.MintPersistenceToken(foreground).has_value(),
              "foreground scope matched a display-wide authorization") ||
      !Expect(!core.AcquirePersistencePermit(*token, foreground),
              "display-wide token admitted a foreground target")) {
    return false;
  }

  const CaptureTargetIdentity changed_display_same_epoch =
      DisplayWideTarget(10, 401, L"\\\\.\\DISPLAY2");
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, changed_display_same_epoch),
                  &generation) == CaptureSafetyUpdateResult::kTargetMismatch &&
                  generation == 2,
              "display-wide monitor changed without an epoch advance")) {
    return false;
  }

  const CaptureTargetIdentity next_display =
      DisplayWideTarget(11, 401, L"\\\\.\\DISPLAY2");
  return Expect(core.UpdateRuntimeAuthorization(
                    AllowedAuthorization(2, next_display), &generation) ==
                        CaptureSafetyUpdateResult::kOk &&
                    generation == 3,
                "epoch-advanced display-wide target was rejected") &&
         Expect(!core.AcquirePersistencePermit(*token, display_wide),
                "old display-wide token survived a monitor change");
}

bool TestRevisionAndGenerationRules() {
  CaptureSafetyCore core(21, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(2, target),
                                              &generation) ==
                      CaptureSafetyUpdateResult::kPolicyRevisionGap &&
                  generation == 1,
              "runtime authorization accepted a non-one initial revision") ||
      !Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "runtime revision one was rejected") ||
      !Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "idempotent runtime authorization advanced generation") ||
      !Expect(core.admission_open(),
              "ordinary idempotent update closed authorization")) {
    return false;
  }

  core.BeginRevoke();
  if (!Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
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
      !Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
                  CaptureSafetyUpdateResult::kStalePolicy,
              "stale runtime revision was accepted") ||
      !Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(4, target),
                                              &generation) ==
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
      !Expect(
          core.privacy_context().consent_granted == WDF_CAPTURE_POLICY_BLOCK &&
              core.privacy_context().policy_revision == 3,
          "post-revoke privacy snapshot exposed stale allow") ||
      !Expect(core.Revoke(&generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 5,
              "idempotent revoke advanced generation")) {
    return false;
  }

  CaptureSafetyCore legacy(22, 1);
  if (!Expect(
          legacy.UpdateLegacyPrivacyContext(AllowedPrivacy(7), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
              generation == 2,
          "legacy initial revision compatibility regressed") ||
      !Expect(!legacy.MintPersistenceToken(target).has_value(),
              "legacy allow minted a target-scoped token") ||
      !Expect(
          legacy.UpdateLegacyPrivacyContext(BlockedPrivacy(9), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
              generation == 3,
          "legacy revision skip compatibility regressed") ||
      !Expect(
          legacy.UpdateLegacyPrivacyContext(BlockedPrivacy(8), &generation) ==
              CaptureSafetyUpdateResult::kStalePolicy,
          "legacy stale revision was accepted") ||
      !Expect(legacy.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &generation) ==
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
  const auto runtime_token = runtime_then_legacy.MintPersistenceToken(target);
  if (!Expect(runtime_token.has_value(),
              "mixed-mode runtime token was not minted") ||
      !Expect(runtime_then_legacy.UpdateLegacyPrivacyContext(AllowedPrivacy(1),
                                                             &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3 && runtime_then_legacy.revoked(),
              "legacy update did not synchronously taint and revoke") ||
      !Expect(
          !runtime_then_legacy.AcquirePersistencePermit(*runtime_token, target),
          "legacy update left the runtime token usable") ||
      !Expect(
          runtime_then_legacy.UpdateRuntimeAuthorization(
              AllowedAuthorization(2, Target(100, 200, 300, 11)),
              &generation) == CaptureSafetyUpdateResult::kRevokedDuringUpdate &&
              generation == 3,
          "runtime authorization resumed after legacy taint")) {
    return false;
  }

  CaptureSafetyCore exhausted(24, std::numeric_limits<uint64_t>::max());
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
  if (core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                      &generation) !=
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
            ticket, RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
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
                  generation == 2 &&
                  persisted.load(std::memory_order_relaxed) == 1,
              "stop did not revoke the in-flight authorization update") ||
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
  if (stop_core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                           &stop_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "stop core could not be authorized");
  }
  const auto stop_token = stop_core.MintPersistenceToken(target);
  if (!stop_token.has_value()) {
    return Expect(false, "stop token was not minted");
  }
  auto held_permit = stop_core.AcquirePersistencePermit(*stop_token, target);
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

bool TestPersistencePermitIssuerBinding() {
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  CaptureSafetyCore issuer(321, 1);
  CaptureSafetyCore foreign(322, 1);
  uint64_t issuer_generation = 0;
  uint64_t foreign_generation = 0;
  if (!Expect(issuer.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &issuer_generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "permit issuer could not be authorized") ||
      !Expect(foreign.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &foreign_generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "foreign permit core could not be authorized")) {
    return false;
  }

  const auto token = issuer.MintPersistenceToken(target);
  if (!Expect(token.has_value(), "issuer token was not minted")) {
    return false;
  }
  auto permit = issuer.AcquirePersistencePermit(*token, target);
  if (!Expect(permit &&
                  issuer.authorization_epoch() == foreign.authorization_epoch(),
              "issuer-binding collision setup was incomplete") ||
      !Expect(issuer.IsPersistencePermitCurrent(permit),
              "issuer rejected its current persistence permit") ||
      !Expect(!foreign.IsPersistencePermitCurrent(permit),
              "foreign core accepted an issuer's persistence permit")) {
    return false;
  }

  if (!Expect(issuer.InvalidateAuthorizationAdmission() != 0,
              "issuer admission could not be invalidated") ||
      !Expect(!issuer.IsPersistencePermitCurrent(permit),
              "revoked issuer reported its permit as current") ||
      !Expect(!foreign.IsPersistencePermitCurrent(permit),
              "foreign core revived a revoked issuer permit")) {
    return false;
  }
  return true;
}

bool TestCallbackTimeAuthorizationInvalidation() {
  const CaptureTargetIdentity target_a = Target(100, 200, 300, 10);
  const CaptureTargetIdentity target_b =
      Target(101, 201, 301, 11, 401, L"\\\\.\\DISPLAY2");
  CaptureSafetyCore core(33, 1, [](uint64_t* low, uint64_t* high) {
    *low = 3'301;
    *high = 3'302;
    return true;
  });
  uint64_t generation = 0;
  if (core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                      &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "callback invalidation core could not be authorized");
  }
  const auto token = core.MintPersistenceToken(target_a);
  CaptureCommandAdmission admission;
  if (!token.has_value() ||
      core.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                 target_a.target_epoch, 7, &admission) !=
          CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "callback invalidation setup failed");
  }

  Gate permit_acquired;
  Gate release_permit;
  std::atomic<bool> permit_valid{false};
  std::atomic<uint32_t> persisted{0};
  std::thread writer([&] {
    auto permit = core.AcquirePersistencePermit(*token, target_a);
    permit_valid.store(static_cast<bool>(permit), std::memory_order_release);
    permit_acquired.Open();
    release_permit.Wait();
    if (core.IsPersistencePermitCurrent(permit)) {
      persisted.fetch_add(1, std::memory_order_relaxed);
    }
  });
  permit_acquired.Wait();
  if (!Expect(permit_valid.load(std::memory_order_acquire),
              "callback test did not hold a persistence permit")) {
    release_permit.Open();
    writer.join();
    return false;
  }

  const uint64_t closed_epoch = core.InvalidateAuthorizationAdmission();
  CaptureCommandAdmissionPermit command_permit;
  if (!Expect(closed_epoch != 0 && (closed_epoch & 1U) == 0,
              "callback invalidation returned no closed epoch") ||
      !Expect(!core.admission_open() && core.persistence_generation() == 2,
              "callback invalidation advanced the persistence barrier") ||
      !Expect(!core.MintPersistenceToken(target_a).has_value() &&
                  !core.AcquirePersistencePermit(*token, target_a),
              "callback invalidation admitted new persistence work") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  admission, CaptureCommand::kStart, 7, &command_permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "callback invalidation admitted a pending command")) {
    release_permit.Open();
    writer.join();
    return false;
  }
  if (!Expect(!core.IsPersistencePermitCurrent(
                  core.AcquirePersistencePermit(*token, target_a)),
              "closed callback gate reported a fresh permit as current")) {
    release_permit.Open();
    writer.join();
    return false;
  }

  Gate barrier_started;
  std::atomic<bool> barrier_finished{false};
  std::atomic<CaptureSafetyUpdateResult> barrier_result{
      CaptureSafetyUpdateResult::kInvalidArgument};
  std::thread barrier([&] {
    barrier_started.Open();
    barrier_result.store(
        core.UpdateRuntimeAuthorization(
            RuntimeAuthorization{BlockedPrivacy(2), std::nullopt}, &generation),
        std::memory_order_relaxed);
    barrier_finished.store(true, std::memory_order_release);
  });
  barrier_started.Wait();
  if (!Expect(!barrier_finished.load(std::memory_order_acquire),
              "callback invalidation waited for or crossed a held permit")) {
    release_permit.Open();
    writer.join();
    barrier.join();
    return false;
  }
  release_permit.Open();
  writer.join();
  barrier.join();
  if (!Expect(barrier_result.load(std::memory_order_relaxed) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3 && core.revoked() &&
                  persisted.load(std::memory_order_relaxed) == 0,
              "blocked barrier did not finalize callback invalidation")) {
    return false;
  }

  CaptureSafetyCore pending(34, 1);
  if (pending.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                         &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "pending invalidation core could not be authorized");
  }
  const uint64_t first_epoch = pending.InvalidateAuthorizationAdmission();
  const uint64_t second_epoch = pending.InvalidateAuthorizationAdmission();
  if (!Expect(first_epoch != 0 && second_epoch > first_epoch &&
                  (second_epoch & 1U) == 0,
              "repeated callbacks did not advance the closed epoch") ||
      !Expect(pending.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, target_b), &generation) ==
                      CaptureSafetyUpdateResult::kAuthorizationSuperseded &&
                  generation == 2 && !pending.admission_open(),
              "an Allow entered native after callback return") ||
      !Expect(
          pending.UpdateRuntimeAuthorization(
              RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
              &generation) == CaptureSafetyUpdateResult::kOk &&
              generation == 3,
          "blocked runtime barrier did not confirm callback invalidation") ||
      !Expect(pending.UpdateRuntimeAuthorization(
                  AllowedAuthorization(3, target_b), &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 4 && pending.admission_open(),
              "resolved Allow did not open after the blocked barrier")) {
    return false;
  }

  CaptureSafetyCore precommit(35, 1);
  if (precommit.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                           &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "precommit invalidation core could not be authorized");
  }
  const auto stale_ticket = precommit.BeginAuthorizationUpdate();
  if (!Expect(precommit.InvalidateAuthorizationAdmission() != 0,
              "precommit callback invalidation failed") ||
      !Expect(
          precommit.CompleteRuntimeAuthorization(
              stale_ticket, AllowedAuthorization(2, target_b), &generation) ==
                  CaptureSafetyUpdateResult::kAuthorizationSuperseded &&
              generation == 2 && !precommit.admission_open(),
          "a precommit stale ticket was not superseded")) {
    return false;
  }

  CaptureSafetyCore stale_barrier(350, 1);
  if (stale_barrier.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target_a), &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "stale barrier core could not be authorized");
  }
  const uint64_t barrier_epoch =
      stale_barrier.InvalidateAuthorizationAdmission();
  const auto stale_block_ticket = stale_barrier.BeginAuthorizationUpdate();
  const uint64_t newer_barrier_epoch =
      stale_barrier.InvalidateAuthorizationAdmission();
  if (!Expect(barrier_epoch != 0 && newer_barrier_epoch > barrier_epoch,
              "a newer callback did not supersede the block barrier") ||
      !Expect(stale_barrier.CompleteRuntimeAuthorization(
                  stale_block_ticket,
                  RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
                  &generation) ==
                      CaptureSafetyUpdateResult::kAuthorizationSuperseded &&
                  generation == 2 && !stale_barrier.admission_open(),
              "a stale block barrier confirmed a newer callback") ||
      !Expect(stale_barrier.UpdateRuntimeAuthorization(
                  AllowedAuthorization(2, target_b), &generation) ==
                      CaptureSafetyUpdateResult::kAuthorizationSuperseded &&
                  generation == 2,
              "Allow reopened after a stale block barrier") ||
      !Expect(stale_barrier.UpdateRuntimeAuthorization(
                  RuntimeAuthorization{BlockedPrivacy(2), std::nullopt},
                  &generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "a fresh block barrier did not confirm the newer callback") ||
      !Expect(stale_barrier.UpdateRuntimeAuthorization(
                  AllowedAuthorization(3, target_b), &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 4 && stale_barrier.admission_open(),
              "Allow did not reopen after the fresh block barrier")) {
    return false;
  }

  Gate committed;
  Gate release_commit;
  std::atomic<bool> intercept_commit{false};
  CaptureSafetyCore postcommit(36, 1, {}, 2, [&] {
    if (intercept_commit.load(std::memory_order_acquire)) {
      committed.Open();
      release_commit.Wait();
    }
  });
  if (postcommit.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                            &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false,
                  "postcommit invalidation core could not be authorized");
  }
  intercept_commit.store(true, std::memory_order_release);
  std::atomic<CaptureSafetyUpdateResult> postcommit_result{
      CaptureSafetyUpdateResult::kInvalidArgument};
  std::thread committed_update([&] {
    postcommit_result.store(postcommit.UpdateRuntimeAuthorization(
                                AllowedAuthorization(2, target_b), &generation),
                            std::memory_order_relaxed);
  });
  committed.Wait();
  const uint64_t postcommit_epoch =
      postcommit.InvalidateAuthorizationAdmission();
  release_commit.Open();
  committed_update.join();
  intercept_commit.store(false, std::memory_order_release);
  if (!Expect(postcommit_epoch != 0 &&
                  postcommit_result.load(std::memory_order_relaxed) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3 && !postcommit.admission_open() &&
                  postcommit.target_epoch() == target_b.target_epoch &&
                  postcommit.privacy_context().policy_revision == 2,
              "a committed Allow reported failure after callback closure") ||
      !Expect(postcommit.UpdateRuntimeAuthorization(
                  AllowedAuthorization(3, target_b), &generation) ==
                      CaptureSafetyUpdateResult::kAuthorizationSuperseded &&
                  generation == 3,
              "postcommit callback pending state reopened Allow") ||
      !Expect(postcommit.UpdateRuntimeAuthorization(
                  RuntimeAuthorization{BlockedPrivacy(3), std::nullopt},
                  &generation) == CaptureSafetyUpdateResult::kOk &&
                  generation == 4,
              "postcommit callback barrier was rejected")) {
    return false;
  }

  CaptureSafetyCore exhausted(37, 1, {},
                              std::numeric_limits<uint64_t>::max() - 3U);
  const uint64_t final_epoch = exhausted.InvalidateAuthorizationAdmission();
  return Expect(final_epoch == std::numeric_limits<uint64_t>::max() - 1U,
                "the final callback authorization epoch was not issued") &&
         Expect(
             exhausted.InvalidateAuthorizationAdmission() == 0 &&
                 !exhausted.admission_open(),
             "callback authorization epoch exhaustion did not fail closed") &&
         Expect(exhausted.UpdateRuntimeAuthorization(
                    RuntimeAuthorization{BlockedPrivacy(1), std::nullopt},
                    &generation) ==
                    CaptureSafetyUpdateResult::kGenerationExhausted,
                "an exhausted callback epoch accepted a later update");
}

bool TestCommandAdmissionAuthenticityAndInvalidation() {
  uint64_t next_nonce = 100;
  CaptureSafetyCore core(41, 1, [&next_nonce](uint64_t* low, uint64_t* high) {
    *low = ++next_nonce;
    *high = 1'000 + next_nonce;
    return true;
  });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                              &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "command core could not be authorized")) {
    return false;
  }

  constexpr uint64_t kOwnerEpoch = 7;
  CaptureCommandAdmission admission;
  if (!Expect(core.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                         target.target_epoch, kOwnerEpoch,
                                         &admission) ==
                  CaptureCommandAdmissionResult::kOk,
              "valid command admission was not issued") ||
      !Expect(admission.instance_epoch == 41 &&
                  admission.runtime_policy_revision == 1 &&
                  admission.persistence_generation == 2 &&
                  admission.target_epoch == 10 &&
                  (admission.authorization_epoch & 1U) != 0 &&
                  admission.nonce_low != 0 && admission.nonce_high != 0,
              "issued command admission snapshot was incomplete")) {
    return false;
  }

  CaptureCommandAdmissionPermit permit;
  CaptureCommandAdmission forged = admission;
  forged.nonce_low ^= 1U;
  if (!Expect(core.AcquireCommandAdmissionPermit(forged, CaptureCommand::kStart,
                                                 kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "forged nonce was accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  admission, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                      CaptureCommandAdmissionResult::kOk &&
                  permit,
              "a foreign nonce attempt consumed the valid admission")) {
    return false;
  }
  permit = {};
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  admission, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "consumed admission was replayed")) {
    return false;
  }

  CaptureCommandAdmission tamper_source;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &tamper_source) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "tamper admission could not be issued");
  }
  CaptureCommandAdmission tampered = tamper_source;
  ++tampered.target_epoch;
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  tampered, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "tampered admission fields were accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  tamper_source, CaptureCommand::kStart, kOwnerEpoch,
                  &permit) == CaptureCommandAdmissionResult::kAdmissionRejected,
              "matching-nonce tamper did not consume the admission")) {
    return false;
  }

  CaptureCommandAdmission wrong_action;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &wrong_action) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "wrong-action admission could not be issued");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  wrong_action, CaptureCommand::kResume, kOwnerEpoch,
                  &permit) == CaptureCommandAdmissionResult::kAdmissionRejected,
              "start admission was accepted for resume") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  wrong_action, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "wrong-action attempt did not consume the admission")) {
    return false;
  }

  CaptureCommandAdmission overwritten;
  CaptureCommandAdmission replacement;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &overwritten) != CaptureCommandAdmissionResult::kOk ||
      core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &replacement) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "replacement admission could not be issued");
  }
  if (!Expect(
          overwritten.nonce_low != replacement.nonce_low &&
              core.AcquireCommandAdmissionPermit(
                  overwritten, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
          "replacement did not invalidate the prior admission") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  replacement, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kOk,
              "replaced admission attempt consumed the current nonce")) {
    return false;
  }
  permit = {};

  CaptureSafetyCore foreign(42, 1, [](uint64_t* low, uint64_t* high) {
    *low = 9'001;
    *high = 9'002;
    return true;
  });
  uint64_t foreign_generation = 0;
  if (foreign.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                         &foreign_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "foreign command core could not be authorized");
  }
  CaptureCommandAdmission local_only;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &local_only) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "local-only admission could not be issued");
  }
  if (!Expect(foreign.AcquireCommandAdmissionPermit(
                  local_only, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "admission crossed native instances") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  local_only, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kOk,
              "foreign instance attempt consumed the local admission")) {
    return false;
  }
  permit = {};

  CaptureCommandAdmission stale_owner;
  if (core.IssueCommandAdmission(
          CaptureCommand::kResume, generation, target.target_epoch, kOwnerEpoch,
          &stale_owner) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "owner-bound admission could not be issued");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  stale_owner, CaptureCommand::kResume, kOwnerEpoch + 1,
                  &permit) == CaptureCommandAdmissionResult::kAdmissionRejected,
              "stale runtime owner epoch was accepted")) {
    return false;
  }

  CaptureCommandAdmission invalidated_by_expected_pair;
  if (core.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                 target.target_epoch, kOwnerEpoch,
                                 &invalidated_by_expected_pair) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "expected-pair admission could not be issued");
  }
  CaptureCommandAdmission rejected_output;
  if (!Expect(core.IssueCommandAdmission(CaptureCommand::kStart, generation + 1,
                                         target.target_epoch, kOwnerEpoch,
                                         &rejected_output) ==
                      CaptureCommandAdmissionResult::kAdmissionRejected &&
                  rejected_output.instance_epoch == 0,
              "mismatched expected generation was accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(invalidated_by_expected_pair,
                                                 CaptureCommand::kStart,
                                                 kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "failed replacement issue retained the prior admission")) {
    return false;
  }

  CaptureCommandAdmission idempotent_stale;
  if (core.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target.target_epoch, kOwnerEpoch,
          &idempotent_stale) != CaptureCommandAdmissionResult::kOk ||
      core.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                      &generation) !=
          CaptureSafetyUpdateResult::kOk ||
      generation != 2 ||
      core.authorization_epoch() == idempotent_stale.authorization_epoch) {
    return Expect(false, "idempotent update did not rotate admission epoch");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  idempotent_stale, CaptureCommand::kStart, kOwnerEpoch,
                  &permit) == CaptureCommandAdmissionResult::kAdmissionRejected,
              "idempotent close/reopen revived an old admission")) {
    return false;
  }

  CaptureCommandAdmission stopped;
  if (core.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                 target.target_epoch, kOwnerEpoch, &stopped) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "pre-stop admission could not be issued");
  }
  core.BeginRevoke();
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  stopped, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "stop did not invalidate command admission")) {
    return false;
  }

  CaptureSafetyCore rng_failure(43, 1,
                                [](uint64_t*, uint64_t*) { return false; });
  uint64_t rng_generation = 0;
  if (rng_failure.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                             &rng_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "RNG failure core could not be authorized");
  }
  CaptureCommandAdmission failed_nonce;
  if (!Expect(rng_failure.IssueCommandAdmission(
                  CaptureCommand::kStart, rng_generation, target.target_epoch,
                  kOwnerEpoch, &failed_nonce) ==
                      CaptureCommandAdmissionResult::kInternalError &&
                  failed_nonce.instance_epoch == 0,
              "nonce generator failure did not fail closed")) {
    return false;
  }

  CaptureSafetyCore zero_nonce(44, 1, [](uint64_t* low, uint64_t* high) {
    *low = 0;
    *high = 0;
    return true;
  });
  uint64_t zero_nonce_generation = 0;
  if (zero_nonce.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                            &zero_nonce_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "zero-nonce core could not be authorized");
  }
  CaptureCommandAdmission zero_admission;
  return Expect(zero_nonce.IssueCommandAdmission(
                    CaptureCommand::kStart, zero_nonce_generation,
                    target.target_epoch, kOwnerEpoch, &zero_admission) ==
                        CaptureCommandAdmissionResult::kInternalError &&
                    zero_admission.instance_epoch == 0,
                "all-zero generated nonce did not fail closed");
}

bool TestCommandAdmissionLinearization() {
  const CaptureTargetIdentity target_a = Target(100, 200, 300, 10);
  const CaptureTargetIdentity target_b = Target(101, 201, 301, 11);
  CaptureSafetyCore start_first(51, 1, [](uint64_t* low, uint64_t* high) {
    *low = 5'001;
    *high = 5'002;
    return true;
  });
  uint64_t generation = 0;
  if (start_first.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                             &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "start-first core could not be authorized");
  }
  CaptureCommandAdmission admission;
  if (start_first.IssueCommandAdmission(CaptureCommand::kStart, generation,
                                        target_a.target_epoch, 1, &admission) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "start-first admission could not be issued");
  }

  Gate permit_acquired;
  Gate release_permit;
  Gate admission_closed;
  std::atomic<bool> update_finished{false};
  std::atomic<CaptureCommandAdmissionResult> acquire_result{
      CaptureCommandAdmissionResult::kInvalidArgument};
  std::atomic<CaptureSafetyUpdateResult> update_result{
      CaptureSafetyUpdateResult::kInvalidArgument};
  std::thread starter([&] {
    CaptureCommandAdmissionPermit permit;
    acquire_result.store(start_first.AcquireCommandAdmissionPermit(
                             admission, CaptureCommand::kStart, 1, &permit),
                         std::memory_order_relaxed);
    permit_acquired.Open();
    release_permit.Wait();
  });
  permit_acquired.Wait();
  std::thread updater([&] {
    const auto ticket = start_first.BeginAuthorizationUpdate();
    admission_closed.Open();
    uint64_t updated_generation = 0;
    update_result.store(
        start_first.CompleteRuntimeAuthorization(
            ticket, AllowedAuthorization(2, target_b), &updated_generation),
        std::memory_order_relaxed);
    generation = updated_generation;
    update_finished.store(true, std::memory_order_release);
  });
  admission_closed.Wait();
  if (!Expect(acquire_result.load(std::memory_order_relaxed) ==
                  CaptureCommandAdmissionResult::kOk,
              "start did not acquire the A admission") ||
      !Expect(!update_finished.load(std::memory_order_acquire),
              "A-to-B update crossed the held command permit")) {
    release_permit.Open();
    starter.join();
    updater.join();
    return false;
  }
  release_permit.Open();
  starter.join();
  updater.join();
  if (!Expect(update_result.load(std::memory_order_relaxed) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "A-to-B update did not complete after start admission")) {
    return false;
  }

  CaptureSafetyCore update_first(52, 1, [](uint64_t* low, uint64_t* high) {
    *low = 5'101;
    *high = 5'102;
    return true;
  });
  if (update_first.UpdateRuntimeAuthorization(AllowedAuthorization(1, target_a),
                                              &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "update-first core could not be authorized");
  }
  if (update_first.IssueCommandAdmission(
          CaptureCommand::kStart, generation, target_a.target_epoch, 1,
          &admission) != CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "update-first admission could not be issued");
  }
  const auto ticket = update_first.BeginAuthorizationUpdate();
  CaptureCommandAdmissionPermit permit;
  if (!Expect(update_first.AcquireCommandAdmissionPermit(
                  admission, CaptureCommand::kStart, 1, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "closed B transition admitted stale A start")) {
    return false;
  }
  return Expect(update_first.CompleteRuntimeAuthorization(
                    ticket, AllowedAuthorization(2, target_b), &generation) ==
                        CaptureSafetyUpdateResult::kOk &&
                    generation == 3,
                "update-first B transition did not complete");
}

bool TestRuntimeOwnerWaiterDrainBeforeRestart() {
  Gate waiter_at_exit;
  Gate release_waiter;
  Gate worker_started;
  std::atomic<bool> hold_waiter_exit{false};
  std::atomic<uint32_t> exit_hook_calls{0};
  CaptureRuntimeOwner owner([&] {
    if (hold_waiter_exit.load(std::memory_order_acquire) &&
        exit_hook_calls.fetch_add(1, std::memory_order_relaxed) == 0) {
      waiter_at_exit.Open();
      release_waiter.Wait();
    }
  });
  uint64_t next_nonce = 7'000;
  CaptureSafetyCore safety(60, 1, [&next_nonce](uint64_t* low, uint64_t* high) {
    *low = ++next_nonce;
    *high = 10'000 + next_nonce;
    return true;
  });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(safety.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "waiter-drain safety core could not be authorized")) {
    return false;
  }

  CaptureCommandAdmissionPermit start_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &start_permit),
              "waiter-drain start permit could not be acquired") ||
      !Expect(owner.Start(std::move(start_permit),
                          [&](CaptureRuntimeOwner& runtime, PersistenceToken) {
                            worker_started.Open();
                            static_cast<void>(runtime.WaitForStop(5'000));
                          }),
              "waiter-drain worker did not start")) {
    return false;
  }
  worker_started.Wait();
  if (!Expect(owner.RequestStop() == CaptureRuntimeStopResult::kStopRequested,
              "waiter-drain stop was not requested")) {
    owner.Shutdown();
    return false;
  }

  CaptureRuntimeWaitResult old_wait_result = CaptureRuntimeWaitResult::kTimeout;
  hold_waiter_exit.store(true, std::memory_order_release);
  std::thread old_waiter([&] { old_wait_result = owner.WaitStopped(5'000); });
  waiter_at_exit.Wait();

  CaptureCommandAdmissionPermit blocked_start_permit;
  const bool acquired_blocked_start = AcquireOwnerPermit(
      safety, owner, target, CaptureCommand::kStart, &blocked_start_permit);
  const uint64_t epoch_before_blocked_start = owner.owner_epoch();
  const bool blocked_start_result =
      acquired_blocked_start &&
      owner.Start(std::move(blocked_start_permit),
                  [](CaptureRuntimeOwner&, PersistenceToken) {});
  const uint64_t epoch_after_blocked_start = owner.owner_epoch();

  release_waiter.Open();
  old_waiter.join();
  if (!Expect(acquired_blocked_start,
              "restart permit could not be acquired during waiter drain") ||
      !Expect(!blocked_start_result,
              "a new run started before the old waiter drained") ||
      !Expect(epoch_after_blocked_start == epoch_before_blocked_start,
              "rejected restart advanced the owner epoch") ||
      !Expect(old_wait_result == CaptureRuntimeWaitResult::kStopped &&
                  owner.join_count() == 1,
              "old waiter did not retain the old run result")) {
    owner.Shutdown();
    return false;
  }

  CaptureCommandAdmissionPermit restart_permit;
  return Expect(AcquireOwnerPermit(safety, owner, target,
                                   CaptureCommand::kStart, &restart_permit),
                "post-drain restart permit could not be acquired") &&
         Expect(owner.Start(std::move(restart_permit),
                            [](CaptureRuntimeOwner&, PersistenceToken) {}),
                "post-drain restart was rejected") &&
         Expect(owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kStopped,
                "post-drain run did not stop") &&
         Expect(owner.join_count() == 2,
                "post-drain run was not joined exactly once");
}

bool TestRuntimeOwnerExitHookFailurePreservesLifecycleResult() {
  Gate worker_started;
  CaptureRuntimeOwner owner(
      [] { throw std::runtime_error("injected waiter-exit hook failure"); });
  uint64_t next_nonce = 7'500;
  CaptureSafetyCore safety(601, 1,
                           [&next_nonce](uint64_t* low, uint64_t* high) {
                             *low = ++next_nonce;
                             *high = 15'000 + next_nonce;
                             return true;
                           });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(safety.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "exit-hook safety core could not be authorized")) {
    return false;
  }

  CaptureCommandAdmissionPermit start_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &start_permit),
              "exit-hook start permit could not be acquired") ||
      !Expect(owner.Start(std::move(start_permit),
                          [&](CaptureRuntimeOwner& runtime, PersistenceToken) {
                            worker_started.Open();
                            static_cast<void>(runtime.WaitForStop(5'000));
                          }),
              "exit-hook worker did not start")) {
    return false;
  }
  worker_started.Wait();

  if (!Expect(owner.WaitStopped(0) == CaptureRuntimeWaitResult::kTimeout,
              "exit-hook failure converted timeout into a terminal result")) {
    owner.Shutdown();
    return false;
  }
  owner.Shutdown();
  return Expect(owner.join_count() == 1,
                "shutdown did not join after an exit-hook failure") &&
         Expect(!owner.worker_failed(),
                "exit-hook failure was attributed to the worker");
}

bool TestRuntimeOwnerTimeoutAndSingleJoin() {
  CaptureRuntimeOwner owner;
  uint64_t next_nonce = 8'000;
  CaptureSafetyCore safety(61, 1, [&next_nonce](uint64_t* low, uint64_t* high) {
    *low = ++next_nonce;
    *high = 20'000 + next_nonce;
    return true;
  });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (safety.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                        &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "runtime owner safety core could not be authorized");
  }

  Gate worker_started;
  Gate stop_observed;
  Gate release_worker;
  CaptureCommandAdmissionPermit start_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &start_permit),
              "runtime start grant could not be acquired") ||
      !Expect(owner.Start(std::move(start_permit),
                          [&](CaptureRuntimeOwner& runtime, PersistenceToken) {
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

  if (!Expect(
          owner.RequestPause() == CaptureRuntimePauseResult::kPauseRequested,
          "runtime pause was not requested before resume")) {
    release_worker.Open();
    owner.Shutdown();
    return false;
  }
  CaptureCommandAdmissionPermit resume_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kResume,
                                 &resume_permit),
              "runtime resume grant could not be acquired") ||
      !Expect(owner.Resume(std::move(resume_permit)),
              "runtime resume grant was rejected")) {
    release_worker.Open();
    owner.Shutdown();
    return false;
  }

  const uint64_t stale_owner_epoch = owner.owner_epoch();
  CaptureCommandAdmission stale_resume;
  if (!Expect(safety.IssueCommandAdmission(CaptureCommand::kResume, generation,
                                           target.target_epoch,
                                           stale_owner_epoch, &stale_resume) ==
                  CaptureCommandAdmissionResult::kOk,
              "stale owner admission could not be issued") ||
      !Expect(owner.RequestStop() == CaptureRuntimeStopResult::kStopRequested,
              "first stop request was not observed") ||
      !Expect(owner.owner_epoch() != stale_owner_epoch,
              "stop did not advance runtime owner epoch") ||
      !Expect(owner.RequestStop() == CaptureRuntimeStopResult::kAlreadyStopped,
              "repeated stop request was not idempotent") ||
      !Expect(owner.WaitStopped(0) == CaptureRuntimeWaitResult::kTimeout,
              "wait did not time out while the worker was active")) {
    release_worker.Open();
    owner.Shutdown();
    return false;
  }
  CaptureCommandAdmissionPermit stale_permit;
  if (!Expect(safety.AcquireCommandAdmissionPermit(
                  stale_resume, CaptureCommand::kResume, owner.owner_epoch(),
                  &stale_permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "stop did not invalidate the runtime owner binding")) {
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
    waiters.emplace_back(
        [&, index] { results[index] = owner.WaitStopped(5'000); });
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

  CaptureCommandAdmissionPermit fault_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &fault_permit),
              "fault-injection start grant could not be acquired") ||
      !Expect(owner.Start(std::move(fault_permit),
                          [](CaptureRuntimeOwner&, PersistenceToken) {
                            throw std::runtime_error("injected worker failure");
                          }),
              "runtime owner could not restart for fault injection") ||
      !Expect(
          owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kWorkerFailed,
          "worker exception did not produce kWorkerFailed") ||
      !Expect(owner.join_count() == 2,
              "faulted worker was not joined exactly once")) {
    return false;
  }

  CaptureCommandAdmissionPermit final_permit;
  return Expect(AcquireOwnerPermit(safety, owner, target,
                                   CaptureCommand::kStart, &final_permit),
                "final start grant could not be acquired") &&
         Expect(owner.Start(std::move(final_permit),
                            [](CaptureRuntimeOwner&, PersistenceToken) {}),
                "runtime owner could not restart after a fault") &&
         Expect(owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kStopped,
                "naturally exited worker was not joined") &&
         Expect(owner.join_count() == 3,
                "naturally exited worker join count was incorrect");
}

bool TestRuntimeOwnerControlMailbox() {
  CaptureRuntimeOwner owner;
  uint64_t next_nonce = 9'000;
  CaptureSafetyCore safety(71, 1, [&next_nonce](uint64_t* low, uint64_t* high) {
    *low = ++next_nonce;
    *high = 30'000 + next_nonce;
    return true;
  });
  const CaptureTargetIdentity target = Target(101, 201, 301, 11);
  uint64_t generation = 0;
  if (!Expect(safety.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "mailbox safety core could not be authorized")) {
    return false;
  }

  Gate worker_started;
  Gate first_pause_observed;
  Gate first_resume_observed;
  Gate second_pause_observed;
  Gate stop_observed;
  std::atomic<bool> control_wait_timed_out{false};
  PersistenceToken observed_initial_token;
  std::vector<PersistenceToken> observed_replacement_tokens;

  CaptureCommandAdmissionPermit start_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &start_permit),
              "mailbox start permit could not be acquired")) {
    return false;
  }
  const PersistenceToken expected_initial_token =
      start_permit.persistence_token();
  if (!Expect(owner.Start(
                  std::move(start_permit),
                  [&](CaptureRuntimeOwner& runtime, PersistenceToken token) {
                    observed_initial_token = std::move(token);
                    auto snapshot = runtime.ReadControlSnapshot();
                    size_t pause_count = 0;
                    worker_started.Open();
                    while (!snapshot.stop_requested) {
                      auto changed = runtime.WaitForControlChange(
                          snapshot.sequence, 5'000);
                      if (!changed.has_value()) {
                        control_wait_timed_out.store(true,
                                                     std::memory_order_relaxed);
                        return;
                      }
                      snapshot = std::move(*changed);
                      if (snapshot.stop_requested) {
                        stop_observed.Open();
                        return;
                      }
                      if (snapshot.pause_requested) {
                        ++pause_count;
                        if (pause_count == 1) {
                          first_pause_observed.Open();
                        } else if (pause_count == 2) {
                          second_pause_observed.Open();
                        }
                      } else if (snapshot.replacement_token.has_value()) {
                        observed_replacement_tokens.push_back(
                            *snapshot.replacement_token);
                        if (observed_replacement_tokens.size() == 1) {
                          first_resume_observed.Open();
                        }
                      }
                    }
                  }),
              "mailbox worker did not start")) {
    return false;
  }
  worker_started.Wait();

  const auto initial_snapshot = owner.ReadControlSnapshot();
  if (!Expect(initial_snapshot.sequence != 0 &&
                  !initial_snapshot.stop_requested &&
                  !initial_snapshot.pause_requested &&
                  !initial_snapshot.replacement_token.has_value(),
              "mailbox did not expose a clean initial snapshot")) {
    owner.Shutdown();
    return false;
  }

  CaptureCommandAdmissionPermit stale_resume_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kResume,
                                 &stale_resume_permit),
              "pre-pause resume permit could not be acquired")) {
    owner.Shutdown();
    return false;
  }
  const uint64_t epoch_before_pause = owner.owner_epoch();
  if (!Expect(
          owner.RequestPause() == CaptureRuntimePauseResult::kPauseRequested,
          "pause did not update the mailbox") ||
      !Expect(owner.owner_epoch() != epoch_before_pause,
              "pause did not advance the owner epoch")) {
    owner.Shutdown();
    return false;
  }
  const auto paused_snapshot = owner.ReadControlSnapshot();
  const uint64_t paused_owner_epoch = owner.owner_epoch();
  if (!Expect(paused_snapshot.sequence > initial_snapshot.sequence &&
                  paused_snapshot.pause_requested &&
                  !paused_snapshot.stop_requested &&
                  !paused_snapshot.replacement_token.has_value(),
              "pause snapshot was incomplete") ||
      !Expect(owner.RequestPause() == CaptureRuntimePauseResult::kAlreadyPaused,
              "repeated pause was not idempotent") ||
      !Expect(
          owner.owner_epoch() == paused_owner_epoch &&
              owner.ReadControlSnapshot().sequence == paused_snapshot.sequence,
          "repeated pause advanced the mailbox")) {
    owner.Shutdown();
    return false;
  }
  first_pause_observed.Wait();

  if (!Expect(!owner.Resume(std::move(stale_resume_permit)),
              "pause accepted a stale resume permit") ||
      !Expect(owner.ReadControlSnapshot().sequence == paused_snapshot.sequence,
              "stale resume changed the mailbox")) {
    owner.Shutdown();
    return false;
  }

  if (!Expect(safety.UpdateRuntimeAuthorization(AllowedAuthorization(2, target),
                                                &generation) ==
                      CaptureSafetyUpdateResult::kOk &&
                  generation == 3,
              "resume authorization did not rotate persistence generation")) {
    owner.Shutdown();
    return false;
  }
  CaptureCommandAdmissionPermit resume_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kResume,
                                 &resume_permit),
              "fresh resume permit could not be acquired")) {
    owner.Shutdown();
    return false;
  }
  const PersistenceToken expected_replacement_token =
      resume_permit.persistence_token();
  const uint64_t epoch_before_resume = owner.owner_epoch();
  if (!Expect(owner.Resume(std::move(resume_permit)),
              "fresh resume permit was rejected") ||
      !Expect(owner.owner_epoch() != epoch_before_resume,
              "resume did not advance the owner epoch")) {
    owner.Shutdown();
    return false;
  }
  const auto resumed_snapshot = owner.ReadControlSnapshot();
  if (!Expect(
          resumed_snapshot.sequence > paused_snapshot.sequence &&
              !resumed_snapshot.stop_requested &&
              !resumed_snapshot.pause_requested &&
              resumed_snapshot.replacement_token == expected_replacement_token,
          "resume did not publish its replacement token")) {
    owner.Shutdown();
    return false;
  }
  first_resume_observed.Wait();

  CaptureCommandAdmissionPermit redundant_resume_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kResume,
                                 &redundant_resume_permit),
              "redundant resume permit could not be acquired")) {
    owner.Shutdown();
    return false;
  }
  const uint64_t resumed_owner_epoch = owner.owner_epoch();
  if (!Expect(!owner.Resume(std::move(redundant_resume_permit)),
              "resume was accepted outside the paused state") ||
      !Expect(
          owner.owner_epoch() == resumed_owner_epoch &&
              owner.ReadControlSnapshot().sequence == resumed_snapshot.sequence,
          "redundant resume advanced the mailbox")) {
    owner.Shutdown();
    return false;
  }

  if (!Expect(
          owner.RequestPause() == CaptureRuntimePauseResult::kPauseRequested,
          "second pause was rejected")) {
    owner.Shutdown();
    return false;
  }
  second_pause_observed.Wait();
  const auto second_paused_snapshot = owner.ReadControlSnapshot();
  CaptureCommandAdmissionPermit racing_resume_permit;
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kResume,
                                 &racing_resume_permit),
              "racing resume permit could not be acquired")) {
    owner.Shutdown();
    return false;
  }

  Gate start_race;
  std::atomic<bool> racing_resume_result{false};
  CaptureRuntimeStopResult racing_stop_result =
      CaptureRuntimeStopResult::kAlreadyStopped;
  std::thread resumer([&owner, &start_race, &racing_resume_result,
                       permit = std::move(racing_resume_permit)]() mutable {
    start_race.Wait();
    racing_resume_result.store(owner.Resume(std::move(permit)),
                               std::memory_order_relaxed);
  });
  std::thread stopper([&] {
    start_race.Wait();
    racing_stop_result = owner.RequestStop();
  });
  start_race.Open();
  resumer.join();
  stopper.join();
  stop_observed.Wait();

  const auto stopped_snapshot = owner.ReadControlSnapshot();
  const uint64_t stopped_owner_epoch = owner.owner_epoch();
  const auto sticky_stop_snapshot =
      owner.WaitForControlChange(stopped_snapshot.sequence, 0);
  const uint64_t expected_minimum_stop_sequence =
      second_paused_snapshot.sequence +
      (racing_resume_result.load(std::memory_order_relaxed) ? 2U : 1U);
  if (!Expect(racing_stop_result == CaptureRuntimeStopResult::kStopRequested,
              "racing stop was not accepted") ||
      !Expect(stopped_snapshot.stop_requested &&
                  !stopped_snapshot.pause_requested &&
                  !stopped_snapshot.replacement_token.has_value() &&
                  stopped_snapshot.sequence >= expected_minimum_stop_sequence,
              "stop did not dominate the final mailbox snapshot") ||
      !Expect(sticky_stop_snapshot.has_value() &&
                  sticky_stop_snapshot->stop_requested,
              "sticky stop depended on another sequence increment") ||
      !Expect(owner.RequestStop() == CaptureRuntimeStopResult::kAlreadyStopped,
              "repeated racing stop was not idempotent") ||
      !Expect(owner.RequestPause() == CaptureRuntimePauseResult::kNotRunning,
              "pause was accepted after stop") ||
      !Expect(
          owner.owner_epoch() == stopped_owner_epoch &&
              owner.ReadControlSnapshot().sequence == stopped_snapshot.sequence,
          "repeated stop or pause advanced the mailbox")) {
    owner.Shutdown();
    return false;
  }
  if (!Expect(owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kStopped &&
                  owner.join_count() == 1,
              "mailbox worker did not stop with a single join") ||
      !Expect(!control_wait_timed_out.load(std::memory_order_relaxed),
              "mailbox worker timed out waiting for control") ||
      !Expect(observed_initial_token == expected_initial_token,
              "start did not pass the initial persistence token by value") ||
      !Expect(
          !observed_replacement_tokens.empty() &&
              observed_replacement_tokens.front() == expected_replacement_token,
          "worker did not observe the resume replacement token")) {
    return false;
  }

  CaptureRuntimeOwner shutdown_owner;
  Gate shutdown_worker_started;
  std::atomic<bool> shutdown_stop_observed{false};
  CaptureCommandAdmissionPermit shutdown_start_permit;
  if (!Expect(
          AcquireOwnerPermit(safety, shutdown_owner, target,
                             CaptureCommand::kStart, &shutdown_start_permit),
          "shutdown start permit could not be acquired") ||
      !Expect(shutdown_owner.Start(
                  std::move(shutdown_start_permit),
                  [&](CaptureRuntimeOwner& runtime, PersistenceToken) {
                    auto snapshot = runtime.ReadControlSnapshot();
                    shutdown_worker_started.Open();
                    while (!snapshot.stop_requested) {
                      auto changed = runtime.WaitForControlChange(
                          snapshot.sequence, 5'000);
                      if (!changed.has_value()) {
                        return;
                      }
                      snapshot = std::move(*changed);
                    }
                    shutdown_stop_observed.store(true,
                                                 std::memory_order_relaxed);
                  }),
              "shutdown worker did not start")) {
    return false;
  }
  shutdown_worker_started.Wait();
  shutdown_owner.Shutdown();
  return Expect(shutdown_stop_observed.load(std::memory_order_relaxed),
                "shutdown did not wake the control mailbox") &&
         Expect(shutdown_owner.join_count() == 1,
                "shutdown joined the mailbox worker more than once");
}

bool TestRuntimeOwnerCompletionRunsAfterExitPublication() {
  CaptureRuntimeOwner owner;
  CaptureSafetyCore safety(75, 1);
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(safety.UpdateRuntimeAuthorization(AllowedAuthorization(1, target),
                                                &generation) ==
                  CaptureSafetyUpdateResult::kOk,
              "completion-timing authorization failed")) {
    return false;
  }

  CaptureCommandAdmissionPermit permit;
  Gate release_worker;
  Gate completion_started;
  Gate release_completion;
  std::atomic<CaptureRuntimePauseResult> pause_from_completion{
      CaptureRuntimePauseResult::kPauseRequested};
  std::atomic<uint32_t> completion_calls{0};
  if (!Expect(AcquireOwnerPermit(safety, owner, target, CaptureCommand::kStart,
                                 &permit),
              "completion-timing permit failed") ||
      !Expect(owner.Start(
                  std::move(permit),
                  [&](CaptureRuntimeOwner&, PersistenceToken) {
                    release_worker.Wait();
                  },
                  [&] {
                    pause_from_completion.store(owner.RequestPause(),
                                                std::memory_order_release);
                    completion_calls.fetch_add(1, std::memory_order_relaxed);
                    completion_started.Open();
                    release_completion.Wait();
                  }),
              "completion-timing worker failed to start")) {
    return false;
  }

  release_worker.Open();
  completion_started.Wait();
  const bool published_before_callback =
      Expect(pause_from_completion.load(std::memory_order_acquire) ==
                 CaptureRuntimePauseResult::kNotRunning,
             "completion ran before worker exit was published");
  release_completion.Open();
  return published_before_callback &&
         Expect(owner.WaitStopped(5'000) == CaptureRuntimeWaitResult::kStopped,
                "completion-timing worker did not join") &&
         Expect(completion_calls.load(std::memory_order_relaxed) == 1,
                "completion was not invoked exactly once");
}

}  // namespace

int main() {
  if (!TestTargetTupleAndInstanceEpoch() || !TestRevisionAndGenerationRules() ||
      !TestDisplayWideTargetTupleAndPermits() ||
      !TestPermitLinearizationAndPersistenceStages() ||
      !TestPersistencePermitIssuerBinding() ||
      !TestCallbackTimeAuthorizationInvalidation() ||
      !TestCommandAdmissionAuthenticityAndInvalidation() ||
      !TestCommandAdmissionRetainsDisplayBinding() ||
      !TestCommandAdmissionLinearization() ||
      !TestRuntimeOwnerWaiterDrainBeforeRestart() ||
      !TestRuntimeOwnerExitHookFailurePreservesLifecycleResult() ||
      !TestRuntimeOwnerCompletionRunsAfterExitPublication() ||
      !TestRuntimeOwnerControlMailbox() ||
      !TestRuntimeOwnerTimeoutAndSingleJoin()) {
    return 1;
  }
  std::cout << "capture safety core tests passed\n";
  return 0;
}

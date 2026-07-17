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
using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureCommandAdmission;
using windayflow::capture::CaptureCommandAdmissionPermit;
using windayflow::capture::CaptureCommandAdmissionResult;
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

bool AcquireOwnerPermit(CaptureSafetyCore& core,
                        CaptureRuntimeOwner& owner,
                        const CaptureTargetIdentity& target,
                        CaptureCommand command,
                        CaptureCommandAdmissionPermit* permit) {
  const uint64_t owner_epoch = owner.owner_epoch();
  CaptureCommandAdmission admission;
  return owner_epoch != 0 &&
         core.IssueCommandAdmission(command,
                                    core.persistence_generation(),
                                    target.target_epoch,
                                    owner_epoch,
                                    &admission) ==
             CaptureCommandAdmissionResult::kOk &&
         core.AcquireCommandAdmissionPermit(
             admission, command, owner_epoch, permit) ==
             CaptureCommandAdmissionResult::kOk;
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

bool TestCommandAdmissionAuthenticityAndInvalidation() {
  uint64_t next_nonce = 100;
  CaptureSafetyCore core(
      41,
      1,
      [&next_nonce](uint64_t* low, uint64_t* high) {
        *low = ++next_nonce;
        *high = 1'000 + next_nonce;
        return true;
      });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (!Expect(core.UpdateRuntimeAuthorization(
                  AllowedAuthorization(1, target), &generation) ==
                  CaptureSafetyUpdateResult::kOk &&
                  generation == 2,
              "command core could not be authorized")) {
    return false;
  }

  constexpr uint64_t kOwnerEpoch = 7;
  CaptureCommandAdmission admission;
  if (!Expect(core.IssueCommandAdmission(CaptureCommand::kStart,
                                         generation,
                                         target.target_epoch,
                                         kOwnerEpoch,
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
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  forged, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "forged nonce was accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  admission, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kOk && permit,
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
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &tamper_source) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "tamper admission could not be issued");
  }
  CaptureCommandAdmission tampered = tamper_source;
  ++tampered.target_epoch;
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  tampered, CaptureCommand::kStart, kOwnerEpoch, &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "tampered admission fields were accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  tamper_source,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "matching-nonce tamper did not consume the admission")) {
    return false;
  }

  CaptureCommandAdmission wrong_action;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &wrong_action) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "wrong-action admission could not be issued");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  wrong_action,
                  CaptureCommand::kResume,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "start admission was accepted for resume") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  wrong_action,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "wrong-action attempt did not consume the admission")) {
    return false;
  }

  CaptureCommandAdmission overwritten;
  CaptureCommandAdmission replacement;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &overwritten) !=
          CaptureCommandAdmissionResult::kOk ||
      core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &replacement) !=
          CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "replacement admission could not be issued");
  }
  if (!Expect(overwritten.nonce_low != replacement.nonce_low &&
                  core.AcquireCommandAdmissionPermit(
                      overwritten,
                      CaptureCommand::kStart,
                      kOwnerEpoch,
                      &permit) ==
                      CaptureCommandAdmissionResult::kAdmissionRejected,
              "replacement did not invalidate the prior admission") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  replacement,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) == CaptureCommandAdmissionResult::kOk,
              "replaced admission attempt consumed the current nonce")) {
    return false;
  }
  permit = {};

  CaptureSafetyCore foreign(
      42,
      1,
      [](uint64_t* low, uint64_t* high) {
        *low = 9'001;
        *high = 9'002;
        return true;
      });
  uint64_t foreign_generation = 0;
  if (foreign.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &foreign_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "foreign command core could not be authorized");
  }
  CaptureCommandAdmission local_only;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &local_only) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "local-only admission could not be issued");
  }
  if (!Expect(foreign.AcquireCommandAdmissionPermit(
                  local_only,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "admission crossed native instances") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  local_only,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) == CaptureCommandAdmissionResult::kOk,
              "foreign instance attempt consumed the local admission")) {
    return false;
  }
  permit = {};

  CaptureCommandAdmission stale_owner;
  if (core.IssueCommandAdmission(CaptureCommand::kResume,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &stale_owner) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "owner-bound admission could not be issued");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  stale_owner,
                  CaptureCommand::kResume,
                  kOwnerEpoch + 1,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "stale runtime owner epoch was accepted")) {
    return false;
  }

  CaptureCommandAdmission invalidated_by_expected_pair;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &invalidated_by_expected_pair) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "expected-pair admission could not be issued");
  }
  CaptureCommandAdmission rejected_output;
  if (!Expect(core.IssueCommandAdmission(CaptureCommand::kStart,
                                         generation + 1,
                                         target.target_epoch,
                                         kOwnerEpoch,
                                         &rejected_output) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected &&
                  rejected_output.instance_epoch == 0,
              "mismatched expected generation was accepted") ||
      !Expect(core.AcquireCommandAdmissionPermit(
                  invalidated_by_expected_pair,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "failed replacement issue retained the prior admission")) {
    return false;
  }

  CaptureCommandAdmission idempotent_stale;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &idempotent_stale) !=
          CaptureCommandAdmissionResult::kOk ||
      core.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &generation) !=
          CaptureSafetyUpdateResult::kOk ||
      generation != 2 ||
      core.authorization_epoch() == idempotent_stale.authorization_epoch) {
    return Expect(false, "idempotent update did not rotate admission epoch");
  }
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  idempotent_stale,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "idempotent close/reopen revived an old admission")) {
    return false;
  }

  CaptureCommandAdmission stopped;
  if (core.IssueCommandAdmission(CaptureCommand::kStart,
                                 generation,
                                 target.target_epoch,
                                 kOwnerEpoch,
                                 &stopped) !=
      CaptureCommandAdmissionResult::kOk) {
    return Expect(false, "pre-stop admission could not be issued");
  }
  core.BeginRevoke();
  if (!Expect(core.AcquireCommandAdmissionPermit(
                  stopped,
                  CaptureCommand::kStart,
                  kOwnerEpoch,
                  &permit) ==
                  CaptureCommandAdmissionResult::kAdmissionRejected,
              "stop did not invalidate command admission")) {
    return false;
  }

  CaptureSafetyCore rng_failure(
      43,
      1,
      [](uint64_t*, uint64_t*) { return false; });
  uint64_t rng_generation = 0;
  if (rng_failure.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &rng_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "RNG failure core could not be authorized");
  }
  CaptureCommandAdmission failed_nonce;
  if (!Expect(rng_failure.IssueCommandAdmission(
                  CaptureCommand::kStart,
                  rng_generation,
                  target.target_epoch,
                  kOwnerEpoch,
                  &failed_nonce) ==
                  CaptureCommandAdmissionResult::kInternalError &&
                  failed_nonce.instance_epoch == 0,
              "nonce generator failure did not fail closed")) {
    return false;
  }

  CaptureSafetyCore zero_nonce(
      44,
      1,
      [](uint64_t* low, uint64_t* high) {
        *low = 0;
        *high = 0;
        return true;
      });
  uint64_t zero_nonce_generation = 0;
  if (zero_nonce.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &zero_nonce_generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "zero-nonce core could not be authorized");
  }
  CaptureCommandAdmission zero_admission;
  return Expect(zero_nonce.IssueCommandAdmission(
                    CaptureCommand::kStart,
                    zero_nonce_generation,
                    target.target_epoch,
                    kOwnerEpoch,
                    &zero_admission) ==
                    CaptureCommandAdmissionResult::kInternalError &&
                    zero_admission.instance_epoch == 0,
                "all-zero generated nonce did not fail closed");
}

bool TestCommandAdmissionLinearization() {
  const CaptureTargetIdentity target_a = Target(100, 200, 300, 10);
  const CaptureTargetIdentity target_b = Target(101, 201, 301, 11);
  CaptureSafetyCore start_first(
      51,
      1,
      [](uint64_t* low, uint64_t* high) {
        *low = 5'001;
        *high = 5'002;
        return true;
      });
  uint64_t generation = 0;
  if (start_first.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target_a), &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "start-first core could not be authorized");
  }
  CaptureCommandAdmission admission;
  if (start_first.IssueCommandAdmission(CaptureCommand::kStart,
                                        generation,
                                        target_a.target_epoch,
                                        1,
                                        &admission) !=
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
    acquire_result.store(
        start_first.AcquireCommandAdmissionPermit(
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
            ticket,
            AllowedAuthorization(2, target_b),
            &updated_generation),
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

  CaptureSafetyCore update_first(
      52,
      1,
      [](uint64_t* low, uint64_t* high) {
        *low = 5'101;
        *high = 5'102;
        return true;
      });
  if (update_first.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target_a), &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "update-first core could not be authorized");
  }
  if (update_first.IssueCommandAdmission(CaptureCommand::kStart,
                                         generation,
                                         target_a.target_epoch,
                                         1,
                                         &admission) !=
      CaptureCommandAdmissionResult::kOk) {
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
                    ticket,
                    AllowedAuthorization(2, target_b),
                    &generation) == CaptureSafetyUpdateResult::kOk &&
                    generation == 3,
                "update-first B transition did not complete");
}

bool TestRuntimeOwnerTimeoutAndSingleJoin() {
  CaptureRuntimeOwner owner;
  uint64_t next_nonce = 8'000;
  CaptureSafetyCore safety(
      61,
      1,
      [&next_nonce](uint64_t* low, uint64_t* high) {
        *low = ++next_nonce;
        *high = 20'000 + next_nonce;
        return true;
      });
  const CaptureTargetIdentity target = Target(100, 200, 300, 10);
  uint64_t generation = 0;
  if (safety.UpdateRuntimeAuthorization(
          AllowedAuthorization(1, target), &generation) !=
      CaptureSafetyUpdateResult::kOk) {
    return Expect(false, "runtime owner safety core could not be authorized");
  }

  Gate worker_started;
  Gate stop_observed;
  Gate release_worker;
  CaptureCommandAdmissionPermit start_permit;
  if (!Expect(AcquireOwnerPermit(safety,
                                 owner,
                                 target,
                                 CaptureCommand::kStart,
                                 &start_permit),
              "runtime start grant could not be acquired") ||
      !Expect(owner.Start(std::move(start_permit),
                          [&](CaptureRuntimeOwner& runtime) {
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

  CaptureCommandAdmissionPermit resume_permit;
  if (!Expect(AcquireOwnerPermit(safety,
                                 owner,
                                 target,
                                 CaptureCommand::kResume,
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
  if (!Expect(safety.IssueCommandAdmission(CaptureCommand::kResume,
                                           generation,
                                           target.target_epoch,
                                           stale_owner_epoch,
                                           &stale_resume) ==
                  CaptureCommandAdmissionResult::kOk,
              "stale owner admission could not be issued") ||
      !Expect(owner.RequestStop() ==
                  CaptureRuntimeStopResult::kStopRequested,
              "first stop request was not observed") ||
      !Expect(owner.owner_epoch() != stale_owner_epoch,
              "stop did not advance runtime owner epoch") ||
      !Expect(owner.RequestStop() ==
                  CaptureRuntimeStopResult::kAlreadyStopped,
              "repeated stop request was not idempotent") ||
      !Expect(owner.WaitStopped(0) == CaptureRuntimeWaitResult::kTimeout,
              "wait did not time out while the worker was active")) {
    release_worker.Open();
    owner.Shutdown();
    return false;
  }
  CaptureCommandAdmissionPermit stale_permit;
  if (!Expect(safety.AcquireCommandAdmissionPermit(
                  stale_resume,
                  CaptureCommand::kResume,
                  owner.owner_epoch(),
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

  CaptureCommandAdmissionPermit fault_permit;
  if (!Expect(AcquireOwnerPermit(safety,
                                 owner,
                                 target,
                                 CaptureCommand::kStart,
                                 &fault_permit),
              "fault-injection start grant could not be acquired") ||
      !Expect(owner.Start(std::move(fault_permit), [](CaptureRuntimeOwner&) {
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

  CaptureCommandAdmissionPermit final_permit;
  return Expect(AcquireOwnerPermit(safety,
                                   owner,
                                   target,
                                   CaptureCommand::kStart,
                                   &final_permit),
                "final start grant could not be acquired") &&
         Expect(owner.Start(std::move(final_permit),
                            [](CaptureRuntimeOwner&) {}),
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
      !TestCommandAdmissionAuthenticityAndInvalidation() ||
      !TestCommandAdmissionLinearization() ||
      !TestRuntimeOwnerTimeoutAndSingleJoin()) {
    return 1;
  }
  std::cout << "capture safety core tests passed\n";
  return 0;
}

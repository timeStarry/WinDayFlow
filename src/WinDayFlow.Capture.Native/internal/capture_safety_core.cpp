#include "capture_safety_core.h"

#include <atomic>
#include <chrono>
#include <limits>
#include <mutex>
#include <utility>

namespace windayflow::capture {
namespace {

std::atomic<uint64_t> g_next_instance_epoch{1};

uint64_t AllocateInstanceEpoch() {
  uint64_t current = g_next_instance_epoch.load(std::memory_order_relaxed);
  while (current != 0) {
    const uint64_t next =
        current == std::numeric_limits<uint64_t>::max() ? 0 : current + 1U;
    if (g_next_instance_epoch.compare_exchange_weak(current,
                                                    next,
                                                    std::memory_order_relaxed,
                                                    std::memory_order_relaxed)) {
      return current;
    }
  }
  return 0;
}

bool IsValidTarget(const CaptureTargetIdentity& target) {
  return target.window_handle != 0 && target.process_id != 0 &&
         target.process_creation_time_100ns != 0 && target.target_epoch != 0;
}

bool HasSameTargetTuple(const CaptureTargetIdentity& left,
                        const CaptureTargetIdentity& right) {
  return left.window_handle == right.window_handle &&
         left.process_id == right.process_id &&
         left.process_creation_time_100ns ==
             right.process_creation_time_100ns &&
         left.target_epoch == right.target_epoch;
}

}  // namespace

bool IsFullyAllowed(const PrivacyContext& context) {
  return EvaluatePrivacyContext(context).allowed;
}

CaptureSafetyCore::CaptureSafetyCore()
    : CaptureSafetyCore(AllocateInstanceEpoch(), 1) {}

CaptureSafetyCore::CaptureSafetyCore(
    uint64_t instance_epoch,
    uint64_t initial_persistence_generation)
    : instance_epoch_(instance_epoch),
      persistence_generation_(initial_persistence_generation),
      generation_exhausted_(instance_epoch == 0 ||
                            initial_persistence_generation == 0),
      observable_{initial_persistence_generation, 0} {}

CaptureSafetyUpdateResult CaptureSafetyCore::UpdateRuntimeAuthorization(
    const RuntimeAuthorization& authorization,
    uint64_t* persistence_generation) {
  const CaptureSafetyUpdateTicket ticket = BeginAuthorizationUpdate();
  return CompleteRuntimeAuthorization(
      ticket, authorization, persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::CompleteRuntimeAuthorization(
    const CaptureSafetyUpdateTicket& ticket,
    const RuntimeAuthorization& authorization,
    uint64_t* persistence_generation) {
  std::unique_lock lock(mutex_);
  return UpdateUnderLock(
      authorization,
      false,
      true,
      ticket,
      persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::UpdateLegacyPrivacyContext(
    const PrivacyContext& context,
    uint64_t* persistence_generation) {
  const CaptureSafetyUpdateTicket ticket = BeginAuthorizationUpdate();
  return CompleteLegacyPrivacyContext(
      ticket, context, persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::CompleteLegacyPrivacyContext(
    const CaptureSafetyUpdateTicket& ticket,
    const PrivacyContext& context,
    uint64_t* persistence_generation) {
  std::unique_lock lock(mutex_);
  return UpdateUnderLock(
      RuntimeAuthorization{context, std::nullopt},
      true,
      false,
      ticket,
      persistence_generation);
}

CaptureSafetyUpdateTicket CaptureSafetyCore::BeginAuthorizationUpdate()
    noexcept {
  return CloseAdmission();
}

void CaptureSafetyCore::BeginRevoke() noexcept {
  static_cast<void>(CloseAdmission());
}

bool CaptureSafetyCore::FinalizeRevoke(
    uint32_t timeout_ms,
    uint64_t* persistence_generation) {
  if (persistence_generation == nullptr) {
    return false;
  }
  BeginRevoke();
  std::unique_lock<std::shared_timed_mutex> lock(mutex_, std::defer_lock);
  if (!lock.try_lock_for(std::chrono::milliseconds(timeout_ms))) {
    return false;
  }

  *persistence_generation = persistence_generation_;
  if (generation_exhausted_ ||
      (revoked_ && !current_.target.has_value())) {
    return true;
  }
  if (!AdvanceGenerationUnderLock()) {
    *persistence_generation = persistence_generation_;
    return true;
  }
  RevokeStateUnderLock();
  *persistence_generation = persistence_generation_;
  return true;
}

CaptureSafetyUpdateResult CaptureSafetyCore::Revoke(
    uint64_t* persistence_generation) {
  if (persistence_generation == nullptr) {
    return CaptureSafetyUpdateResult::kInvalidArgument;
  }

  BeginRevoke();
  std::unique_lock lock(mutex_);
  *persistence_generation = persistence_generation_;
  if (generation_exhausted_) {
    return CaptureSafetyUpdateResult::kGenerationExhausted;
  }
  if (revoked_ && !current_.target.has_value()) {
    return CaptureSafetyUpdateResult::kOk;
  }
  if (!AdvanceGenerationUnderLock()) {
    *persistence_generation = persistence_generation_;
    return CaptureSafetyUpdateResult::kGenerationExhausted;
  }

  RevokeStateUnderLock();
  *persistence_generation = persistence_generation_;
  return CaptureSafetyUpdateResult::kOk;
}

std::optional<PersistenceToken> CaptureSafetyCore::MintPersistenceToken(
    const CaptureTargetIdentity& observed_target) const {
  if (!admission_open()) {
    return std::nullopt;
  }
  std::shared_lock lock(mutex_);
  if (!admission_open() ||
      generation_exhausted_ || revoked_ || !current_.target.has_value() ||
      !IsFullyAllowed(current_.privacy) ||
      !HasSameTargetTuple(*current_.target, observed_target)) {
    return std::nullopt;
  }

  return PersistenceToken{
      instance_epoch_, persistence_generation_, observed_target};
}

PersistencePermit CaptureSafetyCore::AcquirePersistencePermit(
    const PersistenceToken& token,
    const CaptureTargetIdentity& observed_target) const {
  if (!admission_open()) {
    return {};
  }
  std::shared_lock lock(mutex_);
  if (!admission_open() ||
      !IsCurrentTokenUnderLock(token, observed_target)) {
    return {};
  }
  return PersistencePermit(std::move(lock));
}

uint64_t CaptureSafetyCore::instance_epoch() const {
  return instance_epoch_;
}

uint64_t CaptureSafetyCore::persistence_generation() const {
  return observable_snapshot().persistence_generation;
}

uint64_t CaptureSafetyCore::target_epoch() const {
  return observable_snapshot().target_epoch;
}

CaptureSafetyObservableSnapshot CaptureSafetyCore::observable_snapshot() const {
  std::lock_guard lock(observable_mutex_);
  return observable_;
}

bool CaptureSafetyCore::admission_open() const noexcept {
  return (admission_stamp_.load(std::memory_order_acquire) & 1U) != 0;
}

bool CaptureSafetyCore::revoked() const {
  std::shared_lock lock(mutex_);
  return revoked_ || !admission_open();
}

PrivacyContext CaptureSafetyCore::privacy_context() const {
  std::shared_lock lock(mutex_);
  return current_.privacy;
}

CaptureSafetyUpdateResult CaptureSafetyCore::UpdateUnderLock(
    const RuntimeAuthorization& authorization,
    bool allow_missing_target,
    bool require_contiguous_revision,
    const CaptureSafetyUpdateTicket& ticket,
    uint64_t* persistence_generation) {
  if (persistence_generation == nullptr) {
    return CaptureSafetyUpdateResult::kInvalidArgument;
  }
  *persistence_generation = persistence_generation_;
  if (ticket.admission_stamp == 0) {
    generation_exhausted_ = true;
    RevokeStateUnderLock();
    return CaptureSafetyUpdateResult::kGenerationExhausted;
  }
  if (generation_exhausted_) {
    return CaptureSafetyUpdateResult::kGenerationExhausted;
  }
  if (!IsValidPrivacyContext(authorization.privacy) ||
      (authorization.target.has_value() &&
       !IsValidTarget(*authorization.target)) ||
      (!allow_missing_target && IsFullyAllowed(authorization.privacy) &&
       !authorization.target.has_value()) ||
      (!IsFullyAllowed(authorization.privacy) &&
       authorization.target.has_value())) {
    return CaptureSafetyUpdateResult::kInvalidArgument;
  }

  const bool legacy_update = allow_missing_target;
  if (!legacy_update && legacy_tainted_) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }
  const uint64_t revision = authorization.privacy.policy_revision;
  const uint64_t current_revision =
      legacy_update ? legacy_policy_revision_ : runtime_policy_revision_;
  if (current_revision == 0) {
    if (require_contiguous_revision && revision != 1) {
      return CaptureSafetyUpdateResult::kPolicyRevisionGap;
    }
  } else {
    if (revision < current_revision) {
      return CaptureSafetyUpdateResult::kStalePolicy;
    }
    if (revision == current_revision) {
      const bool same_snapshot = legacy_update
                                     ? last_legacy_privacy_.has_value() &&
                                           *last_legacy_privacy_ ==
                                               authorization.privacy
                                     : authorization == current_;
      if (same_snapshot) {
        if (admission_stamp_.load(std::memory_order_acquire) !=
            ticket.admission_stamp) {
          return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
        }
        if (!legacy_update && ticket.admission_was_open &&
            authorization.target.has_value() &&
            IsFullyAllowed(authorization.privacy)) {
          uint64_t expected = ticket.admission_stamp;
          if (!admission_stamp_.compare_exchange_strong(
                  expected,
                  ticket.admission_stamp | 1U,
                  std::memory_order_acq_rel,
                  std::memory_order_acquire)) {
            return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
          }
        }
        return CaptureSafetyUpdateResult::kOk;
      }
      return CaptureSafetyUpdateResult::kPolicyRevisionConflict;
    }
    if (require_contiguous_revision &&
        (current_revision == std::numeric_limits<uint64_t>::max() ||
         revision != current_revision + 1U)) {
      return CaptureSafetyUpdateResult::kPolicyRevisionGap;
    }
  }

  if (authorization.target.has_value()) {
    const CaptureTargetIdentity& target = *authorization.target;
    if (target.target_epoch < maximum_target_epoch_ ||
        (target.target_epoch == maximum_target_epoch_ &&
         (!last_target_.has_value() ||
          !HasSameTargetTuple(*last_target_, target)))) {
      return CaptureSafetyUpdateResult::kTargetMismatch;
    }
  }

  if (admission_stamp_.load(std::memory_order_acquire) !=
      ticket.admission_stamp) {
    return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  }

  if (!AdvanceGenerationUnderLock()) {
    *persistence_generation = persistence_generation_;
    return CaptureSafetyUpdateResult::kGenerationExhausted;
  }

  current_ = authorization;
  if (legacy_update) {
    legacy_tainted_ = true;
    legacy_policy_revision_ = revision;
    last_legacy_privacy_ = authorization.privacy;
  } else {
    runtime_policy_revision_ = revision;
  }
  revoked_ = !authorization.target.has_value() ||
             !IsFullyAllowed(authorization.privacy);
  if (authorization.target.has_value()) {
    last_target_ = authorization.target;
    maximum_target_epoch_ = authorization.target->target_epoch;
  }
  PublishObservableUnderLock();
  *persistence_generation = persistence_generation_;
  if (!revoked_) {
    uint64_t expected = ticket.admission_stamp;
    if (!admission_stamp_.compare_exchange_strong(
            expected,
            ticket.admission_stamp | 1U,
            std::memory_order_acq_rel,
            std::memory_order_acquire)) {
      return CaptureSafetyUpdateResult::kRevokedDuringUpdate;
    }
  }
  return CaptureSafetyUpdateResult::kOk;
}

CaptureSafetyUpdateTicket CaptureSafetyCore::CloseAdmission() noexcept {
  uint64_t current = admission_stamp_.load(std::memory_order_relaxed);
  while ((current & ~uint64_t{1}) <=
         std::numeric_limits<uint64_t>::max() - 2U) {
    const bool admission_was_open = (current & 1U) != 0;
    const uint64_t next = (current & ~uint64_t{1}) + 2U;
    if (admission_stamp_.compare_exchange_weak(
            current,
            next,
            std::memory_order_acq_rel,
            std::memory_order_relaxed)) {
      return CaptureSafetyUpdateTicket{next, admission_was_open};
    }
  }
  const bool admission_was_open = (current & 1U) != 0;
  if (admission_was_open) {
    admission_stamp_.store(current & ~uint64_t{1}, std::memory_order_release);
  }
  return CaptureSafetyUpdateTicket{0, admission_was_open};
}

bool CaptureSafetyCore::AdvanceGenerationUnderLock() {
  if (persistence_generation_ == std::numeric_limits<uint64_t>::max()) {
    generation_exhausted_ = true;
    RevokeStateUnderLock();
    return false;
  }
  ++persistence_generation_;
  return true;
}

void CaptureSafetyCore::RevokeStateUnderLock() {
  const uint64_t revision = current_.privacy.policy_revision;
  current_ = RuntimeAuthorization{};
  current_.privacy.consent_granted = WDF_CAPTURE_POLICY_BLOCK;
  current_.privacy.policy_revision = revision;
  revoked_ = true;
  PublishObservableUnderLock();
}

void CaptureSafetyCore::PublishObservableUnderLock() {
  std::lock_guard lock(observable_mutex_);
  observable_.persistence_generation = persistence_generation_;
  observable_.target_epoch =
      current_.target.has_value() ? current_.target->target_epoch : 0;
}

bool CaptureSafetyCore::IsCurrentTokenUnderLock(
    const PersistenceToken& token,
    const CaptureTargetIdentity& observed_target) const {
  return !generation_exhausted_ && !revoked_ &&
         token.instance_epoch == instance_epoch_ &&
         token.persistence_generation == persistence_generation_ &&
         current_.target.has_value() &&
         HasSameTargetTuple(token.target, observed_target) &&
         HasSameTargetTuple(*current_.target, observed_target) &&
         IsFullyAllowed(current_.privacy);
}

}  // namespace windayflow::capture

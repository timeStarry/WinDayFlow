#include "capture_safety_core.h"

#include <Windows.h>
#include <bcrypt.h>

#include <array>
#include <atomic>
#include <chrono>
#include <limits>
#include <mutex>
#include <string_view>
#include <utility>

namespace windayflow::capture {
namespace {

std::atomic<uint64_t> g_next_instance_epoch{1};
constexpr size_t kMaximumDisplayDeviceKeyCharacters = 31;
static_assert(std::atomic<uint64_t>::is_always_lock_free);

uint64_t AllocateInstanceEpoch() {
  uint64_t current = g_next_instance_epoch.load(std::memory_order_relaxed);
  while (current != 0) {
    const uint64_t next =
        current == std::numeric_limits<uint64_t>::max() ? 0 : current + 1U;
    if (g_next_instance_epoch.compare_exchange_weak(
            current, next, std::memory_order_relaxed,
            std::memory_order_relaxed)) {
      return current;
    }
  }
  return 0;
}

bool IsValidDisplayDeviceKey(std::wstring_view value) {
  if (value.empty() || value.size() > kMaximumDisplayDeviceKeyCharacters) {
    return false;
  }

  std::array<WORD, kMaximumDisplayDeviceKeyCharacters> character_types{};
  if (GetStringTypeW(CT_CTYPE1, value.data(), static_cast<int>(value.size()),
                     character_types.data()) == 0) {
    return false;
  }

  bool all_whitespace = true;
  for (size_t index = 0; index < value.size(); ++index) {
    if (value[index] == L'\0' || (character_types[index] & C1_CNTRL) != 0) {
      return false;
    }
    all_whitespace = all_whitespace && (character_types[index] & C1_SPACE) != 0;
  }
  return !all_whitespace;
}

bool IsValidTarget(const CaptureTargetIdentity& target) {
  return target.window_handle != 0 && target.process_id != 0 &&
         target.process_creation_time_100ns != 0 && target.target_epoch != 0 &&
         target.display_monitor_handle != 0 &&
         IsValidDisplayDeviceKey(target.display_device_key);
}

bool DisplayDeviceKeysEqual(std::wstring_view left, std::wstring_view right) {
  if (left.size() != right.size() ||
      left.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }
  if (left.empty()) {
    return true;
  }
  return CompareStringOrdinal(left.data(), static_cast<int>(left.size()),
                              right.data(), static_cast<int>(right.size()),
                              TRUE) == CSTR_EQUAL;
}

bool HasSameTargetTuple(const CaptureTargetIdentity& left,
                        const CaptureTargetIdentity& right) {
  return left == right;
}

bool IsValidCommand(CaptureCommand command) {
  return command == CaptureCommand::kStart ||
         command == CaptureCommand::kResume;
}

bool GenerateCommandAdmissionNonce(uint64_t* nonce_low, uint64_t* nonce_high) {
  if (nonce_low == nullptr || nonce_high == nullptr) {
    return false;
  }
  std::array<uint64_t, 2> nonce{};
  const NTSTATUS status = BCryptGenRandom(
      nullptr, reinterpret_cast<PUCHAR>(nonce.data()),
      static_cast<ULONG>(sizeof(nonce)), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
  if (status != 0 || (nonce[0] == 0 && nonce[1] == 0)) {
    return false;
  }
  *nonce_low = nonce[0];
  *nonce_high = nonce[1];
  return true;
}

}  // namespace

bool CaptureTargetIdentity::operator==(
    const CaptureTargetIdentity& other) const {
  return window_handle == other.window_handle &&
         process_id == other.process_id &&
         process_creation_time_100ns == other.process_creation_time_100ns &&
         target_epoch == other.target_epoch &&
         display_monitor_handle == other.display_monitor_handle &&
         DisplayDeviceKeysEqual(display_device_key, other.display_device_key);
}

bool IsFullyAllowed(const PrivacyContext& context) {
  return EvaluatePrivacyContext(context).allowed;
}

CaptureSafetyCore::CaptureSafetyCore()
    : CaptureSafetyCore(AllocateInstanceEpoch(), 1, {}, 2, {}) {}

CaptureSafetyCore::CaptureSafetyCore(
    uint64_t instance_epoch, uint64_t initial_persistence_generation,
    CommandAdmissionNonceGenerator nonce_generator,
    uint64_t initial_admission_stamp,
    AuthorizationCommitHook authorization_commit_hook)
    : instance_epoch_(instance_epoch),
      persistence_generation_(initial_persistence_generation),
      generation_exhausted_(
          instance_epoch == 0 || initial_persistence_generation == 0 ||
          initial_admission_stamp == 0 || (initial_admission_stamp & 1U) != 0),
      admission_stamp_(initial_admission_stamp & ~uint64_t{1}),
      nonce_generator_(nonce_generator ? std::move(nonce_generator)
                                       : GenerateCommandAdmissionNonce),
      authorization_commit_hook_(std::move(authorization_commit_hook)),
      observable_{initial_persistence_generation, 0} {}

CaptureSafetyUpdateResult CaptureSafetyCore::UpdateRuntimeAuthorization(
    const RuntimeAuthorization& authorization,
    uint64_t* persistence_generation) {
  const CaptureSafetyUpdateTicket ticket = BeginAuthorizationUpdate();
  return CompleteRuntimeAuthorization(ticket, authorization,
                                      persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::CompleteRuntimeAuthorization(
    const CaptureSafetyUpdateTicket& ticket,
    const RuntimeAuthorization& authorization,
    uint64_t* persistence_generation) {
  std::unique_lock lock(mutex_);
  return UpdateUnderLock(authorization, false, true, ticket,
                         persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::UpdateLegacyPrivacyContext(
    const PrivacyContext& context, uint64_t* persistence_generation) {
  const CaptureSafetyUpdateTicket ticket = BeginAuthorizationUpdate();
  return CompleteLegacyPrivacyContext(ticket, context, persistence_generation);
}

CaptureSafetyUpdateResult CaptureSafetyCore::CompleteLegacyPrivacyContext(
    const CaptureSafetyUpdateTicket& ticket, const PrivacyContext& context,
    uint64_t* persistence_generation) {
  std::unique_lock lock(mutex_);
  return UpdateUnderLock(RuntimeAuthorization{context, std::nullopt}, true,
                         false, ticket, persistence_generation);
}

CaptureSafetyUpdateTicket
CaptureSafetyCore::BeginAuthorizationUpdate() noexcept {
  return CloseAdmission();
}

uint64_t CaptureSafetyCore::InvalidateAuthorizationAdmission() noexcept {
  const uint64_t invalidation_epoch = AdvanceCallbackInvalidationEpoch();
  const uint64_t admission_epoch = CloseAdmission().admission_stamp;
  return invalidation_epoch == 0 ? 0 : admission_epoch;
}

void CaptureSafetyCore::BeginRevoke() noexcept {
  static_cast<void>(CloseAdmission());
}

bool CaptureSafetyCore::FinalizeRevoke(uint32_t timeout_ms,
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
  if (generation_exhausted_ || (revoked_ && !current_.target.has_value())) {
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

CaptureCommandAdmissionResult CaptureSafetyCore::IssueCommandAdmission(
    CaptureCommand command, uint64_t expected_persistence_generation,
    uint64_t expected_target_epoch, uint64_t runtime_owner_epoch,
    CaptureCommandAdmission* admission) {
  if (admission == nullptr || !IsValidCommand(command) ||
      expected_persistence_generation == 0 || expected_target_epoch == 0 ||
      runtime_owner_epoch == 0) {
    return CaptureCommandAdmissionResult::kInvalidArgument;
  }
  *admission = {};

  std::lock_guard command_lock(command_mutex_);
  issued_command_admission_.reset();

  uint64_t nonce_low = 0;
  uint64_t nonce_high = 0;
  bool nonce_generated = false;
  try {
    nonce_generated = nonce_generator_(&nonce_low, &nonce_high);
  } catch (...) {
    nonce_generated = false;
  }
  if (!nonce_generated || (nonce_low == 0 && nonce_high == 0)) {
    return CaptureCommandAdmissionResult::kInternalError;
  }

  std::shared_lock safety_lock(mutex_);
  const uint64_t authorization_epoch =
      admission_stamp_.load(std::memory_order_acquire);
  if (generation_exhausted_ || instance_epoch_ == 0) {
    return CaptureCommandAdmissionResult::kGenerationExhausted;
  }
  if (revoked_ || !current_.target.has_value() ||
      !IsFullyAllowed(current_.privacy)) {
    return CaptureCommandAdmissionResult::kPolicyBlocked;
  }
  if ((authorization_epoch & 1U) == 0) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }
  if (persistence_generation_ != expected_persistence_generation ||
      current_.target->target_epoch != expected_target_epoch) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }

  CaptureCommandAdmission value{
      instance_epoch_,
      current_.privacy.policy_revision,
      persistence_generation_,
      current_.target->target_epoch,
      authorization_epoch,
      nonce_low,
      nonce_high,
  };
  if (value.runtime_policy_revision == 0 ||
      admission_stamp_.load(std::memory_order_acquire) != authorization_epoch) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }

  issued_command_admission_ = IssuedCommandAdmission{
      value, command, *current_.target, runtime_owner_epoch};
  *admission = value;
  return CaptureCommandAdmissionResult::kOk;
}

CaptureCommandAdmissionResult CaptureSafetyCore::AcquireCommandAdmissionPermit(
    const CaptureCommandAdmission& admission, CaptureCommand expected_command,
    uint64_t runtime_owner_epoch, CaptureCommandAdmissionPermit* permit) const {
  if (permit == nullptr || !IsValidCommand(expected_command) ||
      runtime_owner_epoch == 0) {
    return CaptureCommandAdmissionResult::kInvalidArgument;
  }
  *permit = {};

  std::lock_guard command_lock(command_mutex_);
  if (!issued_command_admission_.has_value()) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }

  const IssuedCommandAdmission issued = *issued_command_admission_;
  const bool nonce_matches =
      admission.nonce_low == issued.admission.nonce_low &&
      admission.nonce_high == issued.admission.nonce_high;
  if (!nonce_matches) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }
  issued_command_admission_.reset();

  const uint64_t current_authorization_epoch =
      admission_stamp_.load(std::memory_order_acquire);
  if (admission != issued.admission || expected_command != issued.command ||
      runtime_owner_epoch != issued.runtime_owner_epoch ||
      (current_authorization_epoch & 1U) == 0 ||
      current_authorization_epoch != admission.authorization_epoch) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }

  std::shared_lock safety_lock(mutex_);
  if (generation_exhausted_ || revoked_ ||
      admission_stamp_.load(std::memory_order_acquire) !=
          admission.authorization_epoch ||
      instance_epoch_ != admission.instance_epoch ||
      persistence_generation_ != admission.persistence_generation ||
      !current_.target.has_value() ||
      current_.privacy.policy_revision != admission.runtime_policy_revision ||
      !HasSameTargetTuple(*current_.target, issued.target) ||
      current_.target->target_epoch != admission.target_epoch ||
      !IsFullyAllowed(current_.privacy)) {
    return CaptureCommandAdmissionResult::kAdmissionRejected;
  }

  *permit = CaptureCommandAdmissionPermit(
      std::move(safety_lock), expected_command, runtime_owner_epoch,
      PersistenceToken{instance_epoch_, persistence_generation_,
                       issued.target});
  return CaptureCommandAdmissionResult::kOk;
}

std::optional<PersistenceToken> CaptureSafetyCore::MintPersistenceToken(
    const CaptureTargetIdentity& observed_target) const {
  if (!admission_open()) {
    return std::nullopt;
  }
  std::shared_lock lock(mutex_);
  if (!admission_open() || generation_exhausted_ || revoked_ ||
      !current_.target.has_value() || !IsFullyAllowed(current_.privacy) ||
      !HasSameTargetTuple(*current_.target, observed_target)) {
    return std::nullopt;
  }

  return PersistenceToken{instance_epoch_, persistence_generation_,
                          observed_target};
}

PersistencePermit CaptureSafetyCore::AcquirePersistencePermit(
    const PersistenceToken& token,
    const CaptureTargetIdentity& observed_target) const {
  const uint64_t authorization_epoch =
      admission_stamp_.load(std::memory_order_acquire);
  if ((authorization_epoch & 1U) == 0) {
    return {};
  }
  std::shared_lock lock(mutex_);
  if (admission_stamp_.load(std::memory_order_acquire) != authorization_epoch ||
      !IsCurrentTokenUnderLock(token, observed_target)) {
    return {};
  }
  return PersistencePermit(this, std::move(lock), authorization_epoch);
}

bool CaptureSafetyCore::IsPersistencePermitCurrent(
    const PersistencePermit& permit) const noexcept {
  return permit && permit.issuer_ == this &&
         permit.authorization_epoch() != 0 &&
         admission_stamp_.load(std::memory_order_acquire) ==
             permit.authorization_epoch();
}

uint64_t CaptureSafetyCore::instance_epoch() const { return instance_epoch_; }

uint64_t CaptureSafetyCore::persistence_generation() const {
  return observable_snapshot().persistence_generation;
}

uint64_t CaptureSafetyCore::target_epoch() const {
  return observable_snapshot().target_epoch;
}

uint64_t CaptureSafetyCore::authorization_epoch() const noexcept {
  return admission_stamp_.load(std::memory_order_acquire);
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
    const RuntimeAuthorization& authorization, bool allow_missing_target,
    bool require_contiguous_revision, const CaptureSafetyUpdateTicket& ticket,
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
  if (callback_invalidation_exhausted_.load(std::memory_order_acquire)) {
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
  const auto admission_mismatch_result = [this, &ticket]() noexcept {
    return callback_invalidation_epoch_.load(std::memory_order_acquire) !=
                   ticket.callback_invalidation_epoch
               ? CaptureSafetyUpdateResult::kAuthorizationSuperseded
               : CaptureSafetyUpdateResult::kRevokedDuringUpdate;
  };
  const bool fully_allowed = IsFullyAllowed(authorization.privacy);
  if (!legacy_update && fully_allowed &&
      callback_invalidation_epoch_.load(std::memory_order_acquire) !=
          confirmed_callback_invalidation_epoch_) {
    return CaptureSafetyUpdateResult::kAuthorizationSuperseded;
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
      const bool same_snapshot =
          legacy_update ? last_legacy_privacy_.has_value() &&
                              *last_legacy_privacy_ == authorization.privacy
                        : authorization == current_;
      if (same_snapshot) {
        if (admission_stamp_.load(std::memory_order_acquire) !=
            ticket.admission_stamp) {
          return admission_mismatch_result();
        }
        if (!legacy_update && ticket.admission_was_open &&
            authorization.target.has_value() && fully_allowed) {
          uint64_t expected = ticket.admission_stamp;
          if (!admission_stamp_.compare_exchange_strong(
                  expected, ticket.admission_stamp | 1U,
                  std::memory_order_acq_rel, std::memory_order_acquire)) {
            return admission_mismatch_result();
          }
        }
        if (!legacy_update && !fully_allowed) {
          ConfirmCallbackInvalidationUnderLock(
              ticket.callback_invalidation_epoch);
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
    return admission_mismatch_result();
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
  revoked_ = !authorization.target.has_value() || !fully_allowed;
  if (authorization.target.has_value()) {
    last_target_ = authorization.target;
    maximum_target_epoch_ = authorization.target->target_epoch;
  }
  PublishObservableUnderLock();
  *persistence_generation = persistence_generation_;
  if (!legacy_update && revoked_) {
    ConfirmCallbackInvalidationUnderLock(ticket.callback_invalidation_epoch);
  }
  if (!revoked_) {
    if (authorization_commit_hook_) {
      authorization_commit_hook_();
    }
    uint64_t expected = ticket.admission_stamp;
    static_cast<void>(admission_stamp_.compare_exchange_strong(
        expected, ticket.admission_stamp | 1U, std::memory_order_acq_rel,
        std::memory_order_acquire));
  }
  return CaptureSafetyUpdateResult::kOk;
}

CaptureSafetyUpdateTicket CaptureSafetyCore::CloseAdmission() noexcept {
  uint64_t current = admission_stamp_.load(std::memory_order_relaxed);
  while ((current & ~uint64_t{1}) <=
         std::numeric_limits<uint64_t>::max() - 2U) {
    const bool admission_was_open = (current & 1U) != 0;
    const uint64_t next = (current & ~uint64_t{1}) + 2U;
    if (admission_stamp_.compare_exchange_weak(current, next,
                                               std::memory_order_acq_rel,
                                               std::memory_order_relaxed)) {
      return CaptureSafetyUpdateTicket{
          next, admission_was_open,
          callback_invalidation_epoch_.load(std::memory_order_acquire)};
    }
  }
  const bool admission_was_open = (current & 1U) != 0;
  if (admission_was_open) {
    admission_stamp_.store(current & ~uint64_t{1}, std::memory_order_release);
  }
  return CaptureSafetyUpdateTicket{
      0, admission_was_open,
      callback_invalidation_epoch_.load(std::memory_order_acquire)};
}

uint64_t CaptureSafetyCore::AdvanceCallbackInvalidationEpoch() noexcept {
  uint64_t current =
      callback_invalidation_epoch_.load(std::memory_order_relaxed);
  while (current != std::numeric_limits<uint64_t>::max()) {
    const uint64_t next = current + 1U;
    if (callback_invalidation_epoch_.compare_exchange_weak(
            current, next, std::memory_order_acq_rel,
            std::memory_order_relaxed)) {
      return next;
    }
  }
  callback_invalidation_exhausted_.store(true, std::memory_order_release);
  return 0;
}

void CaptureSafetyCore::ConfirmCallbackInvalidationUnderLock(
    uint64_t invalidation_epoch) {
  if (invalidation_epoch > confirmed_callback_invalidation_epoch_) {
    confirmed_callback_invalidation_epoch_ = invalidation_epoch;
  }
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

#ifndef WINDAYFLOW_CAPTURE_SAFETY_CORE_H_
#define WINDAYFLOW_CAPTURE_SAFETY_CORE_H_

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <shared_mutex>
#include <utility>

#include "privacy_guard.h"

namespace windayflow::capture {

struct CaptureTargetIdentity {
  uint64_t window_handle = 0;
  uint32_t process_id = 0;
  uint64_t process_creation_time_100ns = 0;
  uint64_t target_epoch = 0;

  bool operator==(const CaptureTargetIdentity&) const = default;
};

struct RuntimeAuthorization {
  PrivacyContext privacy;
  std::optional<CaptureTargetIdentity> target;

  bool operator==(const RuntimeAuthorization&) const = default;
};

struct PersistenceToken {
  uint64_t instance_epoch = 0;
  uint64_t persistence_generation = 0;
  CaptureTargetIdentity target;

  bool operator==(const PersistenceToken&) const = default;
};

enum class CaptureCommand {
  kStart = WDF_CAPTURE_COMMAND_START,
  kResume = WDF_CAPTURE_COMMAND_RESUME,
};

struct CaptureCommandAdmission {
  uint64_t instance_epoch = 0;
  uint64_t runtime_policy_revision = 0;
  uint64_t persistence_generation = 0;
  uint64_t target_epoch = 0;
  uint64_t authorization_epoch = 0;
  uint64_t nonce_low = 0;
  uint64_t nonce_high = 0;

  bool operator==(const CaptureCommandAdmission&) const = default;
};

using CommandAdmissionNonceGenerator =
    std::function<bool(uint64_t* nonce_low, uint64_t* nonce_high)>;

enum class CaptureCommandAdmissionResult {
  kOk,
  kInvalidArgument,
  kPolicyBlocked,
  kAdmissionRejected,
  kGenerationExhausted,
  kInternalError,
};

struct CaptureSafetyUpdateTicket {
  uint64_t admission_stamp = 0;
  bool admission_was_open = false;
};

struct CaptureSafetyObservableSnapshot {
  uint64_t persistence_generation = 0;
  uint64_t target_epoch = 0;
};

enum class CaptureSafetyUpdateResult {
  kOk,
  kInvalidArgument,
  kStalePolicy,
  kPolicyRevisionConflict,
  kTargetMismatch,
  kPolicyRevisionGap,
  kGenerationExhausted,
  kRevokedDuringUpdate,
};

class PersistencePermit {
 public:
  PersistencePermit() = default;
  PersistencePermit(const PersistencePermit&) = delete;
  PersistencePermit& operator=(const PersistencePermit&) = delete;
  PersistencePermit(PersistencePermit&&) noexcept = default;
  PersistencePermit& operator=(PersistencePermit&&) noexcept = default;

  explicit operator bool() const { return lock_.owns_lock(); }

 private:
  friend class CaptureSafetyCore;
  explicit PersistencePermit(std::shared_lock<std::shared_timed_mutex> lock)
      : lock_(std::move(lock)) {}

  std::shared_lock<std::shared_timed_mutex> lock_;
};

class CaptureCommandAdmissionPermit {
 public:
  CaptureCommandAdmissionPermit() = default;
  CaptureCommandAdmissionPermit(const CaptureCommandAdmissionPermit&) = delete;
  CaptureCommandAdmissionPermit& operator=(
      const CaptureCommandAdmissionPermit&) = delete;
  CaptureCommandAdmissionPermit(CaptureCommandAdmissionPermit&&) noexcept =
      default;
  CaptureCommandAdmissionPermit& operator=(
      CaptureCommandAdmissionPermit&&) noexcept = default;

  explicit operator bool() const { return lock_.owns_lock(); }
  CaptureCommand command() const { return command_; }
  uint64_t runtime_owner_epoch() const { return runtime_owner_epoch_; }
  const PersistenceToken& persistence_token() const {
    return persistence_token_;
  }

 private:
  friend class CaptureSafetyCore;
  CaptureCommandAdmissionPermit(
      std::shared_lock<std::shared_timed_mutex> lock,
      CaptureCommand command,
      uint64_t runtime_owner_epoch,
      PersistenceToken persistence_token)
      : lock_(std::move(lock)),
        command_(command),
        runtime_owner_epoch_(runtime_owner_epoch),
        persistence_token_(std::move(persistence_token)) {}

  std::shared_lock<std::shared_timed_mutex> lock_;
  CaptureCommand command_ = CaptureCommand::kStart;
  uint64_t runtime_owner_epoch_ = 0;
  PersistenceToken persistence_token_;
};

class CaptureSafetyCore {
 public:
  CaptureSafetyCore();
  CaptureSafetyCore(uint64_t instance_epoch,
                     uint64_t initial_persistence_generation,
                     CommandAdmissionNonceGenerator nonce_generator = {});

  CaptureSafetyUpdateResult UpdateRuntimeAuthorization(
      const RuntimeAuthorization& authorization,
      uint64_t* persistence_generation);
  CaptureSafetyUpdateResult UpdateLegacyPrivacyContext(
      const PrivacyContext& context,
      uint64_t* persistence_generation);
  CaptureSafetyUpdateTicket BeginAuthorizationUpdate() noexcept;
  CaptureSafetyUpdateResult CompleteRuntimeAuthorization(
      const CaptureSafetyUpdateTicket& ticket,
      const RuntimeAuthorization& authorization,
      uint64_t* persistence_generation);
  CaptureSafetyUpdateResult CompleteLegacyPrivacyContext(
      const CaptureSafetyUpdateTicket& ticket,
      const PrivacyContext& context,
      uint64_t* persistence_generation);
  void BeginRevoke() noexcept;
  bool FinalizeRevoke(uint32_t timeout_ms,
                      uint64_t* persistence_generation);
  CaptureSafetyUpdateResult Revoke(uint64_t* persistence_generation);

  CaptureCommandAdmissionResult IssueCommandAdmission(
      CaptureCommand command,
      uint64_t expected_persistence_generation,
      uint64_t expected_target_epoch,
      uint64_t runtime_owner_epoch,
      CaptureCommandAdmission* admission);
  CaptureCommandAdmissionResult AcquireCommandAdmissionPermit(
      const CaptureCommandAdmission& admission,
      CaptureCommand expected_command,
      uint64_t runtime_owner_epoch,
      CaptureCommandAdmissionPermit* permit) const;

  std::optional<PersistenceToken> MintPersistenceToken(
      const CaptureTargetIdentity& observed_target) const;
  PersistencePermit AcquirePersistencePermit(
      const PersistenceToken& token,
      const CaptureTargetIdentity& observed_target) const;

  uint64_t instance_epoch() const;
  uint64_t persistence_generation() const;
  uint64_t target_epoch() const;
  uint64_t authorization_epoch() const noexcept;
  CaptureSafetyObservableSnapshot observable_snapshot() const;
  bool admission_open() const noexcept;
  bool revoked() const;
  PrivacyContext privacy_context() const;

 private:
  CaptureSafetyUpdateResult UpdateUnderLock(
      const RuntimeAuthorization& authorization,
      bool allow_missing_target,
      bool require_contiguous_revision,
      const CaptureSafetyUpdateTicket& ticket,
      uint64_t* persistence_generation);
  CaptureSafetyUpdateTicket CloseAdmission() noexcept;
  void PublishObservableUnderLock();
  bool AdvanceGenerationUnderLock();
  void RevokeStateUnderLock();
  bool IsCurrentTokenUnderLock(
      const PersistenceToken& token,
      const CaptureTargetIdentity& observed_target) const;

  struct IssuedCommandAdmission {
    CaptureCommandAdmission admission;
    CaptureCommand command = CaptureCommand::kStart;
    CaptureTargetIdentity target;
    uint64_t runtime_owner_epoch = 0;
  };

  mutable std::shared_timed_mutex mutex_;
  RuntimeAuthorization current_;
  std::optional<CaptureTargetIdentity> last_target_;
  std::optional<PrivacyContext> last_legacy_privacy_;
  uint64_t maximum_target_epoch_ = 0;
  uint64_t runtime_policy_revision_ = 0;
  uint64_t legacy_policy_revision_ = 0;
  uint64_t instance_epoch_ = 0;
  uint64_t persistence_generation_ = 1;
  bool revoked_ = true;
  bool legacy_tainted_ = false;
  bool generation_exhausted_ = false;
  std::atomic<uint64_t> admission_stamp_{2};
  // Command records are always locked before the shared safety gate.
  mutable std::mutex command_mutex_;
  mutable std::optional<IssuedCommandAdmission> issued_command_admission_;
  CommandAdmissionNonceGenerator nonce_generator_;
  mutable std::mutex observable_mutex_;
  CaptureSafetyObservableSnapshot observable_{1, 0};
};

bool IsFullyAllowed(const PrivacyContext& context);

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_SAFETY_CORE_H_

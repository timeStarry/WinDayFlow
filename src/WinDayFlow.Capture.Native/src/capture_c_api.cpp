#include "windayflow_capture.h"

#include <Windows.h>

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>

#include "capture_event_queue.h"
#include "capture_policy.h"
#include "capture_runtime_owner.h"
#include "capture_safety_core.h"
#include "privacy_guard.h"
#include "windows_capture_worker_backend.h"

namespace {

using windayflow::capture::CaptureEventQueue;
using windayflow::capture::CaptureEventReadResult;
using windayflow::capture::CapturePolicy;
using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureCommandAdmission;
using windayflow::capture::CaptureCommandAdmissionPermit;
using windayflow::capture::CaptureCommandAdmissionResult;
using windayflow::capture::CaptureRuntimeWaitResult;
using windayflow::capture::CaptureSafetyCore;
using windayflow::capture::CaptureSafetyUpdateTicket;
using windayflow::capture::CaptureSafetyUpdateResult;
using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::PrivacyContext;
using windayflow::capture::RuntimeAuthorization;

constexpr uint32_t kMinimumEventQueueCapacity = 16;
constexpr uint32_t kMaximumEventQueueCapacity = 4'096;
constexpr uint32_t kMinimumCaptureWidth = 320;
constexpr uint32_t kMaximumCaptureWidth = 7'680;
constexpr uint32_t kMinimumCaptureHeight = 200;
constexpr uint32_t kMaximumCaptureHeight = 4'320;
constexpr uint32_t kMaximumOutputDirectoryBytes = 32'767;
constexpr uint32_t kMaximumPollTimeoutMs = 60'000;
constexpr size_t kVersionedStructHeaderSize = sizeof(uint32_t) * 2U;
constexpr size_t kLegacyRuntimeAuthorizationSize =
    WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE;
constexpr size_t kCurrentRuntimeAuthorizationSize =
    sizeof(wdf_capture_runtime_authorization_v1);
constexpr int kMaximumDisplayDeviceKeyUtf16Characters = 31;

static_assert(kLegacyRuntimeAuthorizationSize ==
              offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_monitor_handle));
static_assert(kCurrentRuntimeAuthorizationSize == 224);

struct CaptureInstance {
  CaptureInstance(CapturePolicy initial_policy,
                  size_t event_queue_capacity,
                  uint32_t maximum_capture_width,
                  uint32_t maximum_capture_height,
                  std::wstring output_directory)
      : events(event_queue_capacity),
        policy(initial_policy),
        max_width(maximum_capture_width),
        max_height(maximum_capture_height),
        output_directory_utf16(std::move(output_directory)) {}

  std::mutex lifetime_mutex;
  std::condition_variable no_active_calls;
  uint32_t active_calls = 0;
  bool destroying = false;

  std::mutex state_mutex;
  CaptureEventQueue events;
  CapturePolicy policy;
  CaptureSafetyCore safety;
  windayflow::capture::CaptureRuntimeOwner runtime;
  PrivacyContext privacy;
  uint32_t max_width = 0;
  uint32_t max_height = 0;
  std::wstring output_directory_utf16;
  wdf_capture_state state = WDF_CAPTURE_STATE_UNAVAILABLE;
  wdf_capture_reason pending_stop_reason = WDF_CAPTURE_REASON_USER_STOPPED;
  bool stop_requested_for_join = false;
};

class InstanceLease {
 public:
  InstanceLease() = default;
  explicit InstanceLease(std::shared_ptr<CaptureInstance> instance)
      : instance_(std::move(instance)) {}

  InstanceLease(const InstanceLease&) = delete;
  InstanceLease& operator=(const InstanceLease&) = delete;

  InstanceLease(InstanceLease&& other) noexcept
      : instance_(std::move(other.instance_)) {}

  InstanceLease& operator=(InstanceLease&& other) noexcept {
    if (this != &other) {
      Release();
      instance_ = std::move(other.instance_);
    }
    return *this;
  }

  ~InstanceLease() { Release(); }

  explicit operator bool() const { return instance_ != nullptr; }
  CaptureInstance* get() const { return instance_.get(); }

 private:
  void Release() {
    if (!instance_) {
      return;
    }
    {
      std::lock_guard lock(instance_->lifetime_mutex);
      if (instance_->active_calls > 0) {
        --instance_->active_calls;
      }
    }
    instance_->no_active_calls.notify_all();
    instance_.reset();
  }

  std::shared_ptr<CaptureInstance> instance_;
};

std::mutex g_registry_mutex;
std::unordered_map<wdf_capture_handle, std::shared_ptr<CaptureInstance>>
    g_instances;
std::atomic<wdf_capture_handle> g_next_handle{1};

int64_t CurrentUnixMilliseconds() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::system_clock::now().time_since_epoch())
      .count();
}

wdf_capture_handle AllocateHandle() {
  wdf_capture_handle current = g_next_handle.load(std::memory_order_relaxed);
  while (current != 0) {
    const wdf_capture_handle next =
        current == std::numeric_limits<wdf_capture_handle>::max()
            ? 0
            : current + 1U;
    if (g_next_handle.compare_exchange_weak(current,
                                            next,
                                            std::memory_order_relaxed,
                                            std::memory_order_relaxed)) {
      return current;
    }
  }
  return 0;
}

wdf_capture_result ValidateStructHeader(const void* value,
                                        size_t required_size) {
  if (value == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  uint32_t struct_size = 0;
  std::memcpy(&struct_size, value, sizeof(struct_size));
  if (struct_size < kVersionedStructHeaderSize) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  uint32_t abi_version = 0;
  std::memcpy(&abi_version,
              static_cast<const unsigned char*>(value) + sizeof(uint32_t),
              sizeof(abi_version));
  if (abi_version != WDF_CAPTURE_ABI_VERSION) {
    return WDF_CAPTURE_RESULT_ABI_MISMATCH;
  }
  return struct_size < required_size ? WDF_CAPTURE_RESULT_INVALID_ARGUMENT
                                     : WDF_CAPTURE_RESULT_OK;
}

bool IsValidUtf8(std::string_view value) {
  if (value.empty() ||
      value.size() > static_cast<size_t>(std::numeric_limits<int>::max()) ||
      value.find('\0') != std::string_view::npos) {
    return false;
  }
  return MultiByteToWideChar(CP_UTF8,
                             MB_ERR_INVALID_CHARS,
                             value.data(),
                             static_cast<int>(value.size()),
                             nullptr,
                             0) > 0;
}

bool HasOnlyZeroBytes(const char* value, size_t begin, size_t size) {
  for (size_t index = begin; index < size; ++index) {
    if (value[index] != 0) {
      return false;
    }
  }
  return true;
}

bool TryCopyDisplayDeviceKey(const char* source,
                             uint32_t source_length,
                             std::wstring* destination) {
  if (source == nullptr || destination == nullptr || source_length == 0 ||
      source_length > WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH) {
    return false;
  }

  const std::string_view encoded(source, source_length);
  if (!IsValidUtf8(encoded)) {
    return false;
  }
  const int wide_length = MultiByteToWideChar(CP_UTF8,
                                               MB_ERR_INVALID_CHARS,
                                               encoded.data(),
                                               static_cast<int>(encoded.size()),
                                               nullptr,
                                               0);
  if (wide_length <= 0 ||
      wide_length > kMaximumDisplayDeviceKeyUtf16Characters) {
    return false;
  }

  std::wstring decoded(static_cast<size_t>(wide_length), L'\0');
  if (MultiByteToWideChar(CP_UTF8,
                          MB_ERR_INVALID_CHARS,
                          encoded.data(),
                          static_cast<int>(encoded.size()),
                          decoded.data(),
                          wide_length) != wide_length) {
    return false;
  }

  std::array<WORD, kMaximumDisplayDeviceKeyUtf16Characters> character_types{};
  if (GetStringTypeW(CT_CTYPE1,
                     decoded.data(),
                     wide_length,
                     character_types.data()) == 0) {
    return false;
  }
  bool all_whitespace = true;
  for (int index = 0; index < wide_length; ++index) {
    const WORD type = character_types[static_cast<size_t>(index)];
    if ((type & C1_CNTRL) != 0) {
      return false;
    }
    all_whitespace = all_whitespace && (type & C1_SPACE) != 0;
  }
  if (all_whitespace) {
    return false;
  }
  *destination = std::move(decoded);
  return true;
}

bool IsWithin(uint32_t value, uint32_t minimum, uint32_t maximum) {
  return value >= minimum && value <= maximum;
}

wdf_capture_result ValidateConfig(const wdf_capture_config_v1* config,
                                  std::wstring* output_directory_utf16) {
  if (output_directory_utf16 == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  output_directory_utf16->clear();
  const wdf_capture_result header =
      ValidateStructHeader(config, sizeof(wdf_capture_config_v1));
  if (header != WDF_CAPTURE_RESULT_OK) {
    return header;
  }

  const CapturePolicy policy{
      config->capture_interval_ms,
      config->context_interval_ms,
      config->chunk_duration_ms,
  };
  if (!windayflow::capture::IsValidCapturePolicy(policy) ||
      !IsWithin(config->max_width,
                kMinimumCaptureWidth,
                kMaximumCaptureWidth) ||
      !IsWithin(config->max_height,
                kMinimumCaptureHeight,
                kMaximumCaptureHeight) ||
      !IsWithin(config->event_queue_capacity,
                kMinimumEventQueueCapacity,
                kMaximumEventQueueCapacity) ||
      config->output_directory_utf8 == nullptr ||
      config->output_directory_utf8_length == 0 ||
      config->output_directory_utf8_length > kMaximumOutputDirectoryBytes ||
      !IsValidUtf8(std::string_view(config->output_directory_utf8,
                                    config->output_directory_utf8_length)) ||
      !windayflow::capture::TryConvertCaptureOutputDirectory(
          std::string_view(config->output_directory_utf8,
                           config->output_directory_utf8_length),
          output_directory_utf16)) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  return WDF_CAPTURE_RESULT_OK;
}

wdf_capture_result CopyPrivacyContext(
    const wdf_capture_privacy_context_v1* source,
    PrivacyContext* destination) {
  if (destination == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  const wdf_capture_result header = ValidateStructHeader(
      source, sizeof(wdf_capture_privacy_context_v1));
  if (header != WDF_CAPTURE_RESULT_OK) {
    return header;
  }

  PrivacyContext value;
  value.consent_granted = source->consent_granted;
  value.session_unlocked = source->session_unlocked;
  value.secure_desktop_clear = source->secure_desktop_clear;
  value.remote_session_allowed = source->remote_session_allowed;
  value.presentation_allowed = source->presentation_allowed;
  value.application_allowed = source->application_allowed;
  value.window_allowed = source->window_allowed;
  value.storage_available = source->storage_available;
  value.policy_revision = source->policy_revision;
  if (!windayflow::capture::IsValidPrivacyContext(value)) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  *destination = value;
  return WDF_CAPTURE_RESULT_OK;
}

wdf_capture_result MapSafetyUpdateResult(CaptureSafetyUpdateResult result) {
  switch (result) {
    case CaptureSafetyUpdateResult::kOk:
      return WDF_CAPTURE_RESULT_OK;
    case CaptureSafetyUpdateResult::kInvalidArgument:
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    case CaptureSafetyUpdateResult::kStalePolicy:
      return WDF_CAPTURE_RESULT_STALE_POLICY;
    case CaptureSafetyUpdateResult::kPolicyRevisionConflict:
      return WDF_CAPTURE_RESULT_POLICY_REVISION_CONFLICT;
    case CaptureSafetyUpdateResult::kTargetMismatch:
      return WDF_CAPTURE_RESULT_TARGET_MISMATCH;
    case CaptureSafetyUpdateResult::kPolicyRevisionGap:
      return WDF_CAPTURE_RESULT_POLICY_REVISION_GAP;
    case CaptureSafetyUpdateResult::kGenerationExhausted:
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    case CaptureSafetyUpdateResult::kRevokedDuringUpdate:
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    case CaptureSafetyUpdateResult::kAuthorizationSuperseded:
      return WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED;
    default:
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

wdf_capture_result MapCommandAdmissionResult(
    CaptureCommandAdmissionResult result) {
  switch (result) {
    case CaptureCommandAdmissionResult::kOk:
      return WDF_CAPTURE_RESULT_OK;
    case CaptureCommandAdmissionResult::kInvalidArgument:
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    case CaptureCommandAdmissionResult::kPolicyBlocked:
      return WDF_CAPTURE_RESULT_POLICY_BLOCKED;
    case CaptureCommandAdmissionResult::kAdmissionRejected:
      return WDF_CAPTURE_RESULT_ADMISSION_REJECTED;
    case CaptureCommandAdmissionResult::kGenerationExhausted:
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    case CaptureCommandAdmissionResult::kInternalError:
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    default:
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

bool TryCopyCommand(wdf_capture_command source, CaptureCommand* destination) {
  if (destination == nullptr) {
    return false;
  }
  switch (source) {
    case WDF_CAPTURE_COMMAND_START:
      *destination = CaptureCommand::kStart;
      return true;
    case WDF_CAPTURE_COMMAND_RESUME:
      *destination = CaptureCommand::kResume;
      return true;
    default:
      return false;
  }
}

wdf_capture_result CopyCommandAdmission(
    const wdf_capture_command_admission_v1* source,
    CaptureCommandAdmission* destination) {
  if (destination == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  const wdf_capture_result header = ValidateStructHeader(
      source, sizeof(wdf_capture_command_admission_v1));
  if (header != WDF_CAPTURE_RESULT_OK) {
    return header;
  }
  *destination = CaptureCommandAdmission{
      source->instance_epoch,
      source->runtime_policy_revision,
      source->persistence_generation,
      source->target_epoch,
      source->authorization_epoch,
      source->nonce_low,
      source->nonce_high,
  };
  return WDF_CAPTURE_RESULT_OK;
}

void WriteCommandAdmission(
    const CaptureCommandAdmission& source,
    wdf_capture_command_admission_v1* destination) {
  *destination = {};
  destination->struct_size = sizeof(*destination);
  destination->abi_version = WDF_CAPTURE_ABI_VERSION;
  destination->instance_epoch = source.instance_epoch;
  destination->runtime_policy_revision = source.runtime_policy_revision;
  destination->persistence_generation = source.persistence_generation;
  destination->target_epoch = source.target_epoch;
  destination->authorization_epoch = source.authorization_epoch;
  destination->nonce_low = source.nonce_low;
  destination->nonce_high = source.nonce_high;
}

wdf_capture_result CopyRuntimeAuthorization(
    const wdf_capture_runtime_authorization_v1* source,
    RuntimeAuthorization* destination) {
  if (destination == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  const wdf_capture_result header = ValidateStructHeader(
      source, kLegacyRuntimeAuthorizationSize);
  if (header != WDF_CAPTURE_RESULT_OK) {
    return header;
  }

  const bool has_display_tail =
      source->struct_size >= kCurrentRuntimeAuthorizationSize;
  if (source->struct_size != kLegacyRuntimeAuthorizationSize &&
      !has_display_tail) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  constexpr wdf_capture_target_flags kKnownTargetFlags =
      WDF_CAPTURE_TARGET_PRESENT | WDF_CAPTURE_TARGET_DISPLAY_PRESENT;
  if ((source->target_flags & ~kKnownTargetFlags) != 0) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  for (const uint32_t reserved : source->reserved) {
    if (reserved != 0) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
  }

  PrivacyContext privacy;
  privacy.consent_granted = source->consent_granted;
  privacy.session_unlocked = source->session_unlocked;
  privacy.secure_desktop_clear = source->secure_desktop_clear;
  privacy.remote_session_allowed = source->remote_session_allowed;
  privacy.presentation_allowed = source->presentation_allowed;
  privacy.application_allowed = source->application_allowed;
  privacy.window_allowed = source->window_allowed;
  privacy.storage_available = source->storage_available;
  privacy.policy_revision = source->runtime_policy_revision;
  if (!windayflow::capture::IsValidPrivacyContext(privacy)) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  const bool target_present =
      (source->target_flags & WDF_CAPTURE_TARGET_PRESENT) != 0;
  const bool display_present =
      (source->target_flags & WDF_CAPTURE_TARGET_DISPLAY_PRESENT) != 0;
  const bool target_values_present = source->target_epoch != 0 ||
                                     source->target_window_handle != 0 ||
                                     source->target_process_creation_time_100ns !=
                                         0 ||
                                     source->target_process_id != 0;
  if (target_present != target_values_present ||
      target_present != display_present) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  std::wstring display_device_key;
  if (has_display_tail) {
    if (source->target_display_reserved != 0) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    const bool display_buffer_has_value = !HasOnlyZeroBytes(
        source->target_display_device_key_utf8,
        0,
        WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY);
    const bool display_values_present =
        source->target_display_monitor_handle != 0 ||
        source->target_display_device_key_utf8_length != 0 ||
        display_buffer_has_value;
    if (display_present != display_values_present) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    if (display_present &&
        (source->target_display_monitor_handle == 0 ||
         !TryCopyDisplayDeviceKey(
             source->target_display_device_key_utf8,
             source->target_display_device_key_utf8_length,
             &display_device_key) ||
         !HasOnlyZeroBytes(
             source->target_display_device_key_utf8,
             source->target_display_device_key_utf8_length,
             WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY))) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
  } else if (display_present) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  RuntimeAuthorization authorization;
  authorization.privacy = privacy;
  if (target_present) {
    if (source->target_epoch == 0 || source->target_window_handle == 0 ||
        source->target_process_creation_time_100ns == 0 ||
        source->target_process_id == 0) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    authorization.target = CaptureTargetIdentity{
        source->target_window_handle,
        source->target_process_id,
        source->target_process_creation_time_100ns,
        source->target_epoch,
        source->target_display_monitor_handle,
        std::move(display_device_key)};
  }

  const bool fully_allowed = windayflow::capture::IsFullyAllowed(privacy);
  if (fully_allowed != target_present) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }

  *destination = authorization;
  return WDF_CAPTURE_RESULT_OK;
}

InstanceLease AcquireInstance(wdf_capture_handle handle) {
  if (handle == 0) {
    return {};
  }

  std::shared_ptr<CaptureInstance> instance;
  {
    std::lock_guard registry_lock(g_registry_mutex);
    const auto match = g_instances.find(handle);
    if (match == g_instances.end()) {
      return {};
    }
    instance = match->second;

    std::lock_guard lifetime_lock(instance->lifetime_mutex);
    if (instance->destroying ||
        instance->active_calls == std::numeric_limits<uint32_t>::max()) {
      return {};
    }
    ++instance->active_calls;
  }
  return InstanceLease(std::move(instance));
}

bool PublishState(CaptureInstance& instance,
                  wdf_capture_state state,
                  wdf_capture_reason reason,
                  std::string detail) {
  const auto safety_snapshot = instance.safety.observable_snapshot();
  instance.state = state;
  const uint64_t sequence =
      instance.events.Push(WDF_CAPTURE_EVENT_STATE_CHANGED,
                           state,
                           reason,
                           WDF_CAPTURE_ERROR_NONE,
                           std::move(detail),
                           CurrentUnixMilliseconds(),
                           safety_snapshot.persistence_generation,
                           safety_snapshot.target_epoch);
  if (sequence == 0) {
    instance.state = WDF_CAPTURE_STATE_FAULTED;
    return false;
  }
  return true;
}

wdf_capture_result RequestStopCore(CaptureInstance& instance,
                                   wdf_capture_reason reason) {
  std::lock_guard lock(instance.state_mutex);
  instance.safety.InvalidatePendingCommandAdmission();
  instance.runtime.RequestStop();
  instance.stop_requested_for_join = true;

  if (instance.state == WDF_CAPTURE_STATE_STOPPED ||
      instance.state == WDF_CAPTURE_STATE_STOPPING) {
    return WDF_CAPTURE_RESULT_OK;
  }
  instance.pending_stop_reason = reason;
  if (!PublishState(instance,
                    WDF_CAPTURE_STATE_STOPPING,
                    reason,
                    "Capture stop requested; waiting for native worker shutdown.")) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
  return WDF_CAPTURE_RESULT_OK;
}

wdf_capture_result WaitStoppedCore(CaptureInstance& instance,
                                   uint32_t timeout_ms) {
  const auto started = std::chrono::steady_clock::now();
  const CaptureRuntimeWaitResult wait_result =
      instance.runtime.WaitStopped(timeout_ms);
  if (wait_result == CaptureRuntimeWaitResult::kTimeout) {
    return WDF_CAPTURE_RESULT_TIMEOUT;
  }

  const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::steady_clock::now() - started);
  const uint64_t elapsed_ms =
      elapsed.count() <= 0 ? 0 : static_cast<uint64_t>(elapsed.count());
  const uint32_t remaining_ms =
      elapsed_ms >= timeout_ms
          ? 0
          : timeout_ms - static_cast<uint32_t>(elapsed_ms);
  uint64_t persistence_generation = 0;
  if (!instance.safety.FinalizeRevoke(remaining_ms, &persistence_generation)) {
    return WDF_CAPTURE_RESULT_TIMEOUT;
  }
  static_cast<void>(persistence_generation);

  std::lock_guard lock(instance.state_mutex);
  instance.privacy = instance.safety.privacy_context();
  if (instance.state != WDF_CAPTURE_STATE_STOPPED) {
    if (!PublishState(instance,
                      WDF_CAPTURE_STATE_STOPPED,
                      instance.pending_stop_reason,
                      "Capture worker stopped and joined.")) {
      instance.stop_requested_for_join = false;
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
  }
  instance.stop_requested_for_join = false;
  return wait_result == CaptureRuntimeWaitResult::kWorkerFailed
             ? WDF_CAPTURE_RESULT_INTERNAL_ERROR
             : WDF_CAPTURE_RESULT_OK;
}

wdf_capture_result LegacyStartOrResume(wdf_capture_handle handle) {
  InstanceLease lease = AcquireInstance(handle);
  if (!lease) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  return WDF_CAPTURE_RESULT_ADMISSION_REQUIRED;
}

bool IsCommandStateValid(wdf_capture_state state, CaptureCommand command) {
  if (command == CaptureCommand::kResume) {
    return state == WDF_CAPTURE_STATE_PAUSED ||
           state == WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT;
  }
  return state == WDF_CAPTURE_STATE_STOPPED ||
         state == WDF_CAPTURE_STATE_UNAVAILABLE;
}

wdf_capture_result StartOrResumeAuthorized(
    wdf_capture_handle handle,
    const wdf_capture_command_admission_v1* admission,
    CaptureCommand command) {
  CaptureCommandAdmission value;
  const wdf_capture_result validation =
      CopyCommandAdmission(admission, &value);
  if (validation != WDF_CAPTURE_RESULT_OK) {
    return validation;
  }
  InstanceLease lease = AcquireInstance(handle);
  if (!lease) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  CaptureInstance* const instance = lease.get();
  std::lock_guard lock(instance->state_mutex);
  const uint64_t owner_epoch = instance->runtime.owner_epoch();
  if (owner_epoch == 0) {
    return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
  }
  CaptureCommandAdmissionPermit permit;
  const CaptureCommandAdmissionResult admission_result =
      instance->safety.AcquireCommandAdmissionPermit(
          value, command, owner_epoch, &permit);
  if (admission_result != CaptureCommandAdmissionResult::kOk) {
    return MapCommandAdmissionResult(admission_result);
  }
  if (!IsCommandStateValid(instance->state, command)) {
    return WDF_CAPTURE_RESULT_INVALID_STATE;
  }

  if (!PublishState(
          *instance,
          WDF_CAPTURE_STATE_UNAVAILABLE,
          WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE,
          "The native capture engine is not connected in this foundation build.")) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
  return WDF_CAPTURE_RESULT_NOT_IMPLEMENTED;
}

}  // namespace

extern "C" uint32_t WDF_CAPTURE_CALL
wdf_capture_get_abi_version(void) noexcept {
  return WDF_CAPTURE_ABI_VERSION;
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_get_capabilities(wdf_capture_capabilities* capabilities) noexcept {
  try {
    if (capabilities == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *capabilities = WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD |
                    WDF_CAPTURE_CAPABILITY_EVENT_QUEUE |
                    WDF_CAPTURE_CAPABILITY_TARGET_SCOPED_AUTHORIZATION |
                    WDF_CAPTURE_CAPABILITY_PERSISTENCE_GENERATION_BARRIER |
                    WDF_CAPTURE_CAPABILITY_DETERMINISTIC_STOP |
                    WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION |
                    WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION |
                    WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL wdf_capture_create(
    const wdf_capture_config_v1* config,
    wdf_capture_handle* handle) noexcept {
  try {
    if (handle == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *handle = 0;
    std::wstring output_directory_utf16;
    const wdf_capture_result validation =
        ValidateConfig(config, &output_directory_utf16);
    if (validation != WDF_CAPTURE_RESULT_OK) {
      return validation;
    }

    const CapturePolicy policy{
        config->capture_interval_ms,
        config->context_interval_ms,
        config->chunk_duration_ms,
    };
    auto instance = std::make_shared<CaptureInstance>(
        policy,
        config->event_queue_capacity,
        config->max_width,
        config->max_height,
        std::move(output_directory_utf16));
    if (!PublishState(
            *instance,
            WDF_CAPTURE_STATE_UNAVAILABLE,
            WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE,
            "Native capture foundation loaded; live capture remains disabled.")) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }

    const wdf_capture_handle raw_handle = AllocateHandle();
    if (raw_handle == 0) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    {
      std::lock_guard lock(g_registry_mutex);
      const auto [position, inserted] =
          g_instances.emplace(raw_handle, std::move(instance));
      static_cast<void>(position);
      if (!inserted) {
        return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
      }
    }
    *handle = raw_handle;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_update_privacy_context(
    wdf_capture_handle handle,
    const wdf_capture_privacy_context_v1* context) noexcept {
  try {
    PrivacyContext value;
    const wdf_capture_result validation = CopyPrivacyContext(context, &value);
    if (validation != WDF_CAPTURE_RESULT_OK) {
      return validation;
    }
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    CaptureInstance* const instance = lease.get();
    CaptureSafetyUpdateTicket ticket;
    {
      std::lock_guard lock(instance->state_mutex);
      if (instance->state == WDF_CAPTURE_STATE_STOPPING) {
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      ticket = instance->safety.BeginAuthorizationUpdate();
    }
    instance->runtime.NotifyAuthorizationChanged();
    uint64_t persistence_generation = 0;
    const CaptureSafetyUpdateResult update_result =
        instance->safety.CompleteLegacyPrivacyContext(
            ticket, value, &persistence_generation);
    static_cast<void>(persistence_generation);
    if (update_result != CaptureSafetyUpdateResult::kOk) {
      return MapSafetyUpdateResult(update_result);
    }
    {
      std::lock_guard lock(instance->state_mutex);
      if (instance->state == WDF_CAPTURE_STATE_STOPPING) {
        instance->safety.BeginRevoke();
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      instance->privacy = instance->safety.privacy_context();
    }
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_update_runtime_authorization(
    wdf_capture_handle handle,
    const wdf_capture_runtime_authorization_v1* context,
    uint64_t* persistence_generation) noexcept {
  try {
    if (persistence_generation == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *persistence_generation = 0;

    RuntimeAuthorization value;
    const wdf_capture_result validation =
        CopyRuntimeAuthorization(context, &value);
    if (validation != WDF_CAPTURE_RESULT_OK) {
      return validation;
    }
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    CaptureInstance* const instance = lease.get();
    CaptureSafetyUpdateTicket ticket;
    {
      std::lock_guard lock(instance->state_mutex);
      if (instance->state == WDF_CAPTURE_STATE_STOPPING) {
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      ticket = instance->safety.BeginAuthorizationUpdate();
    }
    instance->runtime.NotifyAuthorizationChanged();
    const CaptureSafetyUpdateResult update_result =
        instance->safety.CompleteRuntimeAuthorization(
            ticket, value, persistence_generation);
    if (update_result == CaptureSafetyUpdateResult::kOk) {
      std::lock_guard lock(instance->state_mutex);
      if (instance->state == WDF_CAPTURE_STATE_STOPPING) {
        instance->safety.BeginRevoke();
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
      instance->privacy = instance->safety.privacy_context();
    }
    return MapSafetyUpdateResult(update_result);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_invalidate_runtime_authorization(
    wdf_capture_handle handle,
    uint64_t* authorization_epoch) noexcept {
  try {
    if (authorization_epoch == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *authorization_epoch = 0;
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    const uint64_t closed_epoch =
        lease.get()->safety.InvalidateAuthorizationAdmission();
    lease.get()->runtime.NotifyAuthorizationChanged();
    if (closed_epoch == 0) {
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    }
    *authorization_epoch = closed_epoch;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_revoke_runtime_authorization(
    wdf_capture_handle handle,
    uint64_t* persistence_generation) noexcept {
  try {
    if (persistence_generation == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    *persistence_generation = 0;
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    CaptureInstance* const instance = lease.get();
    const CaptureSafetyUpdateResult revoke_result =
        instance->safety.Revoke(persistence_generation);
    instance->runtime.NotifyAuthorizationChanged();
    {
      std::lock_guard lock(instance->state_mutex);
      instance->privacy = instance->safety.privacy_context();
    }
    return MapSafetyUpdateResult(revoke_result);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_issue_command_admission(
    wdf_capture_handle handle,
    wdf_capture_command command,
    uint64_t expected_persistence_generation,
    uint64_t expected_target_epoch,
    wdf_capture_command_admission_v1* admission) noexcept {
  try {
    const wdf_capture_result header = ValidateStructHeader(
        admission, sizeof(wdf_capture_command_admission_v1));
    if (header != WDF_CAPTURE_RESULT_OK) {
      return header;
    }
    *admission = {};
    admission->struct_size = sizeof(*admission);
    admission->abi_version = WDF_CAPTURE_ABI_VERSION;

    CaptureCommand value = CaptureCommand::kStart;
    if (!TryCopyCommand(command, &value) ||
        expected_persistence_generation == 0 || expected_target_epoch == 0) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    CaptureInstance* const instance = lease.get();
    std::lock_guard lock(instance->state_mutex);
    if (!IsCommandStateValid(instance->state, value)) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }

    const uint64_t owner_epoch = instance->runtime.owner_epoch();
    if (owner_epoch == 0) {
      return WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED;
    }
    CaptureCommandAdmission issued;
    const CaptureCommandAdmissionResult result =
        instance->safety.IssueCommandAdmission(
            value,
            expected_persistence_generation,
            expected_target_epoch,
            owner_epoch,
            &issued);
    if (result == CaptureCommandAdmissionResult::kOk) {
      WriteCommandAdmission(issued, admission);
    }
    return MapCommandAdmissionResult(result);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_start(wdf_capture_handle handle) noexcept {
  try {
    return LegacyStartOrResume(handle);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_start_authorized(
    wdf_capture_handle handle,
    const wdf_capture_command_admission_v1* admission) noexcept {
  try {
    return StartOrResumeAuthorized(
        handle, admission, CaptureCommand::kStart);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_pause(wdf_capture_handle handle) noexcept {
  try {
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    CaptureInstance* const instance = lease.get();
    std::lock_guard lock(instance->state_mutex);
    if (instance->state != WDF_CAPTURE_STATE_RECORDING &&
        instance->state != WDF_CAPTURE_STATE_STARTING &&
        instance->state != WDF_CAPTURE_STATE_RESUMING) {
      return WDF_CAPTURE_RESULT_INVALID_STATE;
    }
    return PublishState(*instance,
                        WDF_CAPTURE_STATE_PAUSED,
                        WDF_CAPTURE_REASON_USER_PAUSED,
                        "Capture paused by the user.")
               ? WDF_CAPTURE_RESULT_OK
               : WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_resume(wdf_capture_handle handle) noexcept {
  try {
    return LegacyStartOrResume(handle);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_resume_authorized(
    wdf_capture_handle handle,
    const wdf_capture_command_admission_v1* admission) noexcept {
  try {
    return StartOrResumeAuthorized(
        handle, admission, CaptureCommand::kResume);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_request_stop(wdf_capture_handle handle) noexcept {
  try {
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    return RequestStopCore(
        *lease.get(), WDF_CAPTURE_REASON_USER_STOPPED);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL wdf_capture_wait_stopped(
    wdf_capture_handle handle,
    uint32_t timeout_ms) noexcept {
  try {
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    CaptureInstance* const instance = lease.get();
    {
      std::lock_guard lock(instance->state_mutex);
      if (!instance->stop_requested_for_join &&
          (instance->state == WDF_CAPTURE_STATE_STOPPED ||
           instance->state == WDF_CAPTURE_STATE_UNAVAILABLE)) {
        return WDF_CAPTURE_RESULT_OK;
      }
      if (!instance->stop_requested_for_join) {
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      }
    }
    return WaitStoppedCore(*instance, timeout_ms);
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL wdf_capture_poll_event(
    wdf_capture_handle handle,
    uint32_t timeout_ms,
    wdf_capture_event_v1* event,
    char* detail_utf8,
    uint32_t detail_utf8_capacity,
    uint32_t* detail_utf8_required) noexcept {
  try {
    if (detail_utf8_required == nullptr ||
        (detail_utf8 == nullptr && detail_utf8_capacity != 0) ||
        timeout_ms > kMaximumPollTimeoutMs) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    const wdf_capture_result header =
        ValidateStructHeader(event, sizeof(wdf_capture_event_v1));
    if (header != WDF_CAPTURE_RESULT_OK) {
      return header;
    }
    InstanceLease lease = AcquireInstance(handle);
    if (!lease) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    const CaptureEventReadResult result =
        lease.get()->events.Read(timeout_ms,
                                 event,
                                 detail_utf8,
                                 detail_utf8_capacity,
                                 detail_utf8_required);
    switch (result) {
      case CaptureEventReadResult::kEmpty:
        return WDF_CAPTURE_RESULT_NO_EVENT;
      case CaptureEventReadResult::kBufferTooSmall:
        return WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL;
      case CaptureEventReadResult::kSuccess:
        return WDF_CAPTURE_RESULT_OK;
      case CaptureEventReadResult::kClosed:
        return WDF_CAPTURE_RESULT_INVALID_STATE;
      case CaptureEventReadResult::kInternalError:
      default:
        return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_destroy(wdf_capture_handle* handle) noexcept {
  try {
    if (handle == nullptr) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    if (*handle == 0) {
      return WDF_CAPTURE_RESULT_OK;
    }

    const wdf_capture_handle raw_handle = *handle;
    std::shared_ptr<CaptureInstance> instance;
    {
      std::lock_guard registry_lock(g_registry_mutex);
      const auto match = g_instances.find(raw_handle);
      if (match == g_instances.end()) {
        *handle = 0;
        return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
      }
      instance = match->second;
      {
        std::lock_guard lifetime_lock(instance->lifetime_mutex);
        instance->destroying = true;
      }
      g_instances.erase(match);
    }

    static_cast<void>(RequestStopCore(
        *instance, WDF_CAPTURE_REASON_SHUTDOWN));
    instance->runtime.Shutdown();
    uint64_t persistence_generation = 0;
    static_cast<void>(instance->safety.Revoke(&persistence_generation));
    static_cast<void>(persistence_generation);
    {
      std::lock_guard state_lock(instance->state_mutex);
      instance->privacy = instance->safety.privacy_context();
      if (instance->state != WDF_CAPTURE_STATE_STOPPED) {
        static_cast<void>(PublishState(
            *instance,
            WDF_CAPTURE_STATE_STOPPED,
            instance->pending_stop_reason,
            "Capture worker stopped and joined during destruction."));
      }
      instance->stop_requested_for_join = false;
    }
    instance->events.Close();
    {
      std::unique_lock lifetime_lock(instance->lifetime_mutex);
      instance->no_active_calls.wait(
          lifetime_lock,
          [&instance] { return instance->active_calls == 0; });
    }
    *handle = 0;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

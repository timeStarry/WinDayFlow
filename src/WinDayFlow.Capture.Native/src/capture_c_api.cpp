#include "windayflow_capture.h"

#include <Windows.h>

#include <array>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

#include "analysis_evidence_extractor.h"
#include "capture_instance_controller.h"
#include "capture_chunk_fingerprint.h"
#include "capture_policy.h"
#include "privacy_guard.h"
#include "windows_capture_worker_backend.h"

namespace {

using windayflow::capture::CaptureActivationMode;
using windayflow::capture::CaptureAuthorizationScope;
using windayflow::capture::CaptureChunkFingerprintResult;
using windayflow::capture::CaptureEventReadResult;
using windayflow::capture::CaptureInstanceController;
using windayflow::capture::CaptureInstanceControllerConfiguration;
using windayflow::capture::CapturePolicy;
using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureCommandAdmission;
using windayflow::capture::CaptureSafetyUpdateResult;
using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::AnalysisEvidenceRequest;
using windayflow::capture::AnalysisEvidenceResult;
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

#if WDF_ENABLE_DEV_LIVE_CAPTURE
constexpr CaptureActivationMode kCaptureActivationMode =
    CaptureActivationMode::kEnabled;
constexpr uint32_t kDevLiveCaptureAverageBitrate = 500'000;
constexpr wdf_capture_capabilities kLiveCaptureCapabilities =
    WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE |
    WDF_CAPTURE_CAPABILITY_H264_CHUNKS;
#else
constexpr CaptureActivationMode kCaptureActivationMode =
    CaptureActivationMode::kDisabled;
constexpr wdf_capture_capabilities kLiveCaptureCapabilities = 0;
#endif

static_assert(kLegacyRuntimeAuthorizationSize ==
              offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_monitor_handle));
static_assert(kCurrentRuntimeAuthorizationSize == 224);

struct CaptureInstance {
  CaptureInstance(CaptureInstanceControllerConfiguration configuration,
                  std::unique_ptr<windayflow::capture::CaptureWorkerBackend>
                      backend)
      : controller(std::move(configuration), std::move(backend)) {}

  std::mutex lifetime_mutex;
  std::condition_variable no_active_calls;
  uint32_t active_calls = 0;
  bool destroying = false;

  CaptureInstanceController controller;
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

bool TryCopyUtf8Wide(std::string_view encoded, std::wstring* decoded) {
  if (decoded == nullptr || !IsValidUtf8(encoded)) {
    return false;
  }
  decoded->clear();
  const int wide_length = MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, encoded.data(),
      static_cast<int>(encoded.size()), nullptr, 0);
  if (wide_length <= 0) {
    return false;
  }
  std::wstring value(static_cast<size_t>(wide_length), L'\0');
  if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, encoded.data(),
                          static_cast<int>(encoded.size()), value.data(),
                          wide_length) != wide_length) {
    return false;
  }
  *decoded = std::move(value);
  return true;
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

wdf_capture_result MapChunkFingerprintResult(
    CaptureChunkFingerprintResult result) {
  switch (result) {
    case CaptureChunkFingerprintResult::kOk:
      return WDF_CAPTURE_RESULT_OK;
    case CaptureChunkFingerprintResult::kInvalidArgument:
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    case CaptureChunkFingerprintResult::kNotFound:
      return WDF_CAPTURE_RESULT_EVIDENCE_NOT_FOUND;
    case CaptureChunkFingerprintResult::kUnsafeEvidence:
      return WDF_CAPTURE_RESULT_UNSAFE_EVIDENCE;
    case CaptureChunkFingerprintResult::kTooLarge:
      return WDF_CAPTURE_RESULT_EVIDENCE_TOO_LARGE;
    case CaptureChunkFingerprintResult::kChangedDuringRead:
      return WDF_CAPTURE_RESULT_EVIDENCE_CHANGED;
    case CaptureChunkFingerprintResult::kIoFailure:
      return WDF_CAPTURE_RESULT_IO_FAILURE;
    case CaptureChunkFingerprintResult::kCryptoFailure:
      return WDF_CAPTURE_RESULT_CRYPTO_FAILURE;
    default:
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

wdf_capture_result MapAnalysisEvidenceResult(AnalysisEvidenceResult result) {
  switch (result) {
    case AnalysisEvidenceResult::kOk:
      return WDF_CAPTURE_RESULT_OK;
    case AnalysisEvidenceResult::kInvalidArgument:
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    case AnalysisEvidenceResult::kNotFound:
      return WDF_CAPTURE_RESULT_EVIDENCE_NOT_FOUND;
    case AnalysisEvidenceResult::kUnsafeEvidence:
      return WDF_CAPTURE_RESULT_UNSAFE_EVIDENCE;
    case AnalysisEvidenceResult::kTooLarge:
      return WDF_CAPTURE_RESULT_EVIDENCE_TOO_LARGE;
    case AnalysisEvidenceResult::kChangedDuringRead:
      return WDF_CAPTURE_RESULT_EVIDENCE_CHANGED;
    case AnalysisEvidenceResult::kIoFailure:
      return WDF_CAPTURE_RESULT_IO_FAILURE;
    case AnalysisEvidenceResult::kCryptoFailure:
      return WDF_CAPTURE_RESULT_CRYPTO_FAILURE;
    case AnalysisEvidenceResult::kInvalidEvidence:
      return WDF_CAPTURE_RESULT_EVIDENCE_INVALID;
    case AnalysisEvidenceResult::kDecoderFailure:
      return WDF_CAPTURE_RESULT_DECODER_FAILURE;
    case AnalysisEvidenceResult::kConflict:
      return WDF_CAPTURE_RESULT_EVIDENCE_CONFLICT;
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
      WDF_CAPTURE_TARGET_PRESENT | WDF_CAPTURE_TARGET_DISPLAY_PRESENT |
      WDF_CAPTURE_TARGET_DISPLAY_WIDE_SCOPE;
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
  const bool display_wide_scope =
      (source->target_flags & WDF_CAPTURE_TARGET_DISPLAY_WIDE_SCOPE) != 0;
  const bool window_values_present = source->target_window_handle != 0 ||
                                     source->target_process_creation_time_100ns !=
                                         0 ||
                                     source->target_process_id != 0;
  const bool scope_present = target_present || display_wide_scope;
  if ((target_present && display_wide_scope) ||
      (scope_present && !display_present) ||
      (!scope_present && display_present) ||
      (target_present != window_values_present) ||
      (scope_present != (source->target_epoch != 0))) {
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
  if (scope_present) {
    if (source->target_epoch == 0 ||
        (target_present && (source->target_window_handle == 0 ||
        source->target_process_creation_time_100ns == 0 ||
        source->target_process_id == 0))) {
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    authorization.target = CaptureTargetIdentity{
        source->target_window_handle,
        source->target_process_id,
        source->target_process_creation_time_100ns,
        source->target_epoch,
        source->target_display_monitor_handle,
        std::move(display_device_key),
        display_wide_scope ? CaptureAuthorizationScope::kDisplayWide
                           : CaptureAuthorizationScope::kForegroundTarget};
  }

  const bool fully_allowed = windayflow::capture::IsFullyAllowed(privacy);
  if (fully_allowed != scope_present) {
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

wdf_capture_result LegacyStartOrResume(wdf_capture_handle handle) {
  InstanceLease lease = AcquireInstance(handle);
  if (!lease) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  return WDF_CAPTURE_RESULT_ADMISSION_REQUIRED;
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
  return command == CaptureCommand::kStart
             ? lease.get()->controller.StartAuthorized(value)
             : lease.get()->controller.ResumeAuthorized(value);
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
    *capabilities = kLiveCaptureCapabilities |
                    WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD |
                    WDF_CAPTURE_CAPABILITY_EVENT_QUEUE |
                    WDF_CAPTURE_CAPABILITY_TARGET_SCOPED_AUTHORIZATION |
                    WDF_CAPTURE_CAPABILITY_PERSISTENCE_GENERATION_BARRIER |
                    WDF_CAPTURE_CAPABILITY_DETERMINISTIC_STOP |
                    WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION |
                    WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION |
                    WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION |
                    WDF_CAPTURE_CAPABILITY_DISPLAY_WIDE_CONTINUOUS_AUTHORIZATION;
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
    CaptureInstanceControllerConfiguration controller_configuration;
    controller_configuration.activation_mode = kCaptureActivationMode;
    controller_configuration.event_queue_capacity = config->event_queue_capacity;
    controller_configuration.worker.policy = policy;
    controller_configuration.worker.maximum_width = config->max_width;
    controller_configuration.worker.maximum_height = config->max_height;
#if WDF_ENABLE_DEV_LIVE_CAPTURE
    controller_configuration.worker.average_bitrate =
        kDevLiveCaptureAverageBitrate;
#endif
    auto backend = windayflow::capture::CreateWindowsCaptureWorkerBackend(
        std::move(output_directory_utf16));
    if (backend == nullptr) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    auto instance = std::make_shared<CaptureInstance>(
        std::move(controller_configuration), std::move(backend));

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
    uint64_t persistence_generation = 0;
    const CaptureSafetyUpdateResult update_result =
        lease.get()->controller.UpdatePrivacyContext(
            value, &persistence_generation);
    static_cast<void>(persistence_generation);
    return MapSafetyUpdateResult(update_result);
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

    const CaptureSafetyUpdateResult update_result =
        lease.get()->controller.UpdateRuntimeAuthorization(
            value, persistence_generation);
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

    return lease.get()->controller.InvalidateRuntimeAuthorization(
        authorization_epoch);
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

    const CaptureSafetyUpdateResult revoke_result =
        lease.get()->controller.RevokeRuntimeAuthorization(
            persistence_generation);
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
    CaptureCommandAdmission issued;
    const wdf_capture_result result = lease.get()->controller.IssueAdmission(
        value, expected_persistence_generation, expected_target_epoch, &issued);
    if (result == WDF_CAPTURE_RESULT_OK) {
      WriteCommandAdmission(issued, admission);
    }
    return result;
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
    return lease.get()->controller.Pause();
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
    return lease.get()->controller.RequestStop(
        WDF_CAPTURE_REASON_USER_STOPPED);
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
    return lease.get()->controller.WaitStopped(timeout_ms);
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
        lease.get()->controller.Poll(timeout_ms,
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
wdf_capture_compute_chunk_fingerprint(
    const char* data_root_utf8,
    uint32_t data_root_utf8_length,
    const char* canonical_chunk_id_utf8,
    uint32_t canonical_chunk_id_utf8_length,
    uint64_t expected_video_byte_count,
    char* fingerprint_utf8,
    uint32_t fingerprint_utf8_capacity,
    uint32_t* fingerprint_utf8_required) noexcept {
  if (fingerprint_utf8_required == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  *fingerprint_utf8_required =
      WDF_CAPTURE_CHUNK_FINGERPRINT_UTF8_CAPACITY;
  const auto clear_output = [fingerprint_utf8,
                             fingerprint_utf8_capacity]() noexcept {
    if (fingerprint_utf8 != nullptr && fingerprint_utf8_capacity > 0) {
      fingerprint_utf8[0] = '\0';
    }
  };

  try {
    if (data_root_utf8 == nullptr || data_root_utf8_length == 0 ||
        data_root_utf8_length > kMaximumOutputDirectoryBytes ||
        canonical_chunk_id_utf8 == nullptr ||
        canonical_chunk_id_utf8_length == 0 ||
        expected_video_byte_count == 0 ||
        expected_video_byte_count >
            windayflow::capture::kMaximumFingerprintVideoBytes ||
        (fingerprint_utf8 == nullptr && fingerprint_utf8_capacity != 0)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    const std::string_view encoded_root(data_root_utf8,
                                        data_root_utf8_length);
    const std::string_view encoded_chunk_id(canonical_chunk_id_utf8,
                                            canonical_chunk_id_utf8_length);
    std::wstring data_root;
    if (!TryCopyUtf8Wide(encoded_root, &data_root) ||
        !IsValidUtf8(encoded_chunk_id) ||
        !windayflow::capture::IsCanonicalCaptureChunkId(encoded_chunk_id)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    const std::string chunk_id(encoded_chunk_id);

    if (fingerprint_utf8 == nullptr ||
        fingerprint_utf8_capacity <
            WDF_CAPTURE_CHUNK_FINGERPRINT_UTF8_CAPACITY) {
      clear_output();
      return WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL;
    }

    std::array<char,
               windayflow::capture::kCaptureChunkFingerprintBufferSize>
        fingerprint{};
    const wdf_capture_result result = MapChunkFingerprintResult(
        windayflow::capture::ComputeCaptureChunkFingerprint(
            data_root, chunk_id,
            static_cast<size_t>(expected_video_byte_count), &fingerprint));
    if (result != WDF_CAPTURE_RESULT_OK) {
      clear_output();
      return result;
    }
    std::memcpy(fingerprint_utf8, fingerprint.data(), fingerprint.size());
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    clear_output();
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_extract_analysis_evidence(
    const char* data_root_utf8,
    uint32_t data_root_utf8_length,
    const char* canonical_chunk_id_utf8,
    uint32_t canonical_chunk_id_utf8_length,
    uint64_t expected_video_byte_count,
    uint32_t expected_frame_count,
    uint32_t expected_video_width,
    uint32_t expected_video_height,
    uint64_t expected_duration_ms,
    const char* expected_source_fingerprint_utf8,
    uint32_t expected_source_fingerprint_utf8_length,
    char* manifest_utf8,
    uint32_t manifest_utf8_capacity,
    uint32_t* manifest_utf8_required) noexcept {
  if (manifest_utf8_required == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  *manifest_utf8_required = 0;
  const auto clear_output = [manifest_utf8,
                             manifest_utf8_capacity]() noexcept {
    if (manifest_utf8 != nullptr && manifest_utf8_capacity > 0) {
      manifest_utf8[0] = '\0';
    }
  };

  try {
    if (data_root_utf8 == nullptr || data_root_utf8_length == 0 ||
        data_root_utf8_length > kMaximumOutputDirectoryBytes ||
        canonical_chunk_id_utf8 == nullptr ||
        canonical_chunk_id_utf8_length == 0 ||
        expected_source_fingerprint_utf8 == nullptr ||
        expected_source_fingerprint_utf8_length !=
            WDF_CAPTURE_CHUNK_FINGERPRINT_UTF8_LENGTH ||
        (manifest_utf8 == nullptr && manifest_utf8_capacity != 0)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    const std::string_view encoded_root(data_root_utf8,
                                        data_root_utf8_length);
    const std::string_view encoded_chunk_id(canonical_chunk_id_utf8,
                                            canonical_chunk_id_utf8_length);
    const std::string_view encoded_fingerprint(
        expected_source_fingerprint_utf8,
        expected_source_fingerprint_utf8_length);
    std::wstring data_root;
    if (!TryCopyUtf8Wide(encoded_root, &data_root) ||
        !IsValidUtf8(encoded_chunk_id) ||
        !windayflow::capture::IsCanonicalCaptureChunkId(encoded_chunk_id) ||
        !IsValidUtf8(encoded_fingerprint) ||
        !windayflow::capture::IsCanonicalSourceFingerprint(
            encoded_fingerprint)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }

    AnalysisEvidenceRequest request;
    request.data_root = std::move(data_root);
    request.canonical_chunk_id.assign(encoded_chunk_id);
    request.expected_video_byte_count = expected_video_byte_count;
    request.expected_frame_count = expected_frame_count;
    request.expected_video_width = expected_video_width;
    request.expected_video_height = expected_video_height;
    request.expected_duration_ms = expected_duration_ms;
    request.expected_source_fingerprint.assign(encoded_fingerprint);
    std::string manifest;
    const wdf_capture_result result = MapAnalysisEvidenceResult(
        windayflow::capture::ExtractAnalysisEvidence(request, &manifest));
    if (result != WDF_CAPTURE_RESULT_OK) {
      clear_output();
      return result;
    }
    if (manifest.empty() ||
        manifest.size() >
            WDF_CAPTURE_ANALYSIS_EVIDENCE_MANIFEST_UTF8_MAX_LENGTH ||
        manifest.size() >= std::numeric_limits<uint32_t>::max()) {
      clear_output();
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    *manifest_utf8_required = static_cast<uint32_t>(manifest.size() + 1U);
    if (manifest_utf8 == nullptr ||
        manifest_utf8_capacity < *manifest_utf8_required) {
      clear_output();
      return WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL;
    }
    std::memcpy(manifest_utf8, manifest.data(), manifest.size());
    manifest_utf8[manifest.size()] = '\0';
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    clear_output();
    *manifest_utf8_required = 0;
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_read_analysis_evidence_frame(
    const char* data_root_utf8,
    uint32_t data_root_utf8_length,
    const char* canonical_chunk_id_utf8,
    uint32_t canonical_chunk_id_utf8_length,
    const char* canonical_source_fingerprint_utf8,
    uint32_t canonical_source_fingerprint_utf8_length,
    uint32_t frame_index,
    uint8_t* frame_bytes,
    uint32_t frame_bytes_capacity,
    uint32_t* frame_bytes_required) noexcept {
  if (frame_bytes_required == nullptr) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  *frame_bytes_required = 0;
  const auto clear_output = [frame_bytes, frame_bytes_capacity]() noexcept {
    if (frame_bytes != nullptr && frame_bytes_capacity > 0) {
      frame_bytes[0] = 0;
    }
  };

  try {
    if (data_root_utf8 == nullptr || data_root_utf8_length == 0 ||
        data_root_utf8_length > kMaximumOutputDirectoryBytes ||
        canonical_chunk_id_utf8 == nullptr ||
        canonical_chunk_id_utf8_length == 0 ||
        canonical_source_fingerprint_utf8 == nullptr ||
        canonical_source_fingerprint_utf8_length !=
            WDF_CAPTURE_CHUNK_FINGERPRINT_UTF8_LENGTH ||
        (frame_bytes == nullptr && frame_bytes_capacity != 0)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    const std::string_view encoded_root(data_root_utf8,
                                        data_root_utf8_length);
    const std::string_view encoded_chunk_id(canonical_chunk_id_utf8,
                                            canonical_chunk_id_utf8_length);
    const std::string_view encoded_fingerprint(
        canonical_source_fingerprint_utf8,
        canonical_source_fingerprint_utf8_length);
    std::wstring data_root;
    if (!TryCopyUtf8Wide(encoded_root, &data_root) ||
        !IsValidUtf8(encoded_chunk_id) ||
        !windayflow::capture::IsCanonicalCaptureChunkId(encoded_chunk_id) ||
        !IsValidUtf8(encoded_fingerprint) ||
        !windayflow::capture::IsCanonicalSourceFingerprint(
            encoded_fingerprint)) {
      clear_output();
      return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
    }
    std::vector<uint8_t> bytes;
    const wdf_capture_result result = MapAnalysisEvidenceResult(
        windayflow::capture::ReadAnalysisEvidenceFrame(
            data_root, encoded_chunk_id, encoded_fingerprint, frame_index,
            &bytes));
    if (result != WDF_CAPTURE_RESULT_OK) {
      clear_output();
      return result;
    }
    if (bytes.empty() ||
        bytes.size() > WDF_CAPTURE_ANALYSIS_EVIDENCE_FRAME_MAX_BYTES) {
      clear_output();
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    *frame_bytes_required = static_cast<uint32_t>(bytes.size());
    if (frame_bytes == nullptr ||
        frame_bytes_capacity < *frame_bytes_required) {
      clear_output();
      return WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL;
    }
    std::memcpy(frame_bytes, bytes.data(), bytes.size());
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    clear_output();
    *frame_bytes_required = 0;
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

    instance->controller.Shutdown();
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

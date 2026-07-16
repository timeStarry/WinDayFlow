#include "windayflow_capture.h"

#include <Windows.h>

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
#include "privacy_guard.h"

namespace {

using windayflow::capture::CaptureEventQueue;
using windayflow::capture::CaptureEventReadResult;
using windayflow::capture::CapturePolicy;
using windayflow::capture::PrivacyContext;
using windayflow::capture::PrivacyDecision;

constexpr uint32_t kMinimumEventQueueCapacity = 16;
constexpr uint32_t kMaximumEventQueueCapacity = 4'096;
constexpr uint32_t kMinimumCaptureWidth = 320;
constexpr uint32_t kMaximumCaptureWidth = 7'680;
constexpr uint32_t kMinimumCaptureHeight = 200;
constexpr uint32_t kMaximumCaptureHeight = 4'320;
constexpr uint32_t kMaximumOutputDirectoryBytes = 32'767;
constexpr uint32_t kMaximumPollTimeoutMs = 60'000;
constexpr size_t kVersionedStructHeaderSize = sizeof(uint32_t) * 2U;

struct CaptureInstance {
  CaptureInstance(CapturePolicy initial_policy,
                  size_t event_queue_capacity,
                  std::string output_directory)
      : events(event_queue_capacity),
        policy(initial_policy),
        output_directory_utf8(std::move(output_directory)) {}

  std::mutex lifetime_mutex;
  std::condition_variable no_active_calls;
  uint32_t active_calls = 0;
  bool destroying = false;

  std::mutex state_mutex;
  CaptureEventQueue events;
  CapturePolicy policy;
  PrivacyContext privacy;
  std::string output_directory_utf8;
  wdf_capture_state state = WDF_CAPTURE_STATE_UNAVAILABLE;
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

bool IsWithin(uint32_t value, uint32_t minimum, uint32_t maximum) {
  return value >= minimum && value <= maximum;
}

wdf_capture_result ValidateConfig(const wdf_capture_config_v1* config) {
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
                                    config->output_directory_utf8_length))) {
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
  instance.state = state;
  const uint64_t sequence =
      instance.events.Push(WDF_CAPTURE_EVENT_STATE_CHANGED,
                           state,
                           reason,
                           WDF_CAPTURE_ERROR_NONE,
                           std::move(detail),
                           CurrentUnixMilliseconds());
  if (sequence == 0) {
    instance.state = WDF_CAPTURE_STATE_FAULTED;
    return false;
  }
  return true;
}

wdf_capture_result StartOrResume(wdf_capture_handle handle, bool is_resume) {
  InstanceLease lease = AcquireInstance(handle);
  if (!lease) {
    return WDF_CAPTURE_RESULT_INVALID_ARGUMENT;
  }
  CaptureInstance* const instance = lease.get();
  std::lock_guard lock(instance->state_mutex);

  const bool valid_state = is_resume
                               ? instance->state == WDF_CAPTURE_STATE_PAUSED ||
                                     instance->state ==
                                         WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT
                               : instance->state == WDF_CAPTURE_STATE_STOPPED ||
                                     instance->state ==
                                         WDF_CAPTURE_STATE_UNAVAILABLE;
  if (!valid_state) {
    return WDF_CAPTURE_RESULT_INVALID_STATE;
  }

  const PrivacyDecision decision =
      windayflow::capture::EvaluatePrivacyContext(instance->privacy);
  if (!decision.allowed) {
    const wdf_capture_state blocked_state =
        decision.reason == WDF_CAPTURE_REASON_CONSENT_REQUIRED
            ? WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT
            : WDF_CAPTURE_STATE_PAUSED;
    if (!PublishState(*instance,
                      blocked_state,
                      decision.reason,
                      "Capture is blocked by the fail-closed privacy policy.")) {
      return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
    }
    return WDF_CAPTURE_RESULT_POLICY_BLOCKED;
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
                    WDF_CAPTURE_CAPABILITY_EVENT_QUEUE;
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
    const wdf_capture_result validation = ValidateConfig(config);
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
        std::string(config->output_directory_utf8,
                    config->output_directory_utf8_length));
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
    std::lock_guard lock(instance->state_mutex);
    if (value.policy_revision < instance->privacy.policy_revision) {
      return WDF_CAPTURE_RESULT_STALE_POLICY;
    }
    if (value.policy_revision == instance->privacy.policy_revision &&
        instance->privacy.policy_revision != 0) {
      return value == instance->privacy
                 ? WDF_CAPTURE_RESULT_OK
                 : WDF_CAPTURE_RESULT_POLICY_REVISION_CONFLICT;
    }
    instance->privacy = value;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

extern "C" wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_start(wdf_capture_handle handle) noexcept {
  try {
    return StartOrResume(handle, false);
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
    return StartOrResume(handle, true);
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
    CaptureInstance* const instance = lease.get();
    std::lock_guard lock(instance->state_mutex);
    if (instance->state == WDF_CAPTURE_STATE_STOPPED) {
      return WDF_CAPTURE_RESULT_OK;
    }
    return PublishState(*instance,
                        WDF_CAPTURE_STATE_STOPPED,
                        WDF_CAPTURE_REASON_USER_STOPPED,
                        "Capture stopped.")
               ? WDF_CAPTURE_RESULT_OK
               : WDF_CAPTURE_RESULT_INTERNAL_ERROR;
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
    std::lock_guard lock(instance->state_mutex);
    if (instance->state == WDF_CAPTURE_STATE_STOPPED ||
        instance->state == WDF_CAPTURE_STATE_UNAVAILABLE) {
      return WDF_CAPTURE_RESULT_OK;
    }
    static_cast<void>(timeout_ms);
    return WDF_CAPTURE_RESULT_TIMEOUT;
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

    instance->events.Close();
    {
      std::unique_lock lifetime_lock(instance->lifetime_mutex);
      instance->no_active_calls.wait(
          lifetime_lock,
          [&instance] { return instance->active_calls == 0; });
    }
    {
      std::lock_guard state_lock(instance->state_mutex);
      instance->state = WDF_CAPTURE_STATE_STOPPED;
    }
    *handle = 0;
    return WDF_CAPTURE_RESULT_OK;
  } catch (...) {
    return WDF_CAPTURE_RESULT_INTERNAL_ERROR;
  }
}

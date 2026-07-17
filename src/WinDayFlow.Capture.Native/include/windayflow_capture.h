#ifndef WINDAYFLOW_CAPTURE_H_
#define WINDAYFLOW_CAPTURE_H_

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define WDF_CAPTURE_CALL __cdecl
#if defined(WDF_CAPTURE_BUILD)
#define WDF_CAPTURE_API __declspec(dllexport)
#else
#define WDF_CAPTURE_API __declspec(dllimport)
#endif
#else
#define WDF_CAPTURE_CALL
#define WDF_CAPTURE_API
#endif

#if defined(__cplusplus)
#define WDF_CAPTURE_NOEXCEPT noexcept
extern "C" {
#else
#define WDF_CAPTURE_NOEXCEPT
#endif

#define WDF_CAPTURE_ABI_VERSION 1U

typedef int32_t wdf_capture_result;
enum {
  WDF_CAPTURE_RESULT_OK = 0,
  WDF_CAPTURE_RESULT_NO_EVENT = 1,
  WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL = 2,
  WDF_CAPTURE_RESULT_INVALID_ARGUMENT = -1,
  WDF_CAPTURE_RESULT_ABI_MISMATCH = -2,
  WDF_CAPTURE_RESULT_INVALID_STATE = -3,
  WDF_CAPTURE_RESULT_NOT_IMPLEMENTED = -4,
  WDF_CAPTURE_RESULT_TIMEOUT = -5,
  WDF_CAPTURE_RESULT_POLICY_BLOCKED = -6,
  WDF_CAPTURE_RESULT_STALE_POLICY = -7,
  WDF_CAPTURE_RESULT_POLICY_REVISION_CONFLICT = -8,
  WDF_CAPTURE_RESULT_TARGET_MISMATCH = -9,
  WDF_CAPTURE_RESULT_POLICY_REVISION_GAP = -10,
  WDF_CAPTURE_RESULT_GENERATION_EXHAUSTED = -11,
  WDF_CAPTURE_RESULT_ADMISSION_REQUIRED = -12,
  WDF_CAPTURE_RESULT_ADMISSION_REJECTED = -13,
  WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED = -14,
  WDF_CAPTURE_RESULT_INTERNAL_ERROR = -255
};

typedef int32_t wdf_capture_state;
enum {
  WDF_CAPTURE_STATE_UNAVAILABLE = 0,
  WDF_CAPTURE_STATE_STOPPED = 1,
  WDF_CAPTURE_STATE_STARTING = 2,
  WDF_CAPTURE_STATE_RECORDING = 3,
  WDF_CAPTURE_STATE_PAUSING = 4,
  WDF_CAPTURE_STATE_PAUSED = 5,
  WDF_CAPTURE_STATE_RESUMING = 6,
  WDF_CAPTURE_STATE_STOPPING = 7,
  WDF_CAPTURE_STATE_FAULTED = 8,
  WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT = 9
};

typedef int32_t wdf_capture_reason;
enum {
  WDF_CAPTURE_REASON_NONE = 0,
  WDF_CAPTURE_REASON_CONSENT_REQUIRED = 1,
  WDF_CAPTURE_REASON_USER_PAUSED = 2,
  WDF_CAPTURE_REASON_USER_STOPPED = 3,
  WDF_CAPTURE_REASON_EXCLUDED_APPLICATION = 4,
  WDF_CAPTURE_REASON_EXCLUDED_WINDOW = 5,
  WDF_CAPTURE_REASON_SESSION_LOCKED = 6,
  WDF_CAPTURE_REASON_SECURE_DESKTOP = 7,
  WDF_CAPTURE_REASON_REMOTE_SESSION = 8,
  WDF_CAPTURE_REASON_PRESENTATION_MODE = 9,
  WDF_CAPTURE_REASON_SYSTEM_SLEEP = 10,
  WDF_CAPTURE_REASON_DISPLAY_UNAVAILABLE = 11,
  WDF_CAPTURE_REASON_ACCESS_LOST = 12,
  WDF_CAPTURE_REASON_STORAGE_CONSTRAINED = 13,
  WDF_CAPTURE_REASON_POLICY_BLOCKED = 14,
  WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE = 15,
  WDF_CAPTURE_REASON_BACKEND_FAULT = 16,
  WDF_CAPTURE_REASON_SHUTDOWN = 17
};

typedef int32_t wdf_capture_error;
enum {
  WDF_CAPTURE_ERROR_NONE = 0,
  WDF_CAPTURE_ERROR_ABI_VERSION_MISMATCH = 1,
  WDF_CAPTURE_ERROR_INVALID_CONFIGURATION = 2,
  WDF_CAPTURE_ERROR_INVALID_STATE = 3,
  WDF_CAPTURE_ERROR_DEVICE_UNAVAILABLE = 4,
  WDF_CAPTURE_ERROR_ACCESS_LOST = 5,
  WDF_CAPTURE_ERROR_ENCODER_UNAVAILABLE = 6,
  WDF_CAPTURE_ERROR_ENCODER_FAILURE = 7,
  WDF_CAPTURE_ERROR_STORAGE_UNAVAILABLE = 8,
  WDF_CAPTURE_ERROR_STORAGE_FULL = 9,
  WDF_CAPTURE_ERROR_IO_FAILURE = 10,
  WDF_CAPTURE_ERROR_OPERATION_TIMED_OUT = 11,
  WDF_CAPTURE_ERROR_NATIVE_FAILURE = 12,
  WDF_CAPTURE_ERROR_UNKNOWN = 255
};

typedef int32_t wdf_capture_event_kind;
enum {
  WDF_CAPTURE_EVENT_STATE_CHANGED = 1,
  WDF_CAPTURE_EVENT_CHUNK_COMMITTED = 2,
  WDF_CAPTURE_EVENT_ERROR = 3,
  WDF_CAPTURE_EVENT_DIAGNOSTIC = 4
};

typedef int32_t wdf_capture_policy_decision;
enum {
  WDF_CAPTURE_POLICY_UNKNOWN = 0,
  WDF_CAPTURE_POLICY_ALLOW = 1,
  WDF_CAPTURE_POLICY_BLOCK = 2
};

typedef uint64_t wdf_capture_capabilities;
enum {
  WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD = 1ULL << 0,
  WDF_CAPTURE_CAPABILITY_EVENT_QUEUE = 1ULL << 1,
  WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE = 1ULL << 2,
  WDF_CAPTURE_CAPABILITY_H264_CHUNKS = 1ULL << 3,
  WDF_CAPTURE_CAPABILITY_EVIDENCE_EXTRACTION = 1ULL << 4,
  WDF_CAPTURE_CAPABILITY_TARGET_SCOPED_AUTHORIZATION = 1ULL << 5,
  WDF_CAPTURE_CAPABILITY_PERSISTENCE_GENERATION_BARRIER = 1ULL << 6,
  WDF_CAPTURE_CAPABILITY_DETERMINISTIC_STOP = 1ULL << 7,
  WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION = 1ULL << 8,
  WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION = 1ULL << 9,
  WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION = 1ULL << 10,
  WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION = 1ULL << 11
};

typedef int32_t wdf_capture_command;
enum {
  WDF_CAPTURE_COMMAND_START = 1,
  WDF_CAPTURE_COMMAND_RESUME = 2
};

typedef uint32_t wdf_capture_target_flags;
enum {
  WDF_CAPTURE_TARGET_PRESENT = 1U << 0,
  WDF_CAPTURE_TARGET_DISPLAY_PRESENT = 1U << 1
};

#define WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE 112U
#define WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY 96U
#define WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH 93U

#if defined(_MSC_VER) || defined(__clang__) || defined(__GNUC__)
#pragma pack(push, 8)
#endif

typedef struct wdf_capture_config_v1 {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t capture_interval_ms;
  uint32_t context_interval_ms;
  uint32_t chunk_duration_ms;
  uint32_t max_width;
  uint32_t max_height;
  uint32_t event_queue_capacity;
  const char* output_directory_utf8;
  uint32_t output_directory_utf8_length;
  uint32_t reserved[8];
} wdf_capture_config_v1;

typedef struct wdf_capture_privacy_context_v1 {
  uint32_t struct_size;
  uint32_t abi_version;
  wdf_capture_policy_decision consent_granted;
  wdf_capture_policy_decision session_unlocked;
  wdf_capture_policy_decision secure_desktop_clear;
  wdf_capture_policy_decision remote_session_allowed;
  wdf_capture_policy_decision presentation_allowed;
  wdf_capture_policy_decision application_allowed;
  wdf_capture_policy_decision window_allowed;
  wdf_capture_policy_decision storage_available;
  uint64_t policy_revision;
  uint32_t reserved[8];
} wdf_capture_privacy_context_v1;

typedef struct wdf_capture_runtime_authorization_v1 {
  uint32_t struct_size;
  uint32_t abi_version;
  uint64_t runtime_policy_revision;
  uint64_t target_epoch;
  uint64_t target_window_handle;
  uint64_t target_process_creation_time_100ns;
  uint32_t target_process_id;
  wdf_capture_target_flags target_flags;
  wdf_capture_policy_decision consent_granted;
  wdf_capture_policy_decision session_unlocked;
  wdf_capture_policy_decision secure_desktop_clear;
  wdf_capture_policy_decision remote_session_allowed;
  wdf_capture_policy_decision presentation_allowed;
  wdf_capture_policy_decision application_allowed;
  wdf_capture_policy_decision window_allowed;
  wdf_capture_policy_decision storage_available;
  uint32_t reserved[8];
  uint64_t target_display_monitor_handle;
  uint32_t target_display_device_key_utf8_length;
  uint32_t target_display_reserved;
  char target_display_device_key_utf8
      [WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY];
} wdf_capture_runtime_authorization_v1;

typedef struct wdf_capture_command_admission_v1 {
  uint32_t struct_size;
  uint32_t abi_version;
  uint64_t instance_epoch;
  uint64_t runtime_policy_revision;
  uint64_t persistence_generation;
  uint64_t target_epoch;
  uint64_t authorization_epoch;
  uint64_t nonce_low;
  uint64_t nonce_high;
} wdf_capture_command_admission_v1;

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4201)
#endif
typedef struct wdf_capture_event_v1 {
  uint32_t struct_size;
  uint32_t abi_version;
  uint64_t sequence;
  int64_t timestamp_unix_ms;
  wdf_capture_event_kind kind;
  wdf_capture_state state;
  wdf_capture_reason reason;
  wdf_capture_error error;
  uint32_t dropped_before;
  uint32_t detail_utf8_length;
  union {
    struct {
      uint64_t persistence_generation;
      uint64_t target_epoch;
      uint32_t reserved_tail[4];
    };
    uint32_t reserved[8];
  };
} wdf_capture_event_v1;
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

#if defined(_MSC_VER) || defined(__clang__) || defined(__GNUC__)
#pragma pack(pop)
#endif

typedef uintptr_t wdf_capture_handle;

#if UINTPTR_MAX == UINT64_MAX
#if defined(__cplusplus)
static_assert(sizeof(wdf_capture_config_v1) == 80);
static_assert(offsetof(wdf_capture_config_v1, output_directory_utf8) == 32);
static_assert(sizeof(wdf_capture_privacy_context_v1) == 80);
static_assert(offsetof(wdf_capture_privacy_context_v1, policy_revision) == 40);
static_assert(sizeof(wdf_capture_runtime_authorization_v1) == 224);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       runtime_policy_revision) == 8);
static_assert(offsetof(wdf_capture_runtime_authorization_v1, target_epoch) ==
              16);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_window_handle) == 24);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_process_creation_time_100ns) == 32);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_process_id) == 40);
static_assert(offsetof(wdf_capture_runtime_authorization_v1, target_flags) ==
              44);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       consent_granted) == 48);
static_assert(offsetof(wdf_capture_runtime_authorization_v1, reserved) == 80);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_monitor_handle) == 112);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_device_key_utf8_length) == 120);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_reserved) == 124);
static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                       target_display_device_key_utf8) == 128);
static_assert(sizeof(wdf_capture_command_admission_v1) == 64);
static_assert(offsetof(wdf_capture_command_admission_v1, instance_epoch) == 8);
static_assert(offsetof(wdf_capture_command_admission_v1,
                       runtime_policy_revision) == 16);
static_assert(offsetof(wdf_capture_command_admission_v1,
                       persistence_generation) == 24);
static_assert(offsetof(wdf_capture_command_admission_v1, target_epoch) == 32);
static_assert(offsetof(wdf_capture_command_admission_v1,
                       authorization_epoch) == 40);
static_assert(offsetof(wdf_capture_command_admission_v1, nonce_low) == 48);
static_assert(offsetof(wdf_capture_command_admission_v1, nonce_high) == 56);
static_assert(sizeof(wdf_capture_event_v1) == 80);
static_assert(offsetof(wdf_capture_event_v1, sequence) == 8);
static_assert(offsetof(wdf_capture_event_v1, persistence_generation) == 48);
static_assert(offsetof(wdf_capture_event_v1, target_epoch) == 56);
#else
_Static_assert(sizeof(wdf_capture_config_v1) == 80,
               "wdf_capture_config_v1 ABI layout changed");
_Static_assert(offsetof(wdf_capture_config_v1, output_directory_utf8) == 32,
               "wdf_capture_config_v1 path offset changed");
_Static_assert(sizeof(wdf_capture_privacy_context_v1) == 80,
               "wdf_capture_privacy_context_v1 ABI layout changed");
_Static_assert(offsetof(wdf_capture_privacy_context_v1, policy_revision) == 40,
               "wdf_capture_privacy_context_v1 revision offset changed");
_Static_assert(sizeof(wdf_capture_runtime_authorization_v1) == 224,
               "wdf_capture_runtime_authorization_v1 ABI layout changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        runtime_policy_revision) == 8,
               "runtime authorization revision offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1, target_epoch) ==
                   16,
               "runtime authorization target epoch offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_window_handle) == 24,
               "runtime authorization HWND offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_process_creation_time_100ns) == 32,
               "runtime authorization process creation offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_process_id) == 40,
               "runtime authorization PID offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1, target_flags) ==
                   44,
               "runtime authorization flags offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        consent_granted) == 48,
               "runtime authorization decisions offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1, reserved) == 80,
               "runtime authorization reserved offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_display_monitor_handle) == 112,
               "runtime authorization display monitor offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_display_device_key_utf8_length) == 120,
               "runtime authorization display key length offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_display_reserved) == 124,
               "runtime authorization display reserved offset changed");
_Static_assert(offsetof(wdf_capture_runtime_authorization_v1,
                        target_display_device_key_utf8) == 128,
               "runtime authorization display key offset changed");
_Static_assert(sizeof(wdf_capture_command_admission_v1) == 64,
               "wdf_capture_command_admission_v1 ABI layout changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1, instance_epoch) == 8,
               "command admission instance offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1,
                        runtime_policy_revision) == 16,
               "command admission revision offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1,
                        persistence_generation) == 24,
               "command admission generation offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1, target_epoch) == 32,
               "command admission target offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1,
                        authorization_epoch) == 40,
               "command admission authorization offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1, nonce_low) == 48,
               "command admission low nonce offset changed");
_Static_assert(offsetof(wdf_capture_command_admission_v1, nonce_high) == 56,
               "command admission high nonce offset changed");
_Static_assert(sizeof(wdf_capture_event_v1) == 80,
               "wdf_capture_event_v1 ABI layout changed");
_Static_assert(offsetof(wdf_capture_event_v1, sequence) == 8,
               "wdf_capture_event_v1 sequence offset changed");
_Static_assert(offsetof(wdf_capture_event_v1, persistence_generation) == 48,
               "wdf_capture_event_v1 generation offset changed");
_Static_assert(offsetof(wdf_capture_event_v1, target_epoch) == 56,
               "wdf_capture_event_v1 target epoch offset changed");
#endif
#endif

WDF_CAPTURE_API uint32_t WDF_CAPTURE_CALL
wdf_capture_get_abi_version(void) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_get_capabilities(
    wdf_capture_capabilities* capabilities) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL wdf_capture_create(
    const wdf_capture_config_v1* config,
    wdf_capture_handle* handle) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_update_privacy_context(
    wdf_capture_handle handle,
    const wdf_capture_privacy_context_v1* context) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_update_runtime_authorization(
    wdf_capture_handle handle,
    const wdf_capture_runtime_authorization_v1* context,
    uint64_t* persistence_generation) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_invalidate_runtime_authorization(
    wdf_capture_handle handle,
    uint64_t* authorization_epoch) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_revoke_runtime_authorization(
    wdf_capture_handle handle,
    uint64_t* persistence_generation) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_issue_command_admission(
    wdf_capture_handle handle,
    wdf_capture_command command,
    uint64_t expected_persistence_generation,
    uint64_t expected_target_epoch,
    wdf_capture_command_admission_v1* admission) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_start(wdf_capture_handle handle) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_start_authorized(
    wdf_capture_handle handle,
    const wdf_capture_command_admission_v1* admission) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_pause(wdf_capture_handle handle) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_resume(wdf_capture_handle handle) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_resume_authorized(
    wdf_capture_handle handle,
    const wdf_capture_command_admission_v1* admission) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_request_stop(wdf_capture_handle handle) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL wdf_capture_wait_stopped(
    wdf_capture_handle handle,
    uint32_t timeout_ms) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL wdf_capture_poll_event(
    wdf_capture_handle handle,
    uint32_t timeout_ms,
    wdf_capture_event_v1* event,
    char* detail_utf8,
    uint32_t detail_utf8_capacity,
    uint32_t* detail_utf8_required) WDF_CAPTURE_NOEXCEPT;

WDF_CAPTURE_API wdf_capture_result WDF_CAPTURE_CALL
wdf_capture_destroy(wdf_capture_handle* handle) WDF_CAPTURE_NOEXCEPT;

#if defined(__cplusplus)
}
#endif

#endif  // WINDAYFLOW_CAPTURE_H_

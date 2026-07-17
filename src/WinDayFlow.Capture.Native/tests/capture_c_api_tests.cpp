#include "windayflow_capture.h"

#include <Windows.h>

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <limits>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

wdf_capture_config_v1 ValidConfig() {
  static constexpr char kOutputPath[] = "C:\\WinDayFlow-Test-Evidence";
  wdf_capture_config_v1 config{};
  config.struct_size = sizeof(config);
  config.abi_version = WDF_CAPTURE_ABI_VERSION;
  config.capture_interval_ms = 10'000;
  config.context_interval_ms = 1'000;
  config.chunk_duration_ms = 60'000;
  config.max_width = 1'920;
  config.max_height = 1'080;
  config.event_queue_capacity = 16;
  config.output_directory_utf8 = kOutputPath;
  config.output_directory_utf8_length = sizeof(kOutputPath) - 1U;
  return config;
}

wdf_capture_privacy_context_v1 PrivacyContext(
    wdf_capture_policy_decision consent,
    uint64_t revision = 1) {
  wdf_capture_privacy_context_v1 context{};
  context.struct_size = sizeof(context);
  context.abi_version = WDF_CAPTURE_ABI_VERSION;
  context.consent_granted = consent;
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

wdf_capture_runtime_authorization_v1 RuntimeAuthorization(
    wdf_capture_policy_decision consent,
    uint64_t revision,
    uint64_t target_epoch = 1,
    uint64_t window_handle = 100,
    uint32_t process_id = 200,
    uint64_t creation_time = 300,
    uint64_t display_monitor_handle = 400,
    std::string_view display_device_key = "\\\\.\\DISPLAY1") {
  wdf_capture_runtime_authorization_v1 context{};
  context.struct_size = sizeof(context);
  context.abi_version = WDF_CAPTURE_ABI_VERSION;
  context.runtime_policy_revision = revision;
  context.consent_granted = consent;
  context.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
  context.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
  context.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
  context.storage_available = WDF_CAPTURE_POLICY_ALLOW;
  if (consent == WDF_CAPTURE_POLICY_ALLOW) {
    context.target_epoch = target_epoch;
    context.target_window_handle = window_handle;
    context.target_process_creation_time_100ns = creation_time;
    context.target_process_id = process_id;
    context.target_flags = WDF_CAPTURE_TARGET_PRESENT |
                           WDF_CAPTURE_TARGET_DISPLAY_PRESENT;
    context.target_display_monitor_handle = display_monitor_handle;
    context.target_display_device_key_utf8_length =
        static_cast<uint32_t>(display_device_key.size());
    std::memcpy(context.target_display_device_key_utf8,
                display_device_key.data(),
                display_device_key.size());
  }
  return context;
}

wdf_capture_command_admission_v1 EmptyAdmission() {
  wdf_capture_command_admission_v1 admission{};
  admission.struct_size = sizeof(admission);
  admission.abi_version = WDF_CAPTURE_ABI_VERSION;
  return admission;
}

bool PollEvent(wdf_capture_handle handle,
               wdf_capture_event_v1* event,
               std::string* detail) {
  event->struct_size = sizeof(*event);
  event->abi_version = WDF_CAPTURE_ABI_VERSION;
  uint32_t required = 0;
  const wdf_capture_result sizing =
      wdf_capture_poll_event(handle, 0, event, nullptr, 0, &required);
  if (!Expect(sizing == WDF_CAPTURE_RESULT_BUFFER_TOO_SMALL && required > 1,
              "event sizing did not report the required caller buffer")) {
    return false;
  }
  const uint64_t sized_sequence = event->sequence;
  std::vector<char> buffer(required);
  event->struct_size = sizeof(*event);
  event->abi_version = WDF_CAPTURE_ABI_VERSION;
  const wdf_capture_result read = wdf_capture_poll_event(
      handle,
      0,
      event,
      buffer.data(),
      static_cast<uint32_t>(buffer.size()),
      &required);
  if (!Expect(read == WDF_CAPTURE_RESULT_OK,
              "event could not be read after sizing")) {
    return false;
  }
  *detail = buffer.data();
  return Expect(event->sequence == sized_sequence,
                "buffer sizing consumed or changed the event") &&
         Expect(event->detail_utf8_length == detail->size(),
                "event detail byte length was incorrect");
}

bool TestAbiAndArgumentValidation() {
  if (!Expect(wdf_capture_get_abi_version() == WDF_CAPTURE_ABI_VERSION,
              "ABI version query was incorrect")) {
    return false;
  }
  wdf_capture_capabilities capabilities = 0;
  if (!Expect(wdf_capture_get_capabilities(&capabilities) ==
                  WDF_CAPTURE_RESULT_OK &&
                  (capabilities & WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD) != 0 &&
                  (capabilities & WDF_CAPTURE_CAPABILITY_EVENT_QUEUE) != 0 &&
                  (capabilities &
                   WDF_CAPTURE_CAPABILITY_TARGET_SCOPED_AUTHORIZATION) != 0 &&
                  (capabilities &
                   WDF_CAPTURE_CAPABILITY_PERSISTENCE_GENERATION_BARRIER) !=
                      0 &&
                   (capabilities & WDF_CAPTURE_CAPABILITY_DETERMINISTIC_STOP) !=
                       0 &&
                   (capabilities & WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION) ==
                       0 &&
                   (capabilities &
                    WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION) !=
                       0 &&
                    (capabilities &
                     WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION) !=
                        0 &&
                   (capabilities &
                    WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION) !=
                       0 &&
                   (capabilities & WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE) == 0 &&
                  (capabilities & WDF_CAPTURE_CAPABILITY_H264_CHUNKS) == 0 &&
                  (capabilities &
                   WDF_CAPTURE_CAPABILITY_EVIDENCE_EXTRACTION) == 0,
              "foundation capabilities were incorrect")) {
    return false;
  }

  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  config.abi_version += 1U;
  if (!Expect(wdf_capture_create(&config, &handle) ==
                  WDF_CAPTURE_RESULT_ABI_MISMATCH &&
                  handle == 0,
              "incompatible config ABI was accepted")) {
    return false;
  }
  config = ValidConfig();
  config.output_directory_utf8_length = 0;
  if (!Expect(wdf_capture_create(&config, &handle) ==
                      WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                  handle == 0,
              "empty output path was accepted")) {
    return false;
  }

  constexpr std::array<char, 8> kEmbeddedNullPath{'D',  ':', '\\', 'd',
                                                  '\0', 'e', 'v',  'x'};
  config = ValidConfig();
  config.output_directory_utf8 = kEmbeddedNullPath.data();
  config.output_directory_utf8_length =
      static_cast<uint32_t>(kEmbeddedNullPath.size());
  if (!Expect(wdf_capture_create(&config, &handle) ==
                      WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                  handle == 0,
              "output path with an embedded NUL was accepted")) {
    return false;
  }

  constexpr char kRelativePath[] = "relative\\evidence";
  constexpr std::array<char, 2> kMalformedUtf8{
      static_cast<char>(0xC3),
      static_cast<char>(0x28),
  };
  constexpr char kUncPath[] = "\\\\server\\share\\evidence";
  std::string missing_drive_path;
  for (int drive = 'Z'; drive >= 'A'; --drive) {
    const std::array<wchar_t, 4> root{
        static_cast<wchar_t>(drive), L':', L'\\', L'\0'};
    if (GetDriveTypeW(root.data()) == DRIVE_NO_ROOT_DIR) {
      missing_drive_path =
          std::string{static_cast<char>(drive), ':', '\\'} + "evidence";
      break;
    }
  }
  if (!Expect(!missing_drive_path.empty(),
              "test host has no missing drive letter")) {
    return false;
  }

  const auto RejectOutputPath = [&](const char* path,
                                    uint32_t path_length,
                                    const char* message) {
    config = ValidConfig();
    config.output_directory_utf8 = path;
    config.output_directory_utf8_length = path_length;
    handle = 0;
    return Expect(wdf_capture_create(&config, &handle) ==
                          WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                      handle == 0,
                  message);
  };
  return RejectOutputPath(kRelativePath,
                          sizeof(kRelativePath) - 1U,
                          "relative output path was accepted") &&
         RejectOutputPath(kMalformedUtf8.data(),
                          static_cast<uint32_t>(kMalformedUtf8.size()),
                          "malformed UTF-8 output path was accepted") &&
         RejectOutputPath(kUncPath,
                          sizeof(kUncPath) - 1U,
                          "UNC output path was accepted") &&
         RejectOutputPath(missing_drive_path.data(),
                          static_cast<uint32_t>(missing_drive_path.size()),
                          "missing-drive output path was accepted");
}

bool TestTrulyShortVersionedStructures() {
  alignas(8) std::array<std::byte, sizeof(uint32_t)> short_storage{};
  const uint32_t short_size = sizeof(uint32_t);
  std::memcpy(short_storage.data(), &short_size, sizeof(short_size));

  wdf_capture_handle handle = 0;
  if (!Expect(wdf_capture_create(
                  reinterpret_cast<const wdf_capture_config_v1*>(
                      short_storage.data()),
                  &handle) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
              "four-byte config header was read beyond its declared size")) {
    return false;
  }

  wdf_capture_config_v1 config = ValidConfig();
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "short-structure test handle could not be created");
  }
  uint32_t required = 0;
  const bool event_rejected = Expect(
      wdf_capture_poll_event(
          handle,
          0,
          reinterpret_cast<wdf_capture_event_v1*>(short_storage.data()),
          nullptr,
          0,
          &required) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "four-byte event header was read beyond its declared size");
  const bool privacy_rejected = Expect(
      wdf_capture_update_privacy_context(
          handle,
          reinterpret_cast<const wdf_capture_privacy_context_v1*>(
              short_storage.data())) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "four-byte privacy header was read beyond its declared size");
  uint64_t generation = 0;
  const bool runtime_authorization_rejected = Expect(
      wdf_capture_update_runtime_authorization(
          handle,
          reinterpret_cast<const wdf_capture_runtime_authorization_v1*>(
              short_storage.data()),
          &generation) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "four-byte runtime authorization header was read beyond its declared size");
  const bool command_issue_rejected = Expect(
      wdf_capture_issue_command_admission(
          handle,
          WDF_CAPTURE_COMMAND_START,
          1,
          1,
          reinterpret_cast<wdf_capture_command_admission_v1*>(
              short_storage.data())) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "four-byte command admission output was read beyond its declared size");
  const bool command_consume_rejected = Expect(
      wdf_capture_start_authorized(
          handle,
          reinterpret_cast<const wdf_capture_command_admission_v1*>(
              short_storage.data())) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "four-byte command admission input was read beyond its declared size");
  wdf_capture_destroy(&handle);
  return event_rejected && privacy_rejected &&
         runtime_authorization_rejected && command_issue_rejected &&
         command_consume_rejected;
}

bool TestLifecycleIsPrivacyGatedAndUnavailable() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (!Expect(wdf_capture_create(&config, &handle) == WDF_CAPTURE_RESULT_OK &&
                  handle != 0,
              "valid capture handle could not be created")) {
    return false;
  }

  wdf_capture_event_v1 event{};
  std::string detail;
  if (!PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 1 &&
                  event.state == WDF_CAPTURE_STATE_UNAVAILABLE &&
                  event.reason == WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE &&
                  event.persistence_generation == 1 &&
                  event.target_epoch == 0,
              "initial unavailable event was incorrect")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_privacy_context_v1 blocked =
      PrivacyContext(WDF_CAPTURE_POLICY_BLOCK, 1);
  if (!Expect(wdf_capture_update_privacy_context(handle, &blocked) ==
                  WDF_CAPTURE_RESULT_OK &&
                  wdf_capture_start(handle) ==
                      WDF_CAPTURE_RESULT_ADMISSION_REQUIRED &&
                  wdf_capture_resume(handle) ==
                      WDF_CAPTURE_RESULT_ADMISSION_REQUIRED,
              "tokenless lifecycle command bypassed admission")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_privacy_context_v1 allowed =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 2);
  if (!Expect(wdf_capture_update_privacy_context(handle, &allowed) ==
                  WDF_CAPTURE_RESULT_OK &&
                  wdf_capture_resume(handle) ==
                      WDF_CAPTURE_RESULT_ADMISSION_REQUIRED,
              "legacy allow bypassed command admission")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_privacy_context_v1 legacy_during_stop =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 3);
  wdf_capture_runtime_authorization_v1 runtime_during_stop =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 3);
  uint64_t stop_generation = 0;
  if (!Expect(wdf_capture_request_stop(handle) == WDF_CAPTURE_RESULT_OK,
              "stop request failed") ||
      !Expect(wdf_capture_update_privacy_context(
                  handle, &legacy_during_stop) ==
                  WDF_CAPTURE_RESULT_INVALID_STATE,
              "legacy authorization reopened STOPPING") ||
      !Expect(wdf_capture_update_runtime_authorization(
                  handle, &runtime_during_stop, &stop_generation) ==
                  WDF_CAPTURE_RESULT_INVALID_STATE,
              "target authorization reopened STOPPING") ||
      !Expect(wdf_capture_wait_stopped(handle, 0) == WDF_CAPTURE_RESULT_OK,
              "stop wait failed") ||
      !PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 2 &&
                  event.state == WDF_CAPTURE_STATE_STOPPING &&
                  event.reason == WDF_CAPTURE_REASON_USER_STOPPED,
              "stopping event was incorrect") ||
      !PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 3 &&
                  event.state == WDF_CAPTURE_STATE_STOPPED &&
                  event.reason == WDF_CAPTURE_REASON_USER_STOPPED &&
                  event.persistence_generation == 3,
              "joined stopped event was incorrect")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  const wdf_capture_handle stale = handle;
  return Expect(wdf_capture_destroy(&handle) == WDF_CAPTURE_RESULT_OK &&
                    handle == 0,
                "destroy did not invalidate the caller handle") &&
         Expect(wdf_capture_request_stop(stale) ==
                    WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
                "stale handle was accepted") &&
         Expect(wdf_capture_destroy(&handle) == WDF_CAPTURE_RESULT_OK,
                "repeated destroy of a zero handle was not idempotent");
}

bool TestPrivacyRevisionNeverRegresses() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "privacy revision handle could not be created");
  }

  wdf_capture_privacy_context_v1 allow_v1 =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 1);
  wdf_capture_privacy_context_v1 block_v2 =
      PrivacyContext(WDF_CAPTURE_POLICY_BLOCK, 2);
  wdf_capture_privacy_context_v1 conflicting_v2 =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 2);
  const bool valid =
      Expect(wdf_capture_update_privacy_context(handle, &allow_v1) ==
                     WDF_CAPTURE_RESULT_OK &&
                 wdf_capture_update_privacy_context(handle, &block_v2) ==
                     WDF_CAPTURE_RESULT_OK,
             "increasing policy revisions were rejected") &&
      Expect(wdf_capture_update_privacy_context(handle, &allow_v1) ==
                 WDF_CAPTURE_RESULT_STALE_POLICY,
             "stale allow policy replaced a newer block") &&
      Expect(wdf_capture_update_privacy_context(handle, &conflicting_v2) ==
                 WDF_CAPTURE_RESULT_POLICY_REVISION_CONFLICT,
             "same-revision conflicting policy was accepted") &&
      Expect(wdf_capture_update_privacy_context(handle, &block_v2) ==
                 WDF_CAPTURE_RESULT_OK,
             "same-revision idempotent policy was rejected") &&
      Expect(wdf_capture_start(handle) ==
                 WDF_CAPTURE_RESULT_ADMISSION_REQUIRED,
             "tokenless start bypassed command admission");
  wdf_capture_destroy(&handle);
  return valid;
}

bool TestRuntimeAuthorizationDisplayStructureContract() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "display authorization handle could not be created");
  }

  uint64_t generation = 99;
  const auto Reject = [&](wdf_capture_runtime_authorization_v1* value,
                          const char* message) {
    generation = 99;
    return Expect(wdf_capture_update_runtime_authorization(
                      handle, value, &generation) ==
                          WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                      generation == 0,
                  message);
  };

  wdf_capture_runtime_authorization_v1 invalid =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.struct_size =
      WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE + 1U;
  bool valid = Reject(&invalid, "a partial display tail was accepted");

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.struct_size = sizeof(invalid) - 1U;
  valid = Reject(&invalid, "a nearly complete display tail was accepted") &&
          valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.struct_size = WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE;
  invalid.target_flags = WDF_CAPTURE_TARGET_PRESENT;
  valid = Reject(&invalid, "legacy target-only allow was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_flags = WDF_CAPTURE_TARGET_PRESENT;
  valid = Reject(&invalid, "allow without the display-present flag was accepted") &&
          valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_monitor_handle = 0;
  valid = Reject(&invalid, "allow without a display monitor was accepted") &&
          valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length = 0;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  valid = Reject(&invalid, "allow without a display key was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length = 1;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  invalid.target_display_device_key_utf8[0] = static_cast<char>(0xC0);
  valid = Reject(&invalid, "invalid display-key UTF-8 was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length = 1;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  invalid.target_display_device_key_utf8[0] = '\x01';
  valid = Reject(&invalid, "control display-key text was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length = 3;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  std::memset(invalid.target_display_device_key_utf8, ' ', 3);
  valid = Reject(&invalid, "whitespace-only display key was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length = 3;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  invalid.target_display_device_key_utf8[0] = 'A';
  invalid.target_display_device_key_utf8[2] = 'B';
  valid = Reject(&invalid, "embedded display-key NUL was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8
      [invalid.target_display_device_key_utf8_length] = 'X';
  valid = Reject(&invalid, "nonzero display-key buffer tail was accepted") &&
          valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_device_key_utf8_length =
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH + 1U;
  std::memset(invalid.target_display_device_key_utf8,
              'A',
              invalid.target_display_device_key_utf8_length);
  valid = Reject(&invalid, "oversized display key was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_display_reserved = 1;
  valid = Reject(&invalid, "nonzero display reserved data was accepted") && valid;

  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_BLOCK, 1);
  invalid.target_display_monitor_handle = 400;
  valid = Reject(&invalid, "restrictive authorization retained display data") &&
          valid;

  wdf_capture_runtime_authorization_v1 legacy_block =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_BLOCK, 1);
  legacy_block.struct_size = WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE;
  generation = 0;
  valid = Expect(wdf_capture_update_runtime_authorization(
                     handle, &legacy_block, &generation) ==
                         WDF_CAPTURE_RESULT_OK &&
                     generation == 2,
                 "legacy restrictive authorization was rejected") &&
          valid;

  wdf_capture_runtime_authorization_v1 current_allow =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2);
  current_allow.target_display_device_key_utf8_length =
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH;
  std::memset(current_allow.target_display_device_key_utf8,
              0,
              sizeof(current_allow.target_display_device_key_utf8));
  for (size_t index = 0; index < 31; ++index) {
    const size_t offset = index * 3;
    current_allow.target_display_device_key_utf8[offset] =
        static_cast<char>(0xE0);
    current_allow.target_display_device_key_utf8[offset + 1] =
        static_cast<char>(0xA0);
    current_allow.target_display_device_key_utf8[offset + 2] =
        static_cast<char>(0x80);
  }
  valid = Expect(wdf_capture_update_runtime_authorization(
                     handle, &current_allow, &generation) ==
                         WDF_CAPTURE_RESULT_OK &&
                     generation == 3,
                 "maximum display key after legacy block was rejected") &&
          valid;

  wdf_capture_destroy(&handle);
  return valid;
}

bool TestRuntimeAuthorizationBarrierContract() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "runtime authorization handle could not be created");
  }

  uint64_t generation = 99;
  wdf_capture_runtime_authorization_v1 invalid =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_flags |= 1U << 7;
  const bool unknown_flags_rejected = Expect(
      wdf_capture_update_runtime_authorization(
          handle, &invalid, &generation) ==
              WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
          generation == 0,
      "unknown target flags were accepted");
  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.reserved[0] = 1;
  const bool reserved_rejected = Expect(
      wdf_capture_update_runtime_authorization(
          handle, &invalid, &generation) ==
          WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "nonzero runtime authorization reserved data was accepted");
  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  invalid.target_flags = 0;
  invalid.target_epoch = 0;
  invalid.target_window_handle = 0;
  invalid.target_process_creation_time_100ns = 0;
  invalid.target_process_id = 0;
  invalid.target_display_monitor_handle = 0;
  invalid.target_display_device_key_utf8_length = 0;
  std::memset(invalid.target_display_device_key_utf8,
              0,
              sizeof(invalid.target_display_device_key_utf8));
  const bool allow_without_target_rejected = Expect(
      wdf_capture_update_runtime_authorization(
          handle, &invalid, &generation) ==
          WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "fully allowed authorization omitted its target");
  invalid = RuntimeAuthorization(WDF_CAPTURE_POLICY_BLOCK, 1);
  invalid.target_flags = WDF_CAPTURE_TARGET_PRESENT;
  invalid.target_epoch = 1;
  invalid.target_window_handle = 100;
  invalid.target_process_creation_time_100ns = 300;
  invalid.target_process_id = 200;
  const bool block_with_target_rejected = Expect(
      wdf_capture_update_runtime_authorization(
          handle, &invalid, &generation) ==
          WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "restrictive authorization retained a target");

  wdf_capture_runtime_authorization_v1 allow_v1 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  wdf_capture_runtime_authorization_v1 conflict_v1 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_BLOCK, 1);
  wdf_capture_runtime_authorization_v1 gap_v3 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 3);
  wdf_capture_runtime_authorization_v1 reused_epoch_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 1, 101);
  wdf_capture_runtime_authorization_v1 reused_display_epoch_v2 =
      RuntimeAuthorization(
          WDF_CAPTURE_POLICY_ALLOW, 2, 1, 100, 200, 300, 401);
  wdf_capture_runtime_authorization_v1 allow_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 2, 101);
  wdf_capture_runtime_authorization_v1 case_only_allow_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW,
                           2,
                           2,
                           101,
                           200,
                           300,
                           400,
                           "\\\\.\\display1");

  const bool revision_and_target_rules =
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v1, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 2,
             "initial runtime authorization was rejected") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v1, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 2,
             "idempotent runtime authorization advanced generation") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &conflict_v1, &generation) ==
                 WDF_CAPTURE_RESULT_POLICY_REVISION_CONFLICT,
             "runtime revision conflict was accepted") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &gap_v3, &generation) ==
                 WDF_CAPTURE_RESULT_POLICY_REVISION_GAP,
             "runtime revision gap was accepted") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &reused_display_epoch_v2, &generation) ==
                 WDF_CAPTURE_RESULT_TARGET_MISMATCH,
             "display tuple changed without an epoch advance") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &reused_epoch_v2, &generation) ==
                 WDF_CAPTURE_RESULT_TARGET_MISMATCH,
             "target tuple changed without an epoch advance") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v2, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 3,
             "epoch-advanced target was rejected") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &case_only_allow_v2, &generation) ==
                     WDF_CAPTURE_RESULT_OK &&
                 generation == 3,
             "case-only display key change was not idempotent") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v1, &generation) ==
                 WDF_CAPTURE_RESULT_STALE_POLICY,
             "stale runtime authorization was accepted") &&
      Expect(wdf_capture_revoke_runtime_authorization(
                 handle, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 4,
             "runtime authorization was not revoked") &&
      Expect(wdf_capture_revoke_runtime_authorization(
                 handle, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 4,
             "idempotent runtime revoke advanced generation") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v1, nullptr) ==
                 WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
             "runtime authorization accepted a null generation output");

  wdf_capture_destroy(&handle);
  return unknown_flags_rejected && reserved_rejected &&
         allow_without_target_rejected && block_with_target_rejected &&
         revision_and_target_rules;
}

bool TestCallbackTimeAuthorizationInvalidationContract() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "callback invalidation handle could not be created");
  }

  uint64_t authorization_epoch = 99;
  if (!Expect(wdf_capture_invalidate_runtime_authorization(handle, nullptr) ==
                  WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
              "callback invalidation accepted a null epoch output") ||
      !Expect(wdf_capture_invalidate_runtime_authorization(
                  std::numeric_limits<wdf_capture_handle>::max(),
                  &authorization_epoch) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                  authorization_epoch == 0,
              "callback invalidation accepted an invalid handle")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_runtime_authorization_v1 allow_v1 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  uint64_t generation = 0;
  wdf_capture_command_admission_v1 admission = EmptyAdmission();
  if (wdf_capture_update_runtime_authorization(
          handle, &allow_v1, &generation) != WDF_CAPTURE_RESULT_OK ||
      wdf_capture_issue_command_admission(
          handle,
          WDF_CAPTURE_COMMAND_START,
          generation,
          allow_v1.target_epoch,
          &admission) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "callback invalidation setup failed");
  }

  uint64_t first_epoch = 0;
  uint64_t second_epoch = 0;
  wdf_capture_command_admission_v1 rejected_admission = EmptyAdmission();
  wdf_capture_runtime_authorization_v1 allow_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 2, 101);
  wdf_capture_runtime_authorization_v1 block_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_BLOCK, 2);
  wdf_capture_runtime_authorization_v1 allow_v3 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 3, 2, 101);
  const bool contract =
      Expect(wdf_capture_invalidate_runtime_authorization(
                 handle, &first_epoch) == WDF_CAPTURE_RESULT_OK &&
                 first_epoch != 0 && (first_epoch & 1U) == 0,
             "callback invalidation did not return a closed epoch") &&
      Expect(wdf_capture_start_authorized(handle, &admission) ==
                 WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
             "callback invalidation admitted an issued command") &&
      Expect(wdf_capture_issue_command_admission(
                 handle,
                 WDF_CAPTURE_COMMAND_START,
                 generation,
                 allow_v1.target_epoch,
                 &rejected_admission) ==
                 WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
             "callback invalidation issued a new command") &&
      Expect(wdf_capture_invalidate_runtime_authorization(
                 handle, &second_epoch) == WDF_CAPTURE_RESULT_OK &&
                 second_epoch > first_epoch && (second_epoch & 1U) == 0,
             "repeated callback invalidation did not advance the epoch") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v2, &generation) ==
                 WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED &&
                 generation == 2,
             "Allow entered native before the callback barrier") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &block_v2, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 3,
             "blocked callback barrier was rejected") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v3, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 4,
             "resolved Allow did not follow the callback barrier");

  const wdf_capture_handle raw_handle = handle;
  const bool destroyed =
      Expect(wdf_capture_destroy(&handle) == WDF_CAPTURE_RESULT_OK && handle == 0,
             "callback invalidation handle could not be destroyed") &&
      Expect(wdf_capture_invalidate_runtime_authorization(
                 raw_handle, &authorization_epoch) ==
                     WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                 authorization_epoch == 0,
             "callback invalidation accepted a destroyed handle");
  return contract && destroyed;
}

bool TestCommandAdmissionAuthenticityAndOwnership() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle first = 0;
  wdf_capture_handle second = 0;
  if (wdf_capture_create(&config, &first) != WDF_CAPTURE_RESULT_OK ||
      wdf_capture_create(&config, &second) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "command admission handles could not be created");
  }
  wdf_capture_runtime_authorization_v1 authorization =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  uint64_t first_generation = 0;
  uint64_t second_generation = 0;
  if (wdf_capture_update_runtime_authorization(
          first, &authorization, &first_generation) != WDF_CAPTURE_RESULT_OK ||
      wdf_capture_update_runtime_authorization(
          second, &authorization, &second_generation) !=
          WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "command admission handles could not be authorized");
  }

  wdf_capture_command_admission_v1 admission = EmptyAdmission();
  if (!Expect(wdf_capture_issue_command_admission(
                  first,
                  99,
                  first_generation,
                  authorization.target_epoch,
                  &admission) == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
              "unknown command admission action was accepted") ||
      !Expect(admission.struct_size == sizeof(admission) &&
                  admission.instance_epoch == 0,
              "failed command issue exposed admission data") ||
      !Expect(wdf_capture_start(first) ==
                      WDF_CAPTURE_RESULT_ADMISSION_REQUIRED &&
                  wdf_capture_resume(first) ==
                      WDF_CAPTURE_RESULT_ADMISSION_REQUIRED,
              "legacy lifecycle exports bypassed command admission")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  admission = EmptyAdmission();
  if (!Expect(wdf_capture_issue_command_admission(
                  first,
                  WDF_CAPTURE_COMMAND_START,
                  first_generation,
                  authorization.target_epoch,
                  &admission) == WDF_CAPTURE_RESULT_OK,
              "valid start admission was not issued") ||
      !Expect(admission.instance_epoch != 0 &&
                  admission.runtime_policy_revision == 1 &&
                  admission.persistence_generation == first_generation &&
                  admission.target_epoch == authorization.target_epoch &&
                  (admission.authorization_epoch & 1U) != 0 &&
                  (admission.nonce_low != 0 || admission.nonce_high != 0),
              "issued C admission snapshot was incomplete")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  wdf_capture_command_admission_v1 forged = admission;
  forged.nonce_high ^= 1U;
  if (!Expect(wdf_capture_start_authorized(first, &forged) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "forged C admission nonce was accepted") ||
      !Expect(wdf_capture_start_authorized(first, &admission) ==
                  WDF_CAPTURE_RESULT_NOT_IMPLEMENTED,
              "foreign nonce attempt consumed valid C admission") ||
      !Expect(wdf_capture_start_authorized(first, &admission) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "C admission was replayed")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  wdf_capture_command_admission_v1 tamper_source = EmptyAdmission();
  if (wdf_capture_issue_command_admission(first,
                                          WDF_CAPTURE_COMMAND_START,
                                          first_generation,
                                          authorization.target_epoch,
                                          &tamper_source) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "tamper C admission was not issued");
  }
  wdf_capture_command_admission_v1 tampered = tamper_source;
  ++tampered.persistence_generation;
  if (!Expect(wdf_capture_start_authorized(first, &tampered) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "tampered C admission fields were accepted") ||
      !Expect(wdf_capture_start_authorized(first, &tamper_source) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "matching-nonce C tamper did not consume admission")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  wdf_capture_command_admission_v1 overwritten = EmptyAdmission();
  wdf_capture_command_admission_v1 replacement = EmptyAdmission();
  if (wdf_capture_issue_command_admission(first,
                                          WDF_CAPTURE_COMMAND_START,
                                          first_generation,
                                          authorization.target_epoch,
                                          &overwritten) !=
          WDF_CAPTURE_RESULT_OK ||
      wdf_capture_issue_command_admission(first,
                                          WDF_CAPTURE_COMMAND_START,
                                          first_generation,
                                          authorization.target_epoch,
                                          &replacement) !=
          WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "replacement C admission was not issued");
  }
  if (!Expect(wdf_capture_start_authorized(first, &overwritten) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "overwritten C admission was accepted") ||
      !Expect(wdf_capture_start_authorized(first, &replacement) ==
                  WDF_CAPTURE_RESULT_NOT_IMPLEMENTED,
              "old C admission attempt consumed replacement")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  wdf_capture_command_admission_v1 wrong_action = EmptyAdmission();
  if (wdf_capture_issue_command_admission(first,
                                          WDF_CAPTURE_COMMAND_START,
                                          first_generation,
                                          authorization.target_epoch,
                                          &wrong_action) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "wrong-action C admission was not issued");
  }
  if (!Expect(wdf_capture_resume_authorized(first, &wrong_action) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "start admission was accepted by resume") ||
      !Expect(wdf_capture_start_authorized(first, &wrong_action) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "wrong-action C attempt did not consume admission")) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return false;
  }

  wdf_capture_command_admission_v1 local_only = EmptyAdmission();
  if (wdf_capture_issue_command_admission(first,
                                          WDF_CAPTURE_COMMAND_START,
                                          first_generation,
                                          authorization.target_epoch,
                                          &local_only) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&first);
    wdf_capture_destroy(&second);
    return Expect(false, "local-only C admission was not issued");
  }
  const bool valid =
      Expect(wdf_capture_start_authorized(second, &local_only) ==
                 WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
             "C admission crossed capture handles") &&
      Expect(wdf_capture_start_authorized(first, &local_only) ==
                 WDF_CAPTURE_RESULT_NOT_IMPLEMENTED,
             "foreign handle attempt consumed local C admission");
  wdf_capture_destroy(&first);
  wdf_capture_destroy(&second);
  return valid;
}

bool TestCommandAdmissionInvalidationAndConcurrency() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "command invalidation handle could not be created");
  }
  wdf_capture_runtime_authorization_v1 target_a =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1, 1, 100);
  uint64_t generation = 0;
  if (wdf_capture_update_runtime_authorization(
          handle, &target_a, &generation) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "command invalidation handle was not authorized");
  }

  wdf_capture_command_admission_v1 idempotent_stale = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_a.target_epoch,
                                          &idempotent_stale) !=
          WDF_CAPTURE_RESULT_OK ||
      wdf_capture_update_runtime_authorization(
          handle, &target_a, &generation) != WDF_CAPTURE_RESULT_OK ||
      generation != 2) {
    wdf_capture_destroy(&handle);
    return Expect(false, "idempotent C authorization setup failed");
  }
  if (!Expect(wdf_capture_start_authorized(handle, &idempotent_stale) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "idempotent C close/reopen revived admission")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_command_admission_v1 stale_a = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_a.target_epoch,
                                          &stale_a) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "A admission was not issued");
  }
  wdf_capture_runtime_authorization_v1 target_b =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 2, 101);
  if (wdf_capture_update_runtime_authorization(
          handle, &target_b, &generation) != WDF_CAPTURE_RESULT_OK ||
      generation != 3 ||
      !Expect(wdf_capture_start_authorized(handle, &stale_a) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "A admission started after B authorization")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_command_admission_v1 prior = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_b.target_epoch,
                                          &prior) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "expected-pair C admission was not issued");
  }
  wdf_capture_command_admission_v1 mismatch = EmptyAdmission();
  if (!Expect(wdf_capture_issue_command_admission(handle,
                                                  WDF_CAPTURE_COMMAND_START,
                                                  generation + 1,
                                                  target_b.target_epoch,
                                                  &mismatch) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED &&
                  mismatch.instance_epoch == 0,
              "mismatched C expected generation was accepted") ||
      !Expect(wdf_capture_start_authorized(handle, &prior) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "failed C issue did not invalidate prior admission") ||
      !Expect(wdf_capture_issue_command_admission(handle,
                                                  WDF_CAPTURE_COMMAND_START,
                                                  generation,
                                                  target_b.target_epoch + 1,
                                                  &mismatch) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "mismatched C expected target was accepted")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_command_admission_v1 concurrent = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_b.target_epoch,
                                          &concurrent) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "concurrent C admission was not issued");
  }
  std::array<std::atomic<wdf_capture_result>, 2> results{
      WDF_CAPTURE_RESULT_INTERNAL_ERROR,
      WDF_CAPTURE_RESULT_INTERNAL_ERROR,
  };
  std::thread first([&] {
    results[0].store(wdf_capture_start_authorized(handle, &concurrent));
  });
  std::thread second([&] {
    results[1].store(wdf_capture_start_authorized(handle, &concurrent));
  });
  first.join();
  second.join();
  const wdf_capture_result first_result = results[0].load();
  const wdf_capture_result second_result = results[1].load();
  if (!Expect((first_result == WDF_CAPTURE_RESULT_NOT_IMPLEMENTED &&
               second_result == WDF_CAPTURE_RESULT_ADMISSION_REJECTED) ||
                  (second_result == WDF_CAPTURE_RESULT_NOT_IMPLEMENTED &&
                   first_result == WDF_CAPTURE_RESULT_ADMISSION_REJECTED),
              "concurrent C double-consume was not exactly once")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_command_admission_v1 revoked = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_b.target_epoch,
                                          &revoked) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "pre-revoke C admission was not issued");
  }
  if (!Expect(wdf_capture_revoke_runtime_authorization(
                  handle, &generation) == WDF_CAPTURE_RESULT_OK &&
                  generation == 4,
              "C authorization revoke failed") ||
      !Expect(wdf_capture_start_authorized(handle, &revoked) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "revoke did not invalidate C admission")) {
    wdf_capture_destroy(&handle);
    return false;
  }
  wdf_capture_command_admission_v1 blocked = EmptyAdmission();
  if (!Expect(wdf_capture_issue_command_admission(handle,
                                                  WDF_CAPTURE_COMMAND_START,
                                                  generation,
                                                  target_b.target_epoch,
                                                  &blocked) ==
                  WDF_CAPTURE_RESULT_POLICY_BLOCKED,
              "revoked C policy did not block admission issue")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_runtime_authorization_v1 target_c =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 3, 3, 102);
  if (wdf_capture_update_runtime_authorization(
          handle, &target_c, &generation) != WDF_CAPTURE_RESULT_OK ||
      generation != 5) {
    wdf_capture_destroy(&handle);
    return Expect(false, "post-revoke C authorization failed");
  }
  wdf_capture_command_admission_v1 stopped = EmptyAdmission();
  if (wdf_capture_issue_command_admission(handle,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_c.target_epoch,
                                          &stopped) != WDF_CAPTURE_RESULT_OK ||
      wdf_capture_request_stop(handle) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&handle);
    return Expect(false, "pre-stop C admission setup failed");
  }
  if (!Expect(wdf_capture_start_authorized(handle, &stopped) ==
                  WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "stop did not invalidate C admission") ||
      !Expect(wdf_capture_wait_stopped(handle, 5'000) ==
                  WDF_CAPTURE_RESULT_OK,
              "command invalidation stop did not join")) {
    wdf_capture_destroy(&handle);
    return false;
  }
  wdf_capture_destroy(&handle);

  wdf_capture_handle destroyed = 0;
  if (wdf_capture_create(&config, &destroyed) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "destroyed-admission handle could not be created");
  }
  generation = 0;
  if (wdf_capture_update_runtime_authorization(
          destroyed, &target_a, &generation) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&destroyed);
    return Expect(false, "destroyed-admission handle was not authorized");
  }
  wdf_capture_command_admission_v1 destroyed_stamp = EmptyAdmission();
  if (wdf_capture_issue_command_admission(destroyed,
                                          WDF_CAPTURE_COMMAND_START,
                                          generation,
                                          target_a.target_epoch,
                                          &destroyed_stamp) !=
      WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&destroyed);
    return Expect(false, "pre-destroy C admission was not issued");
  }
  const wdf_capture_handle stale_handle = destroyed;
  if (wdf_capture_destroy(&destroyed) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "admission owner could not be destroyed");
  }
  wdf_capture_handle recreated = 0;
  if (wdf_capture_create(&config, &recreated) != WDF_CAPTURE_RESULT_OK ||
      wdf_capture_update_runtime_authorization(
          recreated, &target_a, &generation) != WDF_CAPTURE_RESULT_OK) {
    wdf_capture_destroy(&recreated);
    return Expect(false, "recreated admission handle setup failed");
  }
  const bool valid =
      Expect(wdf_capture_start_authorized(stale_handle, &destroyed_stamp) ==
                 WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
             "destroyed handle accepted command admission") &&
      Expect(wdf_capture_start_authorized(recreated, &destroyed_stamp) ==
                 WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
             "destroyed admission crossed recreated instance");
  wdf_capture_destroy(&recreated);
  return valid;
}

bool TestCommandAdmissionDestroyRace() {
  wdf_capture_config_v1 config = ValidConfig();
  for (int iteration = 0; iteration < 32; ++iteration) {
    wdf_capture_handle handle = 0;
    if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
      return Expect(false, "destroy-race admission handle could not be created");
    }
    wdf_capture_runtime_authorization_v1 authorization =
        RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
    uint64_t generation = 0;
    wdf_capture_command_admission_v1 admission = EmptyAdmission();
    if (wdf_capture_update_runtime_authorization(
            handle, &authorization, &generation) != WDF_CAPTURE_RESULT_OK ||
        wdf_capture_issue_command_admission(
            handle,
            WDF_CAPTURE_COMMAND_START,
            generation,
            authorization.target_epoch,
            &admission) != WDF_CAPTURE_RESULT_OK) {
      wdf_capture_destroy(&handle);
      return Expect(false, "destroy-race admission setup failed");
    }

    const wdf_capture_handle raw_handle = handle;
    std::atomic<bool> ready{false};
    std::atomic<wdf_capture_result> start_result{
        WDF_CAPTURE_RESULT_INTERNAL_ERROR};
    std::thread starter([&] {
      ready.store(true, std::memory_order_release);
      ready.notify_one();
      start_result.store(
          wdf_capture_start_authorized(raw_handle, &admission),
          std::memory_order_release);
    });
    ready.wait(false, std::memory_order_acquire);
    const wdf_capture_result destroy_result = wdf_capture_destroy(&handle);
    starter.join();

    const wdf_capture_result observed =
        start_result.load(std::memory_order_acquire);
    if (!Expect(destroy_result == WDF_CAPTURE_RESULT_OK && handle == 0,
                "destroy failed during command admission race") ||
        !Expect(observed == WDF_CAPTURE_RESULT_NOT_IMPLEMENTED ||
                    observed == WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
                    observed == WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
                "command admission returned an invalid destroy-race result")) {
      return false;
    }
  }
  return true;
}

bool TestCallbackInvalidationDestroyRace() {
  wdf_capture_config_v1 config = ValidConfig();
  for (int iteration = 0; iteration < 32; ++iteration) {
    wdf_capture_handle handle = 0;
    if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
      return Expect(false,
                    "destroy-race invalidation handle could not be created");
    }

    const wdf_capture_handle raw_handle = handle;
    std::atomic<bool> ready{false};
    std::atomic<wdf_capture_result> invalidation_result{
        WDF_CAPTURE_RESULT_INTERNAL_ERROR};
    std::atomic<uint64_t> authorization_epoch{0};
    std::thread invalidator([&] {
      ready.store(true, std::memory_order_release);
      ready.notify_one();
      uint64_t observed_epoch = 0;
      invalidation_result.store(
          wdf_capture_invalidate_runtime_authorization(
              raw_handle, &observed_epoch),
          std::memory_order_release);
      authorization_epoch.store(observed_epoch, std::memory_order_release);
    });
    ready.wait(false, std::memory_order_acquire);
    const wdf_capture_result destroy_result = wdf_capture_destroy(&handle);
    invalidator.join();

    const wdf_capture_result observed =
        invalidation_result.load(std::memory_order_acquire);
    const uint64_t observed_epoch =
        authorization_epoch.load(std::memory_order_acquire);
    if (!Expect(destroy_result == WDF_CAPTURE_RESULT_OK && handle == 0,
                "destroy failed during callback invalidation race") ||
        !Expect((observed == WDF_CAPTURE_RESULT_OK && observed_epoch != 0 &&
                 (observed_epoch & 1U) == 0) ||
                    (observed == WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                     observed_epoch == 0),
                "callback invalidation returned an invalid destroy-race result")) {
      return false;
    }
  }
  return true;
}

bool TestLegacyAndRuntimeRevisionNamespacesCannotMix() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle legacy_first = 0;
  if (wdf_capture_create(&config, &legacy_first) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "legacy-first namespace handle could not be created");
  }
  wdf_capture_privacy_context_v1 legacy_v7 =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 7);
  wdf_capture_runtime_authorization_v1 runtime_v1 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 1);
  uint64_t generation = 0;
  const bool legacy_taints_handle =
      Expect(wdf_capture_update_privacy_context(
                 legacy_first, &legacy_v7) == WDF_CAPTURE_RESULT_OK,
             "legacy revision seven was rejected") &&
      Expect(wdf_capture_update_runtime_authorization(
                 legacy_first, &runtime_v1, &generation) ==
                     WDF_CAPTURE_RESULT_INVALID_STATE &&
                 generation == 2,
             "legacy-tainted handle accepted runtime revision one");
  wdf_capture_destroy(&legacy_first);

  wdf_capture_handle runtime_first = 0;
  if (wdf_capture_create(&config, &runtime_first) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "runtime-first namespace handle could not be created");
  }
  wdf_capture_privacy_context_v1 legacy_v1 =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 1);
  wdf_capture_runtime_authorization_v1 runtime_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 2);
  const bool runtime_downgrades_once =
      Expect(wdf_capture_update_runtime_authorization(
                 runtime_first, &runtime_v1, &generation) ==
                     WDF_CAPTURE_RESULT_OK &&
                 generation == 2,
             "runtime-first authorization was rejected") &&
      Expect(wdf_capture_update_privacy_context(
                 runtime_first, &legacy_v1) == WDF_CAPTURE_RESULT_OK,
             "legacy downgrade did not synchronously revoke runtime mode") &&
      Expect(wdf_capture_update_runtime_authorization(
                 runtime_first, &runtime_v2, &generation) ==
                     WDF_CAPTURE_RESULT_INVALID_STATE &&
                 generation == 3,
             "runtime mode resumed after a legacy downgrade");
  wdf_capture_destroy(&runtime_first);
  return legacy_taints_handle && runtime_downgrades_once;
}

bool TestStaleHandleCannotTargetRecreatedInstance() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle first = 0;
  if (wdf_capture_create(&config, &first) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "first ABA test handle could not be created");
  }
  const wdf_capture_handle stale = first;
  wdf_capture_destroy(&first);

  for (int index = 0; index < 64; ++index) {
    wdf_capture_handle candidate = 0;
    if (!Expect(wdf_capture_create(&config, &candidate) ==
                        WDF_CAPTURE_RESULT_OK &&
                    candidate != stale,
                "destroyed handle identity was reused")) {
      wdf_capture_destroy(&candidate);
      return false;
    }
    wdf_capture_destroy(&candidate);
  }

  wdf_capture_handle live = 0;
  if (wdf_capture_create(&config, &live) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "live ABA test handle could not be created");
  }
  const bool valid = Expect(wdf_capture_request_stop(stale) ==
                                WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
                            "stale token targeted a recreated instance") &&
                     Expect(wdf_capture_request_stop(live) ==
                                WDF_CAPTURE_RESULT_OK,
                            "live token was rejected");
  wdf_capture_destroy(&live);
  return valid;
}

bool TestConcurrentPollAndDestroyAreSafe() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "destroy race handle could not be created");
  }
  wdf_capture_event_v1 initial{};
  std::string detail;
  if (!PollEvent(handle, &initial, &detail)) {
    wdf_capture_destroy(&handle);
    return false;
  }

  const wdf_capture_handle poll_handle = handle;
  std::atomic<bool> poll_call_started{false};
  std::atomic<wdf_capture_result> poll_result{
      WDF_CAPTURE_RESULT_INTERNAL_ERROR};
  std::atomic<wdf_capture_state> poll_state{WDF_CAPTURE_STATE_UNAVAILABLE};
  std::thread poller([&] {
    wdf_capture_event_v1 event{};
    event.struct_size = sizeof(event);
    event.abi_version = WDF_CAPTURE_ABI_VERSION;
    std::array<char, 256> buffer{};
    uint32_t required = 0;
    poll_call_started.store(true, std::memory_order_release);
    poll_call_started.notify_one();
    const wdf_capture_result result = wdf_capture_poll_event(
        poll_handle,
        5'000,
        &event,
        buffer.data(),
        static_cast<uint32_t>(buffer.size()),
        &required);
    if (result == WDF_CAPTURE_RESULT_OK) {
      poll_state.store(event.state);
    }
    poll_result.store(result);
  });
  poll_call_started.wait(false, std::memory_order_acquire);
  // The public ABI has no observable hook after the native lease is acquired.
  std::this_thread::sleep_for(std::chrono::milliseconds(20));
  const bool poll_was_pending = Expect(
      poll_result.load(std::memory_order_acquire) ==
          WDF_CAPTURE_RESULT_INTERNAL_ERROR,
      "poll completed before the concurrent destroy began");
  const wdf_capture_result destroy_result = wdf_capture_destroy(&handle);
  poller.join();
  const wdf_capture_result observed_result = poll_result.load();
  const wdf_capture_state observed_state = poll_state.load();
  return poll_was_pending &&
         Expect(destroy_result == WDF_CAPTURE_RESULT_OK && handle == 0,
                "destroy failed during bounded poll") &&
         Expect(observed_result == WDF_CAPTURE_RESULT_INVALID_ARGUMENT ||
                    observed_result == WDF_CAPTURE_RESULT_INVALID_STATE ||
                    (observed_result == WDF_CAPTURE_RESULT_OK &&
                     (observed_state == WDF_CAPTURE_STATE_STOPPING ||
                      observed_state == WDF_CAPTURE_STATE_STOPPED)),
                "concurrent poll returned an invalid shutdown result");
}

bool TestEventStructureValidation() {
  wdf_capture_config_v1 config = ValidConfig();
  wdf_capture_handle handle = 0;
  if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK) {
    return Expect(false, "event validation handle could not be created");
  }
  wdf_capture_event_v1 event{};
  event.struct_size = sizeof(event) - 1U;
  event.abi_version = WDF_CAPTURE_ABI_VERSION;
  uint32_t required = 0;
  const bool structure_rejected = Expect(
      wdf_capture_poll_event(handle, 0, &event, nullptr, 0, &required) ==
          WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "undersized event structure was accepted");
  event.struct_size = sizeof(event);
  event.abi_version = WDF_CAPTURE_ABI_VERSION;
  const bool timeout_rejected = Expect(
      wdf_capture_poll_event(
          handle, 60'001, &event, nullptr, 0, &required) ==
          WDF_CAPTURE_RESULT_INVALID_ARGUMENT,
      "unbounded poll timeout was accepted");
  wdf_capture_destroy(&handle);
  return structure_rejected && timeout_rejected;
}

}  // namespace

int main() {
  if (!TestAbiAndArgumentValidation() ||
      !TestTrulyShortVersionedStructures() ||
      !TestLifecycleIsPrivacyGatedAndUnavailable() ||
      !TestPrivacyRevisionNeverRegresses() ||
      !TestRuntimeAuthorizationDisplayStructureContract() ||
      !TestRuntimeAuthorizationBarrierContract() ||
      !TestCallbackTimeAuthorizationInvalidationContract() ||
      !TestCommandAdmissionAuthenticityAndOwnership() ||
      !TestCommandAdmissionInvalidationAndConcurrency() ||
      !TestCommandAdmissionDestroyRace() ||
      !TestCallbackInvalidationDestroyRace() ||
      !TestLegacyAndRuntimeRevisionNamespacesCannotMix() ||
      !TestStaleHandleCannotTargetRecreatedInstance() ||
      !TestConcurrentPollAndDestroyAreSafe() ||
      !TestEventStructureValidation()) {
    return 1;
  }
  std::cout << "capture C ABI tests passed\n";
  return 0;
}

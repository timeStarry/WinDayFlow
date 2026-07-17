#include "windayflow_capture.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#pragma pack(push, 8)
typedef struct legacy_runtime_authorization_v1 {
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
} legacy_runtime_authorization_v1;
#pragma pack(pop)

_Static_assert(sizeof(legacy_runtime_authorization_v1) ==
                   WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE,
               "legacy runtime authorization prefix changed");

int main(void) {
  wdf_capture_capabilities capabilities = 0;
  static const char output_path[] = "C:\\WinDayFlow-C-Test";

  if (wdf_capture_get_abi_version() != WDF_CAPTURE_ABI_VERSION) {
    fputs("C translation unit observed an unexpected ABI version\n", stderr);
    return 1;
  }

  if (wdf_capture_get_capabilities(&capabilities) != WDF_CAPTURE_RESULT_OK) {
    fputs("C translation unit could not call the capture DLL\n", stderr);
    return 1;
  }

  if ((capabilities & WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_EVENT_QUEUE) == 0 ||
      (capabilities &
       WDF_CAPTURE_CAPABILITY_TARGET_SCOPED_AUTHORIZATION) == 0 ||
      (capabilities &
       WDF_CAPTURE_CAPABILITY_PERSISTENCE_GENERATION_BARRIER) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_DETERMINISTIC_STOP) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION) != 0 ||
      (capabilities &
       WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION) == 0 ||
      (capabilities &
       WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE) != 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_H264_CHUNKS) != 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_EVIDENCE_EXTRACTION) != 0) {
    fputs("C translation unit observed incomplete foundation capabilities\n",
          stderr);
    return 1;
  }

  if (WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION != (1ULL << 8) ||
      WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION != (1ULL << 9) ||
      WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION != (1ULL << 10) ||
      WDF_CAPTURE_TARGET_DISPLAY_PRESENT != (1U << 1) ||
      WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE != 112U ||
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY != 96U ||
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH != 93U ||
      sizeof(wdf_capture_config_v1) != 80 ||
      sizeof(wdf_capture_privacy_context_v1) != 80 ||
      sizeof(wdf_capture_runtime_authorization_v1) != 224 ||
      sizeof(wdf_capture_command_admission_v1) != 64 ||
      sizeof(wdf_capture_event_v1) != 80 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               runtime_policy_revision) != 8 ||
      offsetof(wdf_capture_runtime_authorization_v1, target_epoch) != 16 ||
      offsetof(wdf_capture_runtime_authorization_v1, target_window_handle) !=
          24 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_process_creation_time_100ns) != 32 ||
      offsetof(wdf_capture_runtime_authorization_v1, target_process_id) != 40 ||
      offsetof(wdf_capture_runtime_authorization_v1, target_flags) != 44 ||
      offsetof(wdf_capture_runtime_authorization_v1, consent_granted) != 48 ||
      offsetof(wdf_capture_runtime_authorization_v1, reserved) != 80 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_monitor_handle) != 112 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_device_key_utf8_length) != 120 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_reserved) != 124 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_device_key_utf8) != 128 ||
      offsetof(wdf_capture_command_admission_v1, instance_epoch) != 8 ||
      offsetof(wdf_capture_command_admission_v1,
               runtime_policy_revision) != 16 ||
      offsetof(wdf_capture_command_admission_v1,
               persistence_generation) != 24 ||
      offsetof(wdf_capture_command_admission_v1, target_epoch) != 32 ||
      offsetof(wdf_capture_command_admission_v1,
               authorization_epoch) != 40 ||
      offsetof(wdf_capture_command_admission_v1, nonce_low) != 48 ||
      offsetof(wdf_capture_command_admission_v1, nonce_high) != 56 ||
      offsetof(wdf_capture_event_v1, persistence_generation) != 48 ||
      offsetof(wdf_capture_event_v1, target_epoch) != 56) {
    fputs("C translation unit observed an incompatible ABI layout\n", stderr);
    return 1;
  }
  {
    wdf_capture_event_v1 source_compatible_event = {0};
    source_compatible_event.reserved[7] = 0;
    source_compatible_event.persistence_generation = 1;
    if (source_compatible_event.reserved[0] != 1) {
      fputs("C event reserved compatibility alias was not preserved\n", stderr);
      return 1;
    }
  }

  {
    wdf_capture_config_v1 config = {0};
    wdf_capture_runtime_authorization_v1 authorization = {0};
    legacy_runtime_authorization_v1 legacy_block = {0};
    legacy_runtime_authorization_v1 legacy_allow = {0};
    wdf_capture_command_admission_v1 admission = {0};
    wdf_capture_handle handle = 0;
    uint64_t generation = 0;
    static const char display_key[] = "\\\\.\\DISPLAY1";
    config.struct_size = (uint32_t)sizeof(config);
    config.abi_version = WDF_CAPTURE_ABI_VERSION;
    config.capture_interval_ms = 10000;
    config.context_interval_ms = 1000;
    config.chunk_duration_ms = 60000;
    config.max_width = 1920;
    config.max_height = 1080;
    config.event_queue_capacity = 16;
    config.output_directory_utf8 = output_path;
    config.output_directory_utf8_length =
        (uint32_t)(sizeof(output_path) - 1U);

    authorization.struct_size = (uint32_t)sizeof(authorization);
    authorization.abi_version = WDF_CAPTURE_ABI_VERSION;
    authorization.runtime_policy_revision = 2;
    authorization.target_epoch = 1;
    authorization.target_window_handle = 100;
    authorization.target_process_creation_time_100ns = 300;
    authorization.target_process_id = 200;
    authorization.target_flags = WDF_CAPTURE_TARGET_PRESENT |
                                 WDF_CAPTURE_TARGET_DISPLAY_PRESENT;
    authorization.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
    authorization.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
    authorization.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
    authorization.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.storage_available = WDF_CAPTURE_POLICY_ALLOW;
    authorization.target_display_monitor_handle = 400;
    authorization.target_display_device_key_utf8_length =
        (uint32_t)(sizeof(display_key) - 1U);
    memcpy(authorization.target_display_device_key_utf8,
           display_key,
           sizeof(display_key) - 1U);
    legacy_block.struct_size =
        WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE;
    legacy_block.abi_version = WDF_CAPTURE_ABI_VERSION;
    legacy_block.runtime_policy_revision = 1;
    legacy_block.consent_granted = WDF_CAPTURE_POLICY_BLOCK;
    legacy_block.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
    legacy_block.storage_available = WDF_CAPTURE_POLICY_ALLOW;
    memcpy(&legacy_allow, &authorization, sizeof(legacy_allow));
    legacy_allow.struct_size =
        WDF_CAPTURE_RUNTIME_AUTHORIZATION_V1_LEGACY_SIZE;
    legacy_allow.runtime_policy_revision = 1;
    legacy_allow.target_flags = WDF_CAPTURE_TARGET_PRESENT;
    admission.struct_size = (uint32_t)sizeof(admission);
    admission.abi_version = WDF_CAPTURE_ABI_VERSION;

    if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK ||
        wdf_capture_update_runtime_authorization(
            handle,
            (const wdf_capture_runtime_authorization_v1*)&legacy_allow,
            &generation) !=
            WDF_CAPTURE_RESULT_INVALID_ARGUMENT ||
        generation != 0 ||
        wdf_capture_update_runtime_authorization(
            handle,
            (const wdf_capture_runtime_authorization_v1*)&legacy_block,
            &generation) != WDF_CAPTURE_RESULT_OK ||
        generation != 2 ||
        wdf_capture_update_runtime_authorization(
            handle, &authorization, &generation) != WDF_CAPTURE_RESULT_OK ||
        generation != 3 ||
        wdf_capture_start(handle) != WDF_CAPTURE_RESULT_ADMISSION_REQUIRED ||
        wdf_capture_resume(handle) != WDF_CAPTURE_RESULT_ADMISSION_REQUIRED ||
        wdf_capture_issue_command_admission(
            handle,
            WDF_CAPTURE_COMMAND_START,
            generation,
            authorization.target_epoch,
            &admission) != WDF_CAPTURE_RESULT_OK ||
        admission.instance_epoch == 0 ||
        admission.runtime_policy_revision != 2 ||
        admission.persistence_generation != generation ||
        admission.target_epoch != authorization.target_epoch ||
        admission.authorization_epoch == 0 ||
        (admission.nonce_low == 0 && admission.nonce_high == 0) ||
        wdf_capture_start_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_NOT_IMPLEMENTED ||
        wdf_capture_start_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_resume_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_revoke_runtime_authorization(handle, &generation) !=
            WDF_CAPTURE_RESULT_OK ||
        generation != 4 ||
        wdf_capture_destroy(&handle) != WDF_CAPTURE_RESULT_OK || handle != 0) {
      fputs("C translation unit could not call the safety-core exports\n",
            stderr);
      return 1;
    }
  }

  puts("C header and DLL compatibility test passed");
  return 0;
}

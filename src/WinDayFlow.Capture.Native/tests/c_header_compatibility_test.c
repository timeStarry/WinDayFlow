#include "windayflow_capture.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

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
      (capabilities &
       WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION) == 0 ||
      (capabilities &
       WDF_CAPTURE_CAPABILITY_DISPLAY_WIDE_CONTINUOUS_AUTHORIZATION) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_HEALTH_SNAPSHOT) == 0) {
    fputs("C translation unit observed incomplete foundation capabilities\n",
          stderr);
    return 1;
  }

#if WDF_ENABLE_DEV_LIVE_CAPTURE
  if ((capabilities & WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_CANONICAL_JPEG_CHUNKS) == 0) {
    fputs("C translation unit did not observe development live capture\n",
          stderr);
    return 1;
  }
#else
  if ((capabilities & WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE) != 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_CANONICAL_JPEG_CHUNKS) != 0) {
    fputs("C translation unit observed production live capture\n", stderr);
    return 1;
  }
#endif

  if (WDF_CAPTURE_CAPABILITY_COMMAND_ADMISSION != (1ULL << 8) ||
      WDF_CAPTURE_CAPABILITY_DISPLAY_SCOPED_AUTHORIZATION != (1ULL << 9) ||
      WDF_CAPTURE_CAPABILITY_DISPLAY_BOUND_COMMAND_ADMISSION != (1ULL << 10) ||
      WDF_CAPTURE_CAPABILITY_CALLBACK_TIME_AUTHORIZATION_INVALIDATION !=
          (1ULL << 11) ||
      WDF_CAPTURE_CAPABILITY_DISPLAY_WIDE_CONTINUOUS_AUTHORIZATION !=
          (1ULL << 12) ||
      WDF_CAPTURE_CAPABILITY_HEALTH_SNAPSHOT != (1ULL << 13) ||
      WDF_CAPTURE_RESULT_AUTHORIZATION_SUPERSEDED != -14 ||
      WDF_CAPTURE_RESULT_EVIDENCE_NOT_FOUND != -15 ||
      WDF_CAPTURE_RESULT_UNSAFE_EVIDENCE != -16 ||
      WDF_CAPTURE_RESULT_EVIDENCE_TOO_LARGE != -17 ||
      WDF_CAPTURE_RESULT_EVIDENCE_CHANGED != -18 ||
      WDF_CAPTURE_RESULT_IO_FAILURE != -19 ||
      WDF_CAPTURE_RESULT_CRYPTO_FAILURE != -20 ||
      WDF_CAPTURE_RESULT_EVIDENCE_INVALID != -21 ||
      WDF_CAPTURE_RESULT_DECODER_FAILURE != -22 ||
      WDF_CAPTURE_RESULT_EVIDENCE_CONFLICT != -23 ||
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_CAPACITY != 96U ||
      WDF_CAPTURE_DISPLAY_DEVICE_KEY_UTF8_MAX_LENGTH != 93U ||
      sizeof(wdf_capture_config_v1) != 80 ||
      sizeof(wdf_capture_privacy_context_v1) != 64 ||
      sizeof(wdf_capture_runtime_authorization_v1) != 184 ||
      sizeof(wdf_capture_command_admission_v1) != 64 ||
      sizeof(wdf_capture_event_v1) != 80 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               runtime_policy_revision) != 8 ||
      offsetof(wdf_capture_runtime_authorization_v1, target_epoch) != 16 ||
      offsetof(wdf_capture_runtime_authorization_v1, consent_granted) != 24 ||
      offsetof(wdf_capture_runtime_authorization_v1, reserved) != 40 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_monitor_handle) != 72 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_device_key_utf8_length) != 80 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_reserved) != 84 ||
      offsetof(wdf_capture_runtime_authorization_v1,
               target_display_device_key_utf8) != 88 ||
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
    wdf_capture_runtime_authorization_v1 blocked = {0};
    wdf_capture_command_admission_v1 admission = {0};
    wdf_capture_handle handle = 0;
    uint64_t generation = 0;
    uint64_t authorization_epoch = 0;
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
    authorization.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
    authorization.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
    authorization.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
    authorization.storage_available = WDF_CAPTURE_POLICY_ALLOW;
    authorization.target_display_monitor_handle = 400;
    authorization.target_display_device_key_utf8_length =
        (uint32_t)(sizeof(display_key) - 1U);
    memcpy(authorization.target_display_device_key_utf8,
           display_key,
           sizeof(display_key) - 1U);
    blocked.struct_size = (uint32_t)sizeof(blocked);
    blocked.abi_version = WDF_CAPTURE_ABI_VERSION;
    blocked.runtime_policy_revision = 1;
    blocked.consent_granted = WDF_CAPTURE_POLICY_BLOCK;
    blocked.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
    blocked.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
    blocked.storage_available = WDF_CAPTURE_POLICY_ALLOW;
    admission.struct_size = (uint32_t)sizeof(admission);
    admission.abi_version = WDF_CAPTURE_ABI_VERSION;

    if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK ||
        wdf_capture_update_runtime_authorization(
            handle, &blocked, &generation) != WDF_CAPTURE_RESULT_OK ||
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
        (admission.nonce_low == 0 && admission.nonce_high == 0)) {
      fputs("C translation unit could not call the safety-core setup exports\n",
            stderr);
      wdf_capture_destroy(&handle);
      return 1;
    }

#if WDF_ENABLE_DEV_LIVE_CAPTURE
    if (wdf_capture_invalidate_runtime_authorization(
            handle, &authorization_epoch) != WDF_CAPTURE_RESULT_OK ||
        authorization_epoch == 0 || (authorization_epoch & 1U) != 0 ||
        wdf_capture_start_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_resume_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_revoke_runtime_authorization(handle, &generation) !=
            WDF_CAPTURE_RESULT_OK ||
        generation != 4 ||
        wdf_capture_destroy(&handle) != WDF_CAPTURE_RESULT_OK || handle != 0) {
      fputs("C translation unit could not call the dev-live safety exports\n",
            stderr);
      return 1;
    }
#else
    if (wdf_capture_start_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_NOT_IMPLEMENTED ||
        wdf_capture_start_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_resume_authorized(handle, &admission) !=
            WDF_CAPTURE_RESULT_ADMISSION_REJECTED ||
        wdf_capture_invalidate_runtime_authorization(
            handle, &authorization_epoch) != WDF_CAPTURE_RESULT_OK ||
        authorization_epoch == 0 || (authorization_epoch & 1U) != 0 ||
        wdf_capture_revoke_runtime_authorization(handle, &generation) !=
            WDF_CAPTURE_RESULT_OK ||
        generation != 4 ||
        wdf_capture_destroy(&handle) != WDF_CAPTURE_RESULT_OK || handle != 0) {
      fputs("C translation unit could not call the safety-core exports\n",
            stderr);
      return 1;
    }
#endif
  }

  puts("C header and DLL compatibility test passed");
  return 0;
}

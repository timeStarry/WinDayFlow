#include "windayflow_capture.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

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
      (capabilities & WDF_CAPTURE_CAPABILITY_SCREEN_CAPTURE) != 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_H264_CHUNKS) != 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_EVIDENCE_EXTRACTION) != 0) {
    fputs("C translation unit observed incomplete foundation capabilities\n",
          stderr);
    return 1;
  }

  if (sizeof(wdf_capture_config_v1) != 80 ||
      sizeof(wdf_capture_privacy_context_v1) != 80 ||
      sizeof(wdf_capture_runtime_authorization_v1) != 112 ||
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
    wdf_capture_handle handle = 0;
    uint64_t generation = 0;
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
    authorization.runtime_policy_revision = 1;
    authorization.target_epoch = 1;
    authorization.target_window_handle = 100;
    authorization.target_process_creation_time_100ns = 300;
    authorization.target_process_id = 200;
    authorization.target_flags = WDF_CAPTURE_TARGET_PRESENT;
    authorization.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
    authorization.session_unlocked = WDF_CAPTURE_POLICY_ALLOW;
    authorization.secure_desktop_clear = WDF_CAPTURE_POLICY_ALLOW;
    authorization.remote_session_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.presentation_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.application_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.window_allowed = WDF_CAPTURE_POLICY_ALLOW;
    authorization.storage_available = WDF_CAPTURE_POLICY_ALLOW;

    if (wdf_capture_create(&config, &handle) != WDF_CAPTURE_RESULT_OK ||
        wdf_capture_update_runtime_authorization(
            handle, &authorization, &generation) != WDF_CAPTURE_RESULT_OK ||
        generation != 2 ||
        wdf_capture_revoke_runtime_authorization(handle, &generation) !=
            WDF_CAPTURE_RESULT_OK ||
        generation != 3 ||
        wdf_capture_destroy(&handle) != WDF_CAPTURE_RESULT_OK || handle != 0) {
      fputs("C translation unit could not call the safety-core exports\n",
            stderr);
      return 1;
    }
  }

  puts("C header and DLL compatibility test passed");
  return 0;
}

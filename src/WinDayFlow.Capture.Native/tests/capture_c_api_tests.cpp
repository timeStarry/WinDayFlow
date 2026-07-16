#include "windayflow_capture.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <string>
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
    uint64_t creation_time = 300) {
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
    context.target_flags = WDF_CAPTURE_TARGET_PRESENT;
  }
  return context;
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
  return Expect(wdf_capture_create(&config, &handle) ==
                    WDF_CAPTURE_RESULT_INVALID_ARGUMENT &&
                    handle == 0,
                "empty output path was accepted");
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
  wdf_capture_destroy(&handle);
  return event_rejected && privacy_rejected &&
         runtime_authorization_rejected;
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
                      WDF_CAPTURE_RESULT_POLICY_BLOCKED,
              "blocked consent reached the capture backend") ||
      !PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 2 &&
                  event.state == WDF_CAPTURE_STATE_BLOCKED_BY_CONSENT &&
                  event.reason == WDF_CAPTURE_REASON_CONSENT_REQUIRED &&
                  event.persistence_generation == 2,
              "consent block event was incorrect") ||
      !Expect(wdf_capture_start(handle) == WDF_CAPTURE_RESULT_INVALID_STATE,
              "start bypassed the explicit resume transition")) {
    wdf_capture_destroy(&handle);
    return false;
  }

  wdf_capture_privacy_context_v1 allowed =
      PrivacyContext(WDF_CAPTURE_POLICY_ALLOW, 2);
  if (!Expect(wdf_capture_update_privacy_context(handle, &allowed) ==
                  WDF_CAPTURE_RESULT_OK &&
                  wdf_capture_resume(handle) ==
                      WDF_CAPTURE_RESULT_NOT_IMPLEMENTED,
              "foundation build incorrectly reported live capture support") ||
      !PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 3 &&
                  event.state == WDF_CAPTURE_STATE_UNAVAILABLE &&
                  event.reason == WDF_CAPTURE_REASON_BACKEND_UNAVAILABLE &&
                  event.persistence_generation == 3,
              "unavailable backend event was incorrect") ||
      !Expect(wdf_capture_resume(handle) == WDF_CAPTURE_RESULT_INVALID_STATE,
              "resume was accepted outside a paused state")) {
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
      !Expect(event.sequence == 4 &&
                  event.state == WDF_CAPTURE_STATE_STOPPING &&
                  event.reason == WDF_CAPTURE_REASON_USER_STOPPED,
              "stopping event was incorrect") ||
      !PollEvent(handle, &event, &detail) ||
      !Expect(event.sequence == 5 &&
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
      Expect(wdf_capture_start(handle) == WDF_CAPTURE_RESULT_POLICY_BLOCKED,
             "newer block policy was not retained");
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
  wdf_capture_runtime_authorization_v1 allow_v2 =
      RuntimeAuthorization(WDF_CAPTURE_POLICY_ALLOW, 2, 2, 101);

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
                 handle, &reused_epoch_v2, &generation) ==
                 WDF_CAPTURE_RESULT_TARGET_MISMATCH,
             "target tuple changed without an epoch advance") &&
      Expect(wdf_capture_update_runtime_authorization(
                 handle, &allow_v2, &generation) == WDF_CAPTURE_RESULT_OK &&
                 generation == 3,
             "epoch-advanced target was rejected") &&
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
      !TestRuntimeAuthorizationBarrierContract() ||
      !TestLegacyAndRuntimeRevisionNamespacesCannotMix() ||
      !TestStaleHandleCannotTargetRecreatedInstance() ||
      !TestConcurrentPollAndDestroyAreSafe() ||
      !TestEventStructureValidation()) {
    return 1;
  }
  std::cout << "capture C ABI tests passed\n";
  return 0;
}

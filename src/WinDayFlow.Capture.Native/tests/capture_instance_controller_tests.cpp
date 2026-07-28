#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <future>
#include <iostream>
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>

#include "capture_instance_controller.h"

namespace windayflow::capture {

class CaptureInstanceControllerTestPeer {
 public:
  static bool Checkpoint(CaptureInstanceController& controller,
                         uint64_t run_id,
                         const CaptureWorkerCheckpoint& checkpoint) {
    return controller.OnWorkerCheckpoint(run_id, checkpoint);
  }

  static void Exit(CaptureInstanceController& controller, uint64_t run_id,
                   CaptureWorkerRunResult result) {
    controller.OnWorkerExited(run_id, result);
  }

  static wdf_capture_result RequestStop(
      CaptureInstanceController& controller, wdf_capture_reason reason,
      uint64_t expected_run_id) {
    return controller.RequestStopCore(reason, expected_run_id);
  }

  static void SetTerminalFinalizationHook(CaptureInstanceController& controller,
                                          void (*hook)()) {
    std::lock_guard lock(controller.mutex_);
    controller.terminal_finalization_hook_ = hook;
  }

  static void SetWorkerCheckpointHook(
      CaptureInstanceController& controller,
      void (*hook)(const CaptureWorkerCheckpoint&)) {
    std::lock_guard lock(controller.mutex_);
    controller.worker_checkpoint_hook_ = hook;
  }

  static uint64_t PauseEpoch(CaptureInstanceController& controller) {
    return controller.runtime_.ReadControlSnapshot().pause_epoch;
  }

  static bool AppendRequiredEvent(CaptureInstanceController& controller) {
    CaptureEventReservation reservation =
        controller.events_.ReserveRequiredEvent();
    return reservation &&
           controller.events_.PushReserved(
               &reservation, WDF_CAPTURE_EVENT_STATE_CHANGED,
               WDF_CAPTURE_STATE_STOPPED, WDF_CAPTURE_REASON_NONE,
               WDF_CAPTURE_ERROR_NONE, "Required saturation event.", 0) != 0;
  }
};

}  // namespace windayflow::capture

namespace {

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

using windayflow::capture::CaptureActivationMode;
using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureEventReadResult;
using windayflow::capture::CaptureInstanceController;
using windayflow::capture::CaptureInstanceControllerConfiguration;
using windayflow::capture::CaptureWorkerBackendResult;

struct BackendState {
  std::atomic<bool> called{false};
  std::atomic<uint32_t> shutdown_calls{0};
  std::mutex mutex;
  std::condition_variable changed;
  bool acquire_entered = false;
  bool release_acquire = false;
};

struct TerminalFinalizationGate {
  std::mutex mutex;
  std::condition_variable changed;
  bool entered = false;
  bool release = false;
};

struct WorkerCheckpointGate {
  std::mutex mutex;
  std::condition_variable changed;
  bool entered = false;
  bool release = false;
};

std::atomic<TerminalFinalizationGate*> g_terminal_finalization_gate{nullptr};
std::atomic<WorkerCheckpointGate*> g_worker_checkpoint_gate{nullptr};
std::atomic<bool> g_throw_terminal_finalization_once{false};

void HoldTerminalFinalization() {
  TerminalFinalizationGate* gate = g_terminal_finalization_gate.load();
  if (gate == nullptr) {
    return;
  }
  std::unique_lock lock(gate->mutex);
  gate->entered = true;
  gate->changed.notify_all();
  gate->changed.wait(lock, [gate] { return gate->release; });
}

void ThrowTerminalFinalizationOnce() {
  if (g_throw_terminal_finalization_once.exchange(false)) {
    throw std::runtime_error("injected terminal finalization failure");
  }
}

void HoldPausedCheckpoint(
    const windayflow::capture::CaptureWorkerCheckpoint& checkpoint) {
  WorkerCheckpointGate* gate = g_worker_checkpoint_gate.load();
  if (gate == nullptr ||
      checkpoint.kind !=
          windayflow::capture::CaptureWorkerCheckpointKind::kPaused) {
    return;
  }
  std::unique_lock lock(gate->mutex);
  gate->entered = true;
  gate->changed.notify_all();
  gate->changed.wait(lock, [gate] { return gate->release; });
}

class TestBackend final
    : public windayflow::capture::CaptureWorkerBackend {
 public:
  TestBackend(bool enabled, bool block_acquire,
              std::shared_ptr<BackendState> state,
              CaptureWorkerBackendResult initialize_result =
                  CaptureWorkerBackendResult::kOk)
      : enabled_(enabled),
        block_acquire_(block_acquire),
        state_(std::move(state)),
        initialize_result_(initialize_result) {}
  ~TestBackend() override = default;

  std::optional<windayflow::capture::CaptureTargetIdentity> ObserveTarget(
      const windayflow::capture::CaptureTargetIdentity& expected)
      noexcept override {
    state_->called.store(true);
    return enabled_ ? std::optional(expected) : std::nullopt;
  }
  windayflow::capture::CaptureWorkerBackendResult InitializeAcquisition(
      const windayflow::capture::CaptureTargetIdentity&) noexcept override {
    state_->called.store(true);
    return enabled_ ? initialize_result_
                    : CaptureWorkerBackendResult::kInternalFailure;
  }
  windayflow::capture::CaptureWorkerBackendResult AcquireFrame(
      uint32_t, windayflow::capture::BgraFrame*) noexcept override {
    state_->called.store(true);
    {
      std::unique_lock lock(state_->mutex);
      state_->acquire_entered = true;
      state_->changed.notify_all();
      if (block_acquire_) {
        state_->changed.wait(
            lock, [this] { return state_->release_acquire; });
      }
    }
    return enabled_ ? CaptureWorkerBackendResult::kTimeout
                    : CaptureWorkerBackendResult::kInternalFailure;
  }
  void ResetAcquisition() noexcept override { state_->called.store(true); }
  windayflow::capture::CaptureWorkerBackendResult TransformFrame(
      const windayflow::capture::BgraFrame&, uint32_t, uint32_t,
      windayflow::capture::BgraFrame*) noexcept override {
    state_->called.store(true);
    return CaptureWorkerBackendResult::kInternalFailure;
  }
  windayflow::capture::CaptureWorkerBackendResult BeginChunk(
      const windayflow::capture::MfH264ChunkWriterConfig&) noexcept override {
    state_->called.store(true);
    return CaptureWorkerBackendResult::kInternalFailure;
  }
  windayflow::capture::CaptureWorkerBackendResult EncodeFrame(
      std::span<const uint8_t>, int64_t) noexcept override {
    state_->called.store(true);
    return CaptureWorkerBackendResult::kInternalFailure;
  }
  windayflow::capture::CaptureWorkerBackendResult FinalizeChunk(
      int64_t, std::vector<uint8_t>*) noexcept override {
    state_->called.store(true);
    return CaptureWorkerBackendResult::kInternalFailure;
  }
  void ResetChunk() noexcept override { state_->called.store(true); }
  bool CreateArtifactId(std::string*) noexcept override {
    state_->called.store(true);
    return false;
  }
  windayflow::capture::CaptureWorkerBackendResult PreparePublication(
      std::string_view, std::span<const uint8_t>,
      const windayflow::capture::ChunkManifest&,
      std::unique_ptr<windayflow::capture::CaptureWorkerPublication>*)
      noexcept override {
    state_->called.store(true);
    return CaptureWorkerBackendResult::kInternalFailure;
  }
  int64_t SteadyNowMilliseconds() noexcept override {
    state_->called.store(true);
    return 0;
  }
  int64_t UnixNowMilliseconds() noexcept override {
    state_->called.store(true);
    return 0;
  }
  void ShutdownThread() noexcept override {
    state_->called.store(true);
    state_->shutdown_calls.fetch_add(1);
  }

 private:
  const bool enabled_;
  const bool block_acquire_;
  std::shared_ptr<BackendState> state_;
  const CaptureWorkerBackendResult initialize_result_;
};

struct ReadEvent {
  CaptureEventReadResult result = CaptureEventReadResult::kInternalError;
  wdf_capture_event_v1 event{};
  std::string detail;
};

ReadEvent ReadOne(CaptureInstanceController& controller,
                  uint32_t timeout_ms = 1'000) {
  ReadEvent value;
  value.event.struct_size = sizeof(value.event);
  value.event.abi_version = WDF_CAPTURE_ABI_VERSION;
  std::array<char, 256> detail{};
  uint32_t required = 0;
  value.result = controller.Poll(timeout_ms, &value.event, detail.data(),
                                 static_cast<uint32_t>(detail.size()),
                                 &required);
  if (value.result == CaptureEventReadResult::kSuccess &&
      value.event.detail_utf8_length <= detail.size()) {
    value.detail.assign(detail.data(), value.event.detail_utf8_length);
  }
  return value;
}

bool ExpectState(CaptureInstanceController& controller,
                 wdf_capture_state expected, const char* message) {
  const ReadEvent value = ReadOne(controller);
  return Expect(value.result == CaptureEventReadResult::kSuccess &&
                    value.event.kind == WDF_CAPTURE_EVENT_STATE_CHANGED &&
                    value.event.state == expected,
                message);
}

bool ExpectStateAndReason(CaptureInstanceController& controller,
                          wdf_capture_state expected_state,
                          wdf_capture_reason expected_reason,
                          const char* message) {
  const ReadEvent value = ReadOne(controller);
  return Expect(value.result == CaptureEventReadResult::kSuccess &&
                    value.event.kind == WDF_CAPTURE_EVENT_STATE_CHANGED &&
                    value.event.state == expected_state &&
                    value.event.reason == expected_reason,
                message);
}

windayflow::capture::PrivacyContext AllowedPrivacy(uint64_t revision) {
  return windayflow::capture::PrivacyContext{
      WDF_CAPTURE_POLICY_ALLOW, WDF_CAPTURE_POLICY_ALLOW,
      WDF_CAPTURE_POLICY_ALLOW, WDF_CAPTURE_POLICY_ALLOW,
      WDF_CAPTURE_POLICY_ALLOW, WDF_CAPTURE_POLICY_ALLOW,
      WDF_CAPTURE_POLICY_ALLOW, WDF_CAPTURE_POLICY_ALLOW, revision};
}

windayflow::capture::PrivacyContext BlockedPrivacy(uint64_t revision) {
  auto privacy = AllowedPrivacy(revision);
  privacy.session_unlocked = WDF_CAPTURE_POLICY_BLOCK;
  return privacy;
}

windayflow::capture::PrivacyContext ApplicationBlockedPrivacy(
    uint64_t revision) {
  auto privacy = AllowedPrivacy(revision);
  privacy.application_allowed = WDF_CAPTURE_POLICY_BLOCK;
  return privacy;
}

windayflow::capture::CaptureTargetIdentity Target() {
  return windayflow::capture::CaptureTargetIdentity{
      10, 20, 30, 1, 40, L"\\\\.\\DISPLAY1"};
}

bool Authorize(CaptureInstanceController& controller, uint64_t revision,
               uint64_t* generation);

bool TestDisabledConsumesAdmissionWithoutStarting() {
  auto backend_state = std::make_shared<BackendState>();
  auto backend =
      std::make_unique<TestBackend>(false, false, backend_state);
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kDisabled;
  CaptureInstanceController controller(configuration, std::move(backend));

  if (!ExpectState(controller, WDF_CAPTURE_STATE_UNAVAILABLE,
                   "disabled controller omitted its initial state")) {
    return false;
  }

  uint64_t generation = 0;
  const auto target = Target();
  if (!Expect(controller.UpdateRuntimeAuthorization(
                  {AllowedPrivacy(1), target}, &generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "disabled controller authorization failed")) {
    return false;
  }
  windayflow::capture::CaptureCommandAdmission admission;
  if (!Expect(controller.IssueAdmission(
                  windayflow::capture::CaptureCommand::kStart, generation,
                  target.target_epoch, &admission) ==
                  WDF_CAPTURE_RESULT_OK,
              "disabled controller admission issuance failed")) {
    return false;
  }
  const wdf_capture_result first = controller.StartAuthorized(admission);
  const wdf_capture_result replay = controller.StartAuthorized(admission);
  const ReadEvent after_start = ReadOne(controller, 0);
  if (!Expect(first == WDF_CAPTURE_RESULT_NOT_IMPLEMENTED,
              "disabled controller did not fail closed") ||
      !Expect(replay == WDF_CAPTURE_RESULT_ADMISSION_REJECTED,
              "disabled controller admission was replayable") ||
      !Expect(!backend_state->called.load(),
              "disabled controller invoked the capture backend") ||
      !Expect(controller.active_run_id() == 0 &&
                  controller.join_count() == 0,
              "disabled controller spawned a worker") ||
      !Expect(after_start.result == CaptureEventReadResult::kEmpty,
              "disabled controller emitted a fake live event")) {
    return false;
  }
  return Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
                "disabled controller rejected stop") &&
         ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                     "disabled stop omitted STOPPING") &&
         Expect(controller.WaitStopped(1'000) == WDF_CAPTURE_RESULT_OK,
                "disabled controller did not finalize revoke") &&
         [&controller] {
           const ReadEvent stopped = ReadOne(controller);
           return Expect(
               stopped.result == CaptureEventReadResult::kSuccess &&
                   stopped.event.kind == WDF_CAPTURE_EVENT_STATE_CHANGED &&
                   stopped.event.state == WDF_CAPTURE_STATE_STOPPED &&
                   stopped.detail == "Capture worker stopped and joined.",
               "disabled stop published a false worker failure");
         }() &&
         Expect(controller.join_count() == 0 &&
                    controller.reserved_event_count() == 0 &&
                    !backend_state->called.load(),
                "disabled stop touched the backend or leaked resources");
}

bool TestDisabledRevokeDoesNotCreateStopRun() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(false, false, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_UNAVAILABLE,
                   "disabled revoke fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation),
              "disabled revoke fixture authorization failed")) {
    return false;
  }
  uint64_t revoked_generation = 0;
  windayflow::capture::CaptureCommandAdmission admission;
  const auto revoke =
      controller.RevokeRuntimeAuthorization(&revoked_generation);
  const wdf_capture_result issue = controller.IssueAdmission(
      CaptureCommand::kStart, revoked_generation, Target().target_epoch,
      &admission);
  return Expect(
             revoke == windayflow::capture::CaptureSafetyUpdateResult::kOk &&
                 issue == WDF_CAPTURE_RESULT_POLICY_BLOCKED,
             "disabled revoke did not preserve policy-blocked admission") &&
         Expect(ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
                "disabled revoke created a pseudo stop run") &&
         Expect(controller.state() == WDF_CAPTURE_STATE_UNAVAILABLE &&
                    controller.reserved_event_count() == 0 &&
                    !backend_state->called.load(),
                "disabled revoke changed runtime state or backend");
}

bool TestSyntheticStopRevokesWhenRequiredQueueIsFull() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.event_queue_capacity = 2;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(false, false, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_UNAVAILABLE,
                   "saturated-stop fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation),
              "saturated-stop fixture authorization failed") ||
      !Expect(windayflow::capture::CaptureInstanceControllerTestPeer::
                  AppendRequiredEvent(controller) &&
                  windayflow::capture::CaptureInstanceControllerTestPeer::
                      AppendRequiredEvent(controller),
              "saturated-stop fixture did not fill its required queue") ||
      !Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "saturated synthetic stop was not accepted") ||
      !Expect(controller.state() == WDF_CAPTURE_STATE_STOPPING,
              "saturated synthetic stop did not enter STOPPING") ||
      !Expect(controller.WaitStopped(2'000) ==
                  WDF_CAPTURE_RESULT_INTERNAL_ERROR,
              "saturated synthetic stop hid its event failure") ||
      !Expect(controller.state() == WDF_CAPTURE_STATE_STOPPED,
              "saturated synthetic stop did not finish revoke")) {
    return false;
  }

  windayflow::capture::CaptureCommandAdmission admission;
  const auto snapshot = controller.safety_snapshot();
  return Expect(controller.IssueAdmission(
                    CaptureCommand::kStart, snapshot.persistence_generation,
                    Target().target_epoch, &admission) ==
                    WDF_CAPTURE_RESULT_POLICY_BLOCKED,
                "saturated synthetic stop left authorization open") &&
         Expect(controller.active_run_id() == 0 &&
                    controller.join_count() == 0 &&
                    controller.reserved_event_count() == 0 &&
                    !backend_state->called.load(),
                "saturated synthetic stop leaked runtime resources");
}

bool Authorize(CaptureInstanceController& controller, uint64_t revision,
               uint64_t* generation) {
  return controller.UpdateRuntimeAuthorization(
             {AllowedPrivacy(revision), Target()}, generation) ==
         windayflow::capture::CaptureSafetyUpdateResult::kOk;
}

bool StartEnabled(CaptureInstanceController& controller, uint64_t generation) {
  windayflow::capture::CaptureCommandAdmission admission;
  return controller.IssueAdmission(CaptureCommand::kStart, generation,
                                   Target().target_epoch, &admission) ==
             WDF_CAPTURE_RESULT_OK &&
         controller.StartAuthorized(admission) == WDF_CAPTURE_RESULT_OK;
}

bool PublishReadyAndExpectRecording(CaptureInstanceController& controller,
                                    const char* message) {
  const uint64_t run_id = controller.active_run_id();
  return Expect(run_id != 0 &&
                    windayflow::capture::CaptureInstanceControllerTestPeer::
                        Checkpoint(
                            controller, run_id,
                            {windayflow::capture::
                                 CaptureWorkerCheckpointKind::kReady,
                             0}),
                "controller rejected an injected Ready checkpoint") &&
         ExpectState(controller, WDF_CAPTURE_STATE_RECORDING, message);
}

bool TestEnabledStateSequenceAndStop() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, false, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "enabled controller omitted its initial state")) {
    return false;
  }

  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "enabled controller did not start")) {
    return false;
  }
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "STARTING was not published before worker readiness") ||
      !PublishReadyAndExpectRecording(
          controller, "Ready checkpoint did not publish RECORDING")) {
    return false;
  }
  const uint64_t run_id = controller.active_run_id();
  const bool stale_checkpoint =
      windayflow::capture::CaptureInstanceControllerTestPeer::Checkpoint(
          controller, run_id + 1U,
          {windayflow::capture::CaptureWorkerCheckpointKind::kReady, 0});
  windayflow::capture::CaptureWorkerRunResult stale_exit;
  stale_exit.reason =
      windayflow::capture::CaptureWorkerExitReason::kDeviceFailure;
  stale_exit.error = WDF_CAPTURE_ERROR_DEVICE_UNAVAILABLE;
  windayflow::capture::CaptureInstanceControllerTestPeer::Exit(
      controller, run_id + 1U, stale_exit);
  if (!Expect(!stale_checkpoint &&
                  ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty &&
                  controller.state() == WDF_CAPTURE_STATE_RECORDING,
              "stale worker callback changed the active run")) {
    return false;
  }

  uint64_t replacement_generation = 0;
  if (!Expect(Authorize(controller, 2, &replacement_generation),
              "authorization replacement failed") ||
      !ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSING,
                            WDF_CAPTURE_REASON_POLICY_BLOCKED,
                            "authorization replacement omitted PAUSING") ||
      !ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSED,
                            WDF_CAPTURE_REASON_POLICY_BLOCKED,
                            "Paused checkpoint did not acknowledge PAUSED")) {
    return false;
  }
  windayflow::capture::CaptureCommandAdmission resume;
  if (!Expect(controller.IssueAdmission(
                  CaptureCommand::kResume, replacement_generation,
                  Target().target_epoch, &resume) == WDF_CAPTURE_RESULT_OK &&
                  controller.ResumeAuthorized(resume) == WDF_CAPTURE_RESULT_OK,
              "enabled controller did not resume") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_RESUMING,
                   "RESUMING state was omitted") ||
      !PublishReadyAndExpectRecording(
          controller, "resumed Ready checkpoint did not publish RECORDING")) {
    return false;
  }

  if (!Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "enabled controller rejected stop") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "STOPPING state was omitted") ||
      !Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
              "enabled controller did not join") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "STOPPED state was omitted")) {
    return false;
  }
  return Expect(controller.join_count() == 1 &&
                    controller.reserved_event_count() == 0 &&
                    backend_state->shutdown_calls.load() == 1,
                "enabled stop leaked a join or event reservation");
}

bool TestStaleDeferredStopCannotStopReplacementRun() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, false, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "stale-stop fixture omitted initial state")) {
    return false;
  }

  uint64_t first_generation = 0;
  if (!Expect(Authorize(controller, 1, &first_generation) &&
                  StartEnabled(controller, first_generation),
              "stale-stop fixture did not start its first run") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "stale-stop first run omitted STARTING") ||
      !PublishReadyAndExpectRecording(
          controller, "stale-stop first run omitted RECORDING")) {
    return false;
  }
  const uint64_t first_run_id = controller.active_run_id();
  if (!Expect(first_run_id != 0,
              "stale-stop first run did not receive an ID") ||
      !Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "stale-stop first run rejected stop") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "stale-stop first run omitted STOPPING") ||
      !Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
              "stale-stop first run did not finish") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "stale-stop first run omitted STOPPED")) {
    return false;
  }

  uint64_t second_generation = 0;
  if (!Expect(Authorize(controller, 2, &second_generation) &&
                  StartEnabled(controller, second_generation),
              "stale-stop fixture did not start its replacement run") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "stale-stop replacement run omitted STARTING") ||
      !PublishReadyAndExpectRecording(
          controller, "stale-stop replacement run omitted RECORDING")) {
    return false;
  }
  const uint64_t second_run_id = controller.active_run_id();
  const wdf_capture_result stale_stop =
      windayflow::capture::CaptureInstanceControllerTestPeer::RequestStop(
          controller, WDF_CAPTURE_REASON_BACKEND_FAULT, first_run_id);
  if (!Expect(second_run_id != 0 && second_run_id != first_run_id,
              "replacement run reused the old run ID") ||
      !Expect(stale_stop == WDF_CAPTURE_RESULT_OK,
              "stale deferred stop was not idempotently ignored") ||
      !Expect(controller.active_run_id() == second_run_id &&
                  controller.state() == WDF_CAPTURE_STATE_RECORDING,
              "stale deferred stop changed the replacement run") ||
      !Expect(ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
              "stale deferred stop published a replacement-run event")) {
    return false;
  }

  return Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
                "replacement run rejected cleanup stop") &&
         ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                     "replacement cleanup omitted STOPPING") &&
         Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
                "replacement run did not stop") &&
         ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                     "replacement cleanup omitted STOPPED") &&
         Expect(controller.join_count() == 2 &&
                    controller.reserved_event_count() == 0 &&
                    backend_state->shutdown_calls.load() == 2,
                "stale-stop fixture leaked a join or reservation");
}

bool TestWaitLeaderTimeoutAndFollowerTakeover() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, true, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "takeover fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "takeover fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "takeover fixture omitted STARTING") ||
      !PublishReadyAndExpectRecording(
          controller, "takeover fixture omitted RECORDING")) {
    return false;
  }
  {
    std::unique_lock lock(backend_state->mutex);
    if (!backend_state->changed.wait_for(
            lock, std::chrono::seconds(1),
            [&backend_state] { return backend_state->acquire_entered; })) {
      return Expect(false, "worker never entered the blocking backend");
    }
  }
  if (!Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "takeover fixture rejected stop") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "takeover fixture omitted STOPPING") ||
      !Expect(controller.WaitStopped(10) == WDF_CAPTURE_RESULT_TIMEOUT,
              "first wait leader did not time out")) {
    return false;
  }

  auto first_follower = std::async(std::launch::async, [&controller] {
    return controller.WaitStopped(2'000);
  });
  auto second_follower = std::async(std::launch::async, [&controller] {
    return controller.WaitStopped(2'000);
  });
  std::this_thread::sleep_for(std::chrono::milliseconds(20));
  {
    std::lock_guard lock(backend_state->mutex);
    backend_state->release_acquire = true;
  }
  backend_state->changed.notify_all();
  const wdf_capture_result first_result = first_follower.get();
  const wdf_capture_result second_result = second_follower.get();
  if (!Expect(first_result == WDF_CAPTURE_RESULT_OK &&
                  second_result == WDF_CAPTURE_RESULT_OK,
              "wait followers did not share the completed result") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "takeover path omitted STOPPED")) {
    return false;
  }
  return Expect(controller.join_count() == 1 &&
                    controller.reserved_event_count() == 0,
                "wait takeover joined twice or leaked reservations");
}

bool TestPausingReasonTracksLatestBlockingAuthorization() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  WorkerCheckpointGate checkpoint_gate;
  g_worker_checkpoint_gate.store(&checkpoint_gate);
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, false, backend_state));
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetWorkerCheckpointHook(controller, &HoldPausedCheckpoint);
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "pause-reason fixture omitted initial state")) {
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "pause-reason fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "pause-reason fixture omitted STARTING") ||
      !PublishReadyAndExpectRecording(
          controller, "pause-reason fixture omitted RECORDING")) {
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  auto release_checkpoint = [&checkpoint_gate] {
    {
      std::lock_guard lock(checkpoint_gate.mutex);
      checkpoint_gate.release = true;
    }
    checkpoint_gate.changed.notify_all();
  };

  uint64_t blocked_generation = 0;
  if (!Expect(controller.UpdateRuntimeAuthorization(
                  {BlockedPrivacy(2), std::nullopt}, &blocked_generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "blocking authorization replacement failed") ||
      !ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSING,
                            WDF_CAPTURE_REASON_SESSION_LOCKED,
                            "blocking replacement omitted PAUSING reason")) {
    release_checkpoint();
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  bool checkpoint_entered = false;
  {
    std::unique_lock lock(checkpoint_gate.mutex);
    checkpoint_entered = checkpoint_gate.changed.wait_for(
        lock, std::chrono::seconds(1),
        [&checkpoint_gate] { return checkpoint_gate.entered; });
  }
  if (!checkpoint_entered) {
    release_checkpoint();
    g_worker_checkpoint_gate.store(nullptr);
    return Expect(false, "Paused checkpoint did not reach its barrier");
  }
  uint64_t application_blocked_generation = 0;
  if (!Expect(controller.UpdateRuntimeAuthorization(
                  {ApplicationBlockedPrivacy(3), std::nullopt},
                  &application_blocked_generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "replacement blocking a later application failed")) {
    release_checkpoint();
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  uint64_t allowed_generation = 0;
  if (!Expect(controller.UpdateRuntimeAuthorization(
                  {AllowedPrivacy(4), Target()}, &allowed_generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "allowing authorization replacement failed")) {
    release_checkpoint();
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  release_checkpoint();
  if (!ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSED,
                             WDF_CAPTURE_REASON_EXCLUDED_APPLICATION,
                             "PAUSED lost its latest blocking reason")) {
    g_worker_checkpoint_gate.store(nullptr);
    return false;
  }
  const bool stopped =
      Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
             "pause-reason fixture rejected cleanup stop") &&
      ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                  "pause-reason cleanup omitted STOPPING") &&
      Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
             "pause-reason fixture did not stop") &&
      ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                  "pause-reason cleanup omitted STOPPED");
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetWorkerCheckpointHook(controller, nullptr);
  g_worker_checkpoint_gate.store(nullptr);
  return stopped;
}

bool TestPausingUserReasonSurvivesAuthorizationUpdates() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  WorkerCheckpointGate checkpoint_gate;
  g_worker_checkpoint_gate.store(&checkpoint_gate);
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, false, backend_state));
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetWorkerCheckpointHook(controller, &HoldPausedCheckpoint);
  auto release_checkpoint = [&checkpoint_gate] {
    {
      std::lock_guard lock(checkpoint_gate.mutex);
      checkpoint_gate.release = true;
    }
    checkpoint_gate.changed.notify_all();
  };
  auto detach_checkpoint = [&controller] {
    windayflow::capture::CaptureInstanceControllerTestPeer::
        SetWorkerCheckpointHook(controller, nullptr);
    g_worker_checkpoint_gate.store(nullptr);
  };

  uint64_t generation = 0;
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "user-reason fixture omitted initial state") ||
      !Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "user-reason fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "user-reason fixture omitted STARTING") ||
      !PublishReadyAndExpectRecording(
          controller, "user-reason fixture omitted RECORDING") ||
      !Expect(controller.Pause() == WDF_CAPTURE_RESULT_OK,
              "user-reason fixture rejected Pause") ||
      !ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSING,
                            WDF_CAPTURE_REASON_USER_PAUSED,
                            "user Pause omitted its PAUSING reason")) {
    release_checkpoint();
    detach_checkpoint();
    return false;
  }

  bool checkpoint_entered = false;
  {
    std::unique_lock lock(checkpoint_gate.mutex);
    checkpoint_entered = checkpoint_gate.changed.wait_for(
        lock, std::chrono::seconds(1),
        [&checkpoint_gate] { return checkpoint_gate.entered; });
  }
  uint64_t blocked_generation = 0;
  uint64_t allowed_generation = 0;
  if (!Expect(checkpoint_entered,
              "user Pause did not reach its Paused checkpoint") ||
      !Expect(controller.UpdateRuntimeAuthorization(
                  {BlockedPrivacy(2), std::nullopt}, &blocked_generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "user-reason blocking replacement failed") ||
      !Expect(controller.UpdateRuntimeAuthorization(
                  {AllowedPrivacy(3), Target()}, &allowed_generation) ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "user-reason allowing replacement failed")) {
    release_checkpoint();
    detach_checkpoint();
    return false;
  }

  release_checkpoint();
  if (!ExpectStateAndReason(controller, WDF_CAPTURE_STATE_PAUSED,
                            WDF_CAPTURE_REASON_USER_PAUSED,
                            "authorization updates replaced USER_PAUSED")) {
    detach_checkpoint();
    return false;
  }

  if (!Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "user-reason fixture rejected cleanup stop") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "user-reason cleanup omitted STOPPING")) {
    detach_checkpoint();
    return false;
  }
  const bool stopped =
      Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
             "user-reason fixture did not stop") &&
      ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                  "user-reason cleanup omitted STOPPED");
  detach_checkpoint();
  return stopped;
}

bool TestLateReadyDoesNotEscapeAuthorizationPause() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  WorkerCheckpointGate checkpoint_gate;
  g_worker_checkpoint_gate.store(&checkpoint_gate);
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, true, backend_state));
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetWorkerCheckpointHook(controller, &HoldPausedCheckpoint);
  auto release_acquire = [&backend_state] {
    {
      std::lock_guard lock(backend_state->mutex);
      backend_state->release_acquire = true;
    }
    backend_state->changed.notify_all();
  };
  auto release_checkpoint = [&checkpoint_gate] {
    {
      std::lock_guard lock(checkpoint_gate.mutex);
      checkpoint_gate.release = true;
    }
    checkpoint_gate.changed.notify_all();
  };
  auto detach_checkpoint = [&controller] {
    windayflow::capture::CaptureInstanceControllerTestPeer::
        SetWorkerCheckpointHook(controller, nullptr);
    g_worker_checkpoint_gate.store(nullptr);
  };
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "late-ready fixture omitted initial state")) {
    release_acquire();
    release_checkpoint();
    detach_checkpoint();
    return false;
  }

  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "late-ready fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "late-ready fixture omitted STARTING")) {
    release_acquire();
    release_checkpoint();
    detach_checkpoint();
    return false;
  }
  {
    std::unique_lock lock(backend_state->mutex);
    if (!backend_state->changed.wait_for(
            lock, std::chrono::seconds(1),
            [&backend_state] { return backend_state->acquire_entered; })) {
      backend_state->release_acquire = true;
      lock.unlock();
      backend_state->changed.notify_all();
      release_checkpoint();
      detach_checkpoint();
      return Expect(false,
                    "late-ready worker did not enter first-frame acquisition");
    }
  }

  uint64_t replacement_generation = 0;
  auto authorization_update = std::async(
      std::launch::async,
      [&controller, &replacement_generation] {
        return controller.UpdateRuntimeAuthorization(
            {AllowedPrivacy(2), Target()}, &replacement_generation);
      });
  if (!ExpectState(controller, WDF_CAPTURE_STATE_PAUSING,
                   "late-ready authorization did not enter PAUSING")) {
    release_acquire();
    release_checkpoint();
    static_cast<void>(authorization_update.get());
    detach_checkpoint();
    return false;
  }
  release_acquire();
  const auto update_result = authorization_update.get();
  bool checkpoint_entered = false;
  {
    std::unique_lock lock(checkpoint_gate.mutex);
    checkpoint_entered = checkpoint_gate.changed.wait_for(
        lock, std::chrono::seconds(1),
        [&checkpoint_gate] { return checkpoint_gate.entered; });
  }
  if (!Expect(update_result ==
                  windayflow::capture::CaptureSafetyUpdateResult::kOk,
              "late-ready authorization replacement failed") ||
      !Expect(checkpoint_entered,
              "late-ready worker did not reach its Paused checkpoint")) {
    release_checkpoint();
    detach_checkpoint();
    return false;
  }

  const uint64_t run_id = controller.active_run_id();
  const uint64_t pause_epoch =
      windayflow::capture::CaptureInstanceControllerTestPeer::PauseEpoch(
          controller);
  if (!Expect(run_id != 0 && pause_epoch != 0,
              "late-ready fixture lost its run or pause epoch") ||
      !Expect(windayflow::capture::CaptureInstanceControllerTestPeer::
                  Checkpoint(
                      controller, run_id,
                      {windayflow::capture::CaptureWorkerCheckpointKind::kReady,
                       0}),
              "PAUSING rejected the delayed Ready checkpoint") ||
      !Expect(controller.state() == WDF_CAPTURE_STATE_PAUSING &&
                  ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
              "delayed Ready escaped PAUSING or published RECORDING")) {
    release_checkpoint();
    detach_checkpoint();
    return false;
  }
  release_checkpoint();
  if (!ExpectState(controller, WDF_CAPTURE_STATE_PAUSED,
                   "late-ready fixture omitted PAUSED") ||
      !Expect(windayflow::capture::CaptureInstanceControllerTestPeer::
                  Checkpoint(
                      controller, run_id,
                      {windayflow::capture::CaptureWorkerCheckpointKind::kReady,
                       0}),
              "PAUSED rejected the delayed Ready checkpoint") ||
      !Expect(controller.state() == WDF_CAPTURE_STATE_PAUSED &&
                  ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
              "delayed Ready escaped PAUSED or published RECORDING")) {
    detach_checkpoint();
    return false;
  }

  if (!Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "late-ready fixture rejected cleanup stop") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "late-ready cleanup omitted STOPPING")) {
    detach_checkpoint();
    return false;
  }
  const bool stopped =
      Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
             "late-ready fixture did not stop") &&
      ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                  "late-ready cleanup omitted STOPPED") &&
      Expect(controller.join_count() == 1 &&
                 controller.reserved_event_count() == 0,
             "late-ready cleanup leaked a join or reservation");
  detach_checkpoint();
  return stopped;
}

bool TestStopRevokesAuthorizationBeforeWorkerStart() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(true, false, backend_state));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "pre-start stop fixture omitted initial state")) {
    return false;
  }

  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation),
              "pre-start stop fixture was not authorized") ||
      !Expect(controller.RequestStop() == WDF_CAPTURE_RESULT_OK,
              "pre-start stop request failed") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPING,
                   "pre-start stop omitted STOPPING") ||
      !Expect(controller.WaitStopped(2'000) == WDF_CAPTURE_RESULT_OK,
              "pre-start stop did not finalize revoke") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "pre-start stop omitted STOPPED")) {
    return false;
  }

  windayflow::capture::CaptureCommandAdmission admission;
  return Expect(controller.IssueAdmission(
                    CaptureCommand::kStart,
                    controller.safety_snapshot().persistence_generation,
                    Target().target_epoch, &admission) ==
                    WDF_CAPTURE_RESULT_POLICY_BLOCKED,
                "pre-start stop left command admission open") &&
         Expect(controller.join_count() == 0 &&
                    controller.reserved_event_count() == 0 &&
                    !backend_state->called.load(),
                "pre-start stop touched worker resources or leaked capacity");
}

bool TestDestructorJoinsActiveWorker() {
  auto backend_state = std::make_shared<BackendState>();
  std::thread release_thread;
  {
    CaptureInstanceControllerConfiguration configuration;
    configuration.activation_mode = CaptureActivationMode::kEnabled;
    configuration.event_queue_capacity = 16;
    CaptureInstanceController controller(
        configuration,
        std::make_unique<TestBackend>(true, true, backend_state));
    if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                     "destructor fixture omitted initial state")) {
      return false;
    }
    uint64_t generation = 0;
    if (!Expect(Authorize(controller, 1, &generation) &&
                    StartEnabled(controller, generation),
                "destructor fixture did not start") ||
        !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                     "destructor fixture omitted STARTING") ||
        !PublishReadyAndExpectRecording(
            controller, "destructor fixture omitted RECORDING")) {
      return false;
    }
    {
      std::unique_lock lock(backend_state->mutex);
      if (!backend_state->changed.wait_for(
              lock, std::chrono::seconds(1),
              [&backend_state] { return backend_state->acquire_entered; })) {
        return Expect(false, "destructor worker did not enter backend");
      }
    }
    release_thread = std::thread([backend_state] {
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
      {
        std::lock_guard lock(backend_state->mutex);
        backend_state->release_acquire = true;
      }
      backend_state->changed.notify_all();
    });
  }
  release_thread.join();
  return Expect(backend_state->shutdown_calls.load() == 1,
                "controller destruction did not join and shut down worker");
}

bool TestFatalExitPublishesErrorWithoutFakeStopping() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(
          true, false, backend_state,
          CaptureWorkerBackendResult::kDeviceUnavailable));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "fatal fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "fatal fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "fatal fixture omitted STARTING")) {
    return false;
  }
  const ReadEvent failure = ReadOne(controller);
  if (!Expect(failure.result == CaptureEventReadResult::kSuccess &&
                  failure.event.kind == WDF_CAPTURE_EVENT_ERROR &&
                  failure.event.state == WDF_CAPTURE_STATE_FAULTED &&
                  failure.event.error == WDF_CAPTURE_ERROR_DEVICE_UNAVAILABLE &&
                  failure.detail ==
                      "Capture worker exited because display acquisition failed.",
              "fatal worker exit did not publish ERROR/FAULTED") ||
      !Expect(!windayflow::capture::CaptureInstanceControllerTestPeer::
                  Checkpoint(
                      controller, controller.active_run_id(),
                      {windayflow::capture::CaptureWorkerCheckpointKind::kReady,
                       0}) &&
                  controller.state() == WDF_CAPTURE_STATE_FAULTED,
              "FAULTED accepted an illegal Ready checkpoint") ||
      !Expect(ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
              "fatal worker exit published fake STOPPING") ||
      !Expect(controller.WaitStopped(2'000) ==
                  WDF_CAPTURE_RESULT_INTERNAL_ERROR,
              "fatal worker result was not cached for waiters")) {
    return false;
  }
  const ReadEvent stopped = ReadOne(controller);
  if (!Expect(stopped.result == CaptureEventReadResult::kSuccess &&
                  stopped.event.kind == WDF_CAPTURE_EVENT_STATE_CHANGED &&
                  stopped.event.state == WDF_CAPTURE_STATE_STOPPED &&
                  stopped.event.reason == WDF_CAPTURE_REASON_BACKEND_FAULT &&
                  stopped.detail ==
                      "Capture worker exited because display acquisition failed.",
              "fatal join omitted its terminal failure detail")) {
    return false;
  }
  return Expect(controller.join_count() == 1 &&
                    controller.reserved_event_count() == 0,
                "fatal exit leaked join or event reservations");
}

bool TestTerminalFailureIsSharedWithConcurrentWaiter() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(
          true, false, backend_state,
          CaptureWorkerBackendResult::kDeviceUnavailable));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "terminal-race fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "terminal-race fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "terminal-race fixture omitted STARTING")) {
    return false;
  }
  const ReadEvent failure = ReadOne(controller);
  if (!Expect(failure.result == CaptureEventReadResult::kSuccess &&
                  failure.event.kind == WDF_CAPTURE_EVENT_ERROR &&
                  failure.event.state == WDF_CAPTURE_STATE_FAULTED,
              "terminal-race fixture omitted its fatal event")) {
    return false;
  }

  TerminalFinalizationGate gate;
  g_terminal_finalization_gate.store(&gate);
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetTerminalFinalizationHook(controller, &HoldTerminalFinalization);
  auto leader = std::async(std::launch::async, [&controller] {
    return controller.WaitStopped(2'000);
  });
  bool hook_entered = false;
  {
    std::unique_lock lock(gate.mutex);
    hook_entered = gate.changed.wait_for(
        lock, std::chrono::seconds(1), [&gate] { return gate.entered; });
  }

  std::promise<void> follower_started;
  auto follower_started_future = follower_started.get_future();
  auto follower = std::async(
      std::launch::async, [&controller, &follower_started] {
        follower_started.set_value();
        return controller.WaitStopped(2'000);
      });
  follower_started_future.wait();
  const bool follower_blocked =
      follower.wait_for(std::chrono::milliseconds(20)) ==
      std::future_status::timeout;
  {
    std::lock_guard lock(gate.mutex);
    gate.release = true;
  }
  gate.changed.notify_all();

  const wdf_capture_result leader_result = leader.get();
  const wdf_capture_result follower_result = follower.get();
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetTerminalFinalizationHook(controller, nullptr);
  g_terminal_finalization_gate.store(nullptr);
  return Expect(hook_entered,
                "terminal finalization hook was not reached") &&
         Expect(follower_blocked,
                "concurrent waiter escaped before terminal finalization") &&
         Expect(leader_result == WDF_CAPTURE_RESULT_INTERNAL_ERROR &&
                    follower_result == WDF_CAPTURE_RESULT_INTERNAL_ERROR,
                "terminal failure result was not shared by waiters") &&
         ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                     "terminal-race fixture omitted STOPPED") &&
         Expect(controller.join_count() == 1 &&
                    controller.reserved_event_count() == 0,
                "terminal-race fixture leaked a join or reservation");
}

bool TestTerminalExceptionRelinquishesWaitLeadership() {
  auto backend_state = std::make_shared<BackendState>();
  CaptureInstanceControllerConfiguration configuration;
  configuration.activation_mode = CaptureActivationMode::kEnabled;
  configuration.event_queue_capacity = 16;
  CaptureInstanceController controller(
      configuration,
      std::make_unique<TestBackend>(
          true, false, backend_state,
          CaptureWorkerBackendResult::kDeviceUnavailable));
  if (!ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                   "leader-exception fixture omitted initial state")) {
    return false;
  }
  uint64_t generation = 0;
  if (!Expect(Authorize(controller, 1, &generation) &&
                  StartEnabled(controller, generation),
              "leader-exception fixture did not start") ||
      !ExpectState(controller, WDF_CAPTURE_STATE_STARTING,
                   "leader-exception fixture omitted STARTING")) {
    return false;
  }
  const ReadEvent failure = ReadOne(controller);
  if (!Expect(failure.result == CaptureEventReadResult::kSuccess &&
                  failure.event.kind == WDF_CAPTURE_EVENT_ERROR &&
                  failure.event.state == WDF_CAPTURE_STATE_FAULTED,
              "leader-exception fixture omitted its fatal event")) {
    return false;
  }

  g_throw_terminal_finalization_once.store(true);
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetTerminalFinalizationHook(controller, &ThrowTerminalFinalizationOnce);
  const wdf_capture_result failed_leader = controller.WaitStopped(2'000);
  const wdf_capture_result takeover = controller.WaitStopped(2'000);
  windayflow::capture::CaptureInstanceControllerTestPeer::
      SetTerminalFinalizationHook(controller, nullptr);
  return Expect(failed_leader == WDF_CAPTURE_RESULT_INTERNAL_ERROR,
                "injected terminal failure escaped as success") &&
         Expect(takeover == WDF_CAPTURE_RESULT_INTERNAL_ERROR,
                "follower did not take over terminal finalization") &&
         ExpectState(controller, WDF_CAPTURE_STATE_STOPPED,
                     "leader-exception takeover omitted STOPPED") &&
         Expect(ReadOne(controller, 0).result == CaptureEventReadResult::kEmpty,
                "leader-exception takeover duplicated STOPPED") &&
         Expect(controller.join_count() == 1 &&
                    controller.reserved_event_count() == 0,
                "leader-exception fixture leaked a join or reservation");
}

}  // namespace

int main() {
  if (!TestDisabledConsumesAdmissionWithoutStarting() ||
      !TestDisabledRevokeDoesNotCreateStopRun() ||
      !TestSyntheticStopRevokesWhenRequiredQueueIsFull() ||
      !TestEnabledStateSequenceAndStop() ||
      !TestStaleDeferredStopCannotStopReplacementRun() ||
      !TestWaitLeaderTimeoutAndFollowerTakeover() ||
      !TestPausingReasonTracksLatestBlockingAuthorization() ||
      !TestPausingUserReasonSurvivesAuthorizationUpdates() ||
      !TestLateReadyDoesNotEscapeAuthorizationPause() ||
      !TestStopRevokesAuthorizationBeforeWorkerStart() ||
      !TestDestructorJoinsActiveWorker() ||
      !TestFatalExitPublishesErrorWithoutFakeStopping() ||
      !TestTerminalFailureIsSharedWithConcurrentWaiter() ||
      !TestTerminalExceptionRelinquishesWaitLeadership()) {
    return 1;
  }
  std::cout << "capture instance controller tests passed\n";
  return 0;
}

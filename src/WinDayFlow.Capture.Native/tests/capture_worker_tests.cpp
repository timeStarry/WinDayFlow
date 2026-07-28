#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "capture_worker.h"

namespace {

using windayflow::capture::BgraFrame;
using windayflow::capture::CaptureCommand;
using windayflow::capture::CaptureCommandAdmission;
using windayflow::capture::CaptureCommandAdmissionPermit;
using windayflow::capture::CaptureCommandAdmissionResult;
using windayflow::capture::CaptureAuthorizationScope;
using windayflow::capture::CaptureEventQueue;
using windayflow::capture::CaptureEventReadResult;
using windayflow::capture::CaptureRuntimeOwner;
using windayflow::capture::CaptureRuntimePauseResult;
using windayflow::capture::CaptureRuntimeStopResult;
using windayflow::capture::CaptureRuntimeWaitResult;
using windayflow::capture::CaptureSafetyCore;
using windayflow::capture::CaptureSafetyUpdateResult;
using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::CaptureWorker;
using windayflow::capture::CaptureWorkerBackend;
using windayflow::capture::CaptureWorkerBackendResult;
using windayflow::capture::CaptureWorkerCheckpoint;
using windayflow::capture::CaptureWorkerCheckpointKind;
using windayflow::capture::CaptureWorkerCheckpointSink;
using windayflow::capture::CaptureWorkerConfiguration;
using windayflow::capture::CaptureWorkerExitReason;
using windayflow::capture::CaptureWorkerPublication;
using windayflow::capture::ChunkManifest;
using windayflow::capture::MfH264ChunkWriterConfig;
using windayflow::capture::PersistenceToken;
using windayflow::capture::PrivacyContext;
using windayflow::capture::RuntimeAuthorization;

constexpr uint32_t kTestTimeoutMs = 1'000;

bool Expect(bool condition, std::string_view message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

PrivacyContext AllowedPrivacy(uint64_t revision) {
  PrivacyContext context;
  context.consent_granted = WDF_CAPTURE_POLICY_ALLOW;
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

PrivacyContext BlockedPrivacy(uint64_t revision) {
  PrivacyContext context = AllowedPrivacy(revision);
  context.application_allowed = WDF_CAPTURE_POLICY_BLOCK;
  return context;
}

CaptureTargetIdentity TestTarget() {
  return CaptureTargetIdentity{100, 200, 300,
                               1,   400, std::wstring(L"\\\\.\\DISPLAY1")};
}

CaptureTargetIdentity TestDisplayWideTarget() {
  return CaptureTargetIdentity{
      0,
      0,
      0,
      1,
      400,
      std::wstring(L"\\\\.\\DISPLAY1"),
      CaptureAuthorizationScope::kDisplayWide,
  };
}

RuntimeAuthorization AllowedAuthorization(uint64_t revision,
                                          const CaptureTargetIdentity& target) {
  return RuntimeAuthorization{AllowedPrivacy(revision), target};
}

std::atomic<uint64_t> g_next_nonce{1'000};

bool NextNonce(uint64_t* low, uint64_t* high) {
  if (low == nullptr || high == nullptr) {
    return false;
  }
  const uint64_t nonce = g_next_nonce.fetch_add(1) + 1;
  *low = nonce;
  *high = nonce + 10'000;
  return true;
}

CaptureWorkerConfiguration TestConfiguration() {
  CaptureWorkerConfiguration configuration;
  configuration.policy.capture_interval_ms = 250;
  configuration.policy.context_interval_ms = 250;
  configuration.policy.chunk_duration_ms = 10'000;
  configuration.maximum_width = 2;
  configuration.maximum_height = 2;
  configuration.acquire_timeout_ms = 0;
  configuration.topology_retry_ms = 5;
  configuration.topology_retry_limit = 4;
  configuration.rollback_retry_limit = 3;
  configuration.rollback_retry_delay_ms = 0;
  configuration.average_bitrate = 1'000;
  configuration.maximum_encoded_chunk_bytes = 1'024;
  return configuration;
}

enum class FaultStage {
  kNone,
  kInitialize,
  kAcquire,
  kTransform,
  kBegin,
  kEncode,
  kFinalize,
  kPrepare,
  kCommit,
};

class WorkerSignals final {
 public:
  void Notify() { changed_.notify_all(); }

  template <typename Predicate>
  bool WaitFor(Predicate predicate, uint32_t timeout_ms = kTestTimeoutMs) {
    std::unique_lock lock(mutex_);
    return changed_.wait_for(lock, std::chrono::milliseconds(timeout_ms),
                             std::move(predicate));
  }

 private:
  std::mutex mutex_;
  std::condition_variable changed_;
};

class CheckpointLog final {
 public:
  bool Push(const CaptureWorkerCheckpoint& checkpoint) {
    {
      std::lock_guard lock(mutex_);
      checkpoints_.push_back(checkpoint);
    }
    changed_.notify_all();
    return true;
  }

  bool WaitForSize(size_t size) {
    std::unique_lock lock(mutex_);
    return changed_.wait_for(
        lock, std::chrono::milliseconds(kTestTimeoutMs),
        [this, size] { return checkpoints_.size() >= size; });
  }

  std::vector<CaptureWorkerCheckpoint> Values() const {
    std::lock_guard lock(mutex_);
    return checkpoints_;
  }

 private:
  mutable std::mutex mutex_;
  std::condition_variable changed_;
  std::vector<CaptureWorkerCheckpoint> checkpoints_;
};

struct FakePublicationState {
  std::string identifier;
  CaptureSafetyCore* safety = nullptr;
  WorkerSignals* signals = nullptr;
  bool invalidate_on_commit = false;
  std::atomic<uint32_t> commit_calls{0};
  std::atomic<uint32_t> acknowledge_calls{0};
  std::atomic<uint32_t> rollback_calls{0};
  std::atomic<uint32_t> rollback_failures_remaining{0};
  std::atomic<bool> always_fail_rollback{false};
  std::atomic<bool> committed{false};
  std::atomic<bool> rolled_back{false};
};

class FakePublication final : public CaptureWorkerPublication {
 public:
  explicit FakePublication(std::shared_ptr<FakePublicationState> state)
      : state_(std::move(state)) {}

  bool committed() const noexcept override { return state_->committed.load(); }

  const std::string& artifact_identifier() const noexcept override {
    return state_->identifier;
  }

  CaptureWorkerBackendResult Commit() noexcept override {
    state_->commit_calls.fetch_add(1);
    state_->committed.store(true);
    if (state_->invalidate_on_commit && state_->safety != nullptr) {
      static_cast<void>(state_->safety->InvalidateAuthorizationAdmission());
    }
    state_->signals->Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  void Acknowledge() noexcept override {
    state_->acknowledge_calls.fetch_add(1);
    state_->signals->Notify();
  }

  CaptureWorkerBackendResult Rollback() noexcept override {
    state_->rollback_calls.fetch_add(1);
    if (state_->always_fail_rollback.load()) {
      state_->signals->Notify();
      return CaptureWorkerBackendResult::kStorageFailure;
    }

    uint32_t remaining = state_->rollback_failures_remaining.load();
    while (remaining > 0) {
      if (state_->rollback_failures_remaining.compare_exchange_weak(
              remaining, remaining - 1U)) {
        state_->signals->Notify();
        return CaptureWorkerBackendResult::kStorageFailure;
      }
    }

    state_->committed.store(false);
    state_->rolled_back.store(true);
    state_->signals->Notify();
    return CaptureWorkerBackendResult::kOk;
  }

 private:
  std::shared_ptr<FakePublicationState> state_;
};

class FakeBackend final : public CaptureWorkerBackend {
 public:
  explicit FakeBackend(CaptureSafetyCore& safety) : safety_(safety) {}

  std::optional<CaptureTargetIdentity> ObserveTarget(
      const CaptureTargetIdentity& expected) noexcept override {
    const uint32_t call = observe_calls.fetch_add(1) + 1U;
    signals_.Notify();
    if (missing_observation_call != 0 && call == missing_observation_call) {
      return std::nullopt;
    }
    return expected;
  }

  CaptureWorkerBackendResult InitializeAcquisition(
      const CaptureTargetIdentity&) noexcept override {
    const uint32_t call = initialize_calls.fetch_add(1) + 1U;
    signals_.Notify();
    if (call <= rebuild_initializations) {
      return CaptureWorkerBackendResult::kRebuildRequired;
    }
    InvalidateAt(FaultStage::kInitialize);
    return CaptureWorkerBackendResult::kOk;
  }

  CaptureWorkerBackendResult AcquireFrame(uint32_t,
                                          BgraFrame* frame) noexcept override {
    acquire_calls.fetch_add(1);
    if (frame == nullptr) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    const CaptureWorkerBackendResult result = acquire_result.load();
    if (result != CaptureWorkerBackendResult::kOk) {
      signals_.Notify();
      return result;
    }
    frame->width = 2;
    frame->height = 2;
    frame->pixels.assign(16, 0x2A);
    AdvanceClocks(acquire_clock_advance_ms.load());
    InvalidateAt(FaultStage::kAcquire);
    signals_.Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  void ResetAcquisition() noexcept override {
    reset_acquisition_calls.fetch_add(1);
    signals_.Notify();
  }

  CaptureWorkerBackendResult TransformFrame(
      const BgraFrame& source, uint32_t, uint32_t,
      BgraFrame* destination) noexcept override {
    transform_calls.fetch_add(1);
    if (destination == nullptr) {
      return CaptureWorkerBackendResult::kInternalFailure;
    }
    *destination = source;
    AdvanceClocks(transform_clock_advance_ms.load());
    InvalidateAt(FaultStage::kTransform);
    signals_.Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  CaptureWorkerBackendResult BeginChunk(
      const MfH264ChunkWriterConfig& configuration) noexcept override {
    begin_calls.fetch_add(1);
    {
      std::lock_guard lock(records_mutex_);
      writer_configurations_.push_back(configuration);
    }
    InvalidateAt(FaultStage::kBegin);
    signals_.Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  CaptureWorkerBackendResult EncodeFrame(
      std::span<const uint8_t> top_down_bgra,
      int64_t timestamp_ticks) noexcept override {
    if (top_down_bgra.size() != 16U || timestamp_ticks < 0) {
      return CaptureWorkerBackendResult::kInvalidFrame;
    }
    const uint32_t call = encode_calls.fetch_add(1) + 1U;
    InvalidateAt(FaultStage::kEncode);
    signals_.Notify();
    if (block_first_encode && call == 1U) {
      std::unique_lock lock(gate_mutex_);
      static_cast<void>(
          gate_changed_.wait_for(lock, std::chrono::milliseconds(2'000),
                                 [this] { return first_encode_released_; }));
    }
    return CaptureWorkerBackendResult::kOk;
  }

  CaptureWorkerBackendResult FinalizeChunk(
      int64_t end_timestamp_ticks,
      std::vector<uint8_t>* encoded_mp4) noexcept override {
    finalize_calls.fetch_add(1);
    if (encoded_mp4 == nullptr || end_timestamp_ticks <= 0) {
      return CaptureWorkerBackendResult::kEncoderFailure;
    }
    encoded_mp4->assign({0x00, 0x00, 0x00, 0x01});
    AdvanceClocks(finalize_clock_advance_ms.load());
    InvalidateAt(FaultStage::kFinalize);
    signals_.Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  void ResetChunk() noexcept override {
    reset_chunk_calls.fetch_add(1);
    signals_.Notify();
  }

  bool CreateArtifactId(std::string* artifact_id) noexcept override {
    if (artifact_id == nullptr) {
      return false;
    }
    const uint32_t id = create_artifact_calls.fetch_add(1) + 1U;
    *artifact_id = "chunk-" + std::to_string(id);
    signals_.Notify();
    return true;
  }

  CaptureWorkerBackendResult PreparePublication(
      std::string_view artifact_id, std::span<const uint8_t> encoded_mp4,
      const ChunkManifest& manifest,
      std::unique_ptr<CaptureWorkerPublication>* publication) noexcept
      override {
    prepare_calls.fetch_add(1);
    if (artifact_id.empty() || encoded_mp4.empty() || publication == nullptr) {
      return CaptureWorkerBackendResult::kStorageFailure;
    }

    auto state = std::make_shared<FakePublicationState>();
    state->identifier = "committed/" + std::string(artifact_id) + ".mp4";
    state->safety = &safety_;
    state->signals = &signals_;
    state->invalidate_on_commit = fault_stage == FaultStage::kCommit &&
                                  !invalidation_fired_.exchange(true);
    state->rollback_failures_remaining.store(rollback_failures_before_success);
    state->always_fail_rollback.store(always_fail_rollback);
    *publication = std::make_unique<FakePublication>(state);

    {
      std::lock_guard lock(records_mutex_);
      manifests_.push_back(manifest);
      publications_.push_back(std::move(state));
    }
    InvalidateAt(FaultStage::kPrepare);
    signals_.Notify();
    return CaptureWorkerBackendResult::kOk;
  }

  int64_t SteadyNowMilliseconds() noexcept override {
    const uint32_t call = steady_now_calls.fetch_add(1) + 1U;
    signals_.Notify();
    if (blocked_steady_now_call != 0 && call == blocked_steady_now_call) {
      std::unique_lock lock(gate_mutex_);
      static_cast<void>(
          gate_changed_.wait_for(lock, std::chrono::milliseconds(2'000),
                                 [this] { return steady_now_released_; }));
    }
    const int64_t value = steady_now_ms.load();
    observed_steady_now_ms.store(value);
    signals_.Notify();
    return value;
  }

  int64_t UnixNowMilliseconds() noexcept override {
    return unix_now_ms.load();
  }

  void ShutdownThread() noexcept override {
    shutdown_calls.fetch_add(1);
    signals_.Notify();
  }

  bool WaitForEncodeCount(uint32_t count) {
    return signals_.WaitFor(
        [this, count] { return encode_calls.load() >= count; });
  }

  bool WaitForAcquireCount(uint32_t count) {
    return signals_.WaitFor(
        [this, count] { return acquire_calls.load() >= count; });
  }

  bool WaitForAcknowledgeCount(uint32_t count) {
    return signals_.WaitFor([this, count] {
      const std::vector<std::shared_ptr<FakePublicationState>> records =
          PublicationRecords();
      uint32_t acknowledgements = 0;
      for (const auto& record : records) {
        acknowledgements += record->acknowledge_calls.load();
      }
      return acknowledgements >= count;
    });
  }

  bool WaitForSteadyNowCount(uint32_t count) {
    return signals_.WaitFor(
        [this, count] { return steady_now_calls.load() >= count; });
  }

  bool WaitForObservedSteadyNow(int64_t value) {
    return signals_.WaitFor(
        [this, value] { return observed_steady_now_ms.load() == value; });
  }

  bool WaitForResetChunkCount(uint32_t count) {
    return signals_.WaitFor(
        [this, count] { return reset_chunk_calls.load() >= count; });
  }

  void ReleaseFirstEncode() {
    {
      std::lock_guard lock(gate_mutex_);
      first_encode_released_ = true;
    }
    gate_changed_.notify_all();
  }

  void ReleaseSteadyNow() {
    {
      std::lock_guard lock(gate_mutex_);
      steady_now_released_ = true;
    }
    gate_changed_.notify_all();
  }

  std::vector<ChunkManifest> Manifests() const {
    std::lock_guard lock(records_mutex_);
    return manifests_;
  }

  std::vector<std::shared_ptr<FakePublicationState>> PublicationRecords()
      const {
    std::lock_guard lock(records_mutex_);
    return publications_;
  }

  FaultStage fault_stage = FaultStage::kNone;
  CaptureRuntimeOwner* provisional_pause_runtime = nullptr;
  bool request_provisional_pause_on_invalidation = false;
  uint32_t rebuild_initializations = 0;
  uint32_t missing_observation_call = 0;
  uint32_t rollback_failures_before_success = 0;
  bool always_fail_rollback = false;
  bool block_first_encode = false;
  uint32_t blocked_steady_now_call = 0;
  std::atomic<CaptureWorkerBackendResult> acquire_result{
      CaptureWorkerBackendResult::kOk};
  std::atomic<int64_t> acquire_clock_advance_ms{0};
  std::atomic<int64_t> finalize_clock_advance_ms{0};
  std::atomic<int64_t> transform_clock_advance_ms{0};
  std::atomic<int64_t> steady_now_ms{1'000};
  std::atomic<int64_t> unix_now_ms{1'700'000'000'000};
  std::atomic<int64_t> observed_steady_now_ms{
      std::numeric_limits<int64_t>::min()};

  std::atomic<uint32_t> observe_calls{0};
  std::atomic<uint32_t> initialize_calls{0};
  std::atomic<uint32_t> acquire_calls{0};
  std::atomic<uint32_t> reset_acquisition_calls{0};
  std::atomic<uint32_t> transform_calls{0};
  std::atomic<uint32_t> begin_calls{0};
  std::atomic<uint32_t> encode_calls{0};
  std::atomic<uint32_t> finalize_calls{0};
  std::atomic<uint32_t> reset_chunk_calls{0};
  std::atomic<uint32_t> create_artifact_calls{0};
  std::atomic<uint32_t> prepare_calls{0};
  std::atomic<uint32_t> steady_now_calls{0};
  std::atomic<uint32_t> shutdown_calls{0};

 private:
  void AdvanceClocks(int64_t advance_ms) noexcept {
    if (advance_ms > 0) {
      steady_now_ms.fetch_add(advance_ms);
      unix_now_ms.fetch_add(advance_ms);
    }
  }

  void InvalidateAt(FaultStage stage) noexcept {
    if (fault_stage == stage && !invalidation_fired_.exchange(true)) {
      if (request_provisional_pause_on_invalidation &&
          provisional_pause_runtime != nullptr) {
        static_cast<void>(provisional_pause_runtime->RequestPause());
      }
      static_cast<void>(safety_.InvalidateAuthorizationAdmission());
    }
  }

  CaptureSafetyCore& safety_;
  WorkerSignals signals_;
  std::atomic<bool> invalidation_fired_{false};
  std::mutex gate_mutex_;
  std::condition_variable gate_changed_;
  bool first_encode_released_ = false;
  bool steady_now_released_ = false;
  mutable std::mutex records_mutex_;
  std::vector<MfH264ChunkWriterConfig> writer_configurations_;
  std::vector<ChunkManifest> manifests_;
  std::vector<std::shared_ptr<FakePublicationState>> publications_;
};

bool AcquireOwnerPermit(CaptureSafetyCore& safety, CaptureRuntimeOwner& runtime,
                        const CaptureTargetIdentity& target,
                        CaptureCommand command,
                        CaptureCommandAdmissionPermit* permit) {
  const uint64_t owner_epoch = runtime.owner_epoch();
  CaptureCommandAdmission admission;
  return owner_epoch != 0 &&
         safety.IssueCommandAdmission(
             command, safety.persistence_generation(), target.target_epoch,
             owner_epoch, &admission) == CaptureCommandAdmissionResult::kOk &&
         safety.AcquireCommandAdmissionPermit(admission, command, owner_epoch,
                                              permit) ==
             CaptureCommandAdmissionResult::kOk;
}

class WorkerFixture final {
 public:
  explicit WorkerFixture(
      size_t event_capacity = 8, void (*event_hook)() = nullptr,
      CaptureWorkerConfiguration configuration = TestConfiguration())
      : target(TestTarget()),
        safety(50, 1, NextNonce),
        events(event_capacity, event_hook),
        backend(safety),
        worker(safety, events, backend, std::move(configuration)) {}

  bool Authorize(uint64_t revision) {
    return safety.UpdateRuntimeAuthorization(
               AllowedAuthorization(revision, target), &generation) ==
           CaptureSafetyUpdateResult::kOk;
  }

  bool Block(uint64_t revision) {
    return safety.UpdateRuntimeAuthorization(
               RuntimeAuthorization{BlockedPrivacy(revision), std::nullopt},
               &generation) == CaptureSafetyUpdateResult::kOk;
  }

  bool Start(CaptureWorkerCheckpointSink checkpoint_sink = {}) {
    CaptureCommandAdmissionPermit permit;
    if (!AcquireOwnerPermit(safety, runtime, target, CaptureCommand::kStart,
                            &permit)) {
      return false;
    }
    backend.provisional_pause_runtime = &runtime;
    return runtime.Start(
        std::move(permit),
        [this, checkpoint_sink = std::move(checkpoint_sink)](
            CaptureRuntimeOwner& owner, PersistenceToken token) mutable {
          worker.Run(owner, std::move(token), std::move(checkpoint_sink));
        });
  }

  bool Resume() {
    CaptureCommandAdmissionPermit permit;
    return AcquireOwnerPermit(safety, runtime, target, CaptureCommand::kResume,
                              &permit) &&
           runtime.Resume(std::move(permit));
  }

  CaptureTargetIdentity target;
  CaptureSafetyCore safety;
  CaptureEventQueue events;
  FakeBackend backend;
  CaptureWorker worker;
  CaptureRuntimeOwner runtime;
  uint64_t generation = 0;
};

struct ReadEvent {
  CaptureEventReadResult result = CaptureEventReadResult::kInternalError;
  wdf_capture_event_v1 event{};
  std::string detail;
};

ReadEvent ReadOne(CaptureEventQueue& queue) {
  ReadEvent value;
  value.event.struct_size = sizeof(value.event);
  value.event.abi_version = WDF_CAPTURE_ABI_VERSION;
  std::array<char, 256> detail{};
  uint32_t required = 0;
  value.result = queue.Read(0, &value.event, detail.data(),
                            static_cast<uint32_t>(detail.size()), &required);
  if (value.result == CaptureEventReadResult::kSuccess) {
    value.detail.assign(detail.data(), value.event.detail_utf8_length);
  }
  return value;
}

enum class EventHookAction {
  kNone,
  kInvalidate,
  kThrowBadAlloc,
};

std::atomic<EventHookAction> g_event_hook_action{EventHookAction::kNone};
std::atomic<CaptureSafetyCore*> g_event_hook_safety{nullptr};

void WorkerEventHook() {
  const EventHookAction action =
      g_event_hook_action.exchange(EventHookAction::kNone);
  if (action == EventHookAction::kInvalidate) {
    CaptureSafetyCore* const safety = g_event_hook_safety.load();
    if (safety != nullptr) {
      static_cast<void>(safety->InvalidateAuthorizationAdmission());
    }
  } else if (action == EventHookAction::kThrowBadAlloc) {
    throw std::bad_alloc();
  }
}

class ScopedEventHook final {
 public:
  ScopedEventHook(EventHookAction action, CaptureSafetyCore* safety = nullptr) {
    g_event_hook_safety.store(safety);
    g_event_hook_action.store(action);
  }

  ~ScopedEventHook() {
    g_event_hook_action.store(EventHookAction::kNone);
    g_event_hook_safety.store(nullptr);
  }

  ScopedEventHook(const ScopedEventHook&) = delete;
  ScopedEventHook& operator=(const ScopedEventHook&) = delete;
};

bool StopAfterFirstFrame(WorkerFixture& fixture) {
  return fixture.backend.WaitForEncodeCount(1) &&
         fixture.runtime.RequestStop() ==
             CaptureRuntimeStopResult::kStopRequested &&
         fixture.runtime.WaitStopped(kTestTimeoutMs) ==
             CaptureRuntimeWaitResult::kStopped;
}

bool TestBoundaryFrameStartsNextChunk() {
  CaptureWorkerConfiguration configuration;
  WorkerFixture fixture(8, nullptr, configuration);
  constexpr int64_t kScheduleStartSteadyMs = 1'000;
  constexpr int64_t kScheduleStartUnixMs = 1'700'000'000'000;
  constexpr int64_t kFirstFrameDelayMs = 7;
  constexpr int64_t kFirstFrameSteadyMs =
      kScheduleStartSteadyMs + kFirstFrameDelayMs;
  constexpr int64_t kFirstFrameUnixMs =
      kScheduleStartUnixMs + kFirstFrameDelayMs;
  constexpr int64_t kCaptureIntervalMs = 10'000;
  constexpr int64_t kChunkDurationMs = 60'000;
  constexpr int64_t kFinalizationDelayMs = 500;
  constexpr int64_t kSecondChunkSteadyMs =
      kFirstFrameSteadyMs + kChunkDurationMs + kFinalizationDelayMs;
  constexpr int64_t kSecondChunkUnixMs =
      kFirstFrameUnixMs + kChunkDurationMs + kFinalizationDelayMs;
  fixture.backend.acquire_clock_advance_ms.store(3);
  fixture.backend.transform_clock_advance_ms.store(4);
  fixture.backend.finalize_clock_advance_ms.store(kFinalizationDelayMs);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "chunk-boundary worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1),
              "chunk-boundary worker did not encode its first frame")) {
    return false;
  }
  fixture.backend.acquire_clock_advance_ms.store(0);
  fixture.backend.transform_clock_advance_ms.store(0);

  for (uint32_t frame_count = 2; frame_count <= 6; ++frame_count) {
    const int64_t frame_offset_ms =
        static_cast<int64_t>(frame_count - 1U) * kCaptureIntervalMs;
    fixture.backend.steady_now_ms.store(kFirstFrameSteadyMs + frame_offset_ms);
    fixture.backend.unix_now_ms.store(kFirstFrameUnixMs + frame_offset_ms);
    fixture.runtime.NotifyAuthorizationChanged();
    if (!Expect(fixture.backend.WaitForEncodeCount(frame_count),
                "chunk-boundary worker missed a pre-boundary frame")) {
      return false;
    }
  }

  fixture.backend.steady_now_ms.store(kScheduleStartSteadyMs +
                                      kChunkDurationMs);
  fixture.backend.unix_now_ms.store(kScheduleStartUnixMs + kChunkDurationMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForObservedSteadyNow(
                  kScheduleStartSteadyMs + kChunkDurationMs),
              "chunk-boundary worker did not evaluate the stale schedule") ||
      !Expect(fixture.backend.acquire_calls.load() == 6 &&
                  fixture.backend.encode_calls.load() == 6,
              "pre-anchor schedule admitted a seventh frame")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(kFirstFrameSteadyMs +
                                      kChunkDurationMs);
  fixture.backend.unix_now_ms.store(kFirstFrameUnixMs + kChunkDurationMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForAcknowledgeCount(1),
              "chunk-boundary worker did not commit its first chunk") ||
      !Expect(fixture.backend.WaitForEncodeCount(7),
              "chunk-boundary frame was not encoded")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(kFirstFrameSteadyMs +
                                      kChunkDurationMs + kCaptureIntervalMs);
  fixture.backend.unix_now_ms.store(kFirstFrameUnixMs + kChunkDurationMs +
                                    kCaptureIntervalMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForObservedSteadyNow(
                  kFirstFrameSteadyMs + kChunkDurationMs +
                  kCaptureIntervalMs),
              "second chunk did not evaluate the stale frame deadline") ||
      !Expect(fixture.backend.acquire_calls.load() == 7 &&
                  fixture.backend.encode_calls.load() == 7,
              "second chunk retained the previous frame deadline")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(kSecondChunkSteadyMs +
                                      kCaptureIntervalMs);
  fixture.backend.unix_now_ms.store(kSecondChunkUnixMs + kCaptureIntervalMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForEncodeCount(8),
              "second chunk missed its reanchored frame deadline")) {
    return false;
  }

  if (!Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "chunk-boundary worker did not stop")) {
    return false;
  }

  const std::vector<ChunkManifest> manifests = fixture.backend.Manifests();
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 8 &&
                    result.committed_chunks == 2,
                "chunk-boundary worker reported incorrect progress") &&
         Expect(manifests.size() == 2 && manifests[0].frame_count == 6 &&
                    manifests[0].end_time_unix_ms -
                            manifests[0].start_time_unix_ms ==
                        kChunkDurationMs &&
                    manifests[1].frame_count == 2 &&
                    manifests[0].end_time_unix_ms <=
                        manifests[1].start_time_unix_ms &&
                    manifests[1].start_time_unix_ms -
                            manifests[0].end_time_unix_ms ==
                        kFinalizationDelayMs,
                "boundary frame was written into the completed chunk") &&
         Expect(fixture.backend.begin_calls.load() == 2 &&
                    fixture.backend.finalize_calls.load() == 2,
                "chunk boundary did not create exactly two writers");
}

bool TestCheckpointOrderAndCleanup() {
  WorkerFixture fixture;
  CheckpointLog checkpoints;
  std::atomic<bool> ready_after_first_frame{false};
  std::atomic<bool> paused_after_cleanup{false};
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    if (value.kind == CaptureWorkerCheckpointKind::kReady) {
      ready_after_first_frame.store(
          fixture.backend.initialize_calls.load(std::memory_order_acquire) > 0 &&
              fixture.backend.acquire_calls.load(std::memory_order_acquire) >
                  0 &&
              fixture.backend.transform_calls.load(std::memory_order_acquire) >
                  0 &&
              fixture.backend.begin_calls.load(std::memory_order_acquire) > 0 &&
              fixture.backend.encode_calls.load(std::memory_order_acquire) > 0,
          std::memory_order_release);
    } else {
      paused_after_cleanup.store(fixture.backend.reset_acquisition_calls.load(
                                     std::memory_order_acquire) > 0 &&
                                     fixture.backend.reset_chunk_calls.load(
                                         std::memory_order_acquire) > 0,
                                 std::memory_order_release);
    }
    return checkpoints.Push(value);
  };

  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "checkpoint worker could not start") ||
      !Expect(checkpoints.WaitForSize(1), "ready checkpoint was not emitted") ||
      !Expect(fixture.runtime.RequestPause() ==
                  CaptureRuntimePauseResult::kPauseRequested,
              "checkpoint pause was not accepted") ||
      !Expect(checkpoints.WaitForSize(2),
              "paused checkpoint was not emitted") ||
      !Expect(fixture.Authorize(2) && fixture.Resume(),
              "checkpoint worker could not resume") ||
      !Expect(checkpoints.WaitForSize(3),
              "resumed ready checkpoint was not emitted") ||
      !Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "checkpoint worker did not stop")) {
    return false;
  }

  const std::vector<CaptureWorkerCheckpoint> values = checkpoints.Values();
  return Expect(values.size() == 3 &&
                    values[0] ==
                        CaptureWorkerCheckpoint{
                            CaptureWorkerCheckpointKind::kReady, 0} &&
                    values[1] ==
                        CaptureWorkerCheckpoint{
                            CaptureWorkerCheckpointKind::kPaused, 1} &&
                    values[2] ==
                        CaptureWorkerCheckpoint{
                            CaptureWorkerCheckpointKind::kReady, 0},
                "checkpoint order was not Ready, Paused(1), Ready") &&
         Expect(ready_after_first_frame.load(std::memory_order_acquire),
                "ready checkpoint preceded the first encoded frame") &&
         Expect(paused_after_cleanup.load(std::memory_order_acquire),
                "paused checkpoint preceded chunk/acquisition cleanup");
}

bool TestTimeoutDoesNotPublishReady() {
  WorkerFixture fixture;
  CheckpointLog checkpoints;
  fixture.backend.acquire_result.store(CaptureWorkerBackendResult::kTimeout);
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    return checkpoints.Push(value);
  };
  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "timeout checkpoint worker could not start") ||
      !Expect(fixture.backend.WaitForAcquireCount(1),
              "timeout checkpoint worker did not attempt acquisition")) {
    return false;
  }

  const uint32_t steady_now_count = fixture.backend.steady_now_calls.load();
  if (!Expect(fixture.backend.WaitForSteadyNowCount(steady_now_count + 1U),
              "timeout checkpoint worker did not leave acquisition")) {
    return false;
  }
  const bool no_ready = checkpoints.Values().empty();
  const bool stopped =
      fixture.runtime.RequestStop() ==
          CaptureRuntimeStopResult::kStopRequested &&
      fixture.runtime.WaitStopped(kTestTimeoutMs) ==
          CaptureRuntimeWaitResult::kStopped;
  const auto result = fixture.worker.last_result();
  return Expect(no_ready, "acquisition timeout published a ready checkpoint") &&
         Expect(stopped, "timeout checkpoint worker did not stop") &&
         Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 0 &&
                    fixture.backend.transform_calls.load() == 0 &&
                    fixture.backend.begin_calls.load() == 0,
                "timeout checkpoint worker advanced before a frame existed");
}

bool TestFirstSuccessfulFrameReanchorsAfterTimeouts() {
  CaptureWorkerConfiguration configuration;
  WorkerFixture fixture(8, nullptr, configuration);
  constexpr int64_t kScheduleStartSteadyMs = 1'000;
  constexpr int64_t kScheduleStartUnixMs = 1'700'000'000'000;
  constexpr int64_t kCaptureIntervalMs = 10'000;
  constexpr int64_t kFrameDelayMs = 7;
  constexpr int64_t kFirstFrameAttemptSteadyMs =
      kScheduleStartSteadyMs + (2 * kCaptureIntervalMs);
  constexpr int64_t kFirstFrameSteadyMs =
      kFirstFrameAttemptSteadyMs + kFrameDelayMs;
  fixture.backend.acquire_result.store(CaptureWorkerBackendResult::kTimeout);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "timeout-reanchor worker could not start") ||
      !Expect(fixture.backend.WaitForAcquireCount(1),
              "timeout-reanchor worker missed its first attempt")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(kScheduleStartSteadyMs +
                                      kCaptureIntervalMs);
  fixture.backend.unix_now_ms.store(kScheduleStartUnixMs + kCaptureIntervalMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForAcquireCount(2),
              "timeout-reanchor worker missed its second attempt")) {
    return false;
  }

  fixture.backend.acquire_result.store(CaptureWorkerBackendResult::kOk);
  fixture.backend.acquire_clock_advance_ms.store(3);
  fixture.backend.transform_clock_advance_ms.store(4);
  fixture.backend.steady_now_ms.store(kFirstFrameAttemptSteadyMs);
  fixture.backend.unix_now_ms.store(kScheduleStartUnixMs +
                                    (2 * kCaptureIntervalMs));
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForEncodeCount(1),
              "timeout-reanchor worker did not encode its first frame")) {
    return false;
  }
  fixture.backend.acquire_clock_advance_ms.store(0);
  fixture.backend.transform_clock_advance_ms.store(0);

  fixture.backend.steady_now_ms.store(kFirstFrameAttemptSteadyMs +
                                      kCaptureIntervalMs);
  fixture.backend.unix_now_ms.store(kScheduleStartUnixMs +
                                    (3 * kCaptureIntervalMs));
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForObservedSteadyNow(
                  kFirstFrameAttemptSteadyMs + kCaptureIntervalMs),
              "timeout-reanchor worker did not evaluate its stale deadline") ||
      !Expect(fixture.backend.acquire_calls.load() == 3 &&
                  fixture.backend.encode_calls.load() == 1,
              "a timeout or stale deadline reanchored the frame schedule")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(kFirstFrameSteadyMs +
                                      kCaptureIntervalMs);
  fixture.backend.unix_now_ms.store(kScheduleStartUnixMs +
                                    (3 * kCaptureIntervalMs) + kFrameDelayMs);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForEncodeCount(2),
              "timeout-reanchor worker missed the first anchored interval")) {
    return false;
  }

  const bool stopped =
      fixture.runtime.RequestStop() ==
          CaptureRuntimeStopResult::kStopRequested &&
      fixture.runtime.WaitStopped(kTestTimeoutMs) ==
          CaptureRuntimeWaitResult::kStopped;
  const auto result = fixture.worker.last_result();
  return Expect(stopped, "timeout-reanchor worker did not stop") &&
         Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 2 &&
                    result.committed_chunks == 1,
                "timeout-reanchor worker reported incorrect progress");
}

bool TestFirstFrameFailureDoesNotPublishReady() {
  WorkerFixture fixture;
  CheckpointLog checkpoints;
  fixture.backend.acquire_result.store(
      CaptureWorkerBackendResult::kDeviceUnavailable);
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    return checkpoints.Push(value);
  };
  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "first-frame failure worker could not start") ||
      !Expect(fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                  CaptureRuntimeWaitResult::kStopped,
              "first-frame failure worker did not exit")) {
    return false;
  }

  const auto result = fixture.worker.last_result();
  return Expect(checkpoints.Values().empty(),
                "first-frame failure published a ready checkpoint") &&
         Expect(result.reason == CaptureWorkerExitReason::kDeviceFailure &&
                    result.encoded_frames == 0 &&
                    fixture.backend.transform_calls.load() == 0 &&
                    fixture.backend.begin_calls.load() == 0,
                "first-frame failure advanced or reported the wrong result");
}

bool TestFirstFramePublishesReadyExactlyOnce() {
  WorkerFixture fixture;
  CheckpointLog checkpoints;
  std::atomic<bool> ready_after_first_frame{false};
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    if (value.kind == CaptureWorkerCheckpointKind::kReady) {
      ready_after_first_frame.store(
          fixture.backend.acquire_calls.load(std::memory_order_acquire) > 0 &&
              fixture.backend.transform_calls.load(std::memory_order_acquire) >
                  0 &&
              fixture.backend.begin_calls.load(std::memory_order_acquire) > 0 &&
              fixture.backend.encode_calls.load(std::memory_order_acquire) > 0,
          std::memory_order_release);
    }
    return checkpoints.Push(value);
  };
  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "single-ready worker could not start") ||
      !Expect(checkpoints.WaitForSize(1),
              "single-ready worker did not publish its first checkpoint")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(1'250);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForEncodeCount(2),
              "single-ready worker did not encode a second frame")) {
    return false;
  }

  const bool stopped =
      fixture.runtime.RequestStop() ==
          CaptureRuntimeStopResult::kStopRequested &&
      fixture.runtime.WaitStopped(kTestTimeoutMs) ==
          CaptureRuntimeWaitResult::kStopped;
  const std::vector<CaptureWorkerCheckpoint> values = checkpoints.Values();
  return Expect(ready_after_first_frame.load(std::memory_order_acquire),
                "ready checkpoint preceded the first encoded frame") &&
         Expect(values.size() == 1 &&
                    values[0] == CaptureWorkerCheckpoint{
                                     CaptureWorkerCheckpointKind::kReady, 0},
                "one token published more than one ready checkpoint") &&
         Expect(stopped, "single-ready worker did not stop");
}

bool TestResumeWaitsForFreshFrameBeforeReady() {
  WorkerFixture fixture;
  CheckpointLog checkpoints;
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    return checkpoints.Push(value);
  };
  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "resume-ready worker could not start") ||
      !Expect(checkpoints.WaitForSize(1),
              "resume-ready worker did not publish its initial ready") ||
      !Expect(fixture.runtime.RequestPause() ==
                  CaptureRuntimePauseResult::kPauseRequested,
              "resume-ready pause was rejected") ||
      !Expect(checkpoints.WaitForSize(2),
              "resume-ready worker did not publish paused")) {
    return false;
  }

  fixture.backend.acquire_result.store(CaptureWorkerBackendResult::kTimeout);
  const uint32_t acquire_count = fixture.backend.acquire_calls.load();
  if (!Expect(fixture.Authorize(2) && fixture.Resume(),
              "resume-ready worker could not resume") ||
      !Expect(fixture.backend.WaitForAcquireCount(acquire_count + 1U),
              "resumed token did not attempt a fresh acquisition")) {
    return false;
  }

  const uint32_t steady_now_count = fixture.backend.steady_now_calls.load();
  if (!Expect(fixture.backend.WaitForSteadyNowCount(steady_now_count + 1U),
              "resumed token did not leave its timed-out acquisition")) {
    return false;
  }
  const bool no_early_ready = checkpoints.Values().size() == 2 &&
                              fixture.backend.encode_calls.load() == 1;
  fixture.backend.acquire_result.store(CaptureWorkerBackendResult::kOk);
  fixture.backend.steady_now_ms.store(1'250);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(checkpoints.WaitForSize(3),
              "fresh resumed frame did not publish ready") ||
      !Expect(fixture.backend.WaitForEncodeCount(2),
              "fresh resumed frame was not encoded")) {
    return false;
  }

  const bool stopped =
      fixture.runtime.RequestStop() ==
          CaptureRuntimeStopResult::kStopRequested &&
      fixture.runtime.WaitStopped(kTestTimeoutMs) ==
          CaptureRuntimeWaitResult::kStopped;
  const std::vector<CaptureWorkerCheckpoint> values = checkpoints.Values();
  return Expect(no_early_ready,
                "Resume published ready before its token encoded a frame") &&
         Expect(values.size() == 3 &&
                    values[0] == CaptureWorkerCheckpoint{
                                     CaptureWorkerCheckpointKind::kReady, 0} &&
                    values[1] == CaptureWorkerCheckpoint{
                                     CaptureWorkerCheckpointKind::kPaused, 1} &&
                    values[2] == CaptureWorkerCheckpoint{
                                     CaptureWorkerCheckpointKind::kReady, 0},
                "Resume did not publish Ready, Paused, Ready") &&
         Expect(stopped, "resume-ready worker did not stop");
}

bool TestReadyCheckpointRunsAfterPermitRelease() {
  WorkerFixture fixture;
  std::atomic<bool> permit_released{false};
  CaptureWorkerCheckpointSink sink = [&](const CaptureWorkerCheckpoint& value) {
    if (value.kind == CaptureWorkerCheckpointKind::kReady) {
      uint64_t generation = 0;
      permit_released.store(fixture.safety.FinalizeRevoke(0, &generation),
                            std::memory_order_release);
    }
    return true;
  };
  if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
              "permit-release checkpoint worker could not start") ||
      !Expect(fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                  CaptureRuntimeWaitResult::kStopped,
              "permit-release checkpoint worker did not exit")) {
    return false;
  }
  return Expect(permit_released.load(std::memory_order_acquire),
                "ready checkpoint ran while a persistence permit was held") &&
         Expect(fixture.worker.last_result().reason ==
                    CaptureWorkerExitReason::kAuthorizationLost,
                "checkpoint revocation did not stop stale authorization");
}

bool TestProvisionalPauseRecoversAuthorizationLossAtCriticalStages() {
  constexpr std::array<FaultStage, 4> stages{
      FaultStage::kInitialize,
      FaultStage::kAcquire,
      FaultStage::kFinalize,
      FaultStage::kCommit,
  };

  for (const FaultStage stage : stages) {
    WorkerFixture fixture;
    CheckpointLog checkpoints;
    fixture.backend.fault_stage = stage;
    const bool late_stage =
        stage == FaultStage::kFinalize || stage == FaultStage::kCommit;
    fixture.backend.request_provisional_pause_on_invalidation = !late_stage;
    CaptureWorkerCheckpointSink sink =
        [&](const CaptureWorkerCheckpoint& value) {
          return checkpoints.Push(value);
        };
    if (!Expect(fixture.Authorize(1) && fixture.Start(std::move(sink)),
                "provisional-pause worker could not start")) {
      return false;
    }

    if (late_stage &&
        !Expect(fixture.backend.WaitForEncodeCount(1) &&
                    fixture.runtime.RequestPause() ==
                        CaptureRuntimePauseResult::kPauseRequested,
                "late-stage provisional pause was not requested")) {
      return false;
    }

    const size_t paused_checkpoint_count =
        stage == FaultStage::kInitialize || stage == FaultStage::kAcquire ? 1U
                                                                         : 2U;
    if (!Expect(checkpoints.WaitForSize(paused_checkpoint_count),
                "authorization loss did not reach a paused checkpoint")) {
      return false;
    }
    const std::vector<CaptureWorkerCheckpoint> paused_values =
        checkpoints.Values();
    if (!Expect(
            paused_values[paused_checkpoint_count - 1U].kind ==
                    CaptureWorkerCheckpointKind::kPaused &&
                paused_values[paused_checkpoint_count - 1U].pause_epoch == 1,
            "authorization loss emitted the wrong pause checkpoint") ||
        !Expect(
            fixture.runtime.RequestPause() ==
                CaptureRuntimePauseResult::kAlreadyPaused,
            "authorization loss terminated the provisionally paused worker") ||
        !Expect(fixture.events.size() == 0,
                "stale authorization published a committed event") ||
        !Expect(fixture.Block(2) && fixture.Authorize(3) && fixture.Resume(),
                "provisional-pause worker did not accept a fresh token") ||
        !Expect(checkpoints.WaitForSize(paused_checkpoint_count + 1U),
                "fresh token did not emit a ready checkpoint")) {
      return false;
    }

    const uint32_t expected_encode_count = late_stage ? 2U : 1U;
    if (!Expect(fixture.backend.WaitForEncodeCount(expected_encode_count) &&
                    fixture.runtime.RequestStop() ==
                        CaptureRuntimeStopResult::kStopRequested &&
                    fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                        CaptureRuntimeWaitResult::kStopped,
                "resumed provisional-pause worker did not stop")) {
      return false;
    }

    const auto result = fixture.worker.last_result();
    const ReadEvent committed = ReadOne(fixture.events);
    const auto publications = fixture.backend.PublicationRecords();
    uint32_t acknowledgements = 0;
    for (const auto& publication : publications) {
      acknowledgements += publication->acknowledge_calls.load();
    }
    if (result.reason != CaptureWorkerExitReason::kStopped ||
        result.committed_chunks != 1) {
      std::cerr << "provisional pause stage " << static_cast<int>(stage)
                << " exited with reason " << static_cast<int>(result.reason)
                << " and committed " << result.committed_chunks << " chunks\n";
      return false;
    }
    if (!Expect(
            committed.result == CaptureEventReadResult::kSuccess &&
                committed.event.persistence_generation == fixture.generation,
            "fresh generation did not own the committed event") ||
        !Expect(acknowledgements == 1,
                "stale generation was acknowledged during provisional pause")) {
      return false;
    }
  }
  return true;
}

bool TestStopCommitsValidPartialChunk() {
  WorkerFixture fixture;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "partial-stop worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "partial-stop worker did not stop deterministically")) {
    return false;
  }

  const auto manifests = fixture.backend.Manifests();
  const auto publications = fixture.backend.PublicationRecords();
  const ReadEvent committed = ReadOne(fixture.events);
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.committed_chunks == 1 &&
                    result.encoded_frames == 1 && !result.compensation_pending,
                "partial stop did not report a committed chunk") &&
         Expect(manifests.size() == 1 && publications.size() == 1,
                "partial stop did not prepare exactly one artifact") &&
         Expect(manifests[0].chunk_id == "chunk-1" &&
                    manifests[0].frame_count == 1 &&
                    manifests[0].video_width == 2 &&
                    manifests[0].video_height == 2 &&
                    manifests[0].persistence_generation == fixture.generation &&
                    manifests[0].target_epoch == fixture.target.target_epoch &&
                    manifests[0].end_time_unix_ms >
                        manifests[0].start_time_unix_ms,
                "partial-stop manifest lost chunk metadata") &&
         Expect(publications[0]->commit_calls.load() == 1 &&
                    publications[0]->acknowledge_calls.load() == 1 &&
                    publications[0]->rollback_calls.load() == 0 &&
                    publications[0]->committed.load(),
                "partial-stop publication lifecycle was incorrect") &&
         Expect(
             committed.result == CaptureEventReadResult::kSuccess &&
                 committed.event.kind == WDF_CAPTURE_EVENT_CHUNK_COMMITTED &&
                 committed.event.state == WDF_CAPTURE_STATE_STOPPING &&
                 committed.event.persistence_generation == fixture.generation &&
                 committed.event.target_epoch == fixture.target.target_epoch &&
                 committed.detail == publications[0]->identifier,
             "partial-stop commit event did not match the artifact");
}

bool TestPauseResumeUsesFreshGeneration() {
  WorkerFixture fixture;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "pause-resume worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1),
              "pause-resume worker did not encode its first frame") ||
      !Expect(fixture.runtime.RequestPause() ==
                  CaptureRuntimePauseResult::kPauseRequested,
              "pause request was rejected") ||
      !Expect(fixture.backend.WaitForAcknowledgeCount(1),
              "pause did not finalize the first partial chunk")) {
    return false;
  }

  const uint64_t first_generation = fixture.generation;
  if (!Expect(fixture.Authorize(2) && fixture.generation != first_generation &&
                  fixture.Resume(),
              "resume did not acquire a fresh persistence token") ||
      !Expect(fixture.backend.WaitForEncodeCount(2),
              "resumed worker did not encode with the new token") ||
      !Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "resumed worker did not stop")) {
    return false;
  }

  const auto manifests = fixture.backend.Manifests();
  const ReadEvent paused = ReadOne(fixture.events);
  const ReadEvent stopped = ReadOne(fixture.events);
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.committed_chunks == 2 && result.encoded_frames == 2,
                "pause-resume progress was incorrect") &&
         Expect(manifests.size() == 2 && manifests[0].frame_count == 1 &&
                    manifests[1].frame_count == 1 &&
                    manifests[0].persistence_generation == first_generation &&
                    manifests[1].persistence_generation == fixture.generation,
                "pause-resume mixed persistence generations") &&
         Expect(paused.result == CaptureEventReadResult::kSuccess &&
                    paused.event.state == WDF_CAPTURE_STATE_PAUSED &&
                    paused.event.persistence_generation == first_generation &&
                    stopped.result == CaptureEventReadResult::kSuccess &&
                    stopped.event.state == WDF_CAPTURE_STATE_STOPPING &&
                    stopped.event.persistence_generation == fixture.generation,
                "pause-resume events lost their generation boundary");
}

bool TestImmediateResumeStillFinalizesPauseExactlyOnce() {
  WorkerFixture fixture;
  fixture.backend.block_first_encode = true;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "merged-pause worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1),
              "merged-pause worker did not enter its first encode")) {
    fixture.backend.ReleaseFirstEncode();
    return false;
  }

  const bool controls_merged = fixture.runtime.RequestPause() ==
                                   CaptureRuntimePauseResult::kPauseRequested &&
                               fixture.Resume();
  fixture.backend.ReleaseFirstEncode();
  if (!Expect(controls_merged,
              "pause and immediate resume could not be merged") ||
      !Expect(fixture.backend.WaitForEncodeCount(2),
              "merged-pause worker did not continue after finalization") ||
      !Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "merged-pause worker did not stop")) {
    return false;
  }

  const auto manifests = fixture.backend.Manifests();
  const ReadEvent paused = ReadOne(fixture.events);
  const ReadEvent stopped = ReadOne(fixture.events);
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 2 && result.committed_chunks == 2,
                "merged pause lost or duplicated chunk progress") &&
         Expect(fixture.backend.finalize_calls.load() == 2 &&
                    manifests.size() == 2 && manifests[0].frame_count == 1 &&
                    manifests[1].frame_count == 1 &&
                    manifests[0].persistence_generation == fixture.generation &&
                    manifests[1].persistence_generation == fixture.generation,
                "merged pause did not finalize its partial exactly once") &&
         Expect(paused.result == CaptureEventReadResult::kSuccess &&
                    paused.event.state == WDF_CAPTURE_STATE_PAUSED &&
                    stopped.result == CaptureEventReadResult::kSuccess &&
                    stopped.event.state == WDF_CAPTURE_STATE_STOPPING,
                "merged pause published an incorrect event sequence");
}

bool TestMergedResumeDiscardsSupersededGeneration() {
  WorkerFixture fixture;
  fixture.backend.blocked_steady_now_call = 5;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "superseded-generation worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1) &&
                  fixture.backend.WaitForSteadyNowCount(5),
              "superseded-generation worker did not reach its merge point")) {
    fixture.backend.ReleaseSteadyNow();
    return false;
  }

  const uint64_t old_generation = fixture.generation;
  const bool controls_merged = fixture.Authorize(2) &&
                               fixture.generation != old_generation &&
                               fixture.runtime.RequestPause() ==
                                   CaptureRuntimePauseResult::kPauseRequested &&
                               fixture.Resume();
  fixture.backend.ReleaseSteadyNow();
  if (!Expect(controls_merged,
              "new-generation pause/resume could not be merged") ||
      !Expect(fixture.backend.WaitForEncodeCount(2),
              "new persistence token did not continue capture") ||
      !Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "superseded-generation worker did not stop")) {
    return false;
  }

  const auto manifests = fixture.backend.Manifests();
  const ReadEvent committed = ReadOne(fixture.events);
  const ReadEvent no_second_event = ReadOne(fixture.events);
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 2 && result.committed_chunks == 1,
                "superseded generation changed worker progress") &&
         Expect(fixture.backend.finalize_calls.load() == 1 &&
                    manifests.size() == 1 && manifests[0].frame_count == 1 &&
                    manifests[0].persistence_generation == fixture.generation &&
                    manifests[0].persistence_generation != old_generation,
                "superseded partial was persisted or mixed") &&
         Expect(
             committed.result == CaptureEventReadResult::kSuccess &&
                 committed.event.state == WDF_CAPTURE_STATE_STOPPING &&
                 committed.event.persistence_generation == fixture.generation &&
                 no_second_event.result == CaptureEventReadResult::kEmpty,
             "superseded generation exposed an old commit event");
}

bool TestPauseResumePauseKeepsWorkerResumable() {
  WorkerFixture fixture;
  fixture.backend.blocked_steady_now_call = 5;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "triple-control worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1) &&
                  fixture.backend.WaitForSteadyNowCount(5),
              "triple-control worker did not reach its merge point")) {
    fixture.backend.ReleaseSteadyNow();
    return false;
  }

  const uint64_t old_generation = fixture.generation;
  bool controls_merged = fixture.Authorize(2) &&
                         fixture.generation != old_generation &&
                         fixture.runtime.RequestPause() ==
                             CaptureRuntimePauseResult::kPauseRequested &&
                         fixture.Resume();
  const auto resumed_snapshot = fixture.runtime.ReadControlSnapshot();
  controls_merged = controls_merged &&
                    resumed_snapshot.replacement_token.has_value() &&
                    fixture.runtime.RequestPause() ==
                        CaptureRuntimePauseResult::kPauseRequested;
  const auto folded_snapshot = fixture.runtime.ReadControlSnapshot();
  fixture.backend.ReleaseSteadyNow();
  if (!Expect(controls_merged,
              "Pause/Resume/Pause controls could not be merged") ||
      !Expect(folded_snapshot.pause_requested &&
                  folded_snapshot.pause_epoch ==
                      resumed_snapshot.pause_epoch + 1U &&
                  folded_snapshot.replacement_token ==
                      resumed_snapshot.replacement_token,
              "merged second Pause cleared the unconsumed replacement token") ||
      !Expect(fixture.backend.WaitForResetChunkCount(1),
              "superseded partial was not discarded while paused") ||
      !Expect(fixture.Resume(),
              "worker could not resume after merged second Pause") ||
      !Expect(fixture.backend.WaitForEncodeCount(2),
              "worker did not encode after the second Resume") ||
      !Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "triple-control worker did not stop")) {
    return false;
  }

  const auto manifests = fixture.backend.Manifests();
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 2 && result.committed_chunks == 1,
                "triple-control worker lost resumable state") &&
         Expect(fixture.backend.finalize_calls.load() == 1 &&
                    manifests.size() == 1 && manifests[0].frame_count == 1 &&
                    manifests[0].persistence_generation == fixture.generation &&
                    manifests[0].persistence_generation != old_generation,
                "triple-control worker persisted a superseded generation");
}

bool TestCallbackInvalidationAtEveryStage() {
  constexpr std::array<FaultStage, 8> stages{
      FaultStage::kInitialize, FaultStage::kAcquire, FaultStage::kTransform,
      FaultStage::kBegin,      FaultStage::kEncode,  FaultStage::kFinalize,
      FaultStage::kPrepare,    FaultStage::kCommit,
  };

  for (const FaultStage stage : stages) {
    WorkerFixture fixture;
    fixture.backend.fault_stage = stage;
    if (!Expect(fixture.Authorize(1) && fixture.Start(),
                "stage-invalidation worker could not start")) {
      return false;
    }

    if (stage == FaultStage::kFinalize || stage == FaultStage::kPrepare ||
        stage == FaultStage::kCommit) {
      if (!Expect(fixture.backend.WaitForEncodeCount(1) &&
                      fixture.runtime.RequestStop() ==
                          CaptureRuntimeStopResult::kStopRequested,
                  "late-stage invalidation could not reach finalization")) {
        return false;
      }
    }
    if (!Expect(fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                    CaptureRuntimeWaitResult::kStopped,
                "stage-invalidation worker did not exit")) {
      return false;
    }

    const auto result = fixture.worker.last_result();
    const auto publications = fixture.backend.PublicationRecords();
    const bool stop_was_already_requested = stage == FaultStage::kFinalize ||
                                            stage == FaultStage::kPrepare ||
                                            stage == FaultStage::kCommit;
    const CaptureWorkerExitReason expected_reason =
        stop_was_already_requested
            ? CaptureWorkerExitReason::kStopped
            : CaptureWorkerExitReason::kAuthorizationLost;
    if (!Expect(result.reason == expected_reason &&
                    result.committed_chunks == 0 &&
                    fixture.events.size() == 0 &&
                    fixture.events.reserved_size() == 0,
                "stage invalidation published unauthorized output")) {
      return false;
    }

    const bool needs_rollback =
        stage == FaultStage::kPrepare || stage == FaultStage::kCommit;
    if (needs_rollback) {
      if (!Expect(publications.size() == 1 &&
                      publications[0]->rollback_calls.load() == 1 &&
                      publications[0]->acknowledge_calls.load() == 0 &&
                      publications[0]->rolled_back.load() &&
                      !publications[0]->committed.load(),
                  "post-prepare invalidation did not compensate")) {
        return false;
      }
    } else if (!Expect(publications.empty(),
                       "pre-prepare invalidation created an artifact")) {
      return false;
    }
  }
  return true;
}

bool TestPostStageTargetMismatchStopsAdvance() {
  WorkerFixture fixture;
  fixture.backend.missing_observation_call = 4;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "post-observation worker could not start") ||
      !Expect(fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                  CaptureRuntimeWaitResult::kStopped,
              "post-observation worker did not exit")) {
    return false;
  }

  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kAuthorizationLost &&
                    fixture.backend.initialize_calls.load() == 1 &&
                    fixture.backend.acquire_calls.load() == 0,
                "missing post-stage target observation advanced capture") &&
         Expect(fixture.events.size() == 0 &&
                    fixture.backend.PublicationRecords().empty(),
                "post-stage target mismatch published evidence");
}

bool TestEventAppendInvalidationStaysInvisible() {
  WorkerFixture fixture(8, WorkerEventHook);
  ScopedEventHook hook(EventHookAction::kInvalidate, &fixture.safety);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "event-invalidation worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "event-invalidation worker did not exit")) {
    return false;
  }

  const auto publications = fixture.backend.PublicationRecords();
  const auto result = fixture.worker.last_result();
  return Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.committed_chunks == 0,
                "event append invalidation returned the wrong result") &&
         Expect(fixture.events.size() == 0 &&
                    fixture.events.reserved_size() == 0 &&
                    ReadOne(fixture.events).result ==
                        CaptureEventReadResult::kEmpty,
                "invalidated event became visible") &&
         Expect(publications.size() == 1 &&
                    publications[0]->commit_calls.load() == 1 &&
                    publications[0]->acknowledge_calls.load() == 0 &&
                    publications[0]->rollback_calls.load() == 1 &&
                    publications[0]->rolled_back.load() &&
                    !publications[0]->committed.load(),
                "invalidated event did not roll back its artifact");
}

bool TestReservationSaturationPreventsPersistence() {
  WorkerFixture fixture(1);
  const uint64_t occupied = fixture.events.Push(
      WDF_CAPTURE_EVENT_CHUNK_COMMITTED, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "occupied", 1, 1, 1);
  if (!Expect(occupied != 0 && fixture.Authorize(1) && fixture.Start(),
              "reservation-saturation worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "reservation-saturation worker did not exit")) {
    return false;
  }

  const auto result = fixture.worker.last_result();
  const ReadEvent retained = ReadOne(fixture.events);
  return Expect(result.reason ==
                        CaptureWorkerExitReason::kEventPublicationFailure &&
                    result.committed_chunks == 0,
                "reservation saturation returned the wrong failure") &&
         Expect(fixture.backend.prepare_calls.load() == 0 &&
                    fixture.backend.create_artifact_calls.load() == 0 &&
                    fixture.backend.PublicationRecords().empty(),
                "reservation saturation persisted an artifact") &&
         Expect(retained.result == CaptureEventReadResult::kSuccess &&
                    retained.event.sequence == occupied &&
                    retained.detail == "occupied",
                "reservation saturation displaced the required event");
}

bool TestEventAppendFailureRollsBack() {
  WorkerFixture fixture(8, WorkerEventHook);
  ScopedEventHook hook(EventHookAction::kThrowBadAlloc);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "append-failure worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "append-failure worker did not exit")) {
    return false;
  }

  const auto publications = fixture.backend.PublicationRecords();
  const auto result = fixture.worker.last_result();
  return Expect(result.reason ==
                        CaptureWorkerExitReason::kEventPublicationFailure &&
                    result.committed_chunks == 0 &&
                    !result.compensation_pending,
                "append failure returned the wrong result") &&
         Expect(
             fixture.events.size() == 0 && fixture.events.reserved_size() == 0,
             "failed append changed event visibility") &&
         Expect(publications.size() == 1 &&
                    publications[0]->commit_calls.load() == 1 &&
                    publications[0]->acknowledge_calls.load() == 0 &&
                    publications[0]->rollback_calls.load() == 1 &&
                    publications[0]->rolled_back.load(),
                "failed append did not roll back once");
}

bool TestTransientRollbackFailureRetries() {
  CaptureWorkerConfiguration configuration = TestConfiguration();
  configuration.rollback_retry_limit = 3;
  configuration.rollback_retry_delay_ms = 5;
  WorkerFixture fixture(8, WorkerEventHook, configuration);
  fixture.backend.rollback_failures_before_success = 2;
  ScopedEventHook hook(EventHookAction::kThrowBadAlloc);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "transient-rollback worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1),
              "transient-rollback worker did not encode")) {
    return false;
  }
  const auto started = std::chrono::steady_clock::now();
  if (!Expect(fixture.runtime.RequestStop() ==
                      CaptureRuntimeStopResult::kStopRequested &&
                  fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                      CaptureRuntimeWaitResult::kStopped,
              "transient-rollback worker did not exit")) {
    return false;
  }
  const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::steady_clock::now() - started);

  const auto publications = fixture.backend.PublicationRecords();
  const auto result = fixture.worker.last_result();
  return Expect(result.reason ==
                        CaptureWorkerExitReason::kEventPublicationFailure &&
                    !result.compensation_pending &&
                    elapsed >= std::chrono::milliseconds(8),
                "transient rollback did not recover") &&
         Expect(publications.size() == 1 &&
                    publications[0]->rollback_calls.load() == 3 &&
                    publications[0]->rolled_back.load() &&
                    !publications[0]->committed.load(),
                "transient rollback was not retried to success");
}

bool TestPermanentRollbackFailureCanBeRetriedExplicitly() {
  CaptureWorkerConfiguration configuration = TestConfiguration();
  configuration.rollback_retry_limit = 2;
  WorkerFixture fixture(8, WorkerEventHook, configuration);
  fixture.backend.always_fail_rollback = true;
  ScopedEventHook hook(EventHookAction::kThrowBadAlloc);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "permanent-rollback worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "permanent-rollback worker did not exit")) {
    return false;
  }

  const auto publications = fixture.backend.PublicationRecords();
  const auto failed = fixture.worker.last_result();
  if (!Expect(failed.reason == CaptureWorkerExitReason::kCompensationFailure &&
                  failed.compensation_pending && publications.size() == 1 &&
                  publications[0]->rollback_calls.load() == 2 &&
                  publications[0]->committed.load(),
              "permanent rollback was not retained for compensation")) {
    return false;
  }

  publications[0]->always_fail_rollback.store(false);
  if (!Expect(fixture.worker.RetryPendingCompensation(2),
              "explicit compensation retry did not succeed")) {
    return false;
  }
  const auto retried = fixture.worker.last_result();
  return Expect(!retried.compensation_pending &&
                    publications[0]->rollback_calls.load() == 3 &&
                    publications[0]->rolled_back.load() &&
                    !publications[0]->committed.load(),
                "explicit compensation retry did not clear pending output");
}

bool TestAuthorizationNotificationWakesLongWait() {
  CaptureWorkerConfiguration configuration = TestConfiguration();
  configuration.policy.capture_interval_ms = 300'000;
  configuration.policy.context_interval_ms = 60'000;
  configuration.policy.chunk_duration_ms = 3'600'000;
  WorkerFixture fixture(8, nullptr, configuration);
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "authorization-wakeup worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1) &&
                  fixture.backend.WaitForSteadyNowCount(5),
              "authorization-wakeup worker did not enter its long wait")) {
    return false;
  }

  static_cast<void>(fixture.safety.InvalidateAuthorizationAdmission());
  const auto started = std::chrono::steady_clock::now();
  fixture.runtime.NotifyAuthorizationChanged();
  const CaptureRuntimeWaitResult wait_result =
      fixture.runtime.WaitStopped(kTestTimeoutMs);
  const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::steady_clock::now() - started);
  const auto result = fixture.worker.last_result();
  return Expect(wait_result == CaptureRuntimeWaitResult::kStopped &&
                    elapsed < std::chrono::milliseconds(750),
                "authorization notification did not wake the long wait") &&
         Expect(result.reason == CaptureWorkerExitReason::kAuthorizationLost &&
                    result.committed_chunks == 0 && fixture.events.size() == 0,
                "authorization wakeup published stale output");
}

bool TestDisplayWideForegroundSwitchKeepsCurrentChunk() {
  WorkerFixture fixture;
  fixture.target = TestDisplayWideTarget();
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "display-wide worker could not start") ||
      !Expect(fixture.backend.WaitForEncodeCount(1),
              "display-wide worker did not encode its first frame")) {
    return false;
  }

  fixture.backend.steady_now_ms.store(1'250);
  fixture.backend.unix_now_ms.store(1'700'000'000'250);
  fixture.runtime.NotifyAuthorizationChanged();
  if (!Expect(fixture.backend.WaitForEncodeCount(2),
              "foreground switch notification interrupted display-wide capture") ||
      !Expect(fixture.backend.finalize_calls.load() == 0 &&
                  fixture.backend.reset_chunk_calls.load() == 0 &&
                  fixture.backend.reset_acquisition_calls.load() == 0,
              "foreground switch split or reset the display-wide chunk")) {
    return false;
  }

  const bool stopped =
      fixture.runtime.RequestStop() ==
          CaptureRuntimeStopResult::kStopRequested &&
      fixture.runtime.WaitStopped(kTestTimeoutMs) ==
          CaptureRuntimeWaitResult::kStopped;
  const auto manifests = fixture.backend.Manifests();
  const auto result = fixture.worker.last_result();
  return Expect(stopped, "display-wide worker did not stop") &&
         Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 2 &&
                    result.committed_chunks == 1,
                "display-wide worker did not commit one continuous chunk") &&
         Expect(manifests.size() == 1 && manifests[0].display_wide_scope,
                "display-wide worker persisted the wrong capture scope");
}

bool TestTopologyRebuildRecovers() {
  WorkerFixture fixture;
  fixture.backend.rebuild_initializations = 1;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "topology-rebuild worker could not start") ||
      !Expect(StopAfterFirstFrame(fixture),
              "topology-rebuild worker did not recover and stop")) {
    return false;
  }

  const auto result = fixture.worker.last_result();
  return Expect(fixture.backend.initialize_calls.load() == 2 &&
                    fixture.backend.reset_acquisition_calls.load() >= 2,
                "topology rebuild did not reset and retry acquisition") &&
         Expect(result.reason == CaptureWorkerExitReason::kStopped &&
                    result.encoded_frames == 1 &&
                    result.committed_chunks == 1 && fixture.events.size() == 1,
                "topology rebuild did not resume chunk publication");
}

bool TestTopologyRebuildBudgetExhausts() {
  CaptureWorkerConfiguration configuration = TestConfiguration();
  configuration.topology_retry_ms = 0;
  configuration.topology_retry_limit = 2;
  WorkerFixture fixture(8, nullptr, configuration);
  fixture.backend.rebuild_initializations = 3;
  if (!Expect(fixture.Authorize(1) && fixture.Start(),
              "topology-budget worker could not start") ||
      !Expect(fixture.runtime.WaitStopped(kTestTimeoutMs) ==
                  CaptureRuntimeWaitResult::kStopped,
              "topology-budget worker did not stop after its retry budget")) {
    return false;
  }

  const auto result = fixture.worker.last_result();
  return Expect(fixture.backend.initialize_calls.load() == 3,
                "topology rebuild exceeded its configured retry budget") &&
         Expect(result.reason == CaptureWorkerExitReason::kDeviceFailure &&
                    result.error == WDF_CAPTURE_ERROR_DEVICE_UNAVAILABLE &&
                    result.encoded_frames == 0 &&
                    result.committed_chunks == 0 && fixture.events.size() == 0,
                "topology retry exhaustion reported the wrong result");
}

}  // namespace

int main() {
  if (!TestBoundaryFrameStartsNextChunk() ||
      !TestCheckpointOrderAndCleanup() ||
      !TestTimeoutDoesNotPublishReady() ||
      !TestFirstSuccessfulFrameReanchorsAfterTimeouts() ||
      !TestFirstFrameFailureDoesNotPublishReady() ||
      !TestFirstFramePublishesReadyExactlyOnce() ||
      !TestResumeWaitsForFreshFrameBeforeReady() ||
      !TestReadyCheckpointRunsAfterPermitRelease() ||
      !TestProvisionalPauseRecoversAuthorizationLossAtCriticalStages() ||
      !TestStopCommitsValidPartialChunk() ||
      !TestPauseResumeUsesFreshGeneration() ||
      !TestImmediateResumeStillFinalizesPauseExactlyOnce() ||
      !TestMergedResumeDiscardsSupersededGeneration() ||
      !TestPauseResumePauseKeepsWorkerResumable() ||
      !TestCallbackInvalidationAtEveryStage() ||
      !TestPostStageTargetMismatchStopsAdvance() ||
      !TestEventAppendInvalidationStaysInvisible() ||
      !TestReservationSaturationPreventsPersistence() ||
      !TestEventAppendFailureRollsBack() ||
      !TestTransientRollbackFailureRetries() ||
      !TestPermanentRollbackFailureCanBeRetriedExplicitly() ||
      !TestAuthorizationNotificationWakesLongWait() ||
      !TestDisplayWideForegroundSwitchKeepsCurrentChunk() ||
      !TestTopologyRebuildRecovers() ||
      !TestTopologyRebuildBudgetExhausts()) {
    return 1;
  }
  std::cout << "capture worker tests passed\n";
  return 0;
}

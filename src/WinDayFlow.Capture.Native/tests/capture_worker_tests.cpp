#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iostream>
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

CaptureTargetIdentity TestTarget() {
  return CaptureTargetIdentity{100, 200, 300,
                               1,   400, std::wstring(L"\\\\.\\DISPLAY1")};
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
    frame->width = 2;
    frame->height = 2;
    frame->pixels.assign(16, 0x2A);
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
    state->invalidate_on_commit = fault_stage == FaultStage::kCommit;
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
    return steady_now_ms;
  }

  int64_t UnixNowMilliseconds() noexcept override { return unix_now_ms; }

  void ShutdownThread() noexcept override {
    shutdown_calls.fetch_add(1);
    signals_.Notify();
  }

  bool WaitForEncodeCount(uint32_t count) {
    return signals_.WaitFor(
        [this, count] { return encode_calls.load() >= count; });
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
  uint32_t rebuild_initializations = 0;
  uint32_t missing_observation_call = 0;
  uint32_t rollback_failures_before_success = 0;
  bool always_fail_rollback = false;
  bool block_first_encode = false;
  uint32_t blocked_steady_now_call = 0;
  int64_t steady_now_ms = 1'000;
  int64_t unix_now_ms = 1'700'000'000'000;

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
  void InvalidateAt(FaultStage stage) noexcept {
    if (fault_stage == stage && !invalidation_fired_.exchange(true)) {
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

  bool Start() {
    CaptureCommandAdmissionPermit permit;
    if (!AcquireOwnerPermit(safety, runtime, target, CaptureCommand::kStart,
                            &permit)) {
      return false;
    }
    return runtime.Start(std::move(permit), [this](CaptureRuntimeOwner& owner,
                                                   PersistenceToken token) {
      worker.Run(owner, std::move(token));
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
    if (!Expect(result.reason == CaptureWorkerExitReason::kAuthorizationLost &&
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
  return Expect(result.reason == CaptureWorkerExitReason::kAuthorizationLost &&
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

}  // namespace

int main() {
  if (!TestStopCommitsValidPartialChunk() ||
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
      !TestTopologyRebuildRecovers()) {
    return 1;
  }
  std::cout << "capture worker tests passed\n";
  return 0;
}

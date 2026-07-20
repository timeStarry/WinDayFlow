#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <new>
#include <thread>
#include <type_traits>
#include <utility>

#include "capture_event_queue.h"

namespace {

using windayflow::capture::CaptureEventReservation;
static_assert(!std::is_copy_constructible_v<CaptureEventReservation>);
static_assert(!std::is_copy_assignable_v<CaptureEventReservation>);
static_assert(std::is_move_constructible_v<CaptureEventReservation>);

std::atomic<bool> g_fail_next_append{false};
std::atomic<bool> g_invalidate_next_append{false};
std::atomic<bool> g_publication_current{true};

void FailAppendWhenRequested() {
  if (g_fail_next_append.exchange(false)) {
    throw std::bad_alloc();
  }
}

void InvalidatePublicationWhenRequested() {
  if (g_invalidate_next_append.exchange(false)) {
    g_publication_current.store(false);
  }
}

bool IsPublicationCurrent(void* context) noexcept {
  const auto* current = static_cast<const std::atomic<bool>*>(context);
  return current != nullptr && current->load();
}

struct CachedValidationContext {
  std::atomic<bool> current{true};
  std::atomic<bool> value_read{false};
  std::atomic<bool> return_validator{false};
};

bool CachePublicationStateThenWait(void* context) noexcept {
  auto* validation = static_cast<CachedValidationContext*>(context);
  if (validation == nullptr) {
    return false;
  }
  const bool current = validation->current.load();
  validation->value_read.store(true);
  while (!validation->return_validator.load()) {
    std::this_thread::yield();
  }
  return current;
}

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

struct ReadValue {
  windayflow::capture::CaptureEventReadResult result =
      windayflow::capture::CaptureEventReadResult::kInternalError;
  wdf_capture_event_v1 event{};
};

ReadValue ReadOne(windayflow::capture::CaptureEventQueue* queue,
                  uint32_t timeout_ms = 0) {
  ReadValue value;
  value.event.struct_size = sizeof(value.event);
  value.event.abi_version = WDF_CAPTURE_ABI_VERSION;
  std::array<char, 64> detail{};
  uint32_t required = 0;
  value.result = queue->Read(timeout_ms, &value.event, detail.data(),
                             static_cast<uint32_t>(detail.size()), &required);
  return value;
}

bool TestSequenceAndOverflowAccounting() {
  windayflow::capture::CaptureEventQueue queue(2);
  const uint64_t first =
      queue.Push(WDF_CAPTURE_EVENT_STATE_CHANGED, WDF_CAPTURE_STATE_STARTING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "first", 1);
  const uint64_t second =
      queue.Push(WDF_CAPTURE_EVENT_STATE_CHANGED, WDF_CAPTURE_STATE_RECORDING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "second", 2);
  const uint64_t third =
      queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "third", 3);
  const ReadValue second_read = ReadOne(&queue);
  const ReadValue third_read = ReadOne(&queue);
  return Expect(first == 1 && second == 2 && third == 3,
                "event sequence was not monotonic") &&
         Expect(second_read.result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    second_read.event.sequence == second &&
                    second_read.event.dropped_before == 1,
                "dropped event was not reported on the next event") &&
         Expect(third_read.result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    third_read.event.sequence == third &&
                    third_read.event.dropped_before == 0,
                "queue did not preserve surviving order");
}

bool TestSizingDoesNotConsume() {
  windayflow::capture::CaptureEventQueue queue(2);
  const uint64_t sequence = queue.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "sized detail", 1);
  wdf_capture_event_v1 event{};
  event.struct_size = sizeof(event);
  event.abi_version = WDF_CAPTURE_ABI_VERSION;
  uint32_t required = 0;
  const auto sizing = queue.Read(0, &event, nullptr, 0, &required);
  const ReadValue consumed = ReadOne(&queue);
  return Expect(sizing == windayflow::capture::CaptureEventReadResult::
                              kBufferTooSmall &&
                    required == 13 && event.sequence == sequence,
                "sizing call did not return the exact required buffer") &&
         Expect(consumed.result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    consumed.event.sequence == sequence,
                "sizing call consumed the event");
}

bool TestSingleSlotAccumulatesDrops() {
  windayflow::capture::CaptureEventQueue queue(1);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "one", 1);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "two", 2);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "three", 3);
  const ReadValue value = ReadOne(&queue);
  return Expect(value.result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    value.event.sequence == 3,
                "single-slot queue did not retain the newest event") &&
         Expect(value.event.dropped_before == 2,
                "single-slot queue did not accumulate drop count");
}

bool TestRequiredEventsAreNeverEvicted() {
  windayflow::capture::CaptureEventQueue full_of_required(2);
  const uint64_t chunk = full_of_required.Push(
      WDF_CAPTURE_EVENT_CHUNK_COMMITTED, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "chunk", 1);
  const uint64_t error =
      full_of_required.Push(WDF_CAPTURE_EVENT_ERROR, WDF_CAPTURE_STATE_FAULTED,
                            WDF_CAPTURE_REASON_BACKEND_FAULT,
                            WDF_CAPTURE_ERROR_NATIVE_FAILURE, "error", 2);
  const uint64_t rejected = full_of_required.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_FAULTED,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "diagnostic", 3);
  const ReadValue first = ReadOne(&full_of_required);
  const ReadValue second = ReadOne(&full_of_required);
  if (!Expect(chunk == 1 && error == 2 && rejected == 0,
              "required-event saturation was not reported") ||
      !Expect(first.event.sequence == chunk && second.event.sequence == error,
              "required event was evicted")) {
    return false;
  }

  windayflow::capture::CaptureEventQueue mixed(2);
  mixed.Push(WDF_CAPTURE_EVENT_STATE_CHANGED, WDF_CAPTURE_STATE_STARTING,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "starting", 1);
  const uint64_t retained_chunk =
      mixed.Push(WDF_CAPTURE_EVENT_CHUNK_COMMITTED, WDF_CAPTURE_STATE_RECORDING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "chunk", 2);
  const uint64_t retained_error =
      mixed.Push(WDF_CAPTURE_EVENT_ERROR, WDF_CAPTURE_STATE_FAULTED,
                 WDF_CAPTURE_REASON_BACKEND_FAULT,
                 WDF_CAPTURE_ERROR_NATIVE_FAILURE, "error", 3);
  const ReadValue retained_first = ReadOne(&mixed);
  const ReadValue retained_second = ReadOne(&mixed);
  return Expect(retained_error != 0 &&
                    retained_first.event.sequence == retained_chunk &&
                    retained_first.event.dropped_before == 1 &&
                    retained_second.event.sequence == retained_error,
                "required event did not replace a coalescible observation");
}

bool TestRequiredReservationProtectsCommitSlot() {
  using windayflow::capture::CaptureEventQueue;
  using windayflow::capture::CaptureEventReadResult;
  using windayflow::capture::CaptureEventReservation;

  CaptureEventQueue queue(2);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE,
             "before reservation", 1);
  CaptureEventReservation reservation = queue.ReserveRequiredEvent();
  const uint64_t diagnostic = queue.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "after reservation", 2);
  const uint64_t committed = queue.PushReserved(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "chunks/committed/capture.mp4", 3, 7, 11);
  const ReadValue first = ReadOne(&queue);
  const ReadValue second = ReadOne(&queue);

  return Expect(static_cast<bool>(reservation) == false && diagnostic != 0 &&
                    committed != 0 && queue.reserved_size() == 0,
                "reserved required event could not be committed") &&
         Expect(first.result == CaptureEventReadResult::kSuccess &&
                    first.event.sequence == diagnostic &&
                    first.event.dropped_before == 1,
                "reservation did not protect capacity or preserve drops") &&
         Expect(second.result == CaptureEventReadResult::kSuccess &&
                    second.event.sequence == committed &&
                    second.event.persistence_generation == 7 &&
                    second.event.target_epoch == 11,
                "reserved commit event lost its safety boundary");
}

bool TestReservationSaturationAndCancellation() {
  windayflow::capture::CaptureEventQueue queue(1);
  auto reservation = queue.ReserveRequiredEvent();
  auto rejected = queue.ReserveRequiredEvent();
  const uint64_t ordinary =
      queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE,
                 "must not consume reserved capacity", 1);
  if (!Expect(static_cast<bool>(reservation) && !rejected && ordinary == 0 &&
                  queue.size() == 0 && queue.reserved_size() == 1,
              "reserved capacity was overcommitted")) {
    return false;
  }

  if (!Expect(queue.CancelReservation(&reservation) && !reservation &&
                  queue.reserved_size() == 0,
              "reservation could not be cancelled")) {
    return false;
  }
  const uint64_t after_cancellation = queue.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "after cancellation", 2);
  const ReadValue value = ReadOne(&queue);
  return Expect(after_cancellation != 0 &&
                    value.result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    value.event.dropped_before == 1,
                "cancelled capacity lost rejected-event accounting");
}

bool TestReservedPushRejectsNonRequiredEventWithoutConsumption() {
  windayflow::capture::CaptureEventQueue queue(1);
  auto reservation = queue.ReserveRequiredEvent();
  const uint64_t rejected = queue.PushReserved(
      &reservation, WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "not required", 1);
  const uint64_t committed = queue.PushReserved(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "chunks/committed/capture.mp4", 2, 3, 4);
  return Expect(rejected == 0 && committed != 0 && !reservation,
                "invalid reserved push consumed the commit slot");
}

bool TestReservationIsBoundToQueueAndMoveOnly() {
  using windayflow::capture::CaptureEventQueue;

  CaptureEventQueue first(1);
  CaptureEventQueue second(1);
  auto original = first.ReserveRequiredEvent();
  CaptureEventReservation moved(std::move(original));
  auto second_reservation = second.ReserveRequiredEvent();
  const uint64_t foreign = second.PushReserved(
      &moved, WDF_CAPTURE_EVENT_CHUNK_COMMITTED, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "must not cross queues",
      1, 2, 3);
  if (!Expect(!original && moved && second_reservation && foreign == 0 &&
                  first.reserved_size() == 1 && second.reserved_size() == 1,
              "foreign reservation changed either queue")) {
    return false;
  }

  const uint64_t first_commit = first.PushReserved(
      &moved, WDF_CAPTURE_EVENT_CHUNK_COMMITTED, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "first", 2, 3, 4);
  const uint64_t second_commit = second.PushReserved(
      &second_reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "second", 3, 4, 5);
  return Expect(first_commit == 1 && second_commit == 1 && !moved &&
                    !second_reservation && first.reserved_size() == 0 &&
                    second.reserved_size() == 0,
                "queue-bound reservations could not be consumed by issuer");
}

bool TestAppendFailurePreservesQueueAndReservation() {
  using windayflow::capture::CaptureEventQueue;
  using windayflow::capture::CaptureEventReadResult;

  CaptureEventQueue ordinary(1, FailAppendWhenRequested);
  const uint64_t first = ordinary.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "first", 1);
  g_fail_next_append.store(true);
  const uint64_t failed = ordinary.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "failed", 2);
  if (!Expect(first == 1 && failed == 0 && ordinary.size() == 1,
              "failed append evicted an event or advanced the queue")) {
    return false;
  }
  const uint64_t retry = ordinary.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "retry", 3);
  const ReadValue retried = ReadOne(&ordinary);
  if (!Expect(
          retry == 2 && retried.result == CaptureEventReadResult::kSuccess &&
              retried.event.sequence == 2 && retried.event.dropped_before == 1,
          "ordinary append failure corrupted sequence or drop accounting")) {
    return false;
  }

  CaptureEventQueue reserved(1, FailAppendWhenRequested);
  auto reservation = reserved.ReserveRequiredEvent();
  g_fail_next_append.store(true);
  const uint64_t failed_commit = reserved.PushReserved(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "failed commit", 4, 5, 6);
  if (!Expect(failed_commit == 0 && reservation && reserved.size() == 0 &&
                  reserved.reserved_size() == 1,
              "failed required append consumed its reservation")) {
    return false;
  }
  const uint64_t committed = reserved.PushReserved(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "committed", 5, 6, 7);
  return Expect(committed == 1 && !reservation && reserved.size() == 1 &&
                    reserved.reserved_size() == 0,
                "reserved append could not retry after allocation failure");
}

bool TestPostAppendValidationPreventsVisibility() {
  using windayflow::capture::CaptureEventQueue;
  using windayflow::capture::CaptureEventReadResult;

  CaptureEventQueue queue(1, InvalidatePublicationWhenRequested);
  auto reservation = queue.ReserveRequiredEvent();
  g_publication_current.store(true);
  g_invalidate_next_append.store(true);
  const uint64_t rejected = queue.PushReservedValidated(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "must remain hidden", 1, 2, 3,
      IsPublicationCurrent, &g_publication_current);
  const ReadValue empty = ReadOne(&queue);
  if (!Expect(rejected == 0 && reservation && queue.size() == 0 &&
                  queue.reserved_size() == 1 &&
                  empty.result == CaptureEventReadResult::kEmpty,
              "failed post-append validation exposed or consumed the event")) {
    return false;
  }

  g_publication_current.store(true);
  const uint64_t committed = queue.PushReservedValidated(
      &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE, "validated", 2, 3, 4, IsPublicationCurrent,
      &g_publication_current);
  const ReadValue value = ReadOne(&queue);
  return Expect(committed == 1 && !reservation && queue.reserved_size() == 0 &&
                    value.result == CaptureEventReadResult::kSuccess &&
                    value.event.sequence == 1 &&
                    value.event.persistence_generation == 3 &&
                    value.event.target_epoch == 4,
                "validated retry changed sequence or safety metadata");
}

bool TestValidationReadIsPublicationLinearizationPoint() {
  using windayflow::capture::CaptureEventQueue;
  using windayflow::capture::CaptureEventReadResult;

  CaptureEventQueue queue(1);
  auto reservation = queue.ReserveRequiredEvent();
  CachedValidationContext validation;
  std::atomic<uint64_t> sequence{0};
  std::thread publisher([&] {
    sequence.store(queue.PushReservedValidated(
        &reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
        WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
        WDF_CAPTURE_ERROR_NONE, "linearized", 1, 2, 3,
        CachePublicationStateThenWait, &validation));
  });

  const auto deadline =
      std::chrono::steady_clock::now() + std::chrono::seconds(1);
  while (!validation.value_read.load() &&
         std::chrono::steady_clock::now() < deadline) {
    std::this_thread::yield();
  }
  const bool read_before_deadline = validation.value_read.load();
  validation.current.store(false);
  validation.return_validator.store(true);
  publisher.join();

  const ReadValue value = ReadOne(&queue);
  return Expect(read_before_deadline && sequence.load() == 1 && !reservation &&
                    value.result == CaptureEventReadResult::kSuccess &&
                    value.event.sequence == 1,
                "invalidation after the validator read reordered publication");
}

bool TestConcurrentReadersConsumeDistinctEvents() {
  windayflow::capture::CaptureEventQueue queue(4);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "one", 1);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "two", 2);

  std::array<ReadValue, 2> values{};
  std::thread first([&] { values[0] = ReadOne(&queue, 1'000); });
  std::thread second([&] { values[1] = ReadOne(&queue, 1'000); });
  first.join();
  second.join();
  return Expect(values[0].result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess &&
                    values[1].result ==
                        windayflow::capture::CaptureEventReadResult::kSuccess,
                "concurrent reader failed") &&
         Expect(values[0].event.sequence != values[1].event.sequence,
                "concurrent readers consumed the same event");
}

bool TestCloseWakesBoundedReader() {
  windayflow::capture::CaptureEventQueue queue(2);
  std::atomic result{
      windayflow::capture::CaptureEventReadResult::kInternalError};
  std::thread reader([&] { result.store(ReadOne(&queue, 5'000).result); });
  std::this_thread::sleep_for(std::chrono::milliseconds(20));
  queue.Close();
  reader.join();
  return Expect(
      result.load() == windayflow::capture::CaptureEventReadResult::kClosed,
      "close did not wake a bounded reader");
}

bool TestCloseDrainsQueuedEventAndAllowsReservationCancellation() {
  using windayflow::capture::CaptureEventQueue;
  using windayflow::capture::CaptureEventReadResult;

  CaptureEventQueue queue(2);
  const uint64_t queued =
      queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC, WDF_CAPTURE_STATE_RECORDING,
                 WDF_CAPTURE_REASON_NONE, WDF_CAPTURE_ERROR_NONE, "queued", 1);
  auto reservation = queue.ReserveRequiredEvent();
  queue.Close();
  const uint64_t rejected =
      queue.PushReserved(&reservation, WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
                         WDF_CAPTURE_STATE_RECORDING, WDF_CAPTURE_REASON_NONE,
                         WDF_CAPTURE_ERROR_NONE, "closed", 2, 3, 4);
  const ReadValue drained = ReadOne(&queue);
  const ReadValue closed = ReadOne(&queue);
  return Expect(queued == 1 && rejected == 0 && reservation &&
                    drained.result == CaptureEventReadResult::kSuccess &&
                    drained.event.sequence == queued &&
                    closed.result == CaptureEventReadResult::kClosed,
                "close did not preserve queued data or reject publication") &&
         Expect(queue.CancelReservation(&reservation) && !reservation &&
                    queue.reserved_size() == 0,
                "closed queue could not release an outstanding reservation");
}

}  // namespace

int main() {
  if (!TestSequenceAndOverflowAccounting() || !TestSizingDoesNotConsume() ||
      !TestSingleSlotAccumulatesDrops() ||
      !TestRequiredEventsAreNeverEvicted() ||
      !TestRequiredReservationProtectsCommitSlot() ||
      !TestReservationSaturationAndCancellation() ||
      !TestReservedPushRejectsNonRequiredEventWithoutConsumption() ||
      !TestReservationIsBoundToQueueAndMoveOnly() ||
      !TestAppendFailurePreservesQueueAndReservation() ||
      !TestPostAppendValidationPreventsVisibility() ||
      !TestValidationReadIsPublicationLinearizationPoint() ||
      !TestConcurrentReadersConsumeDistinctEvents() ||
      !TestCloseWakesBoundedReader() ||
      !TestCloseDrainsQueuedEventAndAllowsReservationCancellation()) {
    return 1;
  }
  std::cout << "capture event queue tests passed\n";
  return 0;
}

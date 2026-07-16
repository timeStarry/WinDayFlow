#include "capture_event_queue.h"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <thread>

namespace {

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
  value.result = queue->Read(timeout_ms,
                             &value.event,
                             detail.data(),
                             static_cast<uint32_t>(detail.size()),
                             &required);
  return value;
}

bool TestSequenceAndOverflowAccounting() {
  windayflow::capture::CaptureEventQueue queue(2);
  const uint64_t first = queue.Push(WDF_CAPTURE_EVENT_STATE_CHANGED,
                                    WDF_CAPTURE_STATE_STARTING,
                                    WDF_CAPTURE_REASON_NONE,
                                    WDF_CAPTURE_ERROR_NONE,
                                    "first",
                                    1);
  const uint64_t second = queue.Push(WDF_CAPTURE_EVENT_STATE_CHANGED,
                                     WDF_CAPTURE_STATE_RECORDING,
                                     WDF_CAPTURE_REASON_NONE,
                                     WDF_CAPTURE_ERROR_NONE,
                                     "second",
                                     2);
  const uint64_t third = queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
                                    WDF_CAPTURE_STATE_RECORDING,
                                    WDF_CAPTURE_REASON_NONE,
                                    WDF_CAPTURE_ERROR_NONE,
                                    "third",
                                    3);
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
  const uint64_t sequence = queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
                                       WDF_CAPTURE_STATE_UNAVAILABLE,
                                       WDF_CAPTURE_REASON_NONE,
                                       WDF_CAPTURE_ERROR_NONE,
                                       "sized detail",
                                       1);
  wdf_capture_event_v1 event{};
  event.struct_size = sizeof(event);
  event.abi_version = WDF_CAPTURE_ABI_VERSION;
  uint32_t required = 0;
  const auto sizing = queue.Read(0, &event, nullptr, 0, &required);
  const ReadValue consumed = ReadOne(&queue);
  return Expect(sizing ==
                        windayflow::capture::CaptureEventReadResult::
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
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
             WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "one",
             1);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
             WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "two",
             2);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
             WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "three",
             3);
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
      WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE,
      "chunk",
      1);
  const uint64_t error = full_of_required.Push(WDF_CAPTURE_EVENT_ERROR,
                                               WDF_CAPTURE_STATE_FAULTED,
                                               WDF_CAPTURE_REASON_BACKEND_FAULT,
                                               WDF_CAPTURE_ERROR_NATIVE_FAILURE,
                                               "error",
                                               2);
  const uint64_t rejected = full_of_required.Push(
      WDF_CAPTURE_EVENT_DIAGNOSTIC,
      WDF_CAPTURE_STATE_FAULTED,
      WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE,
      "diagnostic",
      3);
  const ReadValue first = ReadOne(&full_of_required);
  const ReadValue second = ReadOne(&full_of_required);
  if (!Expect(chunk == 1 && error == 2 && rejected == 0,
              "required-event saturation was not reported") ||
      !Expect(first.event.sequence == chunk && second.event.sequence == error,
              "required event was evicted")) {
    return false;
  }

  windayflow::capture::CaptureEventQueue mixed(2);
  mixed.Push(WDF_CAPTURE_EVENT_STATE_CHANGED,
             WDF_CAPTURE_STATE_STARTING,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "starting",
             1);
  const uint64_t retained_chunk = mixed.Push(
      WDF_CAPTURE_EVENT_CHUNK_COMMITTED,
      WDF_CAPTURE_STATE_RECORDING,
      WDF_CAPTURE_REASON_NONE,
      WDF_CAPTURE_ERROR_NONE,
      "chunk",
      2);
  const uint64_t retained_error = mixed.Push(WDF_CAPTURE_EVENT_ERROR,
                                             WDF_CAPTURE_STATE_FAULTED,
                                             WDF_CAPTURE_REASON_BACKEND_FAULT,
                                             WDF_CAPTURE_ERROR_NATIVE_FAILURE,
                                             "error",
                                             3);
  const ReadValue retained_first = ReadOne(&mixed);
  const ReadValue retained_second = ReadOne(&mixed);
  return Expect(retained_error != 0 &&
                    retained_first.event.sequence == retained_chunk &&
                    retained_first.event.dropped_before == 1 &&
                    retained_second.event.sequence == retained_error,
                "required event did not replace a coalescible observation");
}

bool TestConcurrentReadersConsumeDistinctEvents() {
  windayflow::capture::CaptureEventQueue queue(4);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
             WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "one",
             1);
  queue.Push(WDF_CAPTURE_EVENT_DIAGNOSTIC,
             WDF_CAPTURE_STATE_UNAVAILABLE,
             WDF_CAPTURE_REASON_NONE,
             WDF_CAPTURE_ERROR_NONE,
             "two",
             2);

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
  return Expect(result.load() ==
                    windayflow::capture::CaptureEventReadResult::kClosed,
                "close did not wake a bounded reader");
}

}  // namespace

int main() {
  if (!TestSequenceAndOverflowAccounting() || !TestSizingDoesNotConsume() ||
      !TestSingleSlotAccumulatesDrops() ||
      !TestRequiredEventsAreNeverEvicted() ||
      !TestConcurrentReadersConsumeDistinctEvents() ||
      !TestCloseWakesBoundedReader()) {
    return 1;
  }
  std::cout << "capture event queue tests passed\n";
  return 0;
}

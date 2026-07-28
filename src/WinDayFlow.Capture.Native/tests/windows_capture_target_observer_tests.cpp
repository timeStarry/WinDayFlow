#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <iostream>
#include <string>
#include <utility>
#include <vector>

#include "windows_capture_target_observer.h"

namespace {

using windayflow::capture::CaptureTargetIdentity;
using windayflow::capture::CaptureAuthorizationScope;
using windayflow::capture::IWindowsCaptureTargetObserverApi;

constexpr uint64_t kWindowHandle = 0x101;
constexpr DWORD kThreadId = 0x202;
constexpr DWORD kProcessId = 0x303;
constexpr uint64_t kCreationTime = 0x0102030405060708;
constexpr uint64_t kTargetEpoch = 0x404;
constexpr uint64_t kMonitorHandle = 0x505;
constexpr wchar_t kDeviceKey[] = LR"(\\.\DISPLAY7)";

HWND TestWindow(uint64_t value) {
  return reinterpret_cast<HWND>(static_cast<uintptr_t>(value));
}

HANDLE TestProcess(uint64_t value) {
  return reinterpret_cast<HANDLE>(static_cast<uintptr_t>(value));
}

HMONITOR TestMonitor(uint64_t value) {
  return reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(value));
}

CaptureTargetIdentity ExpectedTarget() {
  return CaptureTargetIdentity{kWindowHandle, kProcessId,     kCreationTime,
                               kTargetEpoch,  kMonitorHandle, kDeviceKey};
}

CaptureTargetIdentity ExpectedDisplayWideTarget() {
  return CaptureTargetIdentity{0,
                               0,
                               0,
                               kTargetEpoch,
                               kMonitorHandle,
                               kDeviceKey,
                               CaptureAuthorizationScope::kDisplayWide};
}

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

struct OwnerRead {
  DWORD thread_id = kThreadId;
  DWORD process_id = kProcessId;
};

struct TimeRead {
  BOOL succeeded = TRUE;
  uint64_t creation_time = kCreationTime;
};

struct MonitorInfoRead {
  BOOL succeeded = TRUE;
  std::wstring device_key = kDeviceKey;
  bool terminate_device_key = true;
};

template <typename T>
const T& NextRead(const std::vector<T>& reads, size_t* index,
                  const T& fallback) noexcept {
  if (*index < reads.size()) {
    return reads[(*index)++];
  }
  ++*index;
  return fallback;
}

class FakeWindowsCaptureTargetObserverApi final
    : public IWindowsCaptureTargetObserverApi {
 public:
  HWND foreground = TestWindow(kWindowHandle);
  OwnerRead owner;
  HANDLE opened_process = TestProcess(0x606);
  DWORD process_id = kProcessId;
  TimeRead process_time;
  HMONITOR monitor = TestMonitor(kMonitorHandle);
  MonitorInfoRead monitor_info;
  BOOL close_result = TRUE;

  std::vector<HWND> foreground_reads;
  std::vector<OwnerRead> owner_reads;
  std::vector<DWORD> process_id_reads;
  std::vector<TimeRead> process_time_reads;
  std::vector<HMONITOR> monitor_reads;
  std::vector<MonitorInfoRead> monitor_info_reads;

  size_t foreground_calls = 0;
  size_t owner_calls = 0;
  size_t open_calls = 0;
  size_t process_id_calls = 0;
  size_t process_time_calls = 0;
  size_t monitor_calls = 0;
  size_t monitor_info_calls = 0;
  size_t close_calls = 0;
  DWORD opened_access = 0;
  BOOL opened_inherit_handle = TRUE;
  DWORD opened_process_id = 0;
  bool monitor_flags_were_strict = true;
  bool monitor_info_sizes_were_valid = true;

  HWND ReadForegroundWindow() noexcept override {
    return NextRead(foreground_reads, &foreground_calls, foreground);
  }

  DWORD ReadWindowOwner(HWND window,
                        DWORD* observed_process_id) noexcept override {
    if (window == nullptr || observed_process_id == nullptr) {
      return 0;
    }
    const OwnerRead& read = NextRead(owner_reads, &owner_calls, owner);
    *observed_process_id = read.process_id;
    return read.thread_id;
  }

  HANDLE OpenTargetProcess(DWORD desired_access, BOOL inherit_handle,
                           DWORD requested_process_id) noexcept override {
    ++open_calls;
    opened_access = desired_access;
    opened_inherit_handle = inherit_handle;
    opened_process_id = requested_process_id;
    return opened_process;
  }

  DWORD ReadProcessId(HANDLE process) noexcept override {
    if (process == nullptr || process == INVALID_HANDLE_VALUE) {
      return 0;
    }
    return NextRead(process_id_reads, &process_id_calls, process_id);
  }

  BOOL ReadProcessTimes(HANDLE process, FILETIME* creation, FILETIME* exit,
                        FILETIME* kernel, FILETIME* user) noexcept override {
    if (process == nullptr || process == INVALID_HANDLE_VALUE ||
        creation == nullptr || exit == nullptr || kernel == nullptr ||
        user == nullptr) {
      return FALSE;
    }
    const TimeRead& read =
        NextRead(process_time_reads, &process_time_calls, process_time);
    *creation = FILETIME{
        static_cast<DWORD>(read.creation_time & 0xffffffffU),
        static_cast<DWORD>(read.creation_time >> 32U),
    };
    *exit = {};
    *kernel = {};
    *user = {};
    return read.succeeded;
  }

  HMONITOR ReadWindowMonitor(HWND window, DWORD flags) noexcept override {
    monitor_flags_were_strict =
        monitor_flags_were_strict && flags == MONITOR_DEFAULTTONULL;
    if (window == nullptr) {
      return nullptr;
    }
    return NextRead(monitor_reads, &monitor_calls, monitor);
  }

  BOOL ReadMonitorInfo(HMONITOR observed_monitor,
                       MONITORINFOEXW* value) noexcept override {
    if (observed_monitor == nullptr || value == nullptr) {
      return FALSE;
    }
    ++monitor_info_calls;
    monitor_info_sizes_were_valid =
        monitor_info_sizes_were_valid && value->cbSize == sizeof(*value);
    const size_t index = monitor_info_calls - 1;
    const MonitorInfoRead& read = index < monitor_info_reads.size()
                                      ? monitor_info_reads[index]
                                      : monitor_info;
    if (read.succeeded == FALSE) {
      return FALSE;
    }

    const DWORD supplied_size = value->cbSize;
    *value = {};
    value->cbSize = supplied_size;
    if (!read.terminate_device_key) {
      std::fill(std::begin(value->szDevice), std::end(value->szDevice), L'X');
      return TRUE;
    }
    const size_t copied = (std::min)(read.device_key.size(),
                                     static_cast<size_t>(CCHDEVICENAME - 1));
    std::copy_n(read.device_key.data(), copied, value->szDevice);
    value->szDevice[copied] = L'\0';
    return TRUE;
  }

  BOOL CloseTargetProcess(HANDLE process) noexcept override {
    if (process == nullptr || process == INVALID_HANDLE_VALUE) {
      return FALSE;
    }
    ++close_calls;
    return close_result;
  }

  size_t TotalCalls() const noexcept {
    return foreground_calls + owner_calls + open_calls + process_id_calls +
           process_time_calls + monitor_calls + monitor_info_calls +
           close_calls;
  }
};

bool TestStableObservationUsesOnlyStrictIdentityCalls() {
  FakeWindowsCaptureTargetObserverApi api;
  api.monitor_info.device_key = LR"(\\.\display7)";
  const CaptureTargetIdentity expected = ExpectedTarget();
  const auto observed =
      windayflow::capture::ObserveWindowsCaptureTargetWithApi(api, expected);

  return Expect(observed.has_value() && *observed == expected,
                "stable target identity was not observed") &&
         Expect(observed->display_device_key == LR"(\\.\display7)",
                "observer did not return the monitor API device key") &&
         Expect(api.foreground_calls == 2 && api.owner_calls == 2 &&
                    api.open_calls == 1 && api.process_id_calls == 2 &&
                    api.process_time_calls == 2 && api.monitor_calls == 2 &&
                    api.monitor_info_calls == 2 && api.close_calls == 1,
                "stable target did not receive complete revalidation") &&
         Expect(api.opened_access == PROCESS_QUERY_LIMITED_INFORMATION &&
                    api.opened_inherit_handle == FALSE &&
                    api.opened_process_id == kProcessId,
                "process was not opened with the minimal strict identity "
                "access") &&
         Expect(
             api.monitor_flags_were_strict && api.monitor_info_sizes_were_valid,
             "display observation used fallback flags or an invalid structure");
}

bool TestInvalidExpectedTargetsAreRejectedBeforeNativeCalls() {
  std::vector<CaptureTargetIdentity> invalid;
  CaptureTargetIdentity value = ExpectedTarget();
  value.window_handle = 0;
  invalid.push_back(value);
  value = ExpectedTarget();
  value.process_id = 0;
  invalid.push_back(value);
  value = ExpectedTarget();
  value.process_creation_time_100ns = 0;
  invalid.push_back(value);
  value = ExpectedTarget();
  value.target_epoch = 0;
  invalid.push_back(value);
  value = ExpectedTarget();
  value.display_monitor_handle = 0;
  invalid.push_back(value);
  value = ExpectedTarget();
  value.display_device_key.clear();
  invalid.push_back(value);
  value = ExpectedTarget();
  value.display_device_key.assign(CCHDEVICENAME, L'X');
  invalid.push_back(value);
  value = ExpectedTarget();
  value.display_device_key = std::wstring{L'X', L'\n'};
  invalid.push_back(value);

  for (const CaptureTargetIdentity& target : invalid) {
    FakeWindowsCaptureTargetObserverApi api;
    if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                    api, target),
                "invalid expected target was accepted") ||
        !Expect(api.TotalCalls() == 0,
                "invalid expected target reached the native API")) {
      return false;
    }
  }
  return true;
}

bool TestEveryNativeFailureFailsClosed() {
  const CaptureTargetIdentity expected = ExpectedTarget();

  std::vector<std::pair<const char*, FakeWindowsCaptureTargetObserverApi>>
      cases;
  FakeWindowsCaptureTargetObserverApi api;
  api.foreground = nullptr;
  cases.emplace_back("null foreground window was accepted", std::move(api));
  api = {};
  api.owner.thread_id = 0;
  cases.emplace_back("window owner failure was accepted", std::move(api));
  api = {};
  api.owner.process_id = 0;
  cases.emplace_back("zero owner PID was accepted", std::move(api));
  api = {};
  api.opened_process = nullptr;
  cases.emplace_back("process open failure was accepted", std::move(api));
  api = {};
  api.opened_process = INVALID_HANDLE_VALUE;
  cases.emplace_back("invalid process handle was accepted", std::move(api));
  api = {};
  api.process_id = 0;
  cases.emplace_back("process ID read failure was accepted", std::move(api));
  api = {};
  api.process_time.succeeded = FALSE;
  cases.emplace_back("process time read failure was accepted", std::move(api));
  api = {};
  api.process_time.creation_time = 0;
  cases.emplace_back("zero process creation time was accepted", std::move(api));
  api = {};
  api.monitor = nullptr;
  cases.emplace_back("null monitor without fallback was accepted",
                     std::move(api));
  api = {};
  api.monitor_info.succeeded = FALSE;
  cases.emplace_back("monitor info failure was accepted", std::move(api));
  api = {};
  api.monitor_info.device_key.clear();
  cases.emplace_back("empty monitor device key was accepted", std::move(api));
  api = {};
  api.monitor_info.terminate_device_key = false;
  cases.emplace_back("unterminated monitor device key was accepted",
                     std::move(api));
  api = {};
  api.close_result = FALSE;
  cases.emplace_back("process close failure was accepted", std::move(api));

  for (auto& [message, failing_api] : cases) {
    if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                    failing_api, expected),
                message)) {
      return false;
    }
    const bool valid_opened_handle =
        failing_api.open_calls != 0 && failing_api.opened_process != nullptr &&
        failing_api.opened_process != INVALID_HANDLE_VALUE;
    if (!Expect(failing_api.close_calls == (valid_opened_handle ? 1U : 0U),
                "an opened process handle was leaked or closed twice")) {
      return false;
    }
  }
  return true;
}

bool TestIdentityMismatchesFailClosed() {
  const CaptureTargetIdentity expected = ExpectedTarget();

  FakeWindowsCaptureTargetObserverApi foreground_mismatch;
  foreground_mismatch.foreground = TestWindow(kWindowHandle + 1);
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  foreground_mismatch, expected),
              "foreground HWND mismatch was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi owner_mismatch;
  owner_mismatch.owner.process_id = kProcessId + 1;
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  owner_mismatch, expected),
              "window owner PID mismatch was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi process_mismatch;
  process_mismatch.process_id = kProcessId + 1;
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  process_mismatch, expected),
              "opened process identity mismatch was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi creation_mismatch;
  creation_mismatch.process_time.creation_time = kCreationTime + 1;
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  creation_mismatch, expected),
              "process creation time mismatch was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi monitor_mismatch;
  monitor_mismatch.monitor = TestMonitor(kMonitorHandle + 1);
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  monitor_mismatch, expected),
              "monitor handle mismatch was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi device_mismatch;
  device_mismatch.monitor_info.device_key = LR"(\\.\DISPLAY8)";
  return Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                    device_mismatch, expected),
                "monitor device key mismatch was accepted");
}

bool TestRevalidationRejectsIdentityRaces() {
  const CaptureTargetIdentity expected = ExpectedTarget();

  FakeWindowsCaptureTargetObserverApi foreground_changed;
  foreground_changed.foreground_reads = {TestWindow(kWindowHandle),
                                         TestWindow(kWindowHandle + 1)};
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  foreground_changed, expected),
              "foreground change during observation was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi owner_changed;
  owner_changed.owner_reads = {{kThreadId, kProcessId},
                               {kThreadId + 1, kProcessId}};
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  owner_changed, expected),
              "window thread change during observation was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi process_changed;
  process_changed.process_id_reads = {kProcessId, kProcessId + 1};
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  process_changed, expected),
              "process handle identity change was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi creation_changed;
  creation_changed.process_time_reads = {{TRUE, kCreationTime},
                                         {TRUE, kCreationTime + 1}};
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  creation_changed, expected),
              "process creation time change was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi monitor_changed;
  monitor_changed.monitor_reads = {TestMonitor(kMonitorHandle),
                                   TestMonitor(kMonitorHandle + 1)};
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  monitor_changed, expected),
              "monitor change during observation was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi device_changed;
  device_changed.monitor_info_reads = {
      {TRUE, LR"(\\.\DISPLAY7)", true},
      {TRUE, LR"(\\.\DISPLAY8)", true},
  };
  return Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                    device_changed, expected),
                "monitor device key change during observation was accepted");
}

bool TestWindowHandleProcessHandleAndPidReuseFailClosed() {
  const CaptureTargetIdentity expected = ExpectedTarget();

  FakeWindowsCaptureTargetObserverApi reused_window;
  reused_window.owner.process_id = kProcessId + 1;
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  reused_window, expected),
              "reused HWND owned by another PID was accepted") ||
      !Expect(reused_window.open_calls == 0,
              "reused HWND opened the replacement process")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi reused_process_handle;
  reused_process_handle.process_id = kProcessId + 1;
  if (!Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                  reused_process_handle, expected),
              "reused process HANDLE for another PID was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi reused_pid;
  reused_pid.process_time.creation_time = kCreationTime + 1;
  return Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                    reused_pid, expected),
                "reused PID with a different creation time was accepted");
}

bool TestDisplayWideObservationAvoidsApplicationIdentityCalls() {
  FakeWindowsCaptureTargetObserverApi api;
  api.foreground = TestWindow(kWindowHandle + 1);
  api.owner = {kThreadId + 1, kProcessId + 1};
  api.monitor_info.device_key = LR"(\\.\display7)";
  const CaptureTargetIdentity expected = ExpectedDisplayWideTarget();
  const auto observed =
      windayflow::capture::ObserveWindowsCaptureAuthorizationWithApi(
          api, expected);

  return Expect(observed.has_value() && *observed == expected,
                "stable display-wide authorization was not observed") &&
         Expect(api.monitor_info_calls == 2 && api.foreground_calls == 0 &&
                    api.owner_calls == 0 && api.open_calls == 0 &&
                    api.process_id_calls == 0 && api.process_time_calls == 0 &&
                    api.monitor_calls == 0 && api.close_calls == 0,
                "display-wide observation read foreground application identity") &&
         Expect(api.monitor_info_sizes_were_valid,
                "display-wide observation used an invalid monitor structure");
}

bool TestDisplayWideObservationFailsClosed() {
  const CaptureTargetIdentity expected = ExpectedDisplayWideTarget();

  FakeWindowsCaptureTargetObserverApi first_read_failed;
  first_read_failed.monitor_info.succeeded = FALSE;
  if (!Expect(
          !windayflow::capture::ObserveWindowsCaptureAuthorizationWithApi(
              first_read_failed, expected),
          "display-wide monitor read failure was accepted")) {
    return false;
  }

  FakeWindowsCaptureTargetObserverApi display_changed;
  display_changed.monitor_info_reads = {
      {TRUE, kDeviceKey, true},
      {TRUE, LR"(\\.\DISPLAY8)", true},
  };
  if (!Expect(
          !windayflow::capture::ObserveWindowsCaptureAuthorizationWithApi(
              display_changed, expected),
          "display-wide monitor identity race was accepted")) {
    return false;
  }

  CaptureTargetIdentity invalid = expected;
  invalid.process_id = kProcessId;
  FakeWindowsCaptureTargetObserverApi invalid_api;
  return Expect(
             !windayflow::capture::ObserveWindowsCaptureAuthorizationWithApi(
                 invalid_api, invalid),
             "display-wide target retained a process identity") &&
         Expect(invalid_api.TotalCalls() == 0,
                "invalid display-wide target reached the native API") &&
         Expect(!windayflow::capture::ObserveWindowsCaptureTargetWithApi(
                     invalid_api, expected),
                "foreground-only observer accepted display-wide scope") &&
         Expect(invalid_api.TotalCalls() == 0,
                "display-wide scope reached foreground observation APIs");
}

}  // namespace

int main() {
  const bool passed =
      TestStableObservationUsesOnlyStrictIdentityCalls() &&
      TestInvalidExpectedTargetsAreRejectedBeforeNativeCalls() &&
      TestEveryNativeFailureFailsClosed() &&
      TestIdentityMismatchesFailClosed() &&
      TestRevalidationRejectsIdentityRaces() &&
      TestWindowHandleProcessHandleAndPidReuseFailClosed() &&
      TestDisplayWideObservationAvoidsApplicationIdentityCalls() &&
      TestDisplayWideObservationFailsClosed();
  if (!passed) {
    return 1;
  }
  std::cout << "windows capture target observer tests passed\n";
  return 0;
}

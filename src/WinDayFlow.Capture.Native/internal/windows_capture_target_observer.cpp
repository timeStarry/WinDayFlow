#include "windows_capture_target_observer.h"

#include <array>
#include <cstdint>
#include <limits>
#include <string>
#include <string_view>
#include <utility>

namespace windayflow::capture {
namespace {

struct WindowOwner {
  DWORD thread_id = 0;
  DWORD process_id = 0;

  bool operator==(const WindowOwner&) const = default;
};

struct DisplayAnchor {
  HMONITOR monitor = nullptr;
  std::wstring device_key;
};

class ScopedProcessHandle {
 public:
  ScopedProcessHandle(IWindowsCaptureTargetObserverApi& api,
                      HANDLE handle) noexcept
      : api_(api), handle_(handle) {}

  ~ScopedProcessHandle() {
    if (IsValid()) {
      static_cast<void>(api_.CloseTargetProcess(handle_));
    }
  }

  ScopedProcessHandle(const ScopedProcessHandle&) = delete;
  ScopedProcessHandle& operator=(const ScopedProcessHandle&) = delete;

  HANDLE get() const noexcept { return handle_; }

  bool Close() noexcept {
    if (!IsValid()) {
      return false;
    }
    const HANDLE handle = std::exchange(handle_, nullptr);
    return api_.CloseTargetProcess(handle) != FALSE;
  }

 private:
  bool IsValid() const noexcept {
    return handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE;
  }

  IWindowsCaptureTargetObserverApi& api_;
  HANDLE handle_ = nullptr;
};

class WindowsCaptureTargetObserverApi final
    : public IWindowsCaptureTargetObserverApi {
 public:
  HWND ReadForegroundWindow() noexcept override {
    return ::GetForegroundWindow();
  }

  DWORD ReadWindowOwner(HWND window, DWORD* process_id) noexcept override {
    return ::GetWindowThreadProcessId(window, process_id);
  }

  HANDLE OpenTargetProcess(DWORD desired_access, BOOL inherit_handle,
                           DWORD process_id) noexcept override {
    return ::OpenProcess(desired_access, inherit_handle, process_id);
  }

  DWORD ReadProcessId(HANDLE process) noexcept override {
    return ::GetProcessId(process);
  }

  BOOL ReadProcessTimes(HANDLE process, FILETIME* creation_time,
                        FILETIME* exit_time, FILETIME* kernel_time,
                        FILETIME* user_time) noexcept override {
    return ::GetProcessTimes(process, creation_time, exit_time, kernel_time,
                             user_time);
  }

  HMONITOR ReadWindowMonitor(HWND window, DWORD flags) noexcept override {
    return ::MonitorFromWindow(window, flags);
  }

  BOOL ReadMonitorInfo(HMONITOR monitor,
                       MONITORINFOEXW* monitor_info) noexcept override {
    if (monitor_info == nullptr) {
      return FALSE;
    }
    return ::GetMonitorInfoW(monitor,
                             reinterpret_cast<LPMONITORINFO>(monitor_info));
  }

  BOOL CloseTargetProcess(HANDLE process) noexcept override {
    return ::CloseHandle(process);
  }
};

uint64_t HandleValue(const void* handle) noexcept {
  return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(handle));
}

bool IsRepresentableHandleValue(uint64_t value) noexcept {
  return value != 0 &&
         static_cast<uint64_t>(static_cast<uintptr_t>(value)) == value;
}

bool IsValidDeviceKey(std::wstring_view value) noexcept {
  if (value.empty() || value.size() >= CCHDEVICENAME ||
      value.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }

  std::array<WORD, CCHDEVICENAME - 1> character_types{};
  if (::GetStringTypeW(CT_CTYPE1, value.data(), static_cast<int>(value.size()),
                       character_types.data()) == 0) {
    return false;
  }

  bool all_whitespace = true;
  for (size_t index = 0; index < value.size(); ++index) {
    if (value[index] == L'\0' || (character_types[index] & C1_CNTRL) != 0) {
      return false;
    }
    all_whitespace = all_whitespace && (character_types[index] & C1_SPACE) != 0;
  }
  return !all_whitespace;
}

bool IsValidExpectedForegroundTarget(
    const CaptureTargetIdentity& expected) noexcept {
  return expected.scope == CaptureAuthorizationScope::kForegroundTarget &&
         IsRepresentableHandleValue(expected.window_handle) &&
         expected.process_id != 0 &&
         expected.process_creation_time_100ns != 0 &&
         expected.target_epoch != 0 &&
         IsRepresentableHandleValue(expected.display_monitor_handle) &&
         IsValidDeviceKey(expected.display_device_key);
}

bool IsValidExpectedDisplayWideTarget(
    const CaptureTargetIdentity& expected) noexcept {
  return expected.scope == CaptureAuthorizationScope::kDisplayWide &&
         expected.window_handle == 0 && expected.process_id == 0 &&
         expected.process_creation_time_100ns == 0 &&
         expected.target_epoch != 0 &&
         IsRepresentableHandleValue(expected.display_monitor_handle) &&
         IsValidDeviceKey(expected.display_device_key);
}

bool IsValidExpectedTarget(const CaptureTargetIdentity& expected) noexcept {
  return IsValidExpectedForegroundTarget(expected) ||
         IsValidExpectedDisplayWideTarget(expected);
}

bool DeviceKeysEqual(std::wstring_view left, std::wstring_view right) noexcept {
  if (!IsValidDeviceKey(left) || !IsValidDeviceKey(right)) {
    return false;
  }
  return ::CompareStringOrdinal(left.data(), static_cast<int>(left.size()),
                                right.data(), static_cast<int>(right.size()),
                                TRUE) == CSTR_EQUAL;
}

bool ReadOwner(IWindowsCaptureTargetObserverApi& api, HWND window,
               WindowOwner* owner) noexcept {
  if (owner == nullptr) {
    return false;
  }
  *owner = {};
  owner->thread_id = api.ReadWindowOwner(window, &owner->process_id);
  return owner->thread_id != 0 && owner->process_id != 0;
}

bool ReadCreationTime(IWindowsCaptureTargetObserverApi& api, HANDLE process,
                      uint64_t* creation_time_100ns) noexcept {
  if (creation_time_100ns == nullptr) {
    return false;
  }
  *creation_time_100ns = 0;
  FILETIME creation_time{};
  FILETIME exit_time{};
  FILETIME kernel_time{};
  FILETIME user_time{};
  if (api.ReadProcessTimes(process, &creation_time, &exit_time, &kernel_time,
                           &user_time) == FALSE) {
    return false;
  }
  *creation_time_100ns =
      (static_cast<uint64_t>(creation_time.dwHighDateTime) << 32U) |
      creation_time.dwLowDateTime;
  return *creation_time_100ns != 0;
}

bool ReadDisplayAnchor(IWindowsCaptureTargetObserverApi& api, HWND window,
                       DisplayAnchor* anchor) {
  if (anchor == nullptr) {
    return false;
  }
  *anchor = {};
  anchor->monitor = api.ReadWindowMonitor(window, MONITOR_DEFAULTTONULL);
  if (anchor->monitor == nullptr) {
    return false;
  }

  MONITORINFOEXW monitor_info{};
  monitor_info.cbSize = sizeof(monitor_info);
  if (api.ReadMonitorInfo(anchor->monitor, &monitor_info) == FALSE) {
    return false;
  }

  size_t device_key_length = 0;
  while (device_key_length < CCHDEVICENAME &&
         monitor_info.szDevice[device_key_length] != L'\0') {
    ++device_key_length;
  }
  if (device_key_length == CCHDEVICENAME) {
    return false;
  }
  anchor->device_key.assign(monitor_info.szDevice, device_key_length);
  return IsValidDeviceKey(anchor->device_key);
}

bool ReadDisplayAnchor(IWindowsCaptureTargetObserverApi& api, HMONITOR monitor,
                       DisplayAnchor* anchor) {
  if (anchor == nullptr || monitor == nullptr) {
    return false;
  }
  *anchor = {};
  anchor->monitor = monitor;

  MONITORINFOEXW monitor_info{};
  monitor_info.cbSize = sizeof(monitor_info);
  if (api.ReadMonitorInfo(anchor->monitor, &monitor_info) == FALSE) {
    return false;
  }

  size_t device_key_length = 0;
  while (device_key_length < CCHDEVICENAME &&
         monitor_info.szDevice[device_key_length] != L'\0') {
    ++device_key_length;
  }
  if (device_key_length == CCHDEVICENAME) {
    return false;
  }
  anchor->device_key.assign(monitor_info.szDevice, device_key_length);
  return IsValidDeviceKey(anchor->device_key);
}

bool MatchesExpectedDisplay(const DisplayAnchor& anchor,
                            const CaptureTargetIdentity& expected) noexcept {
  return HandleValue(anchor.monitor) == expected.display_monitor_handle &&
         DeviceKeysEqual(anchor.device_key, expected.display_device_key);
}

}  // namespace

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureTargetWithApi(
    IWindowsCaptureTargetObserverApi& api,
    const CaptureTargetIdentity& expected) noexcept {
  if (!IsValidExpectedForegroundTarget(expected)) {
    return std::nullopt;
  }

  try {
    const HWND expected_window =
        reinterpret_cast<HWND>(static_cast<uintptr_t>(expected.window_handle));
    const HWND first_window = api.ReadForegroundWindow();
    if (first_window == nullptr || first_window != expected_window) {
      return std::nullopt;
    }

    WindowOwner first_owner;
    if (!ReadOwner(api, first_window, &first_owner) ||
        first_owner.process_id != expected.process_id) {
      return std::nullopt;
    }

    HANDLE opened_process = api.OpenTargetProcess(
        PROCESS_QUERY_LIMITED_INFORMATION, FALSE, first_owner.process_id);
    if (opened_process == nullptr || opened_process == INVALID_HANDLE_VALUE) {
      return std::nullopt;
    }
    ScopedProcessHandle process(api, opened_process);

    const DWORD first_process_id = api.ReadProcessId(process.get());
    uint64_t first_creation_time = 0;
    if (first_process_id != expected.process_id ||
        !ReadCreationTime(api, process.get(), &first_creation_time) ||
        first_creation_time != expected.process_creation_time_100ns) {
      return std::nullopt;
    }

    DisplayAnchor first_display;
    if (!ReadDisplayAnchor(api, first_window, &first_display) ||
        !MatchesExpectedDisplay(first_display, expected)) {
      return std::nullopt;
    }

    const HWND second_window = api.ReadForegroundWindow();
    WindowOwner second_owner;
    if (second_window != first_window ||
        !ReadOwner(api, second_window, &second_owner) ||
        second_owner != first_owner) {
      return std::nullopt;
    }

    const DWORD second_process_id = api.ReadProcessId(process.get());
    uint64_t second_creation_time = 0;
    if (second_process_id != first_process_id ||
        !ReadCreationTime(api, process.get(), &second_creation_time) ||
        second_creation_time != first_creation_time) {
      return std::nullopt;
    }

    DisplayAnchor second_display;
    if (!ReadDisplayAnchor(api, second_window, &second_display) ||
        second_display.monitor != first_display.monitor ||
        !DeviceKeysEqual(second_display.device_key, first_display.device_key) ||
        !MatchesExpectedDisplay(second_display, expected)) {
      return std::nullopt;
    }

    CaptureTargetIdentity observed{
        HandleValue(second_window),
        second_process_id,
        second_creation_time,
        expected.target_epoch,
        HandleValue(second_display.monitor),
        std::move(second_display.device_key),
    };
    if (!(observed == expected) || !process.Close()) {
      return std::nullopt;
    }
    return observed;
  } catch (...) {
    return std::nullopt;
  }
}

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureAuthorizationWithApi(
    IWindowsCaptureTargetObserverApi& api,
    const CaptureTargetIdentity& expected) noexcept {
  if (!IsValidExpectedTarget(expected)) {
    return std::nullopt;
  }
  if (expected.scope == CaptureAuthorizationScope::kForegroundTarget) {
    return ObserveWindowsCaptureTargetWithApi(api, expected);
  }

  try {
    const auto monitor = reinterpret_cast<HMONITOR>(
        static_cast<uintptr_t>(expected.display_monitor_handle));
    DisplayAnchor first_display;
    DisplayAnchor second_display;
    if (!ReadDisplayAnchor(api, monitor, &first_display) ||
        !MatchesExpectedDisplay(first_display, expected) ||
        !ReadDisplayAnchor(api, monitor, &second_display) ||
        second_display.monitor != first_display.monitor ||
        !DeviceKeysEqual(second_display.device_key, first_display.device_key) ||
        !MatchesExpectedDisplay(second_display, expected)) {
      return std::nullopt;
    }
    return expected;
  } catch (...) {
    return std::nullopt;
  }
}

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureTarget(
    const CaptureTargetIdentity& expected) noexcept {
  WindowsCaptureTargetObserverApi api;
  return ObserveWindowsCaptureTargetWithApi(api, expected);
}

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureAuthorization(
    const CaptureTargetIdentity& expected) noexcept {
  WindowsCaptureTargetObserverApi api;
  return ObserveWindowsCaptureAuthorizationWithApi(api, expected);
}

std::optional<CaptureTargetIdentity> ObserveWindowsForegroundTargetForDisplay(
    const CaptureTargetIdentity& expected_display) noexcept {
  if (!IsValidExpectedDisplayWideTarget(expected_display)) {
    return std::nullopt;
  }

  WindowsCaptureTargetObserverApi api;
  try {
    const HWND first_window = api.ReadForegroundWindow();
    WindowOwner first_owner;
    DisplayAnchor first_display;
    if (first_window == nullptr || !ReadOwner(api, first_window, &first_owner) ||
        !ReadDisplayAnchor(api, first_window, &first_display) ||
        !MatchesExpectedDisplay(first_display, expected_display)) {
      return std::nullopt;
    }

    HANDLE opened_process = api.OpenTargetProcess(
        PROCESS_QUERY_LIMITED_INFORMATION, FALSE, first_owner.process_id);
    if (opened_process == nullptr || opened_process == INVALID_HANDLE_VALUE) {
      return std::nullopt;
    }
    ScopedProcessHandle process(api, opened_process);
    uint64_t creation_time = 0;
    if (api.ReadProcessId(process.get()) != first_owner.process_id ||
        !ReadCreationTime(api, process.get(), &creation_time)) {
      return std::nullopt;
    }

    const HWND second_window = api.ReadForegroundWindow();
    WindowOwner second_owner;
    DisplayAnchor second_display;
    if (second_window != first_window ||
        !ReadOwner(api, second_window, &second_owner) ||
        second_owner != first_owner ||
        !ReadDisplayAnchor(api, second_window, &second_display) ||
        second_display.monitor != first_display.monitor ||
        !DeviceKeysEqual(second_display.device_key, first_display.device_key) ||
        !MatchesExpectedDisplay(second_display, expected_display) ||
        !process.Close()) {
      return std::nullopt;
    }

    return CaptureTargetIdentity{
        HandleValue(second_window),
        second_owner.process_id,
        creation_time,
        expected_display.target_epoch,
        HandleValue(second_display.monitor),
        std::move(second_display.device_key),
        CaptureAuthorizationScope::kForegroundTarget};
  } catch (...) {
    return std::nullopt;
  }
}

}  // namespace windayflow::capture

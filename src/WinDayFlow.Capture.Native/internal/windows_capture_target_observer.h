#ifndef WINDAYFLOW_WINDOWS_CAPTURE_TARGET_OBSERVER_H_
#define WINDAYFLOW_WINDOWS_CAPTURE_TARGET_OBSERVER_H_

#include <Windows.h>

#include <optional>

#include "capture_safety_core.h"

namespace windayflow::capture {

class IWindowsCaptureTargetObserverApi {
 public:
  virtual ~IWindowsCaptureTargetObserverApi() = default;

  virtual HWND ReadForegroundWindow() noexcept = 0;
  virtual DWORD ReadWindowOwner(HWND window, DWORD* process_id) noexcept = 0;
  virtual HANDLE OpenTargetProcess(DWORD desired_access, BOOL inherit_handle,
                                   DWORD process_id) noexcept = 0;
  virtual DWORD ReadProcessId(HANDLE process) noexcept = 0;
  virtual BOOL ReadProcessTimes(HANDLE process, FILETIME* creation_time,
                                FILETIME* exit_time, FILETIME* kernel_time,
                                FILETIME* user_time) noexcept = 0;
  virtual HMONITOR ReadWindowMonitor(HWND window, DWORD flags) noexcept = 0;
  virtual BOOL ReadMonitorInfo(HMONITOR monitor,
                               MONITORINFOEXW* monitor_info) noexcept = 0;
  virtual BOOL CloseTargetProcess(HANDLE process) noexcept = 0;
};

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureTargetWithApi(
    IWindowsCaptureTargetObserverApi& api,
    const CaptureTargetIdentity& expected) noexcept;

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureAuthorizationWithApi(
    IWindowsCaptureTargetObserverApi& api,
    const CaptureTargetIdentity& expected) noexcept;

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureTarget(
    const CaptureTargetIdentity& expected) noexcept;

std::optional<CaptureTargetIdentity> ObserveWindowsCaptureAuthorization(
    const CaptureTargetIdentity& expected) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_WINDOWS_CAPTURE_TARGET_OBSERVER_H_

#include "process_telemetry.h"

#include <Windows.h>
#include <Psapi.h>

#include <algorithm>
#include <limits>
#include <string_view>
#include <utility>
#include <vector>

namespace windayflow::capture {
namespace {

uint64_t FileTimeValue(const FILETIME& value) noexcept {
  ULARGE_INTEGER converted{};
  converted.LowPart = value.dwLowDateTime;
  converted.HighPart = value.dwHighDateTime;
  return converted.QuadPart;
}

bool WideToUtf8(std::wstring_view value, std::string* utf8) noexcept {
  if (utf8 == nullptr || value.empty() ||
      value.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }
  utf8->clear();
  const int required = WideCharToMultiByte(
      CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
      nullptr, 0, nullptr, nullptr);
  if (required <= 0) {
    return false;
  }
  try {
    utf8->resize(static_cast<size_t>(required));
  } catch (...) {
    return false;
  }
  if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                          static_cast<int>(value.size()), utf8->data(), required,
                          nullptr, nullptr) != required) {
    utf8->clear();
    return false;
  }
  return true;
}

bool ReadProcessName(HANDLE process, std::string* name_utf8) noexcept {
  try {
    std::vector<wchar_t> path(32'768U);
    DWORD length = static_cast<DWORD>(path.size());
    if (QueryFullProcessImageNameW(process, 0, path.data(), &length) == 0 ||
        length == 0 || length >= path.size()) {
      return false;
    }
    const std::wstring_view full_path(path.data(), length);
    const size_t separator = full_path.find_last_of(L"\\/");
    const std::wstring_view name = separator == std::wstring_view::npos
                                       ? full_path
                                       : full_path.substr(separator + 1U);
    return !name.empty() && WideToUtf8(name, name_utf8) &&
           name_utf8->size() <= 260U;
  } catch (...) {
    return false;
  }
}

}  // namespace

std::optional<ProcessTelemetrySample> ReadProcessTelemetrySample(
    uint32_t process_id,
    uint64_t expected_creation_time_100ns) noexcept {
  if (process_id == 0 || expected_creation_time_100ns == 0) {
    return std::nullopt;
  }

  HANDLE process = OpenProcess(
      PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, process_id);
  if (process == nullptr) {
    process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
  }
  if (process == nullptr) {
    return std::nullopt;
  }

  FILETIME creation{};
  FILETIME exit{};
  FILETIME kernel{};
  FILETIME user{};
  PROCESS_MEMORY_COUNTERS_EX memory{};
  memory.cb = sizeof(memory);
  std::string process_name;
  const bool valid =
      GetProcessTimes(process, &creation, &exit, &kernel, &user) != 0 &&
      FileTimeValue(creation) == expected_creation_time_100ns &&
      ReadProcessName(process, &process_name) &&
      GetProcessMemoryInfo(
          process, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),
          sizeof(memory)) != 0;
  static_cast<void>(CloseHandle(process));
  if (!valid) {
    return std::nullopt;
  }

  return ProcessTelemetrySample{
      std::move(process_name), process_id,
      FileTimeValue(kernel) + FileTimeValue(user),
      static_cast<uint64_t>(memory.WorkingSetSize),
      static_cast<uint64_t>(memory.PrivateUsage)};
}

std::optional<ProcessTelemetryInterval> BuildProcessTelemetryInterval(
    const ProcessTelemetrySample& start,
    const ProcessTelemetrySample& end,
    uint64_t elapsed_milliseconds) noexcept {
  if (start.process_id == 0 || start.process_id != end.process_id ||
      start.process_name_utf8.empty() ||
      start.process_name_utf8 != end.process_name_utf8 ||
      end.process_cpu_time_100ns < start.process_cpu_time_100ns ||
      elapsed_milliseconds == 0) {
    return std::nullopt;
  }

  const DWORD processor_count =
      std::max<DWORD>(1U, GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
  const long double available_cpu_100ns =
      static_cast<long double>(elapsed_milliseconds) * 10'000.0L *
      static_cast<long double>(processor_count);
  const long double used_cpu_100ns = static_cast<long double>(
      end.process_cpu_time_100ns - start.process_cpu_time_100ns);
  const long double raw_basis_points =
      used_cpu_100ns * 10'000.0L / available_cpu_100ns;
  const uint32_t basis_points = static_cast<uint32_t>(std::clamp<long double>(
      raw_basis_points, 0.0L, 10'000.0L));
  return ProcessTelemetryInterval{
      end.process_name_utf8, end.process_id, basis_points,
      end.working_set_bytes, end.private_memory_bytes};
}

}  // namespace windayflow::capture

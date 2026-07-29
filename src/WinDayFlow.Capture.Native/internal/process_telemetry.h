#ifndef WINDAYFLOW_PROCESS_TELEMETRY_H_
#define WINDAYFLOW_PROCESS_TELEMETRY_H_

#include <cstdint>
#include <optional>
#include <string>

namespace windayflow::capture {

struct ProcessTelemetrySample {
  std::string process_name_utf8;
  uint32_t process_id = 0;
  uint64_t process_cpu_time_100ns = 0;
  uint64_t working_set_bytes = 0;
  uint64_t private_memory_bytes = 0;
};

struct ProcessTelemetryInterval {
  std::string process_name_utf8;
  uint32_t process_id = 0;
  uint32_t cpu_usage_basis_points = 0;
  uint64_t working_set_bytes = 0;
  uint64_t private_memory_bytes = 0;
};

std::optional<ProcessTelemetrySample> ReadProcessTelemetrySample(
    uint32_t process_id,
    uint64_t expected_creation_time_100ns) noexcept;

std::optional<ProcessTelemetryInterval> BuildProcessTelemetryInterval(
    const ProcessTelemetrySample& start,
    const ProcessTelemetrySample& end,
    uint64_t elapsed_milliseconds) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_PROCESS_TELEMETRY_H_

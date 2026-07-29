#include <array>
#include <iostream>
#include <string>

#include "chunk_manifest.h"

namespace {

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

windayflow::capture::ChunkManifest ValidManifest() {
  return windayflow::capture::ChunkManifest{
      "chunk_20260717_120000_000001",
      1'784'269'200'000,
      1'784'269'260'000,
      6,
      1'600,
      900,
      8,
      7,
      11,
      false,
      {{0, 0, 4, std::string(64, 'A')},
       {1, 50'000, 4, std::string(64, 'B')}},
      windayflow::capture::ChunkApplicationManifest{
          "Code.exe", 4242, 1'250, 536'870'912, 402'653'184},
  };
}

bool TestBuildsStablePrivacySafeSchema() {
  const auto manifest = ValidManifest();
  std::string json;
  if (!Expect(windayflow::capture::BuildChunkManifestJson(manifest, &json),
              "valid chunk manifest was rejected")) {
    return false;
  }
  const std::array<std::string, 17> required{
      "\"schemaVersion\": 3",
      "\"captureScope\": \"authorized-foreground-display\"",
      "\"chunkId\": \"chunk_20260717_120000_000001\"",
      "\"startTimeUnixMs\": 1784269200000",
      "\"endTimeUnixMs\": 1784269260000",
      "\"persistenceGeneration\": 7",
      "\"targetEpoch\": 11",
      "\"processName\":\"Code.exe\"",
      "\"processId\":4242",
      "\"cpuUsageBasisPoints\":1250",
      "\"workingSetBytes\":536870912",
      "\"privateMemoryBytes\":402653184",
      "\"format\": \"jpeg\"",
      "\"quality\": 82",
      "\"capturedFrameCount\": 6",
      "\"retainedFrameCount\": 2",
      "\"path\":\"frames/frame-000001.jpg\"",
  };
  for (const std::string& fragment : required) {
    if (!Expect(json.find(fragment) != std::string::npos,
                "manifest omitted a required field")) {
      return false;
    }
  }
  const std::array<std::string, 8> forbidden{
      "windowTitle", "processPath", "windowHandle", "displayDeviceKey",
      "monitorHandle", "appName", "codec", "capture.mp4",
  };
  for (const std::string& field : forbidden) {
    if (!Expect(json.find(field) == std::string::npos,
                "manifest exposed forbidden metadata")) {
      return false;
    }
  }
  return Expect(json.ends_with("}\n"),
                "manifest was not terminated predictably");
}

bool TestRejectsInvalidFieldsAndClearsOutput() {
  const auto Reject = [](windayflow::capture::ChunkManifest manifest) {
    std::string output = "stale";
    return !windayflow::capture::BuildChunkManifestJson(manifest, &output) &&
           output.empty();
  };

  auto value = ValidManifest();
  value.chunk_id = "../escape";
  if (!Expect(Reject(value), "unsafe chunk ID was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.captured_frame_count = 1;
  if (!Expect(Reject(value), "retained count above captured count was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.frames[1].offset_milliseconds = 0;
  if (!Expect(Reject(value), "non-increasing offsets were accepted")) {
    return false;
  }
  value = ValidManifest();
  value.frame_byte_count = 7;
  if (!Expect(Reject(value), "incorrect aggregate byte count was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.frames[0].sha256 = "bad";
  if (!Expect(Reject(value), "invalid frame digest was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.persistence_generation = 0;
  if (!Expect(Reject(value), "unbound generation was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.application->cpu_usage_basis_points = 10'001;
  if (!Expect(Reject(value), "invalid process CPU utilization was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.display_wide_scope = true;
  return Expect(Reject(value), "display-wide evidence exposed one process") &&
         Expect(!windayflow::capture::BuildChunkManifestJson(ValidManifest(),
                                                             nullptr),
                "null destination was accepted");
}

}  // namespace

int main() {
  if (!TestBuildsStablePrivacySafeSchema() ||
      !TestRejectsInvalidFieldsAndClearsOutput()) {
    return 1;
  }
  std::cout << "chunk manifest tests passed\n";
  return 0;
}

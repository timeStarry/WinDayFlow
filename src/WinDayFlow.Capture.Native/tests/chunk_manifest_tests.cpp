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
      1'920,
      1'080,
      1,
      10,
      7,
      11,
  };
}

bool TestBuildsStablePrivacySafeSchema() {
  const auto manifest = ValidManifest();
  std::string json;
  if (!Expect(windayflow::capture::BuildChunkManifestJson(manifest, &json),
              "valid chunk manifest was rejected")) {
    return false;
  }
  const std::array<std::string, 11> required{
      "\"schemaVersion\": 1",
      "\"captureScope\": \"authorized-foreground-display\"",
      "\"chunkId\": \"chunk_20260717_120000_000001\"",
      "\"startTimeUnixMs\": 1784269200000",
      "\"endTimeUnixMs\": 1784269260000",
      "\"persistenceGeneration\": 7",
      "\"targetEpoch\": 11",
      "\"codec\": \"h264\"",
      "\"frameCount\": 6",
      "\"width\": 1920",
      "\"frameRateDenominator\": 10",
  };
  for (const std::string& fragment : required) {
    if (!Expect(json.find(fragment) != std::string::npos,
                "manifest omitted a required field")) {
      return false;
    }
  }
  const std::array<std::string, 8> forbidden{
      "windowTitle",  "processName",      "processPath",   "processId",
      "windowHandle", "displayDeviceKey", "monitorHandle", "appName",
  };
  for (const std::string& field : forbidden) {
    if (!Expect(json.find(field) == std::string::npos,
                "manifest exposed sensitive target metadata")) {
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
  if (!Expect(Reject(value), "unsafe manifest chunk ID was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.end_time_unix_ms = value.start_time_unix_ms;
  if (!Expect(Reject(value), "zero-duration manifest was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.frame_count = 0;
  if (!Expect(Reject(value), "empty manifest video was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.video_width = 1'919;
  if (!Expect(Reject(value), "odd manifest width was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.frame_rate_denominator = 0;
  if (!Expect(Reject(value), "zero manifest frame rate was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.persistence_generation = 0;
  if (!Expect(Reject(value), "unbound manifest generation was accepted")) {
    return false;
  }
  value = ValidManifest();
  value.target_epoch = 0;
  return Expect(Reject(value), "unbound manifest target was accepted") &&
         Expect(!windayflow::capture::BuildChunkManifestJson(ValidManifest(),
                                                             nullptr),
                "null manifest destination was accepted");
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

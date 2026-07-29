#include <Windows.h>
#include <bcrypt.h>

#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "atomic_chunk_store.h"
#include "chunk_manifest.h"

namespace {

using windayflow::capture::AtomicChunkPublication;
using windayflow::capture::AtomicChunkStore;
using windayflow::capture::AtomicChunkStoreResult;
using windayflow::capture::AtomicChunkWriter;
using windayflow::capture::ChunkFrameManifest;
using windayflow::capture::ChunkManifest;

constexpr std::array<uint8_t, 4> kMinimalJpeg{0xFFU, 0xD8U, 0xFFU, 0xD9U};

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

std::filesystem::path UniqueTestRoot() {
  std::array<wchar_t, MAX_PATH + 1> temporary{};
  const DWORD length =
      GetTempPathW(static_cast<DWORD>(temporary.size()), temporary.data());
  std::array<uint8_t, 16> nonce{};
  if (length == 0 || length >= temporary.size() ||
      BCryptGenRandom(nullptr, nonce.data(), static_cast<ULONG>(nonce.size()),
                      BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
    return {};
  }
  constexpr wchar_t kHex[] = L"0123456789abcdef";
  std::wstring suffix;
  for (const uint8_t value : nonce) {
    suffix.push_back(kHex[value >> 4U]);
    suffix.push_back(kHex[value & 0x0FU]);
  }
  return std::filesystem::path(temporary.data()) /
         (L"WinDayFlow-JpegChunkStore-" + suffix);
}

class ScopedTestRoot {
 public:
  ScopedTestRoot() : path_(UniqueTestRoot()) {}
  ~ScopedTestRoot() {
    std::error_code ignored;
    std::filesystem::remove_all(path_, ignored);
  }
  const std::filesystem::path& path() const { return path_; }

 private:
  std::filesystem::path path_;
};

std::vector<uint8_t> ReadBytes(const std::filesystem::path& path) {
  std::ifstream input(path, std::ios::binary);
  return std::vector<uint8_t>(std::istreambuf_iterator<char>(input), {});
}

ChunkManifest ValidManifest(std::string id) {
  return ChunkManifest{std::move(id),
                       1'784'269'200'000,
                       1'784'269'260'000,
                       1,
                       2,
                       2,
                       kMinimalJpeg.size(),
                       7,
                       11,
                       false,
                       {{0, 0, static_cast<uint32_t>(kMinimalJpeg.size()),
                         std::string(64, 'A')}}};
}

AtomicChunkStoreResult PrepareOneFrame(
    AtomicChunkStore& store, const ChunkManifest& manifest,
    AtomicChunkPublication* publication, AtomicChunkWriter* writer) {
  AtomicChunkStoreResult result = store.Begin(manifest.chunk_id, writer);
  if (result != AtomicChunkStoreResult::kOk) {
    return result;
  }
  result = writer->AppendFrame(manifest.frames[0], kMinimalJpeg);
  if (result != AtomicChunkStoreResult::kOk) {
    return result;
  }
  return writer->Prepare(manifest, publication);
}

bool TestPrepareCommitAndAcknowledge() {
  ScopedTestRoot root;
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkWriter writer;
  AtomicChunkPublication publication;
  const ChunkManifest manifest = ValidManifest("chunk-commit");
  const auto prepared =
      PrepareOneFrame(store, manifest, &publication, &writer);
  const auto final_directory = root.path() / L"chunks" / L"chunk-commit";
  if (!Expect(prepared == AtomicChunkStoreResult::kOk && publication &&
                  !publication.committed() &&
                  !std::filesystem::exists(final_directory),
              "staging chunk became visible before commit")) {
    return false;
  }
  const AtomicChunkStoreResult commit = publication.Commit();
  if (commit != AtomicChunkStoreResult::kOk) {
    std::cerr << "chunk commit failed: result=" << static_cast<int>(commit)
              << ", win32=" << GetLastError() << '\n';
    return false;
  }
  if (!Expect(publication.committed() &&
                  publication.artifact_identifier() ==
                      "chunks/chunk-commit/manifest.json",
              "committed chunk state was incorrect")) {
    return false;
  }
  std::string expected_manifest;
  static_cast<void>(
      windayflow::capture::BuildChunkManifestJson(manifest, &expected_manifest));
  const auto stored_frame =
      ReadBytes(final_directory / L"frames" / L"frame-000000.jpg");
  const auto stored_manifest = ReadBytes(final_directory / L"manifest.json");
  publication.Acknowledge();
  return Expect(stored_frame == std::vector<uint8_t>(kMinimalJpeg.begin(),
                                                      kMinimalJpeg.end()) &&
                    stored_manifest ==
                        std::vector<uint8_t>(expected_manifest.begin(),
                                             expected_manifest.end()) &&
                    !publication && std::filesystem::exists(final_directory),
                "committed JPEG chunk contents were not durable");
}

bool TestRollbackAndWriterReset() {
  ScopedTestRoot root;
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkWriter writer;
  AtomicChunkPublication publication;
  const ChunkManifest manifest = ValidManifest("chunk-rollback");
  if (!Expect(PrepareOneFrame(store, manifest, &publication, &writer) ==
                  AtomicChunkStoreResult::kOk &&
                  publication.Rollback() == AtomicChunkStoreResult::kOk &&
                  !std::filesystem::exists(root.path() / L"chunks" /
                                           L"chunk-rollback"),
              "prepared chunk rollback failed")) {
    return false;
  }

  AtomicChunkWriter abandoned;
  if (!Expect(store.Begin("chunk-reset", &abandoned) ==
                  AtomicChunkStoreResult::kOk &&
                  abandoned.AppendFrame(manifest.frames[0], kMinimalJpeg) ==
                      AtomicChunkStoreResult::kOk &&
                  abandoned.Reset() == AtomicChunkStoreResult::kOk,
              "active staging writer reset failed")) {
    return false;
  }
  const auto staging = root.path() / L".staging";
  return Expect(std::filesystem::is_empty(staging),
                "reset left private staging data behind");
}

bool TestRejectsInvalidFramesAndDuplicateDestinations() {
  ScopedTestRoot root;
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkWriter writer;
  if (!Expect(store.Begin("chunk-invalid", &writer) ==
                  AtomicChunkStoreResult::kOk,
              "test writer did not begin")) {
    return false;
  }
  const ChunkFrameManifest invalid{0, 0, 4, "bad"};
  if (!Expect(writer.AppendFrame(invalid, kMinimalJpeg) ==
                  AtomicChunkStoreResult::kInvalidArgument,
              "invalid frame digest was accepted")) {
    return false;
  }
  static_cast<void>(writer.Reset());

  const ChunkManifest manifest = ValidManifest("chunk-existing");
  AtomicChunkWriter committed_writer;
  AtomicChunkPublication publication;
  if (!Expect(PrepareOneFrame(store, manifest, &publication,
                             &committed_writer) ==
                  AtomicChunkStoreResult::kOk &&
                  publication.Commit() == AtomicChunkStoreResult::kOk,
              "duplicate setup failed")) {
    return false;
  }
  publication.Acknowledge();
  AtomicChunkWriter duplicate;
  return Expect(store.Begin(manifest.chunk_id, &duplicate) ==
                    AtomicChunkStoreResult::kAlreadyExists,
                "existing destination was accepted");
}

}  // namespace

int main() {
  if (!TestPrepareCommitAndAcknowledge() || !TestRollbackAndWriterReset() ||
      !TestRejectsInvalidFramesAndDuplicateDestinations()) {
    return 1;
  }
  std::cout << "atomic JPEG chunk store tests passed\n";
  return 0;
}

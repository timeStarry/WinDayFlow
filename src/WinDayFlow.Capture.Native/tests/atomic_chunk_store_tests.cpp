#include <Windows.h>
#include <bcrypt.h>
#include <winioctl.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <new>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "atomic_chunk_store.h"
#include "chunk_manifest.h"

namespace {

using windayflow::capture::AtomicChunkPublication;
using windayflow::capture::AtomicChunkStore;
using windayflow::capture::AtomicChunkStoreResult;
using windayflow::capture::ChunkManifest;

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
    suffix.push_back(kHex[(value >> 4U) & 0x0FU]);
    suffix.push_back(kHex[value & 0x0FU]);
  }
  return std::filesystem::path(temporary.data()) /
         (L"WinDayFlow-AtomicChunkStore-" + suffix);
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

class ScopedHandle final {
 public:
  explicit ScopedHandle(HANDLE handle = INVALID_HANDLE_VALUE)
      : handle_(handle) {}
  ~ScopedHandle() {
    if (handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE) {
      static_cast<void>(CloseHandle(handle_));
    }
  }

  ScopedHandle(const ScopedHandle&) = delete;
  ScopedHandle& operator=(const ScopedHandle&) = delete;

  HANDLE get() const { return handle_; }

 private:
  HANDLE handle_;
};

struct MountPointReparseData {
  DWORD tag;
  USHORT data_length;
  USHORT reserved;
  USHORT substitute_offset;
  USHORT substitute_length;
  USHORT print_offset;
  USHORT print_length;
  wchar_t path_buffer[1];
};

bool CreateJunction(const std::filesystem::path& junction,
                    const std::filesystem::path& target) {
  std::error_code error;
  std::filesystem::create_directories(target, error);
  if (error || !CreateDirectoryW(junction.c_str(), nullptr)) {
    return false;
  }

  const std::wstring print_name = std::filesystem::absolute(target).native();
  const std::wstring substitute_name = L"\\??\\" + print_name;
  const size_t substitute_bytes = substitute_name.size() * sizeof(wchar_t);
  const size_t print_bytes = print_name.size() * sizeof(wchar_t);
  const size_t path_bytes =
      substitute_bytes + sizeof(wchar_t) + print_bytes + sizeof(wchar_t);
  const size_t buffer_size =
      offsetof(MountPointReparseData, path_buffer) + path_bytes;
  if (buffer_size > MAXIMUM_REPARSE_DATA_BUFFER_SIZE ||
      path_bytes + sizeof(USHORT) * 4U > std::numeric_limits<USHORT>::max()) {
    return false;
  }

  std::vector<uint8_t> buffer(buffer_size, 0);
  auto* data = reinterpret_cast<MountPointReparseData*>(buffer.data());
  data->tag = IO_REPARSE_TAG_MOUNT_POINT;
  data->data_length = static_cast<USHORT>(sizeof(USHORT) * 4U + path_bytes);
  data->substitute_offset = 0;
  data->substitute_length = static_cast<USHORT>(substitute_bytes);
  data->print_offset = static_cast<USHORT>(substitute_bytes + sizeof(wchar_t));
  data->print_length = static_cast<USHORT>(print_bytes);
  std::memcpy(data->path_buffer, substitute_name.data(), substitute_bytes);
  std::memcpy(
      reinterpret_cast<uint8_t*>(data->path_buffer) + data->print_offset,
      print_name.data(), print_bytes);

  ScopedHandle handle(CreateFileW(
      junction.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (handle.get() == INVALID_HANDLE_VALUE) {
    static_cast<void>(RemoveDirectoryW(junction.c_str()));
    return false;
  }
  DWORD returned = 0;
  const BOOL created = DeviceIoControl(handle.get(), FSCTL_SET_REPARSE_POINT,
                                       data, static_cast<DWORD>(buffer.size()),
                                       nullptr, 0, &returned, nullptr);
  if (created == FALSE) {
    static_cast<void>(RemoveDirectoryW(junction.c_str()));
  }
  return created != FALSE;
}

std::vector<uint8_t> ReadBytes(const std::filesystem::path& path) {
  std::ifstream input(path, std::ios::binary);
  return std::vector<uint8_t>(std::istreambuf_iterator<char>(input), {});
}

ChunkManifest ValidManifest(std::string id) {
  return ChunkManifest{
      std::move(id),
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

bool TestPrepareCommitAndAcknowledge() {
  ScopedTestRoot root;
  const std::array<uint8_t, 8> video{0, 0, 0, 1, 'm', 'p', '4', 0};
  const ChunkManifest manifest = ValidManifest("chunk_20260717_000001");
  std::string expected_manifest;
  if (!windayflow::capture::BuildChunkManifestJson(manifest,
                                                   &expected_manifest)) {
    return Expect(false, "typed manifest setup failed");
  }
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication publication;
  const auto prepared =
      store.Prepare(manifest.chunk_id, video, manifest, &publication);
  const std::filesystem::path final_directory =
      root.path() / L"chunks" / L"chunk_20260717_000001";
  if (!Expect(prepared == AtomicChunkStoreResult::kOk && publication &&
                  !publication.committed() &&
                  !std::filesystem::exists(final_directory),
              "chunk was not prepared privately")) {
    return false;
  }
  if (!Expect(publication.Commit() == AtomicChunkStoreResult::kOk &&
                  publication.committed() &&
                  publication.artifact_identifier() ==
                      "chunks/chunk_20260717_000001/capture.mp4" &&
                  std::filesystem::is_directory(final_directory),
              "prepared chunk was not atomically committed")) {
    return false;
  }

  const std::vector<uint8_t> stored_video =
      ReadBytes(final_directory / L"capture.mp4");
  const std::vector<uint8_t> stored_manifest =
      ReadBytes(final_directory / L"manifest.json");
  publication.Acknowledge();
  return Expect(
      stored_video == std::vector<uint8_t>(video.begin(), video.end()) &&
          stored_manifest == std::vector<uint8_t>(expected_manifest.begin(),
                                                  expected_manifest.end()) &&
          !publication && std::filesystem::exists(final_directory),
      "acknowledged typed chunk contents were not durable");
}

bool TestRollbackBeforeAndAfterCommit() {
  ScopedTestRoot root;
  const std::array<uint8_t, 4> video{1, 2, 3, 4};
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication before;
  const ChunkManifest before_manifest = ValidManifest("before");
  if (store.Prepare("before", video, before_manifest, &before) !=
          AtomicChunkStoreResult::kOk ||
      before.Rollback() != AtomicChunkStoreResult::kOk) {
    return Expect(false, "pre-commit rollback failed");
  }
  if (!Expect(!before &&
                  !std::filesystem::exists(root.path() / L"chunks" / L"before"),
              "staging chunk survived rollback")) {
    return false;
  }

  AtomicChunkPublication after;
  const ChunkManifest after_manifest = ValidManifest("after");
  if (store.Prepare("after", video, after_manifest, &after) !=
          AtomicChunkStoreResult::kOk ||
      after.Commit() != AtomicChunkStoreResult::kOk ||
      after.Rollback() != AtomicChunkStoreResult::kOk) {
    return Expect(false, "post-commit compensation failed");
  }
  return Expect(
      !after && !std::filesystem::exists(root.path() / L"chunks" / L"after"),
      "unacknowledged committed chunk survived compensation");
}

bool TestRollbackFailureIsObservableAndRetryable() {
  ScopedTestRoot root;
  const std::array<uint8_t, 4> video{1, 2, 3, 4};
  const ChunkManifest manifest = ValidManifest("retry");
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication publication;
  if (store.Prepare("retry", video, manifest, &publication) !=
          AtomicChunkStoreResult::kOk ||
      publication.Commit() != AtomicChunkStoreResult::kOk) {
    return Expect(false, "rollback retry setup failed");
  }

  const std::filesystem::path final_directory =
      root.path() / L"chunks" / L"retry";
  const HANDLE blocker =
      CreateFileW((final_directory / L"capture.mp4").c_str(), GENERIC_READ,
                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                  FILE_ATTRIBUTE_NORMAL, nullptr);
  if (blocker == INVALID_HANDLE_VALUE) {
    return Expect(false, "rollback blocker could not be opened");
  }
  const AtomicChunkStoreResult blocked = publication.Rollback();
  static_cast<void>(CloseHandle(blocker));
  if (!Expect(blocked == AtomicChunkStoreResult::kIoFailure && publication &&
                  publication.committed() &&
                  std::filesystem::exists(final_directory / L"capture.mp4"),
              "failed rollback was hidden or discarded retry state")) {
    return false;
  }
  return Expect(publication.Rollback() == AtomicChunkStoreResult::kOk &&
                    !publication && !std::filesystem::exists(final_directory),
                "rollback did not succeed after the sharing conflict cleared");
}

bool TestCollisionNeverOverwrites() {
  ScopedTestRoot root;
  const std::array<uint8_t, 3> first_bytes{1, 2, 3};
  const std::array<uint8_t, 3> second_bytes{9, 8, 7};
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication first;
  const ChunkManifest manifest = ValidManifest("same");
  if (store.Prepare("same", first_bytes, manifest, &first) !=
          AtomicChunkStoreResult::kOk ||
      first.Commit() != AtomicChunkStoreResult::kOk) {
    return Expect(false, "collision setup failed");
  }
  first.Acknowledge();

  AtomicChunkPublication second;
  const auto result = store.Prepare("same", second_bytes, manifest, &second);
  return Expect(
      result == AtomicChunkStoreResult::kAlreadyExists && !second &&
          ReadBytes(root.path() / L"chunks" / L"same" / L"capture.mp4") ==
              std::vector<uint8_t>(first_bytes.begin(), first_bytes.end()),
      "artifact collision overwrote an existing chunk");
}

bool TestTypedManifestCannotBeBypassed() {
  ScopedTestRoot root;
  const std::array<uint8_t, 1> video{1};
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication publication;
  ChunkManifest mismatch = ValidManifest("manifest-id");
  if (!Expect(store.Prepare("path-id", video, mismatch, &publication) ==
                      AtomicChunkStoreResult::kInvalidArgument &&
                  !publication && !std::filesystem::exists(root.path()),
              "manifest ID was not bound to the artifact ID")) {
    return false;
  }
  ChunkManifest invalid = ValidManifest("invalid");
  invalid.frame_count = 0;
  return Expect(store.Prepare("invalid", video, invalid, &publication) ==
                        AtomicChunkStoreResult::kInvalidArgument &&
                    !publication && !std::filesystem::exists(root.path()),
                "invalid typed manifest reached the filesystem");
}

bool TestRejectsJunctionsWithoutFollowingThem() {
  ScopedTestRoot root;
  const std::filesystem::path container = root.path() / L"container";
  const std::filesystem::path outside = root.path() / L"outside";
  std::filesystem::create_directories(container);
  const std::filesystem::path junction = container / L"redirect";
  if (!CreateJunction(junction, outside)) {
    return Expect(false, "junction test setup failed");
  }

  const std::array<uint8_t, 1> video{1};
  const ChunkManifest manifest = ValidManifest("junction");
  AtomicChunkStore redirected((junction / L"created").wstring());
  AtomicChunkPublication publication;
  const auto result =
      redirected.Prepare("junction", video, manifest, &publication);
  if (!Expect(result == AtomicChunkStoreResult::kReparsePoint && !publication &&
                  !std::filesystem::exists(outside / L"created"),
              "root junction was followed before it was rejected")) {
    return false;
  }

  const std::filesystem::path output = root.path() / L"output";
  const std::filesystem::path staging_target = root.path() / L"staging-target";
  std::filesystem::create_directories(output);
  if (!CreateJunction(output / L".staging", staging_target)) {
    return Expect(false, "staging junction setup failed");
  }
  AtomicChunkStore staging_redirect(output.wstring());
  const auto staging_result =
      staging_redirect.Prepare("junction", video, manifest, &publication);
  return Expect(staging_result == AtomicChunkStoreResult::kReparsePoint &&
                    !publication && std::filesystem::is_empty(staging_target),
                "staging junction received chunk evidence");
}

bool TestPublicationDirectoryIdentityIsLocked() {
  ScopedTestRoot root;
  const std::array<uint8_t, 1> video{1};
  const ChunkManifest manifest = ValidManifest("locked");
  AtomicChunkStore store(root.path().wstring());
  AtomicChunkPublication publication;
  if (store.Prepare("locked", video, manifest, &publication) !=
      AtomicChunkStoreResult::kOk) {
    return Expect(false, "directory identity setup failed");
  }
  const std::filesystem::path staging_root = root.path() / L".staging";
  const auto entry = std::filesystem::directory_iterator(staging_root);
  const std::filesystem::path staging_directory = entry->path();
  const std::filesystem::path displaced = root.path() / L"displaced";
  if (!Expect(
          MoveFileExW(staging_directory.c_str(), displaced.c_str(), 0) == FALSE,
          "live staging directory identity could be replaced")) {
    return false;
  }
  if (!Expect(publication.Commit() == AtomicChunkStoreResult::kOk,
              "locked staging directory could not be committed")) {
    return false;
  }
  const std::filesystem::path final_directory =
      root.path() / L"chunks" / L"locked";
  if (!Expect(
          MoveFileExW(final_directory.c_str(), displaced.c_str(), 0) == FALSE,
          "live final directory identity could be replaced")) {
    return false;
  }
  return Expect(publication.Rollback() == AtomicChunkStoreResult::kOk &&
                    !std::filesystem::exists(final_directory),
                "identity-bound publication did not roll back");
}

void ThrowAfterStagingWrite() { throw std::bad_alloc(); }

bool TestExceptionAfterStagingWriteRollsBack() {
  ScopedTestRoot root;
  const std::array<uint8_t, 4> video{1, 2, 3, 4};
  const ChunkManifest manifest = ValidManifest("exception");
  AtomicChunkStore store(root.path().wstring(), &ThrowAfterStagingWrite);
  AtomicChunkPublication publication;
  const auto result = store.Prepare("exception", video, manifest, &publication);
  const std::filesystem::path staging_root = root.path() / L".staging";
  return Expect(result == AtomicChunkStoreResult::kIoFailure && !publication &&
                    std::filesystem::is_directory(staging_root) &&
                    std::filesystem::is_empty(staging_root),
                "exception after staging left partial evidence");
}

bool TestRejectsInvalidInputsAndRoots() {
  const std::array<uint8_t, 1> video{1};
  const ChunkManifest valid = ValidManifest("valid");
  AtomicChunkPublication publication;
  AtomicChunkStore relative(L"relative-output");
  if (!Expect(relative.Prepare("valid", video, valid, &publication) ==
                  AtomicChunkStoreResult::kInvalidRoot,
              "relative output root was accepted")) {
    return false;
  }

  ScopedTestRoot root;
  AtomicChunkStore store(root.path().wstring());
  const std::array<std::string_view, 7> invalid_ids{
      "",
      "../escape",
      "with/slash",
      "with\\slash",
      "with.dot",
      "white space",
      std::string_view("x\0y", 3),
  };
  for (const std::string_view id : invalid_ids) {
    ChunkManifest manifest = valid;
    manifest.chunk_id = std::string(id);
    if (!Expect(store.Prepare(id, video, manifest, &publication) ==
                    AtomicChunkStoreResult::kInvalidArgument,
                "invalid artifact identifier was accepted")) {
      return false;
    }
  }

  std::vector<uint8_t> oversized(
      windayflow::capture::kMaximumEncodedChunkBytes + 1U, 0);
  return Expect(store.Prepare("valid", oversized, valid, &publication) ==
                    AtomicChunkStoreResult::kInvalidArgument,
                "oversized in-memory chunk was accepted");
}

bool TestRejectsRootThatIsAFile() {
  ScopedTestRoot root;
  std::filesystem::create_directories(root.path());
  const std::filesystem::path file = root.path() / L"not-a-directory";
  {
    std::ofstream output(file, std::ios::binary);
    output << "file";
  }
  AtomicChunkStore store(file.wstring());
  AtomicChunkPublication publication;
  const std::array<uint8_t, 1> video{1};
  const ChunkManifest manifest = ValidManifest("valid");
  const auto result = store.Prepare("valid", video, manifest, &publication);
  return Expect(result != AtomicChunkStoreResult::kOk && !publication,
                "file output root was accepted as a directory");
}

}  // namespace

int main() {
  if (!TestPrepareCommitAndAcknowledge() ||
      !TestRollbackBeforeAndAfterCommit() ||
      !TestRollbackFailureIsObservableAndRetryable() ||
      !TestCollisionNeverOverwrites() || !TestTypedManifestCannotBeBypassed() ||
      !TestRejectsJunctionsWithoutFollowingThem() ||
      !TestPublicationDirectoryIdentityIsLocked() ||
      !TestExceptionAfterStagingWriteRollsBack() ||
      !TestRejectsInvalidInputsAndRoots() || !TestRejectsRootThatIsAFile()) {
    return 1;
  }
  std::cout << "atomic chunk store tests passed\n";
  return 0;
}

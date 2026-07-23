#include <Windows.h>
#include <bcrypt.h>
#include <winioctl.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "capture_chunk_fingerprint.h"

namespace {

using windayflow::capture::CaptureChunkFingerprintResult;

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
         (L"WinDayFlow-CaptureChunkFingerprint-" + suffix);
}

class ScopedTestRoot final {
 public:
  ScopedTestRoot() : path_(UniqueTestRoot()) {
    std::error_code error;
    std::filesystem::create_directories(path_, error);
  }

  ~ScopedTestRoot() {
    std::error_code ignored;
    std::filesystem::remove_all(path_, ignored);
  }

  const std::filesystem::path& path() const noexcept { return path_; }

 private:
  std::filesystem::path path_;
};

class ScopedHandle final {
 public:
  explicit ScopedHandle(HANDLE handle = INVALID_HANDLE_VALUE) noexcept
      : handle_(handle) {}
  ~ScopedHandle() {
    if (handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE) {
      static_cast<void>(CloseHandle(handle_));
    }
  }

  ScopedHandle(const ScopedHandle&) = delete;
  ScopedHandle& operator=(const ScopedHandle&) = delete;

  HANDLE get() const noexcept { return handle_; }

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

bool WriteBytes(const std::filesystem::path& path,
                std::span<const uint8_t> bytes) {
  std::ofstream output(path, std::ios::binary | std::ios::trunc);
  output.write(reinterpret_cast<const char*>(bytes.data()),
               static_cast<std::streamsize>(bytes.size()));
  return output.good();
}

bool CreateChunk(const std::filesystem::path& root,
                 std::string_view chunk_id,
                 std::span<const uint8_t> manifest,
                 std::span<const uint8_t> video) {
  const std::wstring wide_id(chunk_id.begin(), chunk_id.end());
  const std::filesystem::path directory = root / L"chunks" / wide_id;
  std::error_code error;
  std::filesystem::create_directories(directory, error);
  return !error && WriteBytes(directory / L"manifest.json", manifest) &&
         WriteBytes(directory / L"capture.mp4", video);
}

bool SetFileLength(const std::filesystem::path& path, uint64_t length) {
  ScopedHandle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
  LARGE_INTEGER offset{};
  offset.QuadPart = static_cast<LONGLONG>(length);
  return file.get() != INVALID_HANDLE_VALUE &&
         SetFilePointerEx(file.get(), offset, nullptr, FILE_BEGIN) != FALSE &&
         SetEndOfFile(file.get()) != FALSE;
}

bool TestCanonicalChunkIdentifierContract() {
  const bool valid =
      windayflow::capture::IsCanonicalCaptureChunkId("chunk-20260723_0001");
  return Expect(valid, "canonical chunk identifier was rejected") &&
         Expect(!windayflow::capture::IsCanonicalCaptureChunkId(""),
                "empty chunk identifier was accepted") &&
         Expect(!windayflow::capture::IsCanonicalCaptureChunkId("Upper"),
                "uppercase chunk identifier was accepted") &&
         Expect(!windayflow::capture::IsCanonicalCaptureChunkId("../escape"),
                "traversal chunk identifier was accepted") &&
         Expect(!windayflow::capture::IsCanonicalCaptureChunkId("con"),
                "reserved device chunk identifier was accepted") &&
         Expect(!windayflow::capture::IsCanonicalCaptureChunkId(
                    std::string(81, 'a')),
                "oversized chunk identifier was accepted");
}

bool TestDeterministicDomainSeparatedHash() {
  ScopedTestRoot root;
  constexpr std::array<uint8_t, 2> kManifest{'{', '}'};
  constexpr std::array<uint8_t, 5> kVideo{0, 1, 2, 3, 0xFF};
  if (!CreateChunk(root.path(), "stable", kManifest, kVideo)) {
    return Expect(false, "deterministic chunk setup failed");
  }

  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      first{};
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      second{};
  const auto first_result = windayflow::capture::ComputeCaptureChunkFingerprint(
      root.path().wstring(), "stable", kVideo.size(), &first);
  const auto second_result =
      windayflow::capture::ComputeCaptureChunkFingerprint(
          root.path().wstring(), "stable", kVideo.size(), &second);
  constexpr std::string_view kExpected =
      "BC2EE05C2C0756D66BBE6DCE87D535CEC11147F0F858D062728A7DD8A45F9481";
  const std::string_view actual(first.data(),
                                windayflow::capture::kCaptureChunkFingerprintHexLength);
  return Expect(first_result == CaptureChunkFingerprintResult::kOk &&
                    second_result == CaptureChunkFingerprintResult::kOk,
                "valid chunk could not be fingerprinted") &&
         Expect(first == second, "stable chunk fingerprint was not deterministic") &&
         Expect(actual == kExpected,
                "chunk fingerprint framing or digest changed") &&
         Expect(first.back() == '\0',
                "chunk fingerprint was not NUL terminated") &&
         Expect(std::all_of(actual.begin(), actual.end(), [](char character) {
                  return (character >= '0' && character <= '9') ||
                         (character >= 'A' && character <= 'F');
                }),
                "chunk fingerprint was not uppercase hexadecimal");
}

bool TestHashCoversManifestAndVideoBytes() {
  ScopedTestRoot root;
  constexpr std::array<uint8_t, 2> kManifestA{'{', '}'};
  constexpr std::array<uint8_t, 2> kManifestB{'[', ']'};
  constexpr std::array<uint8_t, 3> kVideoA{1, 2, 3};
  constexpr std::array<uint8_t, 3> kVideoB{1, 2, 4};
  if (!CreateChunk(root.path(), "first", kManifestA, kVideoA) ||
      !CreateChunk(root.path(), "manifest-change", kManifestB, kVideoA) ||
      !CreateChunk(root.path(), "video-change", kManifestA, kVideoB)) {
    return Expect(false, "coverage chunk setup failed");
  }

  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      first{};
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      manifest_change{};
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      video_change{};
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      size_mismatch{};
  return Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                    root.path().wstring(), "first", kVideoA.size(), &first) ==
                    CaptureChunkFingerprintResult::kOk,
                "baseline coverage chunk failed") &&
         Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                    root.path().wstring(), "manifest-change",
                    kVideoA.size(), &manifest_change) ==
                    CaptureChunkFingerprintResult::kOk,
                "manifest coverage chunk failed") &&
         Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                    root.path().wstring(), "video-change", kVideoB.size(),
                    &video_change) ==
                    CaptureChunkFingerprintResult::kOk,
                "video coverage chunk failed") &&
         Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                    root.path().wstring(), "first", kVideoA.size() + 1U,
                    &size_mismatch) ==
                    CaptureChunkFingerprintResult::kChangedDuringRead &&
                    size_mismatch.front() == '\0',
                "scanner video-length mismatch entered the hash") &&
         Expect(first != manifest_change,
                "manifest bytes were absent from the fingerprint") &&
         Expect(first != video_change,
                "video bytes were absent from the fingerprint");
}

bool TestMissingUnsafeAndOversizedEvidenceFailsClosed() {
  ScopedTestRoot root;
  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      fingerprint{};
  if (!Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                  root.path().wstring(), "missing", 1, &fingerprint) ==
                  CaptureChunkFingerprintResult::kNotFound,
              "missing chunk did not return not-found")) {
    return false;
  }
  if (!Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                  root.path().wstring(), "missing", 0, &fingerprint) ==
                  CaptureChunkFingerprintResult::kInvalidArgument,
              "zero expected video length was accepted") ||
      !Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                  root.path().wstring(), "missing",
                  windayflow::capture::kMaximumFingerprintVideoBytes + 1U,
                  &fingerprint) ==
                  CaptureChunkFingerprintResult::kInvalidArgument,
              "oversized expected video length was accepted")) {
    return false;
  }

  const std::filesystem::path directory_evidence =
      root.path() / L"chunks" / L"directory-evidence";
  std::error_code error;
  std::filesystem::create_directories(directory_evidence / L"manifest.json",
                                      error);
  constexpr std::array<uint8_t, 1> kByte{1};
  if (error || !WriteBytes(directory_evidence / L"capture.mp4", kByte) ||
      !Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                  root.path().wstring(), "directory-evidence", kByte.size(),
                  &fingerprint) ==
                  CaptureChunkFingerprintResult::kUnsafeEvidence,
              "directory evidence was not rejected")) {
    return false;
  }

  const std::filesystem::path oversized =
      root.path() / L"chunks" / L"oversized";
  std::filesystem::create_directories(oversized, error);
  if (error || !WriteBytes(oversized / L"manifest.json", kByte) ||
      !SetFileLength(oversized / L"capture.mp4",
                     windayflow::capture::kMaximumFingerprintVideoBytes + 1U)) {
    return Expect(false, "oversized evidence setup failed");
  }
  if (!Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                  root.path().wstring(), "oversized", kByte.size(),
                  &fingerprint) == CaptureChunkFingerprintResult::kTooLarge,
              "oversized video evidence was not rejected")) {
    return false;
  }

  const std::filesystem::path oversized_manifest =
      root.path() / L"chunks" / L"oversized-manifest";
  std::filesystem::create_directories(oversized_manifest, error);
  if (error ||
      !SetFileLength(
          oversized_manifest / L"manifest.json",
          windayflow::capture::kMaximumFingerprintManifestBytes + 1U) ||
      !WriteBytes(oversized_manifest / L"capture.mp4", kByte)) {
    return Expect(false, "oversized manifest setup failed");
  }
  return Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                    root.path().wstring(), "oversized-manifest", kByte.size(),
                    &fingerprint) == CaptureChunkFingerprintResult::kTooLarge,
                "oversized manifest evidence was not rejected");
}

bool TestReparseDirectoryAndNonLocalInputsFailClosed() {
  ScopedTestRoot root_link_container;
  ScopedTestRoot chunks_link_root;
  ScopedTestRoot chunk_link_root;
  ScopedTestRoot outside;
  const std::filesystem::path linked_root =
      root_link_container.path() / L"linked-root";
  std::error_code error;
  std::filesystem::create_directories(chunk_link_root.path() / L"chunks",
                                      error);
  if (error || !CreateJunction(linked_root, outside.path()) ||
      !CreateJunction(chunks_link_root.path() / L"chunks", outside.path()) ||
      !CreateJunction(chunk_link_root.path() / L"chunks" / L"linked",
                      outside.path())) {
    return Expect(false, "directory-chain junction setup failed");
  }

  std::array<char, windayflow::capture::kCaptureChunkFingerprintBufferSize>
      fingerprint{};
  const bool root_junction_rejected =
      Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                 linked_root.wstring(), "linked", 1, &fingerprint) ==
                 CaptureChunkFingerprintResult::kUnsafeEvidence,
             "reparse-point data root was followed");
  const bool chunks_junction_rejected =
      Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                 chunks_link_root.path().wstring(), "linked", 1,
                 &fingerprint) ==
                 CaptureChunkFingerprintResult::kUnsafeEvidence,
             "reparse-point chunks root was followed");
  const bool chunk_junction_rejected =
      Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                 chunk_link_root.path().wstring(), "linked", 1,
                 &fingerprint) ==
                 CaptureChunkFingerprintResult::kUnsafeEvidence,
             "reparse-point chunk directory was followed");
  const bool relative_rejected =
      Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                 L"relative\\root", "linked", 1, &fingerprint) ==
                 CaptureChunkFingerprintResult::kInvalidArgument,
             "relative data root was accepted");
  const bool unc_rejected =
      Expect(windayflow::capture::ComputeCaptureChunkFingerprint(
                 L"\\\\server\\share\\root", "linked", 1, &fingerprint) ==
                 CaptureChunkFingerprintResult::kInvalidArgument,
             "UNC data root was accepted");
  return root_junction_rejected && chunks_junction_rejected &&
         chunk_junction_rejected && relative_rejected && unc_rejected;
}

}  // namespace

int main() {
  const bool passed = TestCanonicalChunkIdentifierContract() &&
                      TestDeterministicDomainSeparatedHash() &&
                      TestHashCoversManifestAndVideoBytes() &&
                      TestMissingUnsafeAndOversizedEvidenceFailsClosed() &&
                      TestReparseDirectoryAndNonLocalInputsFailClosed();
  if (!passed) {
    return 1;
  }
  std::cout << "capture chunk fingerprint tests passed\n";
  return 0;
}

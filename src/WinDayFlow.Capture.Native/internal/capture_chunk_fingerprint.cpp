#include "capture_chunk_fingerprint.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <limits>
#include <span>
#include <string>
#include <utility>
#include <vector>

namespace windayflow::capture {
namespace {

constexpr size_t kMaximumChunkIdBytes = 80U;
constexpr size_t kMaximumWindowsPathCharacters = 32'767U;
constexpr size_t kReadBufferBytes = 64U * 1024U;
constexpr DWORD kDirectoryAccess = FILE_READ_ATTRIBUTES | SYNCHRONIZE;
constexpr DWORD kDirectoryShareMode = FILE_SHARE_READ | FILE_SHARE_WRITE;
constexpr DWORD kEvidenceShareMode = FILE_SHARE_READ;
constexpr DWORD kNoFollowDirectoryFlags =
    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT;
constexpr DWORD kNoFollowFileFlags =
    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS |
    FILE_FLAG_OPEN_REPARSE_POINT |
    FILE_FLAG_SEQUENTIAL_SCAN;
constexpr char kFingerprintDomain[] =
    "WinDayFlow.CaptureChunkFingerprint.v1";

class ScopedHandle final {
 public:
  ScopedHandle() = default;
  explicit ScopedHandle(HANDLE handle) noexcept : handle_(handle) {}
  ~ScopedHandle() { Reset(); }

  ScopedHandle(const ScopedHandle&) = delete;
  ScopedHandle& operator=(const ScopedHandle&) = delete;

  ScopedHandle(ScopedHandle&& other) noexcept
      : handle_(std::exchange(other.handle_, INVALID_HANDLE_VALUE)) {}

  ScopedHandle& operator=(ScopedHandle&& other) noexcept {
    if (this != &other) {
      Reset(std::exchange(other.handle_, INVALID_HANDLE_VALUE));
    }
    return *this;
  }

  HANDLE get() const noexcept { return handle_; }
  bool valid() const noexcept {
    return handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE;
  }

  void Reset(HANDLE handle = INVALID_HANDLE_VALUE) noexcept {
    if (valid()) {
      static_cast<void>(CloseHandle(handle_));
    }
    handle_ = handle;
  }

 private:
  HANDLE handle_ = INVALID_HANDLE_VALUE;
};

class ScopedAlgorithm final {
 public:
  ~ScopedAlgorithm() {
    if (handle_ != nullptr) {
      static_cast<void>(BCryptCloseAlgorithmProvider(handle_, 0));
    }
  }

  ScopedAlgorithm(const ScopedAlgorithm&) = delete;
  ScopedAlgorithm& operator=(const ScopedAlgorithm&) = delete;
  ScopedAlgorithm() = default;

  BCRYPT_ALG_HANDLE* address() noexcept { return &handle_; }
  BCRYPT_ALG_HANDLE get() const noexcept { return handle_; }

 private:
  BCRYPT_ALG_HANDLE handle_ = nullptr;
};

class ScopedHash final {
 public:
  ~ScopedHash() {
    if (handle_ != nullptr) {
      static_cast<void>(BCryptDestroyHash(handle_));
    }
  }

  ScopedHash(const ScopedHash&) = delete;
  ScopedHash& operator=(const ScopedHash&) = delete;
  ScopedHash() = default;

  BCRYPT_HASH_HANDLE* address() noexcept { return &handle_; }
  BCRYPT_HASH_HANDLE get() const noexcept { return handle_; }

 private:
  BCRYPT_HASH_HANDLE handle_ = nullptr;
};

struct FileSnapshot {
  DWORD volume_serial_number = 0;
  uint64_t file_index = 0;
  uint64_t length = 0;
  uint64_t last_write_time = 0;
  DWORD attributes = 0;
};

enum class PathOpenResult {
  kOk,
  kNotFound,
  kUnsafe,
  kTooLarge,
  kIoFailure,
};

bool IsMissingPathError(DWORD error) noexcept {
  return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ||
         error == ERROR_INVALID_NAME;
}

bool HasUnsafeAttributes(DWORD attributes) noexcept {
  return (attributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DEVICE)) !=
         0;
}

bool TryReadFileSnapshot(HANDLE handle, FileSnapshot* snapshot) noexcept {
  if (handle == nullptr || handle == INVALID_HANDLE_VALUE ||
      snapshot == nullptr) {
    return false;
  }
  BY_HANDLE_FILE_INFORMATION information{};
  if (GetFileInformationByHandle(handle, &information) == FALSE) {
    return false;
  }
  snapshot->volume_serial_number = information.dwVolumeSerialNumber;
  snapshot->file_index =
      (static_cast<uint64_t>(information.nFileIndexHigh) << 32U) |
      information.nFileIndexLow;
  snapshot->length =
      (static_cast<uint64_t>(information.nFileSizeHigh) << 32U) |
      information.nFileSizeLow;
  snapshot->last_write_time =
      (static_cast<uint64_t>(information.ftLastWriteTime.dwHighDateTime)
       << 32U) |
      information.ftLastWriteTime.dwLowDateTime;
  snapshot->attributes = information.dwFileAttributes;
  return true;
}

bool IsSameSnapshot(const FileSnapshot& first,
                    const FileSnapshot& second) noexcept {
  return first.volume_serial_number == second.volume_serial_number &&
         first.file_index == second.file_index &&
         first.length == second.length &&
         first.last_write_time == second.last_write_time &&
         first.attributes == second.attributes;
}

bool TryNormalizeLocalAbsoluteRoot(
    const std::wstring& value, std::filesystem::path* normalized) noexcept {
  if (normalized == nullptr) {
    return false;
  }
  normalized->clear();
  try {
    if (value.empty() || value.size() >= kMaximumWindowsPathCharacters ||
        value.find(L'\0') != std::wstring::npos || value.size() < 3U ||
        value[1] != L':' || (value[2] != L'\\' && value[2] != L'/') ||
        value[0] == L'\\' || value[0] == L'/') {
      return false;
    }

    std::array<wchar_t, 4> drive_root{
        value[0], L':', L'\\', L'\0'};
    const UINT drive_type = GetDriveTypeW(drive_root.data());
    if (drive_type == DRIVE_UNKNOWN || drive_type == DRIVE_NO_ROOT_DIR ||
        drive_type == DRIVE_REMOTE) {
      return false;
    }

    const DWORD required = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
    if (required == 0 ||
        required >= static_cast<DWORD>(kMaximumWindowsPathCharacters)) {
      return false;
    }
    std::vector<wchar_t> buffer(static_cast<size_t>(required) + 1U, L'\0');
    const DWORD written = GetFullPathNameW(
        value.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (written == 0 || written >= buffer.size()) {
      return false;
    }

    std::filesystem::path path(std::wstring(buffer.data(), written));
    const std::wstring canonical = path.native();
    if (!path.is_absolute() || canonical.size() < 3U ||
        canonical[1] != L':' ||
        (canonical[2] != L'\\' && canonical[2] != L'/') ||
        canonical.starts_with(L"\\\\")) {
      return false;
    }
    *normalized = std::move(path);
    return true;
  } catch (...) {
    normalized->clear();
    return false;
  }
}

PathOpenResult OpenDirectoryNoFollow(const std::filesystem::path& path,
                                     ScopedHandle* directory) noexcept {
  if (directory == nullptr) {
    return PathOpenResult::kIoFailure;
  }
  directory->Reset();
  const HANDLE raw_handle =
      CreateFileW(path.c_str(), kDirectoryAccess, kDirectoryShareMode, nullptr,
                  OPEN_EXISTING, kNoFollowDirectoryFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? PathOpenResult::kNotFound
                                              : PathOpenResult::kIoFailure;
  }

  ScopedHandle opened(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  if (GetFileInformationByHandleEx(opened.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE) {
    return PathOpenResult::kIoFailure;
  }
  if (HasUnsafeAttributes(attributes.FileAttributes) ||
      (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    return PathOpenResult::kUnsafe;
  }
  *directory = std::move(opened);
  return PathOpenResult::kOk;
}

PathOpenResult LockDirectoryChain(const std::filesystem::path& root,
                                  std::vector<ScopedHandle>* locks) {
  if (locks == nullptr) {
    return PathOpenResult::kIoFailure;
  }
  locks->clear();
  size_t component_count = 1U;
  for (const auto& ignored : root.relative_path()) {
    static_cast<void>(ignored);
    ++component_count;
  }
  locks->reserve(component_count + 2U);

  std::filesystem::path current = root.root_path();
  ScopedHandle drive;
  PathOpenResult result = OpenDirectoryNoFollow(current, &drive);
  if (result != PathOpenResult::kOk) {
    return result;
  }
  locks->push_back(std::move(drive));

  for (const auto& component : root.relative_path()) {
    if (component.empty() || component == L"." || component == L"..") {
      return PathOpenResult::kUnsafe;
    }
    current /= component;
    ScopedHandle child;
    result = OpenDirectoryNoFollow(current, &child);
    if (result != PathOpenResult::kOk) {
      return result;
    }
    locks->push_back(std::move(child));
  }
  return PathOpenResult::kOk;
}

PathOpenResult LockChildDirectory(const std::filesystem::path& path,
                                  std::vector<ScopedHandle>* locks) {
  ScopedHandle child;
  const PathOpenResult result = OpenDirectoryNoFollow(path, &child);
  if (result == PathOpenResult::kOk) {
    locks->push_back(std::move(child));
  }
  return result;
}

PathOpenResult OpenEvidenceFile(const std::filesystem::path& path,
                                uint64_t maximum_size, ScopedHandle* file,
                                FileSnapshot* snapshot) noexcept {
  if (file == nullptr || snapshot == nullptr) {
    return PathOpenResult::kIoFailure;
  }
  file->Reset();
  *snapshot = {};
  const HANDLE raw_handle = CreateFileW(
      path.c_str(), GENERIC_READ | FILE_READ_ATTRIBUTES, kEvidenceShareMode,
      nullptr, OPEN_EXISTING, kNoFollowFileFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? PathOpenResult::kNotFound
                                              : PathOpenResult::kIoFailure;
  }

  ScopedHandle opened(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  FileSnapshot value{};
  if (GetFileInformationByHandleEx(opened.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE ||
      !TryReadFileSnapshot(opened.get(), &value)) {
    return PathOpenResult::kIoFailure;
  }
  if (HasUnsafeAttributes(attributes.FileAttributes) ||
      (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
      HasUnsafeAttributes(value.attributes) ||
      (value.attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 || value.length == 0) {
    return PathOpenResult::kUnsafe;
  }
  if (value.length > maximum_size) {
    return PathOpenResult::kTooLarge;
  }
  *snapshot = value;
  *file = std::move(opened);
  return PathOpenResult::kOk;
}

CaptureChunkFingerprintResult MapPathOpenResult(
    PathOpenResult result) noexcept {
  switch (result) {
    case PathOpenResult::kOk:
      return CaptureChunkFingerprintResult::kOk;
    case PathOpenResult::kNotFound:
      return CaptureChunkFingerprintResult::kNotFound;
    case PathOpenResult::kUnsafe:
      return CaptureChunkFingerprintResult::kUnsafeEvidence;
    case PathOpenResult::kTooLarge:
      return CaptureChunkFingerprintResult::kTooLarge;
    case PathOpenResult::kIoFailure:
    default:
      return CaptureChunkFingerprintResult::kIoFailure;
  }
}

bool HashBytes(BCRYPT_HASH_HANDLE hash,
               std::span<const uint8_t> bytes) noexcept {
  if (hash == nullptr ||
      bytes.size() > static_cast<size_t>(std::numeric_limits<ULONG>::max())) {
    return false;
  }
  return BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()),
                        static_cast<ULONG>(bytes.size()), 0) == 0;
}

bool HashLength(BCRYPT_HASH_HANDLE hash, uint64_t length) noexcept {
  std::array<uint8_t, sizeof(length)> encoded{};
  for (size_t index = 0; index < encoded.size(); ++index) {
    encoded[index] = static_cast<uint8_t>(length >> (index * 8U));
  }
  return HashBytes(hash, encoded);
}

CaptureChunkFingerprintResult HashFile(BCRYPT_HASH_HANDLE hash, HANDLE file,
                                       uint64_t expected_length) noexcept {
  LARGE_INTEGER beginning{};
  if (SetFilePointerEx(file, beginning, nullptr, FILE_BEGIN) == FALSE) {
    return CaptureChunkFingerprintResult::kIoFailure;
  }

  std::array<uint8_t, kReadBufferBytes> buffer{};
  uint64_t remaining = expected_length;
  while (remaining > 0) {
    const DWORD requested = static_cast<DWORD>(std::min<uint64_t>(
        remaining, static_cast<uint64_t>(buffer.size())));
    DWORD read = 0;
    if (ReadFile(file, buffer.data(), requested, &read, nullptr) == FALSE) {
      return CaptureChunkFingerprintResult::kIoFailure;
    }
    if (read == 0 || read > requested) {
      return CaptureChunkFingerprintResult::kChangedDuringRead;
    }
    if (!HashBytes(hash,
                   std::span<const uint8_t>(buffer.data(),
                                            static_cast<size_t>(read)))) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    remaining -= read;
  }

  uint8_t trailing = 0;
  DWORD trailing_read = 0;
  if (ReadFile(file, &trailing, 1, &trailing_read, nullptr) == FALSE) {
    return CaptureChunkFingerprintResult::kIoFailure;
  }
  return trailing_read == 0
             ? CaptureChunkFingerprintResult::kOk
             : CaptureChunkFingerprintResult::kChangedDuringRead;
}

CaptureChunkFingerprintResult VerifyStableAndReopened(
    const std::filesystem::path& path, uint64_t maximum_size,
    const ScopedHandle& original, const FileSnapshot& before) noexcept {
  FileSnapshot after{};
  if (!TryReadFileSnapshot(original.get(), &after)) {
    return CaptureChunkFingerprintResult::kIoFailure;
  }
  if (!IsSameSnapshot(before, after)) {
    return CaptureChunkFingerprintResult::kChangedDuringRead;
  }

  ScopedHandle reopened;
  FileSnapshot reopened_snapshot{};
  const PathOpenResult reopened_result = OpenEvidenceFile(
      path, maximum_size, &reopened, &reopened_snapshot);
  if (reopened_result == PathOpenResult::kNotFound) {
    return CaptureChunkFingerprintResult::kChangedDuringRead;
  }
  if (reopened_result != PathOpenResult::kOk) {
    return MapPathOpenResult(reopened_result);
  }
  return IsSameSnapshot(before, reopened_snapshot)
             ? CaptureChunkFingerprintResult::kOk
             : CaptureChunkFingerprintResult::kChangedDuringRead;
}

bool IsReservedWindowsName(std::string_view value) noexcept {
  constexpr std::array<std::string_view, 22> kReservedNames{
      "con",  "prn",  "aux",  "nul",  "com1", "com2", "com3", "com4",
      "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3",
      "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"};
  return std::find(kReservedNames.begin(), kReservedNames.end(), value) !=
         kReservedNames.end();
}

}  // namespace

bool IsCanonicalCaptureChunkId(std::string_view value) noexcept {
  if (value.empty() || value.size() > kMaximumChunkIdBytes ||
      IsReservedWindowsName(value)) {
    return false;
  }
  for (const unsigned char character : value) {
    if (!((character >= 'a' && character <= 'z') ||
          (character >= '0' && character <= '9') || character == '-' ||
          character == '_')) {
      return false;
    }
  }
  return true;
}

CaptureChunkFingerprintResult ComputeCaptureChunkFingerprint(
    const std::wstring& data_root, std::string_view canonical_chunk_id,
    size_t expected_video_byte_count,
    std::array<char, kCaptureChunkFingerprintBufferSize>* fingerprint) noexcept {
  if (fingerprint == nullptr) {
    return CaptureChunkFingerprintResult::kInvalidArgument;
  }
  fingerprint->fill('\0');
  if (!IsCanonicalCaptureChunkId(canonical_chunk_id) ||
      expected_video_byte_count == 0 ||
      expected_video_byte_count > kMaximumFingerprintVideoBytes) {
    return CaptureChunkFingerprintResult::kInvalidArgument;
  }

  try {
    std::filesystem::path normalized_root;
    if (!TryNormalizeLocalAbsoluteRoot(data_root, &normalized_root)) {
      return CaptureChunkFingerprintResult::kInvalidArgument;
    }

    std::vector<ScopedHandle> directory_locks;
    PathOpenResult open_result =
        LockDirectoryChain(normalized_root, &directory_locks);
    if (open_result != PathOpenResult::kOk) {
      return MapPathOpenResult(open_result);
    }

    const std::filesystem::path chunks_root = normalized_root / L"chunks";
    open_result = LockChildDirectory(chunks_root, &directory_locks);
    if (open_result != PathOpenResult::kOk) {
      return MapPathOpenResult(open_result);
    }
    const std::wstring wide_chunk_id(canonical_chunk_id.begin(),
                                     canonical_chunk_id.end());
    const std::filesystem::path chunk_root = chunks_root / wide_chunk_id;
    open_result = LockChildDirectory(chunk_root, &directory_locks);
    if (open_result != PathOpenResult::kOk) {
      return MapPathOpenResult(open_result);
    }

    const std::filesystem::path manifest_path = chunk_root / L"manifest.json";
    const std::filesystem::path video_path = chunk_root / L"capture.mp4";
    ScopedHandle manifest;
    ScopedHandle video;
    FileSnapshot manifest_before{};
    FileSnapshot video_before{};
    open_result = OpenEvidenceFile(manifest_path,
                                   kMaximumFingerprintManifestBytes,
                                   &manifest,
                                   &manifest_before);
    if (open_result != PathOpenResult::kOk) {
      return MapPathOpenResult(open_result);
    }
    open_result = OpenEvidenceFile(video_path, kMaximumFingerprintVideoBytes,
                                   &video, &video_before);
    if (open_result != PathOpenResult::kOk) {
      return MapPathOpenResult(open_result);
    }
    if (video_before.length != expected_video_byte_count) {
      return CaptureChunkFingerprintResult::kChangedDuringRead;
    }

    ScopedAlgorithm algorithm;
    if (BCryptOpenAlgorithmProvider(algorithm.address(), BCRYPT_SHA256_ALGORITHM,
                                    nullptr, 0) != 0) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    DWORD object_size = 0;
    DWORD hash_size = 0;
    DWORD copied = 0;
    if (BCryptGetProperty(algorithm.get(), BCRYPT_OBJECT_LENGTH,
                          reinterpret_cast<PUCHAR>(&object_size),
                          sizeof(object_size), &copied, 0) != 0 ||
        copied != sizeof(object_size) || object_size == 0 ||
        BCryptGetProperty(algorithm.get(), BCRYPT_HASH_LENGTH,
                          reinterpret_cast<PUCHAR>(&hash_size),
                          sizeof(hash_size), &copied, 0) != 0 ||
        copied != sizeof(hash_size) || hash_size != 32U) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    std::vector<uint8_t> hash_object(object_size, 0);
    ScopedHash hash;
    if (BCryptCreateHash(algorithm.get(), hash.address(), hash_object.data(),
                         static_cast<ULONG>(hash_object.size()), nullptr, 0,
                         0) != 0) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }

    const auto* domain = reinterpret_cast<const uint8_t*>(kFingerprintDomain);
    if (!HashBytes(hash.get(), std::span<const uint8_t>(
                                   domain, sizeof(kFingerprintDomain))) ||
        !HashLength(hash.get(), manifest_before.length)) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    CaptureChunkFingerprintResult result =
        HashFile(hash.get(), manifest.get(), manifest_before.length);
    if (result != CaptureChunkFingerprintResult::kOk) {
      return result;
    }
    if (!HashLength(hash.get(), video_before.length)) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    result = HashFile(hash.get(), video.get(), video_before.length);
    if (result != CaptureChunkFingerprintResult::kOk) {
      return result;
    }

    result = VerifyStableAndReopened(manifest_path,
                                     kMaximumFingerprintManifestBytes,
                                     manifest,
                                     manifest_before);
    if (result != CaptureChunkFingerprintResult::kOk) {
      return result;
    }
    result = VerifyStableAndReopened(video_path,
                                     kMaximumFingerprintVideoBytes,
                                     video,
                                     video_before);
    if (result != CaptureChunkFingerprintResult::kOk) {
      return result;
    }

    std::array<uint8_t, 32> digest{};
    if (BCryptFinishHash(hash.get(), digest.data(),
                         static_cast<ULONG>(digest.size()), 0) != 0) {
      return CaptureChunkFingerprintResult::kCryptoFailure;
    }
    constexpr char kUpperHex[] = "0123456789ABCDEF";
    for (size_t index = 0; index < digest.size(); ++index) {
      (*fingerprint)[index * 2U] = kUpperHex[(digest[index] >> 4U) & 0x0FU];
      (*fingerprint)[index * 2U + 1U] = kUpperHex[digest[index] & 0x0FU];
    }
    (*fingerprint)[kCaptureChunkFingerprintHexLength] = '\0';
    return CaptureChunkFingerprintResult::kOk;
  } catch (...) {
    fingerprint->fill('\0');
    return CaptureChunkFingerprintResult::kIoFailure;
  }
}

}  // namespace windayflow::capture

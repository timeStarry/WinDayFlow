// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Heavily modified for transactional WinDayFlow JPEG-frame publication; see
// THIRD_PARTY_NOTICES.md.

#include "atomic_chunk_store.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <span>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

#include "chunk_manifest.h"

namespace windayflow::capture {
namespace {

constexpr size_t kMaximumArtifactIdBytes = 80U;
constexpr size_t kMaximumWindowsPathCharacters = 32'767U;
constexpr size_t kStagingNonceBytes = 16U;
constexpr uint32_t kMaximumStagingNameAttempts = 16U;
constexpr DWORD kDirectoryShareMode = FILE_SHARE_READ | FILE_SHARE_WRITE;
constexpr DWORD kInspectionShareMode =
    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
constexpr DWORD kDirectoryLockAccess =
    FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE;
constexpr DWORD kNoFollowFlags =
    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT;

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

enum class DirectoryOpenResult {
  kOk,
  kMissing,
  kReparsePoint,
  kNotDirectory,
  kIoFailure,
};

bool IsMissingPathError(DWORD error) noexcept {
  return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
}

bool IsCollisionError(DWORD error) noexcept {
  return error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS;
}

bool IsCanonicalSha256(std::string_view value) noexcept {
  return value.size() == 64U &&
         std::all_of(value.begin(), value.end(), [](unsigned char value) {
           return (value >= '0' && value <= '9') ||
                  (value >= 'A' && value <= 'F');
         });
}

std::wstring FrameFileName(uint32_t index) {
  wchar_t buffer[32]{};
  const int written = swprintf_s(buffer, L"frame-%06u.jpg", index);
  return written > 0 ? std::wstring(buffer, static_cast<size_t>(written))
                     : std::wstring();
}

bool IsLocalAbsolutePath(const std::filesystem::path& path) noexcept {
  try {
    const std::wstring value = path.native();
    if (!path.is_absolute() || value.size() >= kMaximumWindowsPathCharacters ||
        value.size() < 3 || value[1] != L':' ||
        (value[2] != L'\\' && value[2] != L'/') || value[0] == L'\\' ||
        value[0] == L'/') {
      return false;
    }
    const UINT drive_type = GetDriveTypeW(path.root_path().c_str());
    return drive_type != DRIVE_UNKNOWN && drive_type != DRIVE_NO_ROOT_DIR &&
           drive_type != DRIVE_REMOTE;
  } catch (...) {
    return false;
  }
}

DirectoryOpenResult OpenDirectoryNoFollow(const std::filesystem::path& path,
                                          DWORD desired_access,
                                          DWORD share_mode,
                                          ScopedHandle* directory) noexcept {
  if (directory == nullptr) {
    return DirectoryOpenResult::kIoFailure;
  }
  directory->Reset();
  const HANDLE handle = CreateFileW(path.c_str(), desired_access, share_mode,
                                    nullptr, OPEN_EXISTING, kNoFollowFlags,
                                    nullptr);
  if (handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? DirectoryOpenResult::kMissing
                                              : DirectoryOpenResult::kIoFailure;
  }
  ScopedHandle opened(handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  if (GetFileInformationByHandleEx(opened.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE) {
    return DirectoryOpenResult::kIoFailure;
  }
  if ((attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
    return DirectoryOpenResult::kReparsePoint;
  }
  if ((attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    return DirectoryOpenResult::kNotDirectory;
  }
  *directory = std::move(opened);
  return DirectoryOpenResult::kOk;
}

AtomicChunkStoreResult MapDirectoryOpenResult(
    DirectoryOpenResult result) noexcept {
  switch (result) {
    case DirectoryOpenResult::kOk:
      return AtomicChunkStoreResult::kOk;
    case DirectoryOpenResult::kReparsePoint:
      return AtomicChunkStoreResult::kReparsePoint;
    case DirectoryOpenResult::kNotDirectory:
      return AtomicChunkStoreResult::kInvalidRoot;
    case DirectoryOpenResult::kMissing:
    case DirectoryOpenResult::kIoFailure:
    default:
      return AtomicChunkStoreResult::kIoFailure;
  }
}

AtomicChunkStoreResult EnsureAndLockDirectory(
    const std::filesystem::path& directory, ScopedHandle* lock) noexcept {
  DirectoryOpenResult opened = OpenDirectoryNoFollow(
      directory, kDirectoryLockAccess, kDirectoryShareMode, lock);
  if (opened == DirectoryOpenResult::kMissing) {
    if (CreateDirectoryW(directory.c_str(), nullptr) == FALSE &&
        !IsCollisionError(GetLastError())) {
      return AtomicChunkStoreResult::kIoFailure;
    }
    opened = OpenDirectoryNoFollow(directory, kDirectoryLockAccess,
                                   kDirectoryShareMode, lock);
  }
  return MapDirectoryOpenResult(opened);
}

AtomicChunkStoreResult LockDirectoryChain(
    const std::filesystem::path& directory, std::vector<ScopedHandle>* locks) {
  if (locks == nullptr) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  locks->clear();
  std::filesystem::path current = directory.root_path();
  ScopedHandle root;
  const DirectoryOpenResult root_result = OpenDirectoryNoFollow(
      current, kDirectoryLockAccess, kDirectoryShareMode, &root);
  if (root_result != DirectoryOpenResult::kOk) {
    return MapDirectoryOpenResult(root_result);
  }
  locks->push_back(std::move(root));
  for (const auto& component : directory.relative_path()) {
    current /= component;
    ScopedHandle child;
    const AtomicChunkStoreResult result = EnsureAndLockDirectory(current, &child);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }
    locks->push_back(std::move(child));
  }
  return AtomicChunkStoreResult::kOk;
}

bool FillRandomBytes(std::span<uint8_t> bytes) noexcept {
  return !bytes.empty() &&
         bytes.size() <= static_cast<size_t>(std::numeric_limits<ULONG>::max()) &&
         BCryptGenRandom(nullptr, bytes.data(), static_cast<ULONG>(bytes.size()),
                         BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0;
}

std::wstring HexNonce(std::span<const uint8_t> bytes) {
  constexpr wchar_t kHex[] = L"0123456789abcdef";
  std::wstring value(bytes.size() * 2U, L'0');
  for (size_t index = 0; index < bytes.size(); ++index) {
    value[index * 2U] = kHex[(bytes[index] >> 4U) & 0x0FU];
    value[index * 2U + 1U] = kHex[bytes[index] & 0x0FU];
  }
  return value;
}

bool WriteAll(HANDLE file, const uint8_t* bytes, size_t size) noexcept {
  size_t offset = 0;
  while (offset < size) {
    const DWORD requested = static_cast<DWORD>(std::min<size_t>(
        size - offset,
        static_cast<size_t>(std::numeric_limits<DWORD>::max())));
    DWORD written = 0;
    if (WriteFile(file, bytes + offset, requested, &written, nullptr) == FALSE ||
        written == 0 || written > requested) {
      return false;
    }
    offset += written;
  }
  return true;
}

bool WriteNewFile(const std::filesystem::path& path, const uint8_t* bytes,
                  size_t size) noexcept {
  if (bytes == nullptr || size == 0) {
    return false;
  }
  const HANDLE file = CreateFileW(
      path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW,
      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH |
          FILE_FLAG_OPEN_REPARSE_POINT,
      nullptr);
  if (file == INVALID_HANDLE_VALUE) {
    return false;
  }
  const bool written = WriteAll(file, bytes, size);
  const bool flushed = written && FlushFileBuffers(file) != FALSE;
  const bool closed = CloseHandle(file) != FALSE;
  return flushed && closed;
}

AtomicChunkStoreResult DeleteEntryNoFollow(
    const std::filesystem::path& path, bool expect_directory) noexcept {
  const HANDLE raw_handle = CreateFileW(
      path.c_str(), DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
      kInspectionShareMode, nullptr, OPEN_EXISTING, kNoFollowFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? AtomicChunkStoreResult::kOk
                                              : AtomicChunkStoreResult::kIoFailure;
  }
  ScopedHandle handle(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  if (GetFileInformationByHandleEx(handle.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE) {
    return AtomicChunkStoreResult::kIoFailure;
  }
  const bool is_directory =
      (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
  const bool is_reparse =
      (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
  if (is_reparse || is_directory != expect_directory) {
    return AtomicChunkStoreResult::kIoFailure;
  }
  FILE_DISPOSITION_INFO disposition{};
  disposition.DeleteFile = TRUE;
  return SetFileInformationByHandle(handle.get(), FileDispositionInfo,
                                    &disposition, sizeof(disposition)) != FALSE
             ? AtomicChunkStoreResult::kOk
             : AtomicChunkStoreResult::kIoFailure;
}

bool GetDirectoryPathByHandle(HANDLE directory, std::wstring* path) noexcept {
  if (directory == nullptr || directory == INVALID_HANDLE_VALUE ||
      path == nullptr) {
    return false;
  }
  path->clear();
  try {
    constexpr DWORD kFlags = FILE_NAME_NORMALIZED | VOLUME_NAME_DOS;
    const DWORD required =
        GetFinalPathNameByHandleW(directory, nullptr, 0, kFlags);
    if (required == 0 || required >= kMaximumWindowsPathCharacters) {
      return false;
    }
    std::vector<wchar_t> buffer(required, L'\0');
    const DWORD written = GetFinalPathNameByHandleW(
        directory, buffer.data(), static_cast<DWORD>(buffer.size()), kFlags);
    if (written == 0 || written >= buffer.size()) {
      return false;
    }
    path->assign(buffer.data(), written);
    constexpr std::wstring_view kExtendedDosPrefix = L"\\\\?\\";
    if (!path->starts_with(kExtendedDosPrefix)) {
      path->clear();
      return false;
    }
    path->erase(0, kExtendedDosPrefix.size());
    return IsLocalAbsolutePath(std::filesystem::path(*path));
  } catch (...) {
    path->clear();
    return false;
  }
}

AtomicChunkStoreResult RenameDirectoryByHandle(
    HANDLE directory, HANDLE destination_parent,
    std::wstring_view destination_name) noexcept {
  if (directory == nullptr || directory == INVALID_HANDLE_VALUE ||
      destination_parent == nullptr ||
      destination_parent == INVALID_HANDLE_VALUE ||
      destination_name.empty() ||
      destination_name.size() >
          static_cast<size_t>(std::numeric_limits<DWORD>::max()) /
              sizeof(wchar_t)) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  try {
    std::wstring destination_path;
    if (!GetDirectoryPathByHandle(destination_parent, &destination_path)) {
      return AtomicChunkStoreResult::kIoFailure;
    }
    if (destination_path.back() != L'\\') {
      destination_path.push_back(L'\\');
    }
    if (destination_path.size() >= kMaximumWindowsPathCharacters -
                                      destination_name.size()) {
      return AtomicChunkStoreResult::kInvalidArgument;
    }
    destination_path.append(destination_name);
    const size_t name_bytes = destination_path.size() * sizeof(wchar_t);
    const size_t buffer_size =
        offsetof(FILE_RENAME_INFO, FileName) + name_bytes + sizeof(wchar_t);
    std::vector<uint8_t> buffer(buffer_size, 0);
    auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(buffer.data());
    rename->ReplaceIfExists = FALSE;
    rename->RootDirectory = nullptr;
    rename->FileNameLength = static_cast<DWORD>(name_bytes);
    std::memcpy(rename->FileName, destination_path.data(), name_bytes);
    if (SetFileInformationByHandle(directory, FileRenameInfo, rename,
                                   static_cast<DWORD>(buffer.size())) == FALSE) {
      return IsCollisionError(GetLastError())
                 ? AtomicChunkStoreResult::kAlreadyExists
                 : AtomicChunkStoreResult::kIoFailure;
    }
    return AtomicChunkStoreResult::kOk;
  } catch (...) {
    return AtomicChunkStoreResult::kIoFailure;
  }
}

AtomicChunkStoreResult CreateUniqueStagingDirectory(
    const std::filesystem::path& staging_root, std::string_view artifact_id,
    std::filesystem::path* directory, ScopedHandle* directory_handle) noexcept {
  if (directory == nullptr || directory_handle == nullptr) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  try {
    const std::wstring artifact(artifact_id.begin(), artifact_id.end());
    for (uint32_t attempt = 0; attempt < kMaximumStagingNameAttempts; ++attempt) {
      std::array<uint8_t, kStagingNonceBytes> nonce{};
      if (!FillRandomBytes(nonce)) {
        return AtomicChunkStoreResult::kIoFailure;
      }
      const std::filesystem::path candidate =
          staging_root / (artifact + L"-" + HexNonce(nonce) + L".partial");
      if (CreateDirectoryW(candidate.c_str(), nullptr) == FALSE) {
        if (IsCollisionError(GetLastError())) {
          continue;
        }
        return AtomicChunkStoreResult::kIoFailure;
      }
      ScopedHandle opened;
      const DirectoryOpenResult open_result = OpenDirectoryNoFollow(
          candidate, FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
          kDirectoryShareMode, &opened);
      if (open_result != DirectoryOpenResult::kOk) {
        static_cast<void>(RemoveDirectoryW(candidate.c_str()));
        return MapDirectoryOpenResult(open_result);
      }
      *directory = candidate;
      *directory_handle = std::move(opened);
      return AtomicChunkStoreResult::kOk;
    }
  } catch (...) {
    return AtomicChunkStoreResult::kIoFailure;
  }
  return AtomicChunkStoreResult::kIoFailure;
}

AtomicChunkStoreResult InspectFinalDestination(
    const std::filesystem::path& path) noexcept {
  const HANDLE raw_handle =
      CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, kInspectionShareMode,
                  nullptr, OPEN_EXISTING, kNoFollowFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? AtomicChunkStoreResult::kOk
                                              : AtomicChunkStoreResult::kIoFailure;
  }
  ScopedHandle handle(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  if (GetFileInformationByHandleEx(handle.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE) {
    return AtomicChunkStoreResult::kIoFailure;
  }
  return (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0
             ? AtomicChunkStoreResult::kReparsePoint
             : AtomicChunkStoreResult::kAlreadyExists;
}

}  // namespace

class AtomicChunkState final {
 public:
  ~AtomicChunkState() {
    if (!acknowledged && chunk_directory.valid() && !delete_pending) {
      static_cast<void>(Rollback());
    }
  }

  AtomicChunkStoreResult Commit() noexcept {
    if (committed || !chunk_directory.valid() || !prepared) {
      return AtomicChunkStoreResult::kInvalidArgument;
    }
    frames_directory.Reset();
    const AtomicChunkStoreResult result =
        RenameDirectoryByHandle(chunk_directory.get(), final_parent.get(),
                                final_name);
    if (result == AtomicChunkStoreResult::kOk) {
      committed = true;
    }
    return result;
  }

  AtomicChunkStoreResult Rollback() noexcept {
    if (!chunk_directory.valid() || delete_pending) {
      return AtomicChunkStoreResult::kInvalidArgument;
    }
    try {
      frames_directory.Reset();
      const std::filesystem::path& directory =
          committed ? final_directory : staging_directory;
      for (auto iterator = frame_files.rbegin(); iterator != frame_files.rend();
           ++iterator) {
        const AtomicChunkStoreResult deleted =
            DeleteEntryNoFollow(directory / L"frames" / *iterator, false);
        if (deleted != AtomicChunkStoreResult::kOk) {
          return deleted;
        }
      }
      AtomicChunkStoreResult result =
          DeleteEntryNoFollow(directory / L"manifest.json", false);
      if (result != AtomicChunkStoreResult::kOk) {
        return result;
      }
      result = DeleteEntryNoFollow(directory / L"frames", true);
      if (result != AtomicChunkStoreResult::kOk) {
        return result;
      }
      FILE_DISPOSITION_INFO disposition{};
      disposition.DeleteFile = TRUE;
      if (SetFileInformationByHandle(chunk_directory.get(), FileDispositionInfo,
                                     &disposition, sizeof(disposition)) == FALSE) {
        return AtomicChunkStoreResult::kIoFailure;
      }
      delete_pending = true;
      return AtomicChunkStoreResult::kOk;
    } catch (...) {
      return AtomicChunkStoreResult::kIoFailure;
    }
  }

  std::filesystem::path staging_directory;
  std::filesystem::path final_directory;
  std::wstring final_name;
  std::string chunk_id;
  std::string artifact_identifier;
  std::vector<std::wstring> frame_files;
  std::vector<ChunkFrameManifest> frame_records;
  uint64_t total_frame_bytes = 0;
  std::vector<ScopedHandle> directory_locks;
  ScopedHandle final_parent;
  ScopedHandle chunk_directory;
  ScopedHandle frames_directory;
  AtomicChunkStorePrepareCheckpoint prepare_checkpoint = nullptr;
  bool prepared = false;
  bool committed = false;
  bool acknowledged = false;
  bool delete_pending = false;
};

bool IsValidChunkArtifactId(std::string_view value) noexcept {
  if (value.empty() || value.size() > kMaximumArtifactIdBytes) {
    return false;
  }
  return std::all_of(value.begin(), value.end(), [](unsigned char character) {
    return (character >= 'a' && character <= 'z') ||
           (character >= 'A' && character <= 'Z') ||
           (character >= '0' && character <= '9') || character == '-' ||
           character == '_';
  });
}

AtomicChunkPublication::AtomicChunkPublication() = default;
AtomicChunkPublication::~AtomicChunkPublication() = default;
AtomicChunkPublication::AtomicChunkPublication(
    AtomicChunkPublication&& other) noexcept
    : state_(std::move(other.state_)) {}
AtomicChunkPublication::operator bool() const noexcept {
  return state_ != nullptr && !state_->acknowledged;
}
bool AtomicChunkPublication::committed() const noexcept {
  return state_ != nullptr && state_->committed;
}
const std::string& AtomicChunkPublication::artifact_identifier() const noexcept {
  static const std::string empty;
  return state_ == nullptr ? empty : state_->artifact_identifier;
}
AtomicChunkStoreResult AtomicChunkPublication::Commit() noexcept {
  return state_ == nullptr ? AtomicChunkStoreResult::kInvalidArgument
                           : state_->Commit();
}
void AtomicChunkPublication::Acknowledge() noexcept {
  if (state_ != nullptr && state_->committed) {
    state_->acknowledged = true;
    state_.reset();
  }
}
AtomicChunkStoreResult AtomicChunkPublication::Rollback() noexcept {
  if (state_ == nullptr) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  const AtomicChunkStoreResult result = state_->Rollback();
  if (result == AtomicChunkStoreResult::kOk) {
    state_.reset();
  }
  return result;
}

AtomicChunkWriter::AtomicChunkWriter() = default;
AtomicChunkWriter::~AtomicChunkWriter() = default;
AtomicChunkWriter::AtomicChunkWriter(AtomicChunkWriter&& other) noexcept
    : state_(std::move(other.state_)) {}
AtomicChunkWriter::operator bool() const noexcept {
  return state_ != nullptr && !state_->prepared;
}
const std::string& AtomicChunkWriter::chunk_id() const noexcept {
  static const std::string empty;
  return state_ == nullptr ? empty : state_->chunk_id;
}
AtomicChunkStoreResult AtomicChunkWriter::AppendFrame(
    const ChunkFrameManifest& frame,
    std::span<const uint8_t> jpeg_bytes) noexcept {
  if (state_ == nullptr || state_->prepared ||
      frame.index != state_->frame_records.size() ||
      frame.byte_count != jpeg_bytes.size() ||
      jpeg_bytes.size() < 4U ||
      jpeg_bytes.size() > kMaximumChunkFrameFileBytes ||
      jpeg_bytes[0] != 0xFFU || jpeg_bytes[1] != 0xD8U ||
      jpeg_bytes[jpeg_bytes.size() - 2U] != 0xFFU ||
      jpeg_bytes.back() != 0xD9U || !IsCanonicalSha256(frame.sha256) ||
      state_->total_frame_bytes >
          kMaximumChunkFrameBytes - jpeg_bytes.size()) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  if (!state_->frame_records.empty() &&
      frame.offset_milliseconds <=
          state_->frame_records.back().offset_milliseconds) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  try {
    const std::wstring file_name = FrameFileName(frame.index);
    if (file_name.empty() ||
        !WriteNewFile(state_->staging_directory / L"frames" / file_name,
                      jpeg_bytes.data(), jpeg_bytes.size())) {
      return AtomicChunkStoreResult::kIoFailure;
    }
    state_->frame_files.push_back(file_name);
    state_->frame_records.push_back(frame);
    state_->total_frame_bytes += jpeg_bytes.size();
    return AtomicChunkStoreResult::kOk;
  } catch (...) {
    return AtomicChunkStoreResult::kIoFailure;
  }
}
AtomicChunkStoreResult AtomicChunkWriter::Prepare(
    const ChunkManifest& manifest,
    AtomicChunkPublication* publication) noexcept {
  if (state_ == nullptr || state_->prepared || publication == nullptr ||
      static_cast<bool>(*publication) || manifest.chunk_id != state_->chunk_id ||
      manifest.frames != state_->frame_records ||
      manifest.frame_byte_count != state_->total_frame_bytes ||
      !IsValidChunkManifest(manifest)) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  std::string manifest_utf8;
  if (!BuildChunkManifestJson(manifest, &manifest_utf8) ||
      manifest_utf8.empty() ||
      manifest_utf8.size() > kMaximumChunkManifestBytes ||
      !WriteNewFile(state_->staging_directory / L"manifest.json",
                    reinterpret_cast<const uint8_t*>(manifest_utf8.data()),
                    manifest_utf8.size())) {
    return AtomicChunkStoreResult::kIoFailure;
  }
  if (state_->prepare_checkpoint != nullptr) {
    state_->prepare_checkpoint();
  }
  state_->prepared = true;
  publication->state_ = std::move(state_);
  return AtomicChunkStoreResult::kOk;
}
AtomicChunkStoreResult AtomicChunkWriter::Reset() noexcept {
  if (state_ == nullptr) {
    return AtomicChunkStoreResult::kOk;
  }
  const AtomicChunkStoreResult result = state_->Rollback();
  if (result == AtomicChunkStoreResult::kOk) {
    state_.reset();
  }
  return result;
}

AtomicChunkStore::AtomicChunkStore(
    std::wstring output_root,
    AtomicChunkStorePrepareCheckpoint prepare_checkpoint)
    : output_root_(std::move(output_root)),
      prepare_checkpoint_(prepare_checkpoint) {}

AtomicChunkStoreResult AtomicChunkStore::Begin(
    std::string_view artifact_id, AtomicChunkWriter* writer) const noexcept {
  if (writer == nullptr || static_cast<bool>(*writer) ||
      !IsValidChunkArtifactId(artifact_id)) {
    return AtomicChunkStoreResult::kInvalidArgument;
  }
  std::unique_ptr<AtomicChunkState> pending;
  try {
    std::filesystem::path root(output_root_);
    if (!IsLocalAbsolutePath(root)) {
      return AtomicChunkStoreResult::kInvalidRoot;
    }
    std::error_code error;
    root = std::filesystem::absolute(root, error).lexically_normal();
    if (error || !IsLocalAbsolutePath(root)) {
      return AtomicChunkStoreResult::kInvalidRoot;
    }

    std::vector<ScopedHandle> locks;
    AtomicChunkStoreResult result = LockDirectoryChain(root, &locks);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }
    const std::filesystem::path staging_root = root / L".staging";
    const std::filesystem::path chunks_root = root / L"chunks";
    ScopedHandle staging_root_lock;
    result = EnsureAndLockDirectory(staging_root, &staging_root_lock);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }
    locks.push_back(std::move(staging_root_lock));
    ScopedHandle chunks_root_lock;
    result = EnsureAndLockDirectory(chunks_root, &chunks_root_lock);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }

    const std::wstring artifact(artifact_id.begin(), artifact_id.end());
    const std::filesystem::path final_directory = chunks_root / artifact;
    result = InspectFinalDestination(final_directory);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }

    pending = std::make_unique<AtomicChunkState>();
    pending->chunk_id.assign(artifact_id);
    pending->artifact_identifier =
        "chunks/" + std::string(artifact_id) + "/manifest.json";
    pending->final_directory = final_directory;
    pending->prepare_checkpoint = prepare_checkpoint_;
    pending->final_name = artifact;
    pending->final_parent = std::move(chunks_root_lock);
    pending->directory_locks = std::move(locks);

    result = CreateUniqueStagingDirectory(
        staging_root, artifact_id, &pending->staging_directory,
        &pending->chunk_directory);
    if (result != AtomicChunkStoreResult::kOk) {
      return result;
    }
    const std::filesystem::path frames_path =
        pending->staging_directory / L"frames";
    if (CreateDirectoryW(frames_path.c_str(), nullptr) == FALSE) {
      return AtomicChunkStoreResult::kIoFailure;
    }
    const DirectoryOpenResult opened = OpenDirectoryNoFollow(
        frames_path, FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        kInspectionShareMode, &pending->frames_directory);
    if (opened != DirectoryOpenResult::kOk) {
      return MapDirectoryOpenResult(opened);
    }
    writer->state_ = std::move(pending);
    return AtomicChunkStoreResult::kOk;
  } catch (...) {
    if (pending != nullptr && pending->chunk_directory.valid()) {
      const AtomicChunkStoreResult cleanup = pending->Rollback();
      if (cleanup != AtomicChunkStoreResult::kOk) {
        writer->state_ = std::move(pending);
      }
    }
    return AtomicChunkStoreResult::kIoFailure;
  }
}

}  // namespace windayflow::capture

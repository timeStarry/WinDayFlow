#include "analysis_evidence_extractor.h"

#include <Windows.h>
#include <bcrypt.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <limits>
#include <set>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "capture_chunk_fingerprint.h"
#include "pixel_buffer.h"
#include "wic_bgra_scaler.h"

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;

constexpr uint32_t kMaximumSourceFrames = 14'400U;
constexpr uint32_t kMaximumVideoWidth = 7'680U;
constexpr uint32_t kMaximumVideoHeight = 4'320U;
constexpr uint64_t kMaximumDurationMilliseconds = 3'600'000U;
constexpr uint32_t kMaximumJpegWidth = 1'600U;
constexpr uint32_t kMaximumJpegHeight = 900U;
constexpr size_t kMaximumWindowsPathCharacters = 32'767U;
constexpr size_t kReadBufferBytes = 64U * 1024U;
constexpr size_t kStagingNonceBytes = 16U;
constexpr DWORD kDirectoryAccess = FILE_READ_ATTRIBUTES | SYNCHRONIZE;
constexpr DWORD kDirectoryShareMode = FILE_SHARE_READ | FILE_SHARE_WRITE;
constexpr DWORD kEvidenceShareMode = FILE_SHARE_READ;
constexpr DWORD kNoFollowDirectoryFlags =
    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT;
constexpr DWORD kNoFollowFileFlags =
    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS |
    FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN;
constexpr std::string_view kPolicyVersion = "evidence-v1";

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

struct FileSnapshot {
  DWORD volume_serial_number = 0;
  uint64_t file_index = 0;
  uint64_t length = 0;
  uint64_t last_write_time = 0;
  DWORD attributes = 0;
};

struct EvidenceFrameRecord {
  std::string id;
  uint32_t index = 0;
  uint64_t offset_milliseconds = 0;
  uint32_t byte_count = 0;
  std::string sha256;
  std::vector<uint8_t> jpeg_bytes;
};

struct ParsedEvidenceManifest {
  std::string policy_version;
  std::string chunk_id;
  std::string source_fingerprint;
  std::string artifact_path;
  std::vector<EvidenceFrameRecord> frames;
};

enum class PathResult {
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

bool IsCollisionError(DWORD error) noexcept {
  return error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS;
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
    std::array<wchar_t, 4> drive_root{value[0], L':', L'\\', L'\0'};
    const UINT drive_type = GetDriveTypeW(drive_root.data());
    if (drive_type == DRIVE_UNKNOWN || drive_type == DRIVE_NO_ROOT_DIR ||
        drive_type == DRIVE_REMOTE) {
      return false;
    }
    const DWORD required = GetFullPathNameW(value.c_str(), 0, nullptr, nullptr);
    if (required == 0 || required >= kMaximumWindowsPathCharacters) {
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
    if (!path.is_absolute() || canonical.size() < 3U || canonical[1] != L':' ||
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

PathResult OpenDirectoryNoFollow(const std::filesystem::path& path,
                                 DWORD desired_access, DWORD share_mode,
                                 ScopedHandle* directory) noexcept {
  if (directory == nullptr) {
    return PathResult::kIoFailure;
  }
  directory->Reset();
  const HANDLE raw_handle =
      CreateFileW(path.c_str(), desired_access, share_mode, nullptr,
                  OPEN_EXISTING, kNoFollowDirectoryFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? PathResult::kNotFound
                                              : PathResult::kIoFailure;
  }
  ScopedHandle opened(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  if (GetFileInformationByHandleEx(opened.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE) {
    return PathResult::kIoFailure;
  }
  if (HasUnsafeAttributes(attributes.FileAttributes) ||
      (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    return PathResult::kUnsafe;
  }
  *directory = std::move(opened);
  return PathResult::kOk;
}

PathResult LockDirectoryChain(const std::filesystem::path& root,
                              std::vector<ScopedHandle>* locks) {
  if (locks == nullptr) {
    return PathResult::kIoFailure;
  }
  locks->clear();
  size_t component_count = 1U;
  for (const auto& ignored : root.relative_path()) {
    static_cast<void>(ignored);
    ++component_count;
  }
  locks->reserve(component_count + 8U);
  std::filesystem::path current = root.root_path();
  ScopedHandle drive;
  PathResult result = OpenDirectoryNoFollow(
      current, kDirectoryAccess, kDirectoryShareMode, &drive);
  if (result != PathResult::kOk) {
    return result;
  }
  locks->push_back(std::move(drive));
  for (const auto& component : root.relative_path()) {
    if (component.empty() || component == L"." || component == L"..") {
      return PathResult::kUnsafe;
    }
    current /= component;
    ScopedHandle child;
    result = OpenDirectoryNoFollow(
        current, kDirectoryAccess, kDirectoryShareMode, &child);
    if (result != PathResult::kOk) {
      return result;
    }
    locks->push_back(std::move(child));
  }
  return PathResult::kOk;
}

PathResult LockChildDirectory(const std::filesystem::path& path,
                              std::vector<ScopedHandle>* locks,
                              DWORD desired_access = kDirectoryAccess,
                              DWORD share_mode = kDirectoryShareMode) {
  ScopedHandle child;
  const PathResult result =
      OpenDirectoryNoFollow(path, desired_access, share_mode, &child);
  if (result == PathResult::kOk) {
    locks->push_back(std::move(child));
  }
  return result;
}

PathResult EnsureAndLockChildDirectory(const std::filesystem::path& path,
                                       std::vector<ScopedHandle>* locks) {
  PathResult result = LockChildDirectory(path, locks);
  if (result != PathResult::kNotFound) {
    return result;
  }
  if (CreateDirectoryW(path.c_str(), nullptr) == FALSE &&
      !IsCollisionError(GetLastError())) {
    return PathResult::kIoFailure;
  }
  return LockChildDirectory(path, locks);
}

PathResult OpenBoundedFile(const std::filesystem::path& path,
                           uint64_t maximum_size, ScopedHandle* file,
                           FileSnapshot* snapshot) noexcept {
  if (file == nullptr || snapshot == nullptr) {
    return PathResult::kIoFailure;
  }
  file->Reset();
  *snapshot = {};
  const HANDLE raw_handle = CreateFileW(
      path.c_str(), GENERIC_READ | FILE_READ_ATTRIBUTES, kEvidenceShareMode,
      nullptr, OPEN_EXISTING, kNoFollowFileFlags, nullptr);
  if (raw_handle == INVALID_HANDLE_VALUE) {
    return IsMissingPathError(GetLastError()) ? PathResult::kNotFound
                                              : PathResult::kIoFailure;
  }
  ScopedHandle opened(raw_handle);
  FILE_ATTRIBUTE_TAG_INFO attributes{};
  FileSnapshot value{};
  if (GetFileInformationByHandleEx(opened.get(), FileAttributeTagInfo,
                                   &attributes, sizeof(attributes)) == FALSE ||
      !TryReadFileSnapshot(opened.get(), &value)) {
    return PathResult::kIoFailure;
  }
  if (HasUnsafeAttributes(attributes.FileAttributes) ||
      (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
      HasUnsafeAttributes(value.attributes) ||
      (value.attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 || value.length == 0) {
    return PathResult::kUnsafe;
  }
  if (value.length > maximum_size) {
    return PathResult::kTooLarge;
  }
  *snapshot = value;
  *file = std::move(opened);
  return PathResult::kOk;
}

AnalysisEvidenceResult MapPathResult(PathResult result) noexcept {
  switch (result) {
    case PathResult::kOk:
      return AnalysisEvidenceResult::kOk;
    case PathResult::kNotFound:
      return AnalysisEvidenceResult::kNotFound;
    case PathResult::kUnsafe:
      return AnalysisEvidenceResult::kUnsafeEvidence;
    case PathResult::kTooLarge:
      return AnalysisEvidenceResult::kTooLarge;
    case PathResult::kIoFailure:
    default:
      return AnalysisEvidenceResult::kIoFailure;
  }
}

AnalysisEvidenceResult MapFingerprintResult(
    CaptureChunkFingerprintResult result) noexcept {
  switch (result) {
    case CaptureChunkFingerprintResult::kOk:
      return AnalysisEvidenceResult::kOk;
    case CaptureChunkFingerprintResult::kInvalidArgument:
      return AnalysisEvidenceResult::kInvalidArgument;
    case CaptureChunkFingerprintResult::kNotFound:
      return AnalysisEvidenceResult::kNotFound;
    case CaptureChunkFingerprintResult::kUnsafeEvidence:
      return AnalysisEvidenceResult::kUnsafeEvidence;
    case CaptureChunkFingerprintResult::kTooLarge:
      return AnalysisEvidenceResult::kTooLarge;
    case CaptureChunkFingerprintResult::kChangedDuringRead:
      return AnalysisEvidenceResult::kChangedDuringRead;
    case CaptureChunkFingerprintResult::kIoFailure:
      return AnalysisEvidenceResult::kIoFailure;
    case CaptureChunkFingerprintResult::kCryptoFailure:
      return AnalysisEvidenceResult::kCryptoFailure;
    default:
      return AnalysisEvidenceResult::kIoFailure;
  }
}

AnalysisEvidenceResult VerifyStableFile(
    const std::filesystem::path& path, uint64_t maximum_size,
    const ScopedHandle& original, const FileSnapshot& before) noexcept {
  FileSnapshot after{};
  if (!TryReadFileSnapshot(original.get(), &after)) {
    return AnalysisEvidenceResult::kIoFailure;
  }
  if (!IsSameSnapshot(before, after)) {
    return AnalysisEvidenceResult::kChangedDuringRead;
  }
  ScopedHandle reopened;
  FileSnapshot reopened_snapshot{};
  const PathResult opened =
      OpenBoundedFile(path, maximum_size, &reopened, &reopened_snapshot);
  if (opened == PathResult::kNotFound) {
    return AnalysisEvidenceResult::kChangedDuringRead;
  }
  if (opened != PathResult::kOk) {
    return MapPathResult(opened);
  }
  return IsSameSnapshot(before, reopened_snapshot)
             ? AnalysisEvidenceResult::kOk
             : AnalysisEvidenceResult::kChangedDuringRead;
}

AnalysisEvidenceResult ReadFileFully(const std::filesystem::path& path,
                                     uint64_t maximum_size,
                                     std::vector<uint8_t>* bytes) noexcept {
  if (bytes == nullptr) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  bytes->clear();
  ScopedHandle file;
  FileSnapshot before{};
  const PathResult opened =
      OpenBoundedFile(path, maximum_size, &file, &before);
  if (opened != PathResult::kOk) {
    return MapPathResult(opened);
  }
  try {
    bytes->resize(static_cast<size_t>(before.length));
  } catch (...) {
    return AnalysisEvidenceResult::kIoFailure;
  }
  size_t offset = 0;
  while (offset < bytes->size()) {
    const DWORD requested = static_cast<DWORD>(std::min<size_t>(
        bytes->size() - offset, kReadBufferBytes));
    DWORD read = 0;
    if (ReadFile(file.get(), bytes->data() + offset, requested, &read,
                 nullptr) == FALSE ||
        read == 0 || read > requested) {
      bytes->clear();
      return AnalysisEvidenceResult::kIoFailure;
    }
    offset += read;
  }
  uint8_t trailing = 0;
  DWORD trailing_read = 0;
  if (ReadFile(file.get(), &trailing, 1, &trailing_read, nullptr) == FALSE) {
    bytes->clear();
    return AnalysisEvidenceResult::kIoFailure;
  }
  if (trailing_read != 0) {
    bytes->clear();
    return AnalysisEvidenceResult::kChangedDuringRead;
  }
  const AnalysisEvidenceResult stable =
      VerifyStableFile(path, maximum_size, file, before);
  if (stable != AnalysisEvidenceResult::kOk) {
    bytes->clear();
  }
  return stable;
}

class LockedCaptureSource final {
 public:
  AnalysisEvidenceResult Open(const AnalysisEvidenceRequest& request) {
    if (!TryNormalizeLocalAbsoluteRoot(request.data_root, &data_root_)) {
      return AnalysisEvidenceResult::kInvalidArgument;
    }
    PathResult result = LockDirectoryChain(data_root_, &directory_locks_);
    if (result != PathResult::kOk) {
      return MapPathResult(result);
    }
    const std::filesystem::path chunks_root = data_root_ / L"chunks";
    result = LockChildDirectory(chunks_root, &directory_locks_);
    if (result != PathResult::kOk) {
      return MapPathResult(result);
    }
    const std::wstring chunk_id(request.canonical_chunk_id.begin(),
                                request.canonical_chunk_id.end());
    chunk_root_ = chunks_root / chunk_id;
    result = LockChildDirectory(chunk_root_, &directory_locks_);
    if (result != PathResult::kOk) {
      return MapPathResult(result);
    }
    manifest_path_ = chunk_root_ / L"manifest.json";
    video_path_ = chunk_root_ / L"capture.mp4";
    result = OpenBoundedFile(manifest_path_, kMaximumFingerprintManifestBytes,
                             &manifest_, &manifest_before_);
    if (result != PathResult::kOk) {
      return MapPathResult(result);
    }
    result = OpenBoundedFile(video_path_, kMaximumFingerprintVideoBytes,
                             &video_, &video_before_);
    if (result != PathResult::kOk) {
      return MapPathResult(result);
    }
    if (video_before_.length != request.expected_video_byte_count) {
      return AnalysisEvidenceResult::kChangedDuringRead;
    }
    return AnalysisEvidenceResult::kOk;
  }

  AnalysisEvidenceResult VerifyFingerprint(
      const AnalysisEvidenceRequest& request) const {
    std::array<char, kCaptureChunkFingerprintBufferSize> fingerprint{};
    const CaptureChunkFingerprintResult result =
        ComputeCaptureChunkFingerprint(
            data_root_.wstring(), request.canonical_chunk_id,
            static_cast<size_t>(request.expected_video_byte_count),
            &fingerprint);
    if (result != CaptureChunkFingerprintResult::kOk) {
      return MapFingerprintResult(result);
    }
    const std::string_view actual(fingerprint.data(),
                                  kCaptureChunkFingerprintHexLength);
    return actual == request.expected_source_fingerprint
               ? AnalysisEvidenceResult::kOk
               : AnalysisEvidenceResult::kChangedDuringRead;
  }

  AnalysisEvidenceResult VerifyStable() const noexcept {
    AnalysisEvidenceResult result = VerifyStableFile(
        manifest_path_, kMaximumFingerprintManifestBytes, manifest_,
        manifest_before_);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    return VerifyStableFile(video_path_, kMaximumFingerprintVideoBytes, video_,
                            video_before_);
  }

  const std::filesystem::path& data_root() const noexcept { return data_root_; }
  const std::filesystem::path& video_path() const noexcept {
    return video_path_;
  }

 private:
  std::filesystem::path data_root_;
  std::filesystem::path chunk_root_;
  std::filesystem::path manifest_path_;
  std::filesystem::path video_path_;
  std::vector<ScopedHandle> directory_locks_;
  ScopedHandle manifest_;
  ScopedHandle video_;
  FileSnapshot manifest_before_{};
  FileSnapshot video_before_{};
};

class MediaRuntime final {
 public:
  HRESULT Start() noexcept {
    HRESULT result =
        CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
    if (SUCCEEDED(result)) {
      uninitialize_com_ = true;
    } else if (result != RPC_E_CHANGED_MODE) {
      return result;
    }
    result = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(result)) {
      media_foundation_started_ = true;
    } else if (uninitialize_com_) {
      CoUninitialize();
      uninitialize_com_ = false;
    }
    return result;
  }

  ~MediaRuntime() {
    if (media_foundation_started_) {
      static_cast<void>(MFShutdown());
    }
    if (uninitialize_com_) {
      CoUninitialize();
    }
  }

 private:
  bool uninitialize_com_ = false;
  bool media_foundation_started_ = false;
};

class StrictJsonCursor final {
 public:
  explicit StrictJsonCursor(std::string_view value) : value_(value) {}

  bool Consume(std::string_view literal) noexcept {
    if (!value_.substr(position_).starts_with(literal)) {
      return false;
    }
    position_ += literal.size();
    return true;
  }

  bool ReadString(std::string* destination) {
    if (destination == nullptr || !Consume("\"")) {
      return false;
    }
    destination->clear();
    const size_t start = position_;
    while (position_ < value_.size() && value_[position_] != '"') {
      const unsigned char character =
          static_cast<unsigned char>(value_[position_]);
      if (character < 0x20U || character > 0x7EU || character == '\\') {
        destination->clear();
        return false;
      }
      ++position_;
    }
    if (position_ >= value_.size()) {
      destination->clear();
      return false;
    }
    destination->assign(value_.substr(start, position_ - start));
    ++position_;
    return true;
  }

  bool ReadUnsigned(uint64_t* destination) noexcept {
    if (destination == nullptr || position_ >= value_.size() ||
        value_[position_] < '0' || value_[position_] > '9') {
      return false;
    }
    if (value_[position_] == '0' && position_ + 1U < value_.size() &&
        value_[position_ + 1U] >= '0' && value_[position_ + 1U] <= '9') {
      return false;
    }
    uint64_t result = 0;
    while (position_ < value_.size() && value_[position_] >= '0' &&
           value_[position_] <= '9') {
      const uint64_t digit =
          static_cast<uint64_t>(value_[position_] - '0');
      if (result > (std::numeric_limits<uint64_t>::max() - digit) / 10U) {
        return false;
      }
      result = result * 10U + digit;
      ++position_;
    }
    *destination = result;
    return true;
  }

  bool finished() const noexcept { return position_ == value_.size(); }

 private:
  std::string_view value_;
  size_t position_ = 0;
};

std::string FrameId(uint32_t index) {
  std::array<char, 32> buffer{};
  const int written = sprintf_s(buffer.data(), buffer.size(), "frame-%04u", index);
  return written > 0 ? std::string(buffer.data(), static_cast<size_t>(written))
                     : std::string();
}

std::filesystem::path FrameFileName(uint32_t index) {
  const std::string id = FrameId(index);
  return std::filesystem::path(std::wstring(id.begin(), id.end()) + L".jpg");
}

std::string ExpectedArtifactPath(std::string_view chunk_id,
                                 std::string_view fingerprint) {
  return std::string("evidence/") + std::string(kPolicyVersion) + "/" +
         std::string(chunk_id) + "/" + std::string(fingerprint) +
         "/manifest.json";
}

bool ParseEvidenceManifest(std::string_view json,
                           ParsedEvidenceManifest* manifest) {
  if (manifest == nullptr || json.empty() ||
      json.size() > kMaximumAnalysisEvidenceManifestBytes) {
    return false;
  }
  *manifest = {};
  StrictJsonCursor cursor(json);
  uint64_t schema_version = 0;
  if (!cursor.Consume("{\"schemaVersion\":") ||
      !cursor.ReadUnsigned(&schema_version) || schema_version != 1U ||
      !cursor.Consume(",\"policyVersion\":") ||
      !cursor.ReadString(&manifest->policy_version) ||
      !cursor.Consume(",\"chunkId\":") ||
      !cursor.ReadString(&manifest->chunk_id) ||
      !cursor.Consume(",\"sourceFingerprint\":") ||
      !cursor.ReadString(&manifest->source_fingerprint) ||
      !cursor.Consume(",\"artifactPath\":") ||
      !cursor.ReadString(&manifest->artifact_path) ||
      !cursor.Consume(",\"frames\":[")) {
    return false;
  }

  for (uint32_t ordinal = 0; ordinal < kMaximumAnalysisEvidenceFrames;
       ++ordinal) {
    if (ordinal != 0 && !cursor.Consume(",")) {
      break;
    }
    EvidenceFrameRecord frame;
    uint64_t index = 0;
    uint64_t byte_count = 0;
    if (!cursor.Consume("{\"id\":") || !cursor.ReadString(&frame.id) ||
        !cursor.Consume(",\"index\":") || !cursor.ReadUnsigned(&index) ||
        !cursor.Consume(",\"offsetMilliseconds\":") ||
        !cursor.ReadUnsigned(&frame.offset_milliseconds) ||
        !cursor.Consume(",\"byteCount\":") ||
        !cursor.ReadUnsigned(&byte_count) ||
        !cursor.Consume(",\"sha256\":") ||
        !cursor.ReadString(&frame.sha256) || !cursor.Consume("}")) {
      return false;
    }
    if (index > std::numeric_limits<uint32_t>::max() ||
        byte_count > std::numeric_limits<uint32_t>::max()) {
      return false;
    }
    frame.index = static_cast<uint32_t>(index);
    frame.byte_count = static_cast<uint32_t>(byte_count);
    manifest->frames.push_back(std::move(frame));
    if (cursor.Consume("]}")) {
      return cursor.finished() && !manifest->frames.empty();
    }
  }
  return false;
}

bool ValidateParsedManifest(const ParsedEvidenceManifest& manifest,
                            std::string_view chunk_id,
                            std::string_view fingerprint,
                            uint64_t maximum_duration_ms) noexcept {
  if (manifest.policy_version != kPolicyVersion ||
      manifest.chunk_id != chunk_id ||
      manifest.source_fingerprint != fingerprint ||
      manifest.artifact_path != ExpectedArtifactPath(chunk_id, fingerprint) ||
      manifest.frames.empty() ||
      manifest.frames.size() > kMaximumAnalysisEvidenceFrames) {
    return false;
  }
  size_t total = 0;
  uint64_t previous_offset = 0;
  for (size_t ordinal = 0; ordinal < manifest.frames.size(); ++ordinal) {
    const EvidenceFrameRecord& frame = manifest.frames[ordinal];
    if (frame.index != ordinal || frame.id != FrameId(frame.index) ||
        frame.byte_count < 4U ||
        frame.byte_count > kMaximumAnalysisEvidenceFrameBytes ||
        !IsCanonicalSourceFingerprint(frame.sha256) ||
        frame.offset_milliseconds >= maximum_duration_ms ||
        (ordinal != 0 && frame.offset_milliseconds < previous_offset) ||
        total > kMaximumAnalysisEvidenceTotalBytes - frame.byte_count) {
      return false;
    }
    total += frame.byte_count;
    previous_offset = frame.offset_milliseconds;
  }
  return total <= kMaximumAnalysisEvidenceTotalBytes;
}

std::string SerializeEvidenceManifest(const AnalysisEvidenceRequest& request,
                                      const std::vector<EvidenceFrameRecord>& frames) {
  std::string json =
      "{\"schemaVersion\":1,\"policyVersion\":\"evidence-v1\",\"chunkId\":\"" +
      request.canonical_chunk_id + "\",\"sourceFingerprint\":\"" +
      request.expected_source_fingerprint + "\",\"artifactPath\":\"" +
      ExpectedArtifactPath(request.canonical_chunk_id,
                           request.expected_source_fingerprint) +
      "\",\"frames\":[";
  for (size_t ordinal = 0; ordinal < frames.size(); ++ordinal) {
    if (ordinal != 0) {
      json.push_back(',');
    }
    const EvidenceFrameRecord& frame = frames[ordinal];
    json += "{\"id\":\"" + frame.id + "\",\"index\":" +
            std::to_string(frame.index) + ",\"offsetMilliseconds\":" +
            std::to_string(frame.offset_milliseconds) + ",\"byteCount\":" +
            std::to_string(frame.byte_count) + ",\"sha256\":\"" +
            frame.sha256 + "\"}";
  }
  json += "]}";
  return json;
}

bool IsJpeg(std::span<const uint8_t> bytes) noexcept {
  return bytes.size() >= 4U && bytes[0] == 0xFFU && bytes[1] == 0xD8U &&
         bytes[bytes.size() - 2U] == 0xFFU &&
         bytes[bytes.size() - 1U] == 0xD9U;
}

bool ComputeSha256(std::span<const uint8_t> bytes,
                   std::string* uppercase_hex) noexcept {
  if (uppercase_hex == nullptr || bytes.empty() ||
      bytes.size() > std::numeric_limits<ULONG>::max()) {
    return false;
  }
  uppercase_hex->clear();
  BCRYPT_ALG_HANDLE algorithm = nullptr;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr,
                                  0) != 0) {
    return false;
  }
  std::array<uint8_t, 32> digest{};
  const NTSTATUS status = BCryptHash(
      algorithm, nullptr, 0, const_cast<PUCHAR>(bytes.data()),
      static_cast<ULONG>(bytes.size()), digest.data(),
      static_cast<ULONG>(digest.size()));
  static_cast<void>(BCryptCloseAlgorithmProvider(algorithm, 0));
  if (status != 0) {
    return false;
  }
  constexpr char kUpperHex[] = "0123456789ABCDEF";
  try {
    uppercase_hex->resize(digest.size() * 2U);
    for (size_t index = 0; index < digest.size(); ++index) {
      (*uppercase_hex)[index * 2U] = kUpperHex[digest[index] >> 4U];
      (*uppercase_hex)[index * 2U + 1U] =
          kUpperHex[digest[index] & 0x0FU];
    }
    return true;
  } catch (...) {
    uppercase_hex->clear();
    return false;
  }
}

HRESULT ValidateJpegWithWic(IWICImagingFactory* factory,
                            std::span<const uint8_t> bytes) noexcept {
  if (factory == nullptr || !IsJpeg(bytes) ||
      bytes.size() > std::numeric_limits<DWORD>::max()) {
    return E_INVALIDARG;
  }
  ComPtr<IWICStream> stream;
  HRESULT result = factory->CreateStream(stream.GetAddressOf());
  if (SUCCEEDED(result)) {
    result = stream->InitializeFromMemory(
        const_cast<BYTE*>(bytes.data()), static_cast<DWORD>(bytes.size()));
  }
  ComPtr<IWICBitmapDecoder> decoder;
  if (SUCCEEDED(result)) {
    result = factory->CreateDecoderFromStream(
        stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad,
        decoder.GetAddressOf());
  }
  UINT frame_count = 0;
  if (SUCCEEDED(result)) {
    result = decoder->GetFrameCount(&frame_count);
  }
  if (FAILED(result) || frame_count != 1U) {
    return FAILED(result) ? result : WINCODEC_ERR_BADIMAGE;
  }
  ComPtr<IWICBitmapFrameDecode> frame;
  result = decoder->GetFrame(0, frame.GetAddressOf());
  UINT width = 0;
  UINT height = 0;
  if (SUCCEEDED(result)) {
    result = frame->GetSize(&width, &height);
  }
  return SUCCEEDED(result) && width > 0 && height > 0 &&
                 width <= kMaximumJpegWidth && height <= kMaximumJpegHeight
             ? S_OK
             : FAILED(result) ? result : WINCODEC_ERR_BADIMAGE;
}

HRESULT EncodeJpeg(IWICImagingFactory* factory, const BgraFrame& source,
                   std::vector<uint8_t>* jpeg) noexcept {
  if (jpeg == nullptr) {
    return E_POINTER;
  }
  jpeg->clear();
  if (factory == nullptr || !IsValidBgraFrame(source)) {
    return E_INVALIDARG;
  }
  try {
    BgraFrame scaled;
    HRESULT result = ScaleBgraFrameWithWic(
        factory, source, kMaximumJpegWidth, kMaximumJpegHeight, &scaled);
    if (FAILED(result)) {
      return result;
    }
    const size_t stride = static_cast<size_t>(scaled.width) * 4U;
    std::vector<uint8_t> buffer(kMaximumAnalysisEvidenceFrameBytes, 0);
    ComPtr<IWICStream> stream;
    result = factory->CreateStream(stream.GetAddressOf());
    if (SUCCEEDED(result)) {
      result = stream->InitializeFromMemory(
          buffer.data(), static_cast<DWORD>(buffer.size()));
    }
    ComPtr<IWICBitmapEncoder> encoder;
    if (SUCCEEDED(result)) {
      result = factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr,
                                      encoder.GetAddressOf());
    }
    if (SUCCEEDED(result)) {
      result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    }
    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    if (SUCCEEDED(result)) {
      result = encoder->CreateNewFrame(frame.GetAddressOf(),
                                       properties.GetAddressOf());
    }
    if (SUCCEEDED(result) && properties != nullptr) {
      PROPBAG2 option{};
      option.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
      VARIANT quality;
      VariantInit(&quality);
      quality.vt = VT_R4;
      quality.fltVal = 0.82F;
      result = properties->Write(1, &option, &quality);
      VariantClear(&quality);
    }
    if (SUCCEEDED(result)) {
      result = frame->Initialize(properties.Get());
    }
    if (SUCCEEDED(result)) {
      result = frame->SetSize(scaled.width, scaled.height);
    }
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    if (SUCCEEDED(result)) {
      result = frame->SetPixelFormat(&format);
    }
    if (SUCCEEDED(result) && format != GUID_WICPixelFormat24bppBGR) {
      result = WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
    }
    ComPtr<IWICBitmap> bitmap;
    if (SUCCEEDED(result)) {
      result = factory->CreateBitmapFromMemory(
          scaled.width, scaled.height, GUID_WICPixelFormat32bppBGRA,
          static_cast<UINT>(stride), static_cast<UINT>(scaled.pixels.size()),
          scaled.pixels.data(), bitmap.GetAddressOf());
    }
    ComPtr<IWICFormatConverter> converter;
    if (SUCCEEDED(result)) {
      result = factory->CreateFormatConverter(converter.GetAddressOf());
    }
    if (SUCCEEDED(result)) {
      result = converter->Initialize(
          bitmap.Get(), GUID_WICPixelFormat24bppBGR, WICBitmapDitherTypeNone,
          nullptr, 0.0, WICBitmapPaletteTypeCustom);
    }
    if (SUCCEEDED(result)) {
      result = frame->WriteSource(converter.Get(), nullptr);
    }
    if (SUCCEEDED(result)) {
      result = frame->Commit();
    }
    if (SUCCEEDED(result)) {
      result = encoder->Commit();
    }
    LARGE_INTEGER zero{};
    ULARGE_INTEGER position{};
    if (SUCCEEDED(result)) {
      result = stream->Seek(zero, STREAM_SEEK_CUR, &position);
    }
    if (FAILED(result) || position.QuadPart < 4U ||
        position.QuadPart > kMaximumAnalysisEvidenceFrameBytes) {
      return FAILED(result) ? result : STG_E_MEDIUMFULL;
    }
    buffer.resize(static_cast<size_t>(position.QuadPart));
    if (!IsJpeg(buffer)) {
      return WINCODEC_ERR_BADIMAGE;
    }
    *jpeg = std::move(buffer);
    return S_OK;
  } catch (...) {
    jpeg->clear();
    return E_OUTOFMEMORY;
  }
}

HRESULT ValidateNegotiatedType(IMFSourceReader* reader, uint32_t expected_width,
                               uint32_t expected_height,
                               LONG* negotiated_stride) noexcept {
  if (reader == nullptr || negotiated_stride == nullptr) {
    return E_POINTER;
  }
  *negotiated_stride = 0;
  ComPtr<IMFMediaType> type;
  HRESULT result = reader->GetCurrentMediaType(
      static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
      type.GetAddressOf());
  UINT32 width = 0;
  UINT32 height = 0;
  if (SUCCEEDED(result)) {
    result = MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height);
  }
  GUID subtype{};
  if (SUCCEEDED(result)) {
    result = type->GetGUID(MF_MT_SUBTYPE, &subtype);
  }
  LONG stride = 0;
  UINT32 encoded_stride = 0;
  if (SUCCEEDED(result)) {
    result = type->GetUINT32(MF_MT_DEFAULT_STRIDE, &encoded_stride);
    if (result == MF_E_ATTRIBUTENOTFOUND) {
      result = MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1, width,
                                              &stride);
    } else if (SUCCEEDED(result)) {
      std::memcpy(&stride, &encoded_stride, sizeof(stride));
    }
  }
  const uint64_t absolute_stride =
      stride < 0 ? static_cast<uint64_t>(-(static_cast<int64_t>(stride)))
                 : static_cast<uint64_t>(stride);
  const uint64_t row_bytes = static_cast<uint64_t>(width) * 4U;
  if (SUCCEEDED(result) && subtype == MFVideoFormat_RGB32 &&
      width == expected_width && height == expected_height && stride != 0 &&
      absolute_stride >= row_bytes) {
    *negotiated_stride = stride;
    return S_OK;
  }
  return FAILED(result) ? result : MF_E_INVALIDMEDIATYPE;
}

HRESULT CopyDecodedFrame(IMFSample* sample, uint32_t width, uint32_t height,
                         LONG negotiated_stride, BgraFrame* frame) noexcept {
  if (sample == nullptr || frame == nullptr) {
    return E_POINTER;
  }
  *frame = {};
  const uint64_t row_bytes = static_cast<uint64_t>(width) * 4U;
  const uint64_t frame_bytes = row_bytes * height;
  if (width == 0 || height == 0 ||
      frame_bytes > std::numeric_limits<size_t>::max() ||
      frame_bytes > std::numeric_limits<DWORD>::max()) {
    return E_INVALIDARG;
  }
  ComPtr<IMFMediaBuffer> buffer;
  HRESULT result = sample->ConvertToContiguousBuffer(buffer.GetAddressOf());
  ComPtr<IMF2DBuffer2> buffer_2d;
  if (SUCCEEDED(result)) {
    result = buffer.As(&buffer_2d);
  }
  if (result == E_NOINTERFACE) {
    BYTE* source = nullptr;
    DWORD maximum_length = 0;
    DWORD current_length = 0;
    result = buffer->Lock(&source, &maximum_length, &current_length);
    if (FAILED(result)) {
      return result;
    }
    const auto unlock = [&buffer]() noexcept {
      static_cast<void>(buffer->Unlock());
    };
    const uint64_t absolute_stride =
        negotiated_stride < 0
            ? static_cast<uint64_t>(
                  -(static_cast<int64_t>(negotiated_stride)))
            : static_cast<uint64_t>(negotiated_stride);
    const uint64_t required =
        static_cast<uint64_t>(height - 1U) * absolute_stride + row_bytes;
    if (source == nullptr || negotiated_stride == 0 ||
        absolute_stride < row_bytes || required > current_length ||
        current_length > maximum_length) {
      unlock();
      return MF_E_INVALIDMEDIATYPE;
    }
    try {
      frame->width = width;
      frame->height = height;
      frame->pixels.resize(static_cast<size_t>(frame_bytes));
    } catch (...) {
      unlock();
      *frame = {};
      return E_OUTOFMEMORY;
    }
    const bool copied = CopyDecodedRgb32Rows(
        source, current_length, width, height,
        static_cast<ptrdiff_t>(negotiated_stride), frame->pixels.data(),
        frame->pixels.size());
    unlock();
    if (!copied) {
      *frame = {};
      return MF_E_INVALIDMEDIATYPE;
    }
    return S_OK;
  }
  BYTE* scanline = nullptr;
  LONG pitch = 0;
  BYTE* buffer_start = nullptr;
  DWORD buffer_length = 0;
  if (SUCCEEDED(result)) {
    result = buffer_2d->Lock2DSize(MF2DBuffer_LockFlags_Read, &scanline, &pitch,
                                   &buffer_start, &buffer_length);
  }
  if (FAILED(result)) {
    return result;
  }
  const auto unlock = [&buffer_2d]() noexcept {
    static_cast<void>(buffer_2d->Unlock2D());
  };
  if (scanline == nullptr || buffer_start == nullptr || pitch == 0) {
    unlock();
    return MF_E_INVALIDMEDIATYPE;
  }
  const uint64_t absolute_pitch =
      pitch < 0 ? static_cast<uint64_t>(-(static_cast<int64_t>(pitch)))
                : static_cast<uint64_t>(pitch);
  const uint64_t required =
      static_cast<uint64_t>(height - 1U) * absolute_pitch + row_bytes;
  const BYTE* expected_scanline =
      pitch < 0 ? buffer_start + (height - 1U) * absolute_pitch : buffer_start;
  if (absolute_pitch < row_bytes || required > buffer_length ||
      scanline != expected_scanline) {
    unlock();
    return MF_E_INVALIDMEDIATYPE;
  }
  try {
    frame->width = width;
    frame->height = height;
    frame->pixels.resize(static_cast<size_t>(frame_bytes));
  } catch (...) {
    unlock();
    *frame = {};
    return E_OUTOFMEMORY;
  }
  const bool copied = CopyDecodedRgb32Rows(
      buffer_start, buffer_length, width, height, static_cast<ptrdiff_t>(pitch),
      frame->pixels.data(), frame->pixels.size());
  unlock();
  if (!copied) {
    *frame = {};
    return MF_E_INVALIDMEDIATYPE;
  }
  return S_OK;
}

AnalysisEvidenceResult DecodeEvidenceFrames(
    const AnalysisEvidenceRequest& request,
    const std::filesystem::path& video_path,
    std::vector<EvidenceFrameRecord>* frames) noexcept {
  if (frames == nullptr) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  frames->clear();
  try {
    ComPtr<IMFAttributes> attributes;
    HRESULT result = MFCreateAttributes(attributes.GetAddressOf(), 2);
    if (SUCCEEDED(result)) {
      result = attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
                                     TRUE);
    }
    if (SUCCEEDED(result)) {
      result = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                                     TRUE);
    }
    ComPtr<IMFSourceReader> reader;
    if (SUCCEEDED(result)) {
      result = MFCreateSourceReaderFromURL(video_path.c_str(), attributes.Get(),
                                           reader.GetAddressOf());
    }
    if (FAILED(result)) {
      return AnalysisEvidenceResult::kDecoderFailure;
    }
    const DWORD all_streams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
    const DWORD video_stream =
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    result = reader->SetStreamSelection(all_streams, FALSE);
    if (SUCCEEDED(result)) {
      result = reader->SetStreamSelection(video_stream, TRUE);
    }
    ComPtr<IMFMediaType> decoded_type;
    if (SUCCEEDED(result)) {
      result = MFCreateMediaType(decoded_type.GetAddressOf());
    }
    if (SUCCEEDED(result)) {
      result = decoded_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    }
    if (SUCCEEDED(result)) {
      result = decoded_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    }
    if (SUCCEEDED(result)) {
      result = reader->SetCurrentMediaType(video_stream, nullptr,
                                           decoded_type.Get());
    }
    LONG negotiated_stride = 0;
    if (SUCCEEDED(result)) {
      result = ValidateNegotiatedType(
          reader.Get(), request.expected_video_width,
          request.expected_video_height, &negotiated_stride);
    }
    ComPtr<IWICImagingFactory> wic_factory;
    if (SUCCEEDED(result)) {
      result = CreateWicImagingFactory(&wic_factory);
    }
    if (FAILED(result)) {
      return AnalysisEvidenceResult::kDecoderFailure;
    }

    const uint32_t desired_count =
        std::min(request.expected_frame_count, kMaximumAnalysisEvidenceFrames);
    std::vector<uint32_t> selected_indices;
    selected_indices.reserve(desired_count);
    for (uint32_t ordinal = 0; ordinal < desired_count; ++ordinal) {
      const uint32_t index = desired_count == 1U
                                 ? 0U
                                 : static_cast<uint32_t>(
                                       static_cast<uint64_t>(ordinal) *
                                       (request.expected_frame_count - 1U) /
                                       (desired_count - 1U));
      selected_indices.push_back(index);
    }

    uint32_t decoded_count = 0;
    size_t selected_cursor = 0;
    size_t total_jpeg_bytes = 0;
    bool aggregate_full = false;
    LONGLONG previous_timestamp = -1;
    DWORD actual_video_stream = std::numeric_limits<DWORD>::max();
    for (;;) {
      DWORD stream_index = 0;
      DWORD flags = 0;
      LONGLONG timestamp = 0;
      ComPtr<IMFSample> sample;
      result = reader->ReadSample(video_stream, 0, &stream_index, &flags,
                                  &timestamp, sample.GetAddressOf());
      if (FAILED(result) || (flags & MF_SOURCE_READERF_ERROR) != 0) {
        return AnalysisEvidenceResult::kDecoderFailure;
      }
      if (actual_video_stream == std::numeric_limits<DWORD>::max()) {
        actual_video_stream = stream_index;
      } else if (stream_index != actual_video_stream) {
        return AnalysisEvidenceResult::kInvalidEvidence;
      }
      if ((flags & (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED |
                    MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) != 0) {
        result = ValidateNegotiatedType(
            reader.Get(), request.expected_video_width,
            request.expected_video_height, &negotiated_stride);
        if (FAILED(result)) {
          return AnalysisEvidenceResult::kInvalidEvidence;
        }
      }
      if (sample != nullptr) {
        if (decoded_count >= request.expected_frame_count || timestamp < 0 ||
            timestamp < previous_timestamp ||
            static_cast<uint64_t>(timestamp) >=
                request.expected_duration_ms * 10'000U) {
          return AnalysisEvidenceResult::kInvalidEvidence;
        }
        previous_timestamp = timestamp;
        if (selected_cursor < selected_indices.size() &&
            decoded_count == selected_indices[selected_cursor]) {
          if (!aggregate_full) {
            BgraFrame decoded;
            result = CopyDecodedFrame(
                sample.Get(), request.expected_video_width,
                request.expected_video_height, negotiated_stride, &decoded);
            if (FAILED(result)) {
              return AnalysisEvidenceResult::kDecoderFailure;
            }
            EvidenceFrameRecord frame;
            frame.offset_milliseconds =
                static_cast<uint64_t>(timestamp) / 10'000U;
            result = EncodeJpeg(wic_factory.Get(), decoded, &frame.jpeg_bytes);
            if (FAILED(result) || frame.jpeg_bytes.size() < 4U ||
                frame.jpeg_bytes.size() > kMaximumAnalysisEvidenceFrameBytes) {
              return AnalysisEvidenceResult::kDecoderFailure;
            }
            if (!ComputeSha256(frame.jpeg_bytes, &frame.sha256)) {
              return AnalysisEvidenceResult::kCryptoFailure;
            }
            if (total_jpeg_bytes > kMaximumAnalysisEvidenceTotalBytes -
                                       frame.jpeg_bytes.size()) {
              aggregate_full = true;
            } else {
              frame.index = static_cast<uint32_t>(frames->size());
              frame.id = FrameId(frame.index);
              frame.byte_count =
                  static_cast<uint32_t>(frame.jpeg_bytes.size());
              total_jpeg_bytes += frame.jpeg_bytes.size();
              frames->push_back(std::move(frame));
            }
          }
          ++selected_cursor;
        }
        ++decoded_count;
      }
      if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
        break;
      }
    }
    return decoded_count == request.expected_frame_count &&
                   selected_cursor == selected_indices.size() && !frames->empty()
               ? AnalysisEvidenceResult::kOk
               : AnalysisEvidenceResult::kInvalidEvidence;
  } catch (...) {
    frames->clear();
    return AnalysisEvidenceResult::kDecoderFailure;
  }
}

bool FillRandomBytes(std::span<uint8_t> bytes) noexcept {
  return !bytes.empty() &&
         bytes.size() <= std::numeric_limits<ULONG>::max() &&
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

bool WriteAll(HANDLE file, std::span<const uint8_t> bytes) noexcept {
  size_t offset = 0;
  while (offset < bytes.size()) {
    const DWORD requested = static_cast<DWORD>(std::min<size_t>(
        bytes.size() - offset, std::numeric_limits<DWORD>::max()));
    DWORD written = 0;
    if (WriteFile(file, bytes.data() + offset, requested, &written, nullptr) ==
            FALSE ||
        written == 0 || written > requested) {
      return false;
    }
    offset += written;
  }
  return true;
}

bool WriteNewFile(const std::filesystem::path& path,
                  std::span<const uint8_t> bytes) noexcept {
  const HANDLE raw = CreateFileW(
      path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW,
      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH |
          FILE_FLAG_OPEN_REPARSE_POINT,
      nullptr);
  if (raw == INVALID_HANDLE_VALUE) {
    return false;
  }
  ScopedHandle file(raw);
  return WriteAll(file.get(), bytes) && FlushFileBuffers(file.get()) != FALSE;
}

AnalysisEvidenceResult RenameDirectoryByHandle(
    HANDLE directory, std::wstring_view destination_name) {
  if (directory == nullptr || directory == INVALID_HANDLE_VALUE ||
      destination_name.empty() ||
      destination_name.size() >
          std::numeric_limits<DWORD>::max() / sizeof(wchar_t)) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  const size_t name_bytes = destination_name.size() * sizeof(wchar_t);
  const size_t buffer_size =
      offsetof(FILE_RENAME_INFO, FileName) + name_bytes + sizeof(wchar_t);
  if (buffer_size > std::numeric_limits<DWORD>::max()) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  std::vector<uint8_t> buffer(buffer_size, 0);
  auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(buffer.data());
  rename->ReplaceIfExists = FALSE;
  rename->RootDirectory = nullptr;
  rename->FileNameLength = static_cast<DWORD>(name_bytes);
  std::memcpy(rename->FileName, destination_name.data(), name_bytes);
  if (SetFileInformationByHandle(directory, FileRenameInfo, rename,
                                 static_cast<DWORD>(buffer.size())) == FALSE) {
    return IsCollisionError(GetLastError())
               ? AnalysisEvidenceResult::kConflict
               : AnalysisEvidenceResult::kIoFailure;
  }
  return AnalysisEvidenceResult::kOk;
}

void RemoveStagingDirectory(const std::filesystem::path& staging,
                            size_t frame_count) noexcept {
  static_cast<void>(DeleteFileW((staging / L"manifest.json").c_str()));
  for (size_t index = 0; index < frame_count; ++index) {
    static_cast<void>(DeleteFileW(
        (staging / FrameFileName(static_cast<uint32_t>(index))).c_str()));
  }
  static_cast<void>(RemoveDirectoryW(staging.c_str()));
}

AnalysisEvidenceResult EnumerateEvidenceFiles(
    const std::filesystem::path& directory, size_t frame_count) {
  std::set<std::wstring> expected{L"manifest.json"};
  for (size_t index = 0; index < frame_count; ++index) {
    expected.insert(FrameFileName(static_cast<uint32_t>(index)).native());
  }
  std::set<std::wstring> actual;
  WIN32_FIND_DATAW data{};
  HANDLE find = FindFirstFileW((directory / L"*").c_str(), &data);
  if (find == INVALID_HANDLE_VALUE) {
    return AnalysisEvidenceResult::kIoFailure;
  }
  bool valid = true;
  do {
    const std::wstring_view name(data.cFileName);
    if (name == L"." || name == L"..") {
      continue;
    }
    if (HasUnsafeAttributes(data.dwFileAttributes) ||
        (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
        !actual.insert(std::wstring(name)).second) {
      valid = false;
      break;
    }
  } while (FindNextFileW(find, &data) != FALSE);
  const DWORD error = GetLastError();
  static_cast<void>(FindClose(find));
  return valid && error == ERROR_NO_MORE_FILES && actual == expected
             ? AnalysisEvidenceResult::kOk
             : AnalysisEvidenceResult::kConflict;
}

AnalysisEvidenceResult PrepareEvidenceParent(
    const std::filesystem::path& data_root, std::string_view chunk_id,
    std::vector<ScopedHandle>* locks, std::filesystem::path* parent) {
  if (locks == nullptr || parent == nullptr) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  const std::filesystem::path evidence_root = data_root / L"evidence";
  PathResult result = EnsureAndLockChildDirectory(evidence_root, locks);
  if (result != PathResult::kOk) {
    return MapPathResult(result);
  }
  const std::filesystem::path policy_root =
      evidence_root / std::wstring(kPolicyVersion.begin(), kPolicyVersion.end());
  result = EnsureAndLockChildDirectory(policy_root, locks);
  if (result != PathResult::kOk) {
    return MapPathResult(result);
  }
  const std::filesystem::path chunk_root =
      policy_root / std::wstring(chunk_id.begin(), chunk_id.end());
  result = EnsureAndLockChildDirectory(chunk_root, locks);
  if (result != PathResult::kOk) {
    return MapPathResult(result);
  }
  *parent = chunk_root;
  return AnalysisEvidenceResult::kOk;
}

AnalysisEvidenceResult ReadAndParseManifest(
    const std::filesystem::path& final_directory, std::string_view chunk_id,
    std::string_view fingerprint, uint64_t maximum_duration_ms,
    ParsedEvidenceManifest* parsed, std::string* manifest_utf8) {
  std::vector<uint8_t> bytes;
  AnalysisEvidenceResult result = ReadFileFully(
      final_directory / L"manifest.json",
      kMaximumAnalysisEvidenceManifestBytes, &bytes);
  if (result != AnalysisEvidenceResult::kOk) {
    return result;
  }
  std::string json(bytes.begin(), bytes.end());
  ParsedEvidenceManifest value;
  if (!ParseEvidenceManifest(json, &value) ||
      !ValidateParsedManifest(value, chunk_id, fingerprint,
                              maximum_duration_ms)) {
    return AnalysisEvidenceResult::kConflict;
  }
  if (parsed != nullptr) {
    *parsed = std::move(value);
  }
  if (manifest_utf8 != nullptr) {
    *manifest_utf8 = std::move(json);
  }
  return AnalysisEvidenceResult::kOk;
}

AnalysisEvidenceResult ValidatePublishedEvidence(
    const std::filesystem::path& parent, std::string_view chunk_id,
    std::string_view fingerprint, uint64_t maximum_duration_ms,
    IWICImagingFactory* factory, std::string* manifest_utf8) {
  const std::filesystem::path final_directory =
      parent / std::wstring(fingerprint.begin(), fingerprint.end());
  std::vector<ScopedHandle> final_lock;
  PathResult opened = LockChildDirectory(
      final_directory, &final_lock,
      FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
      FILE_SHARE_READ);
  if (opened != PathResult::kOk) {
    return MapPathResult(opened);
  }
  ParsedEvidenceManifest parsed;
  std::string validated_manifest;
  AnalysisEvidenceResult result = ReadAndParseManifest(
      final_directory, chunk_id, fingerprint, maximum_duration_ms, &parsed,
      &validated_manifest);
  if (result != AnalysisEvidenceResult::kOk) {
    return result;
  }
  result = EnumerateEvidenceFiles(final_directory, parsed.frames.size());
  if (result != AnalysisEvidenceResult::kOk) {
    return result;
  }
  for (const EvidenceFrameRecord& frame : parsed.frames) {
    std::vector<uint8_t> bytes;
    result = ReadFileFully(final_directory / FrameFileName(frame.index),
                           kMaximumAnalysisEvidenceFrameBytes, &bytes);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    std::string sha256;
    if (!ComputeSha256(bytes, &sha256)) {
      return AnalysisEvidenceResult::kCryptoFailure;
    }
    if (bytes.size() != frame.byte_count || sha256 != frame.sha256 ||
        FAILED(ValidateJpegWithWic(factory, bytes))) {
      return AnalysisEvidenceResult::kConflict;
    }
  }
  result = EnumerateEvidenceFiles(final_directory, parsed.frames.size());
  if (result == AnalysisEvidenceResult::kOk && manifest_utf8 != nullptr) {
    *manifest_utf8 = std::move(validated_manifest);
  }
  return result;
}

AnalysisEvidenceResult PublishEvidence(
    const AnalysisEvidenceRequest& request,
    const std::filesystem::path& parent,
    const std::vector<EvidenceFrameRecord>& frames,
    std::string_view manifest_utf8) {
  std::filesystem::path staging;
  ScopedHandle staging_handle;
  for (uint32_t attempt = 0; attempt < 16U; ++attempt) {
    std::array<uint8_t, kStagingNonceBytes> nonce{};
    if (!FillRandomBytes(nonce)) {
      return AnalysisEvidenceResult::kIoFailure;
    }
    staging = parent / (L".staging-" + HexNonce(nonce));
    if (CreateDirectoryW(staging.c_str(), nullptr) == FALSE) {
      if (IsCollisionError(GetLastError())) {
        continue;
      }
      return AnalysisEvidenceResult::kIoFailure;
    }
    const PathResult opened = OpenDirectoryNoFollow(
        staging, FILE_READ_ATTRIBUTES | DELETE | SYNCHRONIZE,
        kDirectoryShareMode, &staging_handle);
    if (opened != PathResult::kOk) {
      static_cast<void>(RemoveDirectoryW(staging.c_str()));
      return MapPathResult(opened);
    }
    break;
  }
  if (!staging_handle.valid()) {
    return AnalysisEvidenceResult::kIoFailure;
  }

  for (const EvidenceFrameRecord& frame : frames) {
    if (!WriteNewFile(staging / FrameFileName(frame.index), frame.jpeg_bytes)) {
      staging_handle.Reset();
      RemoveStagingDirectory(staging, frames.size());
      return AnalysisEvidenceResult::kIoFailure;
    }
  }
  const std::span<const uint8_t> manifest_bytes(
      reinterpret_cast<const uint8_t*>(manifest_utf8.data()),
      manifest_utf8.size());
  if (!WriteNewFile(staging / L"manifest.json", manifest_bytes)) {
    staging_handle.Reset();
    RemoveStagingDirectory(staging, frames.size());
    return AnalysisEvidenceResult::kIoFailure;
  }
  const std::filesystem::path destination =
      parent / std::wstring(request.expected_source_fingerprint.begin(),
                            request.expected_source_fingerprint.end());
  const AnalysisEvidenceResult renamed =
      RenameDirectoryByHandle(staging_handle.get(), destination.wstring());
  staging_handle.Reset();
  if (renamed != AnalysisEvidenceResult::kOk) {
    RemoveStagingDirectory(staging, frames.size());
  }
  return renamed;
}

bool IsValidRequest(const AnalysisEvidenceRequest& request) noexcept {
  return IsCanonicalCaptureChunkId(request.canonical_chunk_id) &&
         IsCanonicalSourceFingerprint(request.expected_source_fingerprint) &&
         request.expected_video_byte_count > 0 &&
         request.expected_video_byte_count <= kMaximumFingerprintVideoBytes &&
         request.expected_frame_count > 0 &&
         request.expected_frame_count <= kMaximumSourceFrames &&
         request.expected_video_width >= 2U &&
         request.expected_video_width <= kMaximumVideoWidth &&
         (request.expected_video_width & 1U) == 0 &&
         request.expected_video_height >= 2U &&
         request.expected_video_height <= kMaximumVideoHeight &&
         (request.expected_video_height & 1U) == 0 &&
         request.expected_duration_ms > 0 &&
         request.expected_duration_ms <= kMaximumDurationMilliseconds;
}

}  // namespace

bool IsCanonicalSourceFingerprint(std::string_view value) noexcept {
  return value.size() == kCaptureChunkFingerprintHexLength &&
         std::all_of(value.begin(), value.end(), [](char character) {
           return (character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F');
         });
}

AnalysisEvidenceResult ExtractAnalysisEvidence(
    const AnalysisEvidenceRequest& request,
    std::string* manifest_utf8) noexcept {
  if (manifest_utf8 == nullptr) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  manifest_utf8->clear();
  if (!IsValidRequest(request)) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  try {
    LockedCaptureSource source;
    AnalysisEvidenceResult result = source.Open(request);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    result = source.VerifyFingerprint(request);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    MediaRuntime runtime;
    if (FAILED(runtime.Start())) {
      return AnalysisEvidenceResult::kDecoderFailure;
    }
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CreateWicImagingFactory(&factory))) {
      return AnalysisEvidenceResult::kDecoderFailure;
    }
    std::vector<ScopedHandle> evidence_locks;
    std::filesystem::path parent;
    result = PrepareEvidenceParent(source.data_root(), request.canonical_chunk_id,
                                   &evidence_locks, &parent);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    result = ValidatePublishedEvidence(
        parent, request.canonical_chunk_id,
        request.expected_source_fingerprint, request.expected_duration_ms,
        factory.Get(), manifest_utf8);
    if (result == AnalysisEvidenceResult::kOk) {
      const AnalysisEvidenceResult stable = source.VerifyStable();
      if (stable != AnalysisEvidenceResult::kOk) {
        manifest_utf8->clear();
        return stable;
      }
      result = source.VerifyFingerprint(request);
      if (result != AnalysisEvidenceResult::kOk) {
        manifest_utf8->clear();
      }
      return result;
    }
    if (result != AnalysisEvidenceResult::kNotFound) {
      return result;
    }

    std::vector<EvidenceFrameRecord> frames;
    result = DecodeEvidenceFrames(request, source.video_path(), &frames);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    result = source.VerifyStable();
    if (result == AnalysisEvidenceResult::kOk) {
      result = source.VerifyFingerprint(request);
    }
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    const std::string generated = SerializeEvidenceManifest(request, frames);
    if (generated.empty() ||
        generated.size() > kMaximumAnalysisEvidenceManifestBytes) {
      return AnalysisEvidenceResult::kTooLarge;
    }
    result = PublishEvidence(request, parent, frames, generated);
    if (result != AnalysisEvidenceResult::kOk &&
        result != AnalysisEvidenceResult::kConflict) {
      return result;
    }
    result = ValidatePublishedEvidence(
        parent, request.canonical_chunk_id,
        request.expected_source_fingerprint, request.expected_duration_ms,
        factory.Get(), manifest_utf8);
    if (result != AnalysisEvidenceResult::kOk) {
      manifest_utf8->clear();
      return result;
    }
    result = source.VerifyStable();
    if (result == AnalysisEvidenceResult::kOk) {
      result = source.VerifyFingerprint(request);
    }
    if (result != AnalysisEvidenceResult::kOk) {
      manifest_utf8->clear();
    }
    return result;
  } catch (...) {
    manifest_utf8->clear();
    return AnalysisEvidenceResult::kIoFailure;
  }
}

AnalysisEvidenceResult ReadAnalysisEvidenceFrame(
    const std::wstring& data_root, std::string_view canonical_chunk_id,
    std::string_view canonical_source_fingerprint, uint32_t frame_index,
    std::vector<uint8_t>* jpeg_bytes) noexcept {
  if (jpeg_bytes == nullptr) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  jpeg_bytes->clear();
  if (!IsCanonicalCaptureChunkId(canonical_chunk_id) ||
      !IsCanonicalSourceFingerprint(canonical_source_fingerprint) ||
      frame_index >= kMaximumAnalysisEvidenceFrames) {
    return AnalysisEvidenceResult::kInvalidArgument;
  }
  try {
    std::filesystem::path root;
    if (!TryNormalizeLocalAbsoluteRoot(data_root, &root)) {
      return AnalysisEvidenceResult::kInvalidArgument;
    }
    std::vector<ScopedHandle> locks;
    PathResult opened = LockDirectoryChain(root, &locks);
    if (opened != PathResult::kOk) {
      return MapPathResult(opened);
    }
    const std::filesystem::path evidence_root = root / L"evidence";
    opened = LockChildDirectory(evidence_root, &locks);
    if (opened != PathResult::kOk) {
      return MapPathResult(opened);
    }
    const std::filesystem::path policy_root = evidence_root / L"evidence-v1";
    opened = LockChildDirectory(policy_root, &locks);
    if (opened != PathResult::kOk) {
      return MapPathResult(opened);
    }
    const std::filesystem::path chunk_root =
        policy_root / std::wstring(canonical_chunk_id.begin(),
                                   canonical_chunk_id.end());
    opened = LockChildDirectory(chunk_root, &locks);
    if (opened != PathResult::kOk) {
      return MapPathResult(opened);
    }
    const std::filesystem::path final_directory =
        chunk_root /
        std::wstring(canonical_source_fingerprint.begin(),
                     canonical_source_fingerprint.end());
    opened = LockChildDirectory(final_directory, &locks);
    if (opened != PathResult::kOk) {
      return MapPathResult(opened);
    }
    ParsedEvidenceManifest manifest;
    AnalysisEvidenceResult result = ReadAndParseManifest(
        final_directory, canonical_chunk_id, canonical_source_fingerprint,
        kMaximumDurationMilliseconds, &manifest, nullptr);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    if (frame_index >= manifest.frames.size()) {
      return AnalysisEvidenceResult::kInvalidArgument;
    }
    result = ReadFileFully(final_directory / FrameFileName(frame_index),
                           kMaximumAnalysisEvidenceFrameBytes, jpeg_bytes);
    if (result != AnalysisEvidenceResult::kOk) {
      return result;
    }
    if (jpeg_bytes->size() != manifest.frames[frame_index].byte_count ||
        !IsJpeg(*jpeg_bytes)) {
      jpeg_bytes->clear();
      return AnalysisEvidenceResult::kConflict;
    }
    std::string sha256;
    if (!ComputeSha256(*jpeg_bytes, &sha256)) {
      jpeg_bytes->clear();
      return AnalysisEvidenceResult::kCryptoFailure;
    }
    if (sha256 != manifest.frames[frame_index].sha256) {
      jpeg_bytes->clear();
      return AnalysisEvidenceResult::kConflict;
    }
    MediaRuntime runtime;
    if (FAILED(runtime.Start())) {
      jpeg_bytes->clear();
      return AnalysisEvidenceResult::kDecoderFailure;
    }
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CreateWicImagingFactory(&factory)) ||
        FAILED(ValidateJpegWithWic(factory.Get(), *jpeg_bytes))) {
      jpeg_bytes->clear();
      return AnalysisEvidenceResult::kConflict;
    }
    return AnalysisEvidenceResult::kOk;
  } catch (...) {
    jpeg_bytes->clear();
    return AnalysisEvidenceResult::kIoFailure;
  }
}

}  // namespace windayflow::capture

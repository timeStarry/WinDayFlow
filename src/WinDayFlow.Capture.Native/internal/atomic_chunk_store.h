// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Heavily modified for transactional WinDayFlow JPEG-frame publication; see
// THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_ATOMIC_CHUNK_STORE_H_
#define WINDAYFLOW_ATOMIC_CHUNK_STORE_H_

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <string_view>

namespace windayflow::capture {

inline constexpr size_t kMaximumChunkFrameBytes = 64U * 1024U * 1024U;
inline constexpr size_t kMaximumChunkFrameFileBytes = 2U * 1024U * 1024U;
inline constexpr size_t kMaximumChunkManifestBytes = 64U * 1024U;

enum class AtomicChunkStoreResult {
  kOk,
  kInvalidArgument,
  kInvalidRoot,
  kReparsePoint,
  kAlreadyExists,
  kIoFailure,
};

struct ChunkFrameManifest;
struct ChunkManifest;
class AtomicChunkState;

using AtomicChunkStorePrepareCheckpoint = void (*)();

class AtomicChunkPublication {
 public:
  AtomicChunkPublication();
  ~AtomicChunkPublication();

  AtomicChunkPublication(const AtomicChunkPublication&) = delete;
  AtomicChunkPublication& operator=(const AtomicChunkPublication&) = delete;
  AtomicChunkPublication(AtomicChunkPublication&& other) noexcept;
  AtomicChunkPublication& operator=(AtomicChunkPublication&& other) = delete;

  explicit operator bool() const noexcept;
  bool committed() const noexcept;
  const std::string& artifact_identifier() const noexcept;

  AtomicChunkStoreResult Commit() noexcept;
  void Acknowledge() noexcept;
  AtomicChunkStoreResult Rollback() noexcept;

 private:
  friend class AtomicChunkWriter;
  std::unique_ptr<AtomicChunkState> state_;
};

class AtomicChunkWriter {
 public:
  AtomicChunkWriter();
  ~AtomicChunkWriter();

  AtomicChunkWriter(const AtomicChunkWriter&) = delete;
  AtomicChunkWriter& operator=(const AtomicChunkWriter&) = delete;
  AtomicChunkWriter(AtomicChunkWriter&& other) noexcept;
  AtomicChunkWriter& operator=(AtomicChunkWriter&& other) = delete;

  explicit operator bool() const noexcept;
  const std::string& chunk_id() const noexcept;

  AtomicChunkStoreResult AppendFrame(
      const ChunkFrameManifest& frame,
      std::span<const uint8_t> jpeg_bytes) noexcept;
  AtomicChunkStoreResult Prepare(
      const ChunkManifest& manifest,
      AtomicChunkPublication* publication) noexcept;
  AtomicChunkStoreResult Reset() noexcept;

 private:
  friend class AtomicChunkStore;
  std::unique_ptr<AtomicChunkState> state_;
};

class AtomicChunkStore {
 public:
  explicit AtomicChunkStore(
      std::wstring output_root,
      AtomicChunkStorePrepareCheckpoint prepare_checkpoint = nullptr);

  AtomicChunkStoreResult Begin(
      std::string_view artifact_id,
      AtomicChunkWriter* writer) const noexcept;

 private:
  std::wstring output_root_;
  AtomicChunkStorePrepareCheckpoint prepare_checkpoint_ = nullptr;
};

bool IsValidChunkArtifactId(std::string_view value) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_ATOMIC_CHUNK_STORE_H_

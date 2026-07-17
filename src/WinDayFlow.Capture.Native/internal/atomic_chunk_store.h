// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Heavily
// modified for transactional WinDayFlow publication; see
// THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_ATOMIC_CHUNK_STORE_H_
#define WINDAYFLOW_ATOMIC_CHUNK_STORE_H_

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <span>
#include <string>
#include <string_view>

namespace windayflow::capture {

inline constexpr size_t kMaximumEncodedChunkBytes = 64U * 1024U * 1024U;
inline constexpr size_t kMaximumChunkManifestBytes = 64U * 1024U;

enum class AtomicChunkStoreResult {
  kOk,
  kInvalidArgument,
  kInvalidRoot,
  kReparsePoint,
  kAlreadyExists,
  kIoFailure,
};

struct ChunkManifest;

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
  friend class AtomicChunkStore;

  class State;
  std::unique_ptr<State> state_;
};

class AtomicChunkStore {
 public:
  explicit AtomicChunkStore(
      std::wstring output_root,
      AtomicChunkStorePrepareCheckpoint prepare_checkpoint = nullptr);

  AtomicChunkStoreResult Prepare(
      std::string_view artifact_id, std::span<const uint8_t> encoded_mp4,
      const ChunkManifest& manifest,
      AtomicChunkPublication* publication) const noexcept;

 private:
  std::wstring output_root_;
  AtomicChunkStorePrepareCheckpoint prepare_checkpoint_ = nullptr;
};

bool IsValidChunkArtifactId(std::string_view value) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_ATOMIC_CHUNK_STORE_H_

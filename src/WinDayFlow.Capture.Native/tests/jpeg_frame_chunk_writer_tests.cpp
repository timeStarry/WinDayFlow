#include <Windows.h>
#include <bcrypt.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "jpeg_frame_chunk_writer.h"
#include "wic_jpeg_encoder.h"

namespace {

using Microsoft::WRL::ComPtr;
using windayflow::capture::AtomicChunkPublication;
using windayflow::capture::AtomicChunkStoreResult;
using windayflow::capture::ChunkManifest;
using windayflow::capture::JpegFrameChunkWriter;
using windayflow::capture::JpegFrameChunkWriterConfig;
using windayflow::capture::JpegFrameChunkWriterResult;

constexpr uint32_t kWidth = 64;
constexpr uint32_t kHeight = 36;

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
         (L"WinDayFlow-JpegFrameWriter-" + suffix);
}

class ScopedTestRoot final {
 public:
  ScopedTestRoot() : path_(UniqueTestRoot()) {}
  ~ScopedTestRoot() {
    std::error_code ignored;
    std::filesystem::remove_all(path_, ignored);
  }

  const std::filesystem::path& path() const noexcept { return path_; }

 private:
  std::filesystem::path path_;
};

class ScopedCom final {
 public:
  ScopedCom()
      : result_(CoInitializeEx(nullptr,
                              COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE)),
        uninitialize_(SUCCEEDED(result_)) {}
  ~ScopedCom() {
    factory_.Reset();
    if (uninitialize_) {
      CoUninitialize();
    }
  }

  bool Initialize() {
    if (FAILED(result_) && result_ != RPC_E_CHANGED_MODE) {
      return false;
    }
    return SUCCEEDED(CoCreateInstance(
        CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(factory_.ReleaseAndGetAddressOf())));
  }

  IWICImagingFactory* factory() const noexcept { return factory_.Get(); }

 private:
  HRESULT result_;
  bool uninitialize_;
  ComPtr<IWICImagingFactory> factory_;
};

std::vector<uint8_t> SolidFrame(uint8_t blue, uint8_t green, uint8_t red) {
  std::vector<uint8_t> pixels(
      static_cast<size_t>(kWidth) * kHeight * 4U);
  for (size_t offset = 0; offset < pixels.size(); offset += 4U) {
    pixels[offset] = blue;
    pixels[offset + 1U] = green;
    pixels[offset + 2U] = red;
    pixels[offset + 3U] = 0xFFU;
  }
  return pixels;
}

ChunkManifest EmptyManifest(std::string chunk_id, uint32_t captured_count) {
  return ChunkManifest{std::move(chunk_id),
                       1'784'269'200'000,
                       1'784'269'205'000,
                       captured_count,
                       kWidth,
                       kHeight,
                       0,
                       7,
                       11,
                       false,
                       {}};
}

std::vector<uint8_t> ReadBytes(const std::filesystem::path& path) {
  std::ifstream input(path, std::ios::binary);
  return std::vector<uint8_t>(std::istreambuf_iterator<char>(input), {});
}

bool TestEncoderProducesBoundedJpeg() {
  ScopedCom com;
  if (!Expect(com.Initialize(), "WIC factory setup failed")) {
    return false;
  }
  std::vector<uint8_t> jpeg;
  const std::vector<uint8_t> frame = SolidFrame(10, 80, 170);
  const HRESULT result = windayflow::capture::EncodeBgraFrameAsJpeg(
      com.factory(), frame, kWidth, kHeight, 0.82F, 64U * 1024U, &jpeg);
  return Expect(SUCCEEDED(result) && jpeg.size() >= 4U &&
                    jpeg.size() <= 64U * 1024U && jpeg[0] == 0xFFU &&
                    jpeg[1] == 0xD8U && jpeg[jpeg.size() - 2U] == 0xFFU &&
                    jpeg.back() == 0xD9U,
                "WIC encoder did not produce a bounded JPEG");
}

bool TestDuplicateRunRetainsChunkEndpoints() {
  ScopedCom com;
  ScopedTestRoot root;
  if (!Expect(com.Initialize() && !root.path().empty(),
              "duplicate test setup failed")) {
    return false;
  }
  JpegFrameChunkWriter writer(root.path().wstring());
  const JpegFrameChunkWriterConfig config{kWidth, kHeight};
  const std::vector<uint8_t> frame = SolidFrame(20, 40, 60);
  if (!Expect(writer.Begin(com.factory(), "chunk-duplicates", config) ==
                  JpegFrameChunkWriterResult::kOk &&
                  writer.AddFrame(frame, 0) ==
                      JpegFrameChunkWriterResult::kOk &&
                  writer.AddFrame(frame, 1'000) ==
                      JpegFrameChunkWriterResult::kOk &&
                  writer.AddFrame(frame, 2'000) ==
                      JpegFrameChunkWriterResult::kOk,
              "duplicate frame run could not be recorded")) {
    return false;
  }
  ChunkManifest manifest = EmptyManifest("chunk-duplicates", 3);
  AtomicChunkPublication publication;
  if (!Expect(writer.Finalize(&manifest, &publication) ==
                  JpegFrameChunkWriterResult::kOk &&
                  manifest.frames.size() == 2U &&
                  manifest.frames[0].offset_milliseconds == 0 &&
                  manifest.frames[1].offset_milliseconds == 2'000 &&
                  publication.Commit() == AtomicChunkStoreResult::kOk,
              "duplicate run did not retain exactly its chunk endpoints")) {
    return false;
  }
  const auto directory =
      root.path() / L"chunks" / L"chunk-duplicates" / L"frames";
  const auto first = ReadBytes(directory / L"frame-000000.jpg");
  const auto last = ReadBytes(directory / L"frame-000001.jpg");
  publication.Acknowledge();
  return Expect(first.size() == manifest.frames[0].byte_count &&
                    last.size() == manifest.frames[1].byte_count &&
                    first[0] == 0xFFU && first[1] == 0xD8U &&
                    last[last.size() - 2U] == 0xFFU && last.back() == 0xD9U,
                "published endpoint JPEG files were invalid");
}

bool TestChangedFramesAreAllRetained() {
  ScopedCom com;
  ScopedTestRoot root;
  if (!Expect(com.Initialize() && !root.path().empty(),
              "changed-frame test setup failed")) {
    return false;
  }
  JpegFrameChunkWriter writer(root.path().wstring());
  const JpegFrameChunkWriterConfig config{kWidth, kHeight};
  const std::vector<uint8_t> first = SolidFrame(0, 0, 0);
  const std::vector<uint8_t> second = SolidFrame(255, 0, 0);
  const std::vector<uint8_t> third = SolidFrame(0, 255, 255);
  ChunkManifest manifest = EmptyManifest("chunk-changes", 3);
  AtomicChunkPublication publication;
  return Expect(writer.Begin(com.factory(), manifest.chunk_id, config) ==
                    JpegFrameChunkWriterResult::kOk &&
                    writer.AddFrame(first, 0) ==
                        JpegFrameChunkWriterResult::kOk &&
                    writer.AddFrame(second, 1'000) ==
                        JpegFrameChunkWriterResult::kOk &&
                    writer.AddFrame(third, 2'000) ==
                        JpegFrameChunkWriterResult::kOk &&
                    writer.Finalize(&manifest, &publication) ==
                        JpegFrameChunkWriterResult::kOk &&
                    manifest.frames.size() == 3U &&
                    manifest.frames[0].index == 0 &&
                    manifest.frames[1].index == 1 &&
                    manifest.frames[2].index == 2 &&
                    publication.Rollback() == AtomicChunkStoreResult::kOk,
                "meaningfully changed screenshots were deduplicated");
}

bool TestResetRemovesStagingData() {
  ScopedCom com;
  ScopedTestRoot root;
  if (!Expect(com.Initialize() && !root.path().empty(),
              "reset test setup failed")) {
    return false;
  }
  JpegFrameChunkWriter writer(root.path().wstring());
  const JpegFrameChunkWriterConfig config{kWidth, kHeight};
  const std::vector<uint8_t> frame = SolidFrame(1, 2, 3);
  const auto staging = root.path() / L".staging";
  return Expect(writer.Begin(com.factory(), "chunk-reset", config) ==
                    JpegFrameChunkWriterResult::kOk &&
                    writer.AddFrame(frame, 0) ==
                        JpegFrameChunkWriterResult::kOk &&
                    writer.Reset() == JpegFrameChunkWriterResult::kOk &&
                    std::filesystem::exists(staging) &&
                    std::filesystem::is_empty(staging),
                "writer reset left private staging data behind");
}

}  // namespace

int main() {
  if (!TestEncoderProducesBoundedJpeg() ||
      !TestDuplicateRunRetainsChunkEndpoints() ||
      !TestChangedFramesAreAllRetained() ||
      !TestResetRemovesStagingData()) {
    return 1;
  }
  std::cout << "JPEG frame chunk writer tests passed\n";
  return 0;
}

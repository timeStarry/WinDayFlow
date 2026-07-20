#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <objidl.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <limits>
#include <span>
#include <thread>
#include <vector>

#include "mf_h264_chunk_writer.h"

namespace {

using Microsoft::WRL::ComPtr;
using windayflow::capture::MfH264ChunkWriter;
using windayflow::capture::MfH264ChunkWriterConfig;
using windayflow::capture::MfH264ChunkWriterState;

constexpr DWORD kSourceReaderAllStreams =
    static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD kSourceReaderFirstVideoStream =
    static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

bool ExpectSucceeded(HRESULT result, const char* operation) {
  if (SUCCEEDED(result)) {
    return true;
  }
  std::cerr << operation << " failed: 0x" << std::hex
            << static_cast<uint32_t>(result) << std::dec << '\n';
  return false;
}

class MediaFoundationTestRuntime final {
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

  ~MediaFoundationTestRuntime() {
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

MfH264ChunkWriterConfig ValidConfig() {
  MfH264ChunkWriterConfig config;
  config.width = 64;
  config.height = 48;
  config.frame_rate_numerator = 10;
  config.frame_rate_denominator = 1;
  config.average_bitrate = 2'500'000;
  return config;
}

std::vector<uint8_t> MakeFrame(uint32_t width, uint32_t height,
                               uint8_t frame_index) {
  std::vector<uint8_t> pixels(static_cast<size_t>(width) *
                              static_cast<size_t>(height) * 4U);
  for (uint32_t y = 0; y < height; ++y) {
    for (uint32_t x = 0; x < width; ++x) {
      const size_t offset = (static_cast<size_t>(y) * width + x) * 4U;
      pixels[offset] =
          static_cast<uint8_t>((x * 3U + frame_index * 17U) & 0xFFU);
      pixels[offset + 1U] =
          static_cast<uint8_t>((y * 5U + frame_index * 29U) & 0xFFU);
      pixels[offset + 2U] =
          static_cast<uint8_t>(((x + y) * 2U + frame_index * 41U) & 0xFFU);
      pixels[offset + 3U] = 0xFFU;
    }
  }
  return pixels;
}

HRESULT CreateByteStream(std::span<const uint8_t> bytes,
                         IMFByteStream** byte_stream) {
  if (byte_stream == nullptr) {
    return E_POINTER;
  }
  *byte_stream = nullptr;
  if (bytes.empty() || bytes.size() > std::numeric_limits<ULONG>::max()) {
    return E_INVALIDARG;
  }

  ComPtr<IStream> stream;
  HRESULT result = CreateStreamOnHGlobal(nullptr, TRUE, stream.GetAddressOf());
  if (FAILED(result)) {
    return result;
  }
  ULONG written = 0;
  result =
      stream->Write(bytes.data(), static_cast<ULONG>(bytes.size()), &written);
  if (FAILED(result) || written != bytes.size()) {
    return FAILED(result) ? result : STG_E_MEDIUMFULL;
  }
  LARGE_INTEGER beginning{};
  result = stream->Seek(beginning, STREAM_SEEK_SET, nullptr);
  if (FAILED(result)) {
    return result;
  }
  return MFCreateMFByteStreamOnStream(stream.Get(), byte_stream);
}

HRESULT ValidateDecodableMp4(std::span<const uint8_t> mp4,
                             uint32_t expected_width, uint32_t expected_height,
                             uint32_t expected_frames) {
  ComPtr<IMFByteStream> byte_stream;
  HRESULT result = CreateByteStream(mp4, byte_stream.GetAddressOf());
  if (FAILED(result)) {
    return result;
  }

  ComPtr<IMFAttributes> reader_attributes;
  result = MFCreateAttributes(reader_attributes.GetAddressOf(), 2);
  if (SUCCEEDED(result)) {
    result = reader_attributes->SetUINT32(
        MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
  }
  if (SUCCEEDED(result)) {
    result = reader_attributes->SetUINT32(
        MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
  }
  ComPtr<IMFSourceReader> reader;
  if (SUCCEEDED(result)) {
    result = MFCreateSourceReaderFromByteStream(
        byte_stream.Get(), reader_attributes.Get(), reader.GetAddressOf());
  }
  if (FAILED(result)) {
    return result;
  }
  result = reader->SetStreamSelection(kSourceReaderAllStreams, FALSE);
  if (SUCCEEDED(result)) {
    result = reader->SetStreamSelection(kSourceReaderFirstVideoStream, TRUE);
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
    result = reader->SetCurrentMediaType(kSourceReaderFirstVideoStream, nullptr,
                                         decoded_type.Get());
  }
  if (FAILED(result)) {
    return result;
  }

  ComPtr<IMFMediaType> negotiated_type;
  result = reader->GetCurrentMediaType(kSourceReaderFirstVideoStream,
                                       negotiated_type.GetAddressOf());
  UINT32 decoded_width = 0;
  UINT32 decoded_height = 0;
  if (SUCCEEDED(result)) {
    result = MFGetAttributeSize(negotiated_type.Get(), MF_MT_FRAME_SIZE,
                                &decoded_width, &decoded_height);
  }
  if (FAILED(result)) {
    return result;
  }
  if (decoded_width != expected_width || decoded_height != expected_height) {
    return MF_E_INVALIDMEDIATYPE;
  }

  uint32_t decoded_frames = 0;
  bool saw_nonempty_sample = false;
  for (;;) {
    DWORD stream_index = 0;
    DWORD flags = 0;
    LONGLONG timestamp = 0;
    ComPtr<IMFSample> sample;
    result = reader->ReadSample(kSourceReaderFirstVideoStream, 0, &stream_index,
                                &flags, &timestamp, sample.GetAddressOf());
    if (FAILED(result)) {
      return result;
    }
    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
      break;
    }
    if (sample == nullptr) {
      continue;
    }
    ComPtr<IMFMediaBuffer> buffer;
    result = sample->ConvertToContiguousBuffer(buffer.GetAddressOf());
    DWORD length = 0;
    if (SUCCEEDED(result)) {
      result = buffer->GetCurrentLength(&length);
    }
    if (FAILED(result)) {
      return result;
    }
    saw_nonempty_sample = saw_nonempty_sample || length != 0;
    ++decoded_frames;
  }

  return decoded_frames == expected_frames && saw_nonempty_sample ? S_OK
                                                                  : E_FAIL;
}

bool TestStateAndArgumentValidation() {
  MfH264ChunkWriter writer;
  const MfH264ChunkWriterConfig config = ValidConfig();
  const std::vector<uint8_t> frame = MakeFrame(config.width, config.height, 0);
  std::vector<uint8_t> output = {1, 2, 3};

  MfH264ChunkWriterConfig invalid = config;
  invalid.width = 63;
  if (!Expect(writer.state() == MfH264ChunkWriterState::kIdle,
              "new writer was not idle") ||
      !Expect(FAILED(writer.AddFrame(frame, 0)),
              "frame before Begin was accepted") ||
      !Expect(writer.Begin(invalid) == E_INVALIDARG,
              "odd video dimensions were accepted") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kIdle,
              "invalid Begin changed writer state") ||
      !ExpectSucceeded(writer.Begin(config), "Begin") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kWriting,
              "Begin did not enter Writing") ||
      !Expect(FAILED(writer.Begin(config)), "second Begin was accepted") ||
      !Expect(FAILED(writer.Finalize(1, &output)) && output.empty(),
              "empty chunk finalized or leaked output") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kWriting,
              "recoverable Finalize error changed state")) {
    return false;
  }

  std::vector<uint8_t> short_frame(frame.size() - 1U);
  if (!Expect(writer.AddFrame(short_frame, 0) == E_INVALIDARG,
              "short BGRA frame was accepted") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kWriting,
              "invalid frame poisoned the writer")) {
    return false;
  }

  HRESULT wrong_thread_result = S_OK;
  std::thread wrong_thread(
      [&]() { wrong_thread_result = writer.AddFrame(frame, 0); });
  wrong_thread.join();
  if (!Expect(wrong_thread_result == RPC_E_WRONG_THREAD,
              "writer accepted a call from another thread") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kWriting,
              "wrong-thread call changed writer state") ||
      !ExpectSucceeded(writer.Reset(), "Reset") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kIdle &&
                  writer.frame_count() == 0,
              "Reset did not restore Idle state")) {
    return false;
  }

  MfH264ChunkWriterConfig oversized = config;
  oversized.max_output_bytes = windayflow::capture::kMaximumH264ChunkBytes + 1U;
  return Expect(writer.Begin(oversized) == E_INVALIDARG,
                "output limit above 64 MiB was accepted") &&
         Expect(writer.state() == MfH264ChunkWriterState::kIdle,
                "invalid output limit changed writer state");
}

bool TestRealMp4RoundTrip() {
  const MfH264ChunkWriterConfig config = ValidConfig();
  MfH264ChunkWriter writer;
  if (!ExpectSucceeded(writer.Begin(config), "round-trip Begin")) {
    return false;
  }

  constexpr uint32_t kFrameCount = 4;
  constexpr int64_t kFrameDurationTicks =
      windayflow::capture::kMediaFoundationTicksPerSecond / 10;
  for (uint32_t index = 0; index < kFrameCount; ++index) {
    const std::vector<uint8_t> frame =
        MakeFrame(config.width, config.height, static_cast<uint8_t>(index));
    if (!ExpectSucceeded(writer.AddFrame(frame, kFrameDurationTicks * index),
                         "AddFrame")) {
      return false;
    }
  }

  std::vector<uint8_t> mp4;
  if (!ExpectSucceeded(writer.Finalize(kFrameDurationTicks * kFrameCount, &mp4),
                       "Finalize") ||
      !Expect(writer.state() == MfH264ChunkWriterState::kFinalized &&
                  writer.frame_count() == kFrameCount,
              "Finalize did not retain the completed state") ||
      !Expect(mp4.size() > 12 && mp4.size() <= config.max_output_bytes,
              "final MP4 was empty or exceeded its bound") ||
      !Expect(std::memcmp(mp4.data() + 4U, "ftyp", 4U) == 0,
              "final bytes did not contain an MP4 file-type box") ||
      !ExpectSucceeded(
          ValidateDecodableMp4(mp4, config.width, config.height, kFrameCount),
          "Media Foundation MP4 decode")) {
    return false;
  }

  std::vector<uint8_t> repeated_output = {9, 9};
  return Expect(FAILED(writer.Finalize(kFrameDurationTicks * kFrameCount,
                                       &repeated_output)) &&
                    repeated_output.empty(),
                "second Finalize succeeded or retained caller bytes") &&
         ExpectSucceeded(writer.Reset(), "post-finalize Reset") &&
         Expect(writer.state() == MfH264ChunkWriterState::kIdle,
                "finalized writer could not be reset");
}

bool TestOutputOverflowFailsClosed() {
  MfH264ChunkWriterConfig config = ValidConfig();
  config.max_output_bytes = 256;
  MfH264ChunkWriter writer;
  std::vector<uint8_t> output;
  HRESULT result = writer.Begin(config);
  if (SUCCEEDED(result)) {
    const std::vector<uint8_t> frame =
        MakeFrame(config.width, config.height, 0);
    result = writer.AddFrame(frame, 0);
    if (SUCCEEDED(result)) {
      output = {7, 7, 7};
      result = writer.Finalize(
          windayflow::capture::kMediaFoundationTicksPerSecond / 10, &output);
    }
  }
  return Expect(FAILED(result), "bounded writer accepted an oversized MP4") &&
         Expect(writer.state() == MfH264ChunkWriterState::kFailed,
                "capacity failure did not enter Failed state") &&
         Expect(output.empty(), "capacity failure retained MP4 bytes") &&
         ExpectSucceeded(writer.Reset(), "overflow Reset") &&
         Expect(writer.state() == MfH264ChunkWriterState::kIdle,
                "overflowed writer could not be reset");
}

}  // namespace

int main() {
  MediaFoundationTestRuntime runtime;
  if (!ExpectSucceeded(runtime.Start(), "test Media Foundation startup") ||
      !TestStateAndArgumentValidation() || !TestRealMp4RoundTrip() ||
      !TestOutputOverflowFailsClosed()) {
    return 1;
  }
  std::cout << "Media Foundation H.264 chunk writer tests passed\n";
  return 0;
}

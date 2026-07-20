// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Heavily
// modified for strict display-bound WinDayFlow capture; see
// THIRD_PARTY_NOTICES.md.

#include "dxgi_desktop_frame_source.h"

#include <array>
#include <cstring>
#include <limits>
#include <utility>

#include "pixel_buffer.h"

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;

constexpr uint32_t kMaximumAcquireTimeoutMs = 1'000;

bool IsRepresentableHandle(uint64_t value) noexcept {
  return value != 0 &&
         static_cast<uint64_t>(static_cast<uintptr_t>(value)) == value;
}

bool IsSupportedBgraFormat(DXGI_FORMAT format) noexcept {
  return format == DXGI_FORMAT_B8G8R8A8_UNORM ||
         format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
}

DxgiDesktopFrameResult MapResolverFailure(
    DxgiOutputResolveResult result) noexcept {
  switch (result) {
    case DxgiOutputResolveResult::kResolved:
      return DxgiDesktopFrameResult::kOk;
    case DxgiOutputResolveResult::kInvalidArgument:
    case DxgiOutputResolveResult::kInvalidTarget:
      return DxgiDesktopFrameResult::kInvalidArgument;
    case DxgiOutputResolveResult::kInvalidTopology:
    case DxgiOutputResolveResult::kAmbiguous:
      return DxgiDesktopFrameResult::kTopologyChanged;
    case DxgiOutputResolveResult::kUnsupportedOutput:
      return DxgiDesktopFrameResult::kUnsupportedFormat;
    case DxgiOutputResolveResult::kEnumerationFailed:
    case DxgiOutputResolveResult::kNotFound:
    default:
      return DxgiDesktopFrameResult::kOutputUnavailable;
  }
}

HRESULT ReleaseAcquiredFrame(void* context) noexcept {
  if (context == nullptr) {
    return E_POINTER;
  }
  return static_cast<IDXGIOutputDuplication*>(context)->ReleaseFrame();
}

struct MappedTextureCleanupContext {
  ID3D11DeviceContext* context = nullptr;
  ID3D11Texture2D* texture = nullptr;
};

HRESULT UnmapTexture(void* context) noexcept {
  auto* mapped = static_cast<MappedTextureCleanupContext*>(context);
  if (mapped == nullptr || mapped->context == nullptr ||
      mapped->texture == nullptr) {
    return E_POINTER;
  }
  mapped->context->Unmap(mapped->texture, 0);
  return S_OK;
}

}  // namespace

ScopedDxgiCleanupAction::ScopedDxgiCleanupAction(
    void* context, DxgiCleanupCallback callback) noexcept
    : context_(context), callback_(callback) {}

ScopedDxgiCleanupAction::~ScopedDxgiCleanupAction() {
  static_cast<void>(RunNow());
}

void ScopedDxgiCleanupAction::Arm() noexcept { armed_ = true; }

HRESULT ScopedDxgiCleanupAction::RunNow() noexcept {
  if (!armed_) {
    return S_OK;
  }
  armed_ = false;
  return callback_ == nullptr ? E_POINTER : callback_(context_);
}

bool TryCalculateDxgiFrameBgraBytes(uint32_t width, uint32_t height,
                                    size_t* bytes) noexcept {
  if (bytes == nullptr) {
    return false;
  }
  *bytes = 0;
  if (width == 0 || height == 0) {
    return false;
  }
  const uint64_t pixels =
      static_cast<uint64_t>(width) * static_cast<uint64_t>(height);
  if (pixels > kMaximumDxgiFramePixels ||
      pixels > static_cast<uint64_t>(kMaximumDxgiFrameBgraBytes) / 4U) {
    return false;
  }
  *bytes = static_cast<size_t>(pixels * 4U);
  return true;
}

bool TryCalculateDxgiMappedFrameSizes(uint32_t row_pitch, uint32_t width,
                                      uint32_t height, size_t* source_size,
                                      size_t* destination_size) noexcept {
  if (source_size == nullptr || destination_size == nullptr) {
    return false;
  }
  *source_size = 0;
  *destination_size = 0;
  size_t packed_bytes = 0;
  if (!TryCalculateDxgiFrameBgraBytes(width, height, &packed_bytes)) {
    return false;
  }
  const size_t row_bytes = static_cast<size_t>(width) * 4U;
  if (row_pitch < row_bytes ||
      row_pitch > static_cast<size_t>(std::numeric_limits<ptrdiff_t>::max())) {
    return false;
  }
  const size_t rows_before_last = static_cast<size_t>(height - 1U);
  if (rows_before_last >
      (std::numeric_limits<size_t>::max() - row_bytes) / row_pitch) {
    return false;
  }
  const size_t mapped_bytes = rows_before_last * row_pitch + row_bytes;
  if (mapped_bytes > kMaximumDxgiFrameBgraBytes) {
    return false;
  }
  *source_size = mapped_bytes;
  *destination_size = packed_bytes;
  return true;
}

DxgiDesktopFrameResult MapDesktopDuplicationFailure(HRESULT result) noexcept {
  if (SUCCEEDED(result)) {
    return DxgiDesktopFrameResult::kOk;
  }
  switch (result) {
    case DXGI_ERROR_WAIT_TIMEOUT:
      return DxgiDesktopFrameResult::kTimeout;
    case DXGI_ERROR_ACCESS_LOST:
    case DXGI_ERROR_DEVICE_REMOVED:
    case DXGI_ERROR_DEVICE_RESET:
    case DXGI_ERROR_SESSION_DISCONNECTED:
    case DXGI_ERROR_NOT_CURRENTLY_AVAILABLE:
      return DxgiDesktopFrameResult::kAccessLost;
    case DXGI_ERROR_UNSUPPORTED:
      return DxgiDesktopFrameResult::kUnsupportedFormat;
    case E_INVALIDARG:
    case E_POINTER:
      return DxgiDesktopFrameResult::kInvalidArgument;
    default:
      return DxgiDesktopFrameResult::kDeviceFailure;
  }
}

DxgiDesktopFrameResult MapDesktopTextureMapFailure(HRESULT result) noexcept {
  if (SUCCEEDED(result)) {
    return DxgiDesktopFrameResult::kOk;
  }
  const DxgiDesktopFrameResult mapped = MapDesktopDuplicationFailure(result);
  return mapped == DxgiDesktopFrameResult::kAccessLost
             ? DxgiDesktopFrameResult::kAccessLost
             : DxgiDesktopFrameResult::kCopyFailure;
}

DxgiDesktopFrameResult ValidateDxgiOutputFingerprint(
    const DxgiOutputFingerprint& expected,
    DxgiOutputResolveResult resolve_result,
    const DxgiOutputFingerprint& current) noexcept {
  if (resolve_result != DxgiOutputResolveResult::kResolved) {
    return MapResolverFailure(resolve_result);
  }
  return SameDxgiOutputFingerprint(expected, current)
             ? DxgiDesktopFrameResult::kOk
             : DxgiDesktopFrameResult::kTopologyChanged;
}

DxgiDesktopFrameResult ValidateDxgiFrameDimensions(
    uint32_t width, uint32_t height,
    const DxgiOutputFingerprint& fingerprint) noexcept {
  const int64_t expected_width =
      static_cast<int64_t>(fingerprint.desktop_coordinates.right) -
      static_cast<int64_t>(fingerprint.desktop_coordinates.left);
  const int64_t expected_height =
      static_cast<int64_t>(fingerprint.desktop_coordinates.bottom) -
      static_cast<int64_t>(fingerprint.desktop_coordinates.top);
  return expected_width > 0 && expected_height > 0 &&
                 expected_width <= std::numeric_limits<uint32_t>::max() &&
                 expected_height <= std::numeric_limits<uint32_t>::max() &&
                 width == static_cast<uint32_t>(expected_width) &&
                 height == static_cast<uint32_t>(expected_height)
             ? DxgiDesktopFrameResult::kOk
             : DxgiDesktopFrameResult::kTopologyChanged;
}

bool RotateBgraFrame(const BgraFrame& source, DXGI_MODE_ROTATION rotation,
                     BgraFrame* destination) noexcept {
  if (destination == nullptr) {
    return false;
  }
  *destination = {};
  size_t bounded_bytes = 0;
  if (!IsValidBgraFrame(source) ||
      !TryCalculateDxgiFrameBgraBytes(source.width, source.height,
                                      &bounded_bytes) ||
      bounded_bytes != source.pixels.size() ||
      (rotation != DXGI_MODE_ROTATION_IDENTITY &&
       rotation != DXGI_MODE_ROTATION_ROTATE90 &&
       rotation != DXGI_MODE_ROTATION_ROTATE180 &&
       rotation != DXGI_MODE_ROTATION_ROTATE270)) {
    return false;
  }

  try {
    const bool quarter_turn = rotation == DXGI_MODE_ROTATION_ROTATE90 ||
                              rotation == DXGI_MODE_ROTATION_ROTATE270;
    BgraFrame output;
    output.width = quarter_turn ? source.height : source.width;
    output.height = quarter_turn ? source.width : source.height;
    output.pixels.resize(source.pixels.size());
    for (uint32_t y = 0; y < source.height; ++y) {
      for (uint32_t x = 0; x < source.width; ++x) {
        uint32_t target_x = x;
        uint32_t target_y = y;
        switch (rotation) {
          case DXGI_MODE_ROTATION_ROTATE90:
            target_x = source.height - 1U - y;
            target_y = x;
            break;
          case DXGI_MODE_ROTATION_ROTATE180:
            target_x = source.width - 1U - x;
            target_y = source.height - 1U - y;
            break;
          case DXGI_MODE_ROTATION_ROTATE270:
            target_x = y;
            target_y = source.width - 1U - x;
            break;
          case DXGI_MODE_ROTATION_IDENTITY:
          default:
            break;
        }
        const size_t source_offset =
            (static_cast<size_t>(y) * source.width + x) * 4U;
        const size_t target_offset =
            (static_cast<size_t>(target_y) * output.width + target_x) * 4U;
        std::memcpy(output.pixels.data() + target_offset,
                    source.pixels.data() + source_offset, 4U);
      }
    }
    *destination = std::move(output);
    return true;
  } catch (...) {
    *destination = {};
    return false;
  }
}

DxgiDesktopFrameResult DxgiDesktopFrameSource::Initialize(
    const CaptureTargetIdentity& target) noexcept {
  Reset();
  if (!IsRepresentableHandle(target.display_monitor_handle) ||
      target.display_device_key.empty()) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }

  ResolvedDxgiOutput resolved;
  const auto monitor = reinterpret_cast<HMONITOR>(
      static_cast<uintptr_t>(target.display_monitor_handle));
  const DxgiOutputResolveResult resolve_result =
      ResolveDxgiOutput(monitor, target.display_device_key, &resolved);
  if (resolve_result != DxgiOutputResolveResult::kResolved) {
    return MapResolverFailure(resolve_result);
  }

  constexpr UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
  constexpr std::array<D3D_FEATURE_LEVEL, 4> feature_levels{
      D3D_FEATURE_LEVEL_11_1,
      D3D_FEATURE_LEVEL_11_0,
      D3D_FEATURE_LEVEL_10_1,
      D3D_FEATURE_LEVEL_10_0,
  };
  D3D_FEATURE_LEVEL selected_level = D3D_FEATURE_LEVEL_10_0;
  HRESULT result = D3D11CreateDevice(
      resolved.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
      feature_levels.data(), static_cast<UINT>(feature_levels.size()),
      D3D11_SDK_VERSION, device_.GetAddressOf(), &selected_level,
      context_.GetAddressOf());
  if (result == E_INVALIDARG) {
    result = D3D11CreateDevice(resolved.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN,
                               nullptr, flags, feature_levels.data() + 1,
                               static_cast<UINT>(feature_levels.size() - 1U),
                               D3D11_SDK_VERSION, device_.GetAddressOf(),
                               &selected_level, context_.GetAddressOf());
  }
  if (FAILED(result)) {
    Reset();
    return MapDesktopDuplicationFailure(result);
  }
  result = resolved.output->DuplicateOutput(device_.Get(),
                                            duplication_.GetAddressOf());
  if (FAILED(result)) {
    Reset();
    return MapDesktopDuplicationFailure(result);
  }

  target_ = target;
  fingerprint_ = resolved.fingerprint;
  adapter_ = std::move(resolved.adapter);
  output_ = std::move(resolved.output);
  return DxgiDesktopFrameResult::kOk;
}

DxgiDesktopFrameResult DxgiDesktopFrameSource::Acquire(
    uint32_t timeout_ms, BgraFrame* frame) noexcept {
  if (frame == nullptr) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }
  *frame = {};
  if (!initialized() || timeout_ms > kMaximumAcquireTimeoutMs) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }

  const DxgiDesktopFrameResult before = RevalidateOutput();
  if (before != DxgiDesktopFrameResult::kOk) {
    return before;
  }

  try {
    DXGI_OUTDUPL_FRAME_INFO frame_info{};
    ComPtr<IDXGIResource> desktop_resource;
    ScopedDxgiCleanupAction acquired(duplication_.Get(), ReleaseAcquiredFrame);
    HRESULT result = duplication_->AcquireNextFrame(
        timeout_ms, &frame_info, desktop_resource.GetAddressOf());
    if (FAILED(result)) {
      return MapDesktopDuplicationFailure(result);
    }
    acquired.Arm();

    ComPtr<ID3D11Texture2D> desktop_texture;
    result = desktop_resource.As(&desktop_texture);
    if (FAILED(result) || desktop_texture == nullptr) {
      return DxgiDesktopFrameResult::kUnsupportedFormat;
    }
    D3D11_TEXTURE2D_DESC description{};
    desktop_texture->GetDesc(&description);
    size_t packed_frame_size = 0;
    if (!IsSupportedBgraFormat(description.Format) ||
        !TryCalculateDxgiFrameBgraBytes(description.Width, description.Height,
                                        &packed_frame_size)) {
      return DxgiDesktopFrameResult::kUnsupportedFormat;
    }

    if (staging_ == nullptr ||
        staging_description_.Width != description.Width ||
        staging_description_.Height != description.Height ||
        staging_description_.Format != description.Format) {
      staging_.Reset();
      D3D11_TEXTURE2D_DESC staging_description = description;
      staging_description.BindFlags = 0;
      staging_description.MiscFlags = 0;
      staging_description.Usage = D3D11_USAGE_STAGING;
      staging_description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
      staging_description.ArraySize = 1;
      staging_description.MipLevels = 1;
      staging_description.SampleDesc.Count = 1;
      staging_description.SampleDesc.Quality = 0;
      result = device_->CreateTexture2D(&staging_description, nullptr,
                                        staging_.GetAddressOf());
      if (FAILED(result)) {
        return MapDesktopDuplicationFailure(result);
      }
      staging_description_ = staging_description;
    }

    context_->CopyResource(staging_.Get(), desktop_texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    MappedTextureCleanupContext mapped_context{context_.Get(), staging_.Get()};
    ScopedDxgiCleanupAction mapped_texture(&mapped_context, UnmapTexture);
    result = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(result)) {
      return MapDesktopTextureMapFailure(result);
    }
    mapped_texture.Arm();

    size_t source_size = 0;
    size_t destination_size = 0;
    if (mapped.pData == nullptr ||
        !TryCalculateDxgiMappedFrameSizes(mapped.RowPitch, description.Width,
                                          description.Height, &source_size,
                                          &destination_size) ||
        destination_size != packed_frame_size) {
      return DxgiDesktopFrameResult::kCopyFailure;
    }
    BgraFrame raw;
    raw.width = description.Width;
    raw.height = description.Height;
    raw.pixels.resize(destination_size);
    if (!CopyDecodedRgb32Rows(static_cast<const uint8_t*>(mapped.pData),
                              source_size, description.Width,
                              description.Height,
                              static_cast<ptrdiff_t>(mapped.RowPitch),
                              raw.pixels.data(), raw.pixels.size())) {
      return DxgiDesktopFrameResult::kCopyFailure;
    }
    result = mapped_texture.RunNow();
    if (FAILED(result)) {
      return DxgiDesktopFrameResult::kCopyFailure;
    }
    result = acquired.RunNow();
    if (FAILED(result)) {
      return MapDesktopDuplicationFailure(result);
    }

    BgraFrame oriented;
    if (!RotateBgraFrame(raw, fingerprint_.rotation, &oriented)) {
      return DxgiDesktopFrameResult::kCopyFailure;
    }
    const DxgiDesktopFrameResult dimensions = ValidateDxgiFrameDimensions(
        oriented.width, oriented.height, fingerprint_);
    if (dimensions != DxgiDesktopFrameResult::kOk) {
      return dimensions;
    }

    const DxgiDesktopFrameResult after = RevalidateOutput();
    if (after != DxgiDesktopFrameResult::kOk) {
      return after;
    }
    *frame = std::move(oriented);
    return DxgiDesktopFrameResult::kOk;
  } catch (...) {
    *frame = {};
    return DxgiDesktopFrameResult::kCopyFailure;
  }
}

void DxgiDesktopFrameSource::Reset() noexcept {
  staging_.Reset();
  duplication_.Reset();
  context_.Reset();
  device_.Reset();
  output_.Reset();
  adapter_.Reset();
  staging_description_ = {};
  fingerprint_ = {};
  target_ = {};
}

bool DxgiDesktopFrameSource::initialized() const noexcept {
  return adapter_ != nullptr && output_ != nullptr && device_ != nullptr &&
         context_ != nullptr && duplication_ != nullptr;
}

const DxgiOutputFingerprint& DxgiDesktopFrameSource::fingerprint()
    const noexcept {
  return fingerprint_;
}

DxgiDesktopFrameResult DxgiDesktopFrameSource::RevalidateOutput()
    const noexcept {
  if (!initialized() ||
      !IsRepresentableHandle(target_.display_monitor_handle)) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }
  const auto monitor = reinterpret_cast<HMONITOR>(
      static_cast<uintptr_t>(target_.display_monitor_handle));
  ResolvedDxgiOutput current;
  const DxgiOutputResolveResult result =
      ResolveDxgiOutput(monitor, target_.display_device_key, &current);
  return ValidateDxgiOutputFingerprint(fingerprint_, result,
                                       current.fingerprint);
}

}  // namespace windayflow::capture

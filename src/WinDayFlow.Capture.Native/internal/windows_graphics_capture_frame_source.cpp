#include "windows_graphics_capture_frame_source.h"

#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <roapi.h>
#include <roerrorapi.h>

#include <array>
#include <chrono>
#include <condition_variable>
#include <limits>
#include <mutex>
#include <utility>

#include "pixel_buffer.h"

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;
using ::Windows::Graphics::DirectX::Direct3D11::
    IDirect3DDxgiInterfaceAccess;
using winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame;
using winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool;
using winrt::Windows::Graphics::Capture::GraphicsCaptureItem;
using winrt::Windows::Graphics::Capture::GraphicsCaptureSession;
using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
using Direct3DDxgiInterfaceAccess =
    ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

constexpr uint32_t kMaximumAcquireTimeoutMs = 1'000;
constexpr size_t kMaximumDesktopNameCharacters = 256;

enum class InputDesktopState {
  kDefault,
  kOther,
  kUnknown,
};

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

InputDesktopState ReadInputDesktopState() noexcept {
  const HDESK desktop =
      OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
  if (desktop == nullptr) {
    return InputDesktopState::kUnknown;
  }

  std::array<wchar_t, kMaximumDesktopNameCharacters> name{};
  DWORD required_bytes = 0;
  const BOOL read = GetUserObjectInformationW(
      desktop, UOI_NAME, name.data(),
      static_cast<DWORD>(name.size() * sizeof(wchar_t)), &required_bytes);
  CloseDesktop(desktop);
  if (read == FALSE || required_bytes < sizeof(wchar_t) ||
      required_bytes > name.size() * sizeof(wchar_t)) {
    return InputDesktopState::kUnknown;
  }

  size_t length = 0;
  while (length < name.size() && name[length] != L'\0') {
    ++length;
  }
  if (length == name.size()) {
    return InputDesktopState::kUnknown;
  }
  return IsDefaultCaptureDesktopName(
             std::wstring_view(name.data(), length))
             ? InputDesktopState::kDefault
             : InputDesktopState::kOther;
}

bool MatchesFingerprintDimensions(
    int32_t width, int32_t height,
    const DxgiOutputFingerprint& fingerprint) noexcept {
  return width > 0 && height > 0 &&
         ValidateDxgiFrameDimensions(static_cast<uint32_t>(width),
                                     static_cast<uint32_t>(height),
                                     fingerprint) ==
             DxgiDesktopFrameResult::kOk;
}

}  // namespace

struct WindowsGraphicsCaptureFrameSource::FrameArrivalState {
  std::mutex gate;
  std::condition_variable changed;
  uint64_t sequence = 0;
  bool closed = false;
};

bool IsDefaultCaptureDesktopName(std::wstring_view name) noexcept {
  static constexpr std::wstring_view kDefaultDesktop = L"Default";
  if (name.empty() ||
      name.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }
  return CompareStringOrdinal(
             name.data(), static_cast<int>(name.size()),
             kDefaultDesktop.data(), static_cast<int>(kDefaultDesktop.size()),
             TRUE) == CSTR_EQUAL;
}

DxgiDesktopFrameResult MapWindowsGraphicsCaptureFailure(
    HRESULT result) noexcept {
  if (SUCCEEDED(result)) {
    return DxgiDesktopFrameResult::kOk;
  }
  switch (result) {
    case E_ACCESSDENIED:
      return DxgiDesktopFrameResult::kAccessDenied;
    case RO_E_CLOSED:
    case RPC_E_DISCONNECTED:
    case DXGI_ERROR_ACCESS_LOST:
    case DXGI_ERROR_DEVICE_REMOVED:
    case DXGI_ERROR_DEVICE_RESET:
    case DXGI_ERROR_SESSION_DISCONNECTED:
    case DXGI_ERROR_NOT_CURRENTLY_AVAILABLE:
      return DxgiDesktopFrameResult::kAccessLost;
    case E_INVALIDARG:
    case E_POINTER:
      return DxgiDesktopFrameResult::kInvalidArgument;
    default:
      return DxgiDesktopFrameResult::kDeviceFailure;
  }
}

WindowsGraphicsCaptureFrameSource::~WindowsGraphicsCaptureFrameSource() {
  Reset();
}

DxgiDesktopFrameResult WindowsGraphicsCaptureFrameSource::Initialize(
    const CaptureTargetIdentity& target) noexcept {
  Reset();
  if (!IsRepresentableHandle(target.display_monitor_handle) ||
      target.display_device_key.empty()) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }
  if (ReadInputDesktopState() != InputDesktopState::kDefault) {
    return DxgiDesktopFrameResult::kAccessLost;
  }

  const HRESULT apartment_result = RoInitialize(RO_INIT_MULTITHREADED);
  if (FAILED(apartment_result) && apartment_result != RPC_E_CHANGED_MODE) {
    return MapWindowsGraphicsCaptureFailure(apartment_result);
  }
  uninitialize_winrt_ = SUCCEEDED(apartment_result);

  try {
    const auto monitor = reinterpret_cast<HMONITOR>(
        static_cast<uintptr_t>(target.display_monitor_handle));
    ResolvedDxgiOutput resolved;
    const DxgiOutputResolveResult resolve_result =
        ResolveDxgiOutput(monitor, target.display_device_key, &resolved);
    if (resolve_result != DxgiOutputResolveResult::kResolved) {
      const DxgiDesktopFrameResult failure =
          MapResolverFailure(resolve_result);
      Reset();
      return failure;
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
      result = D3D11CreateDevice(
          resolved.adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
          feature_levels.data() + 1,
          static_cast<UINT>(feature_levels.size() - 1U), D3D11_SDK_VERSION,
          device_.GetAddressOf(), &selected_level, context_.GetAddressOf());
    }
    if (FAILED(result)) {
      const DxgiDesktopFrameResult failure =
          MapWindowsGraphicsCaptureFailure(result);
      Reset();
      return failure;
    }

    ComPtr<IDXGIDevice> dxgi_device;
    result = device_.As(&dxgi_device);
    if (FAILED(result) || dxgi_device == nullptr) {
      Reset();
      return DxgiDesktopFrameResult::kDeviceFailure;
    }
    winrt::com_ptr<IInspectable> inspectable_device;
    result = CreateDirect3D11DeviceFromDXGIDevice(
        dxgi_device.Get(), inspectable_device.put());
    if (FAILED(result) || inspectable_device == nullptr) {
      const DxgiDesktopFrameResult failure =
          MapWindowsGraphicsCaptureFailure(result);
      Reset();
      return failure;
    }
    direct3d_device_ = inspectable_device.as<IDirect3DDevice>();

    if (!GraphicsCaptureSession::IsSupported()) {
      Reset();
      return DxgiDesktopFrameResult::kUnsupportedFormat;
    }
    auto item_interop = winrt::get_activation_factory<
        GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    result = item_interop->CreateForMonitor(
        monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_));
    if (FAILED(result) || item_ == nullptr) {
      const DxgiDesktopFrameResult failure =
          MapWindowsGraphicsCaptureFailure(result);
      Reset();
      return failure;
    }
    const auto item_size = item_.Size();
    if (!MatchesFingerprintDimensions(item_size.Width, item_size.Height,
                                      resolved.fingerprint)) {
      Reset();
      return DxgiDesktopFrameResult::kTopologyChanged;
    }

    arrival_state_ = std::make_shared<FrameArrivalState>();
    const std::weak_ptr<FrameArrivalState> weak_state = arrival_state_;
    item_closed_token_ = item_.Closed(
        [weak_state](const GraphicsCaptureItem&,
                     const winrt::Windows::Foundation::IInspectable&) noexcept {
          try {
            const std::shared_ptr<FrameArrivalState> state = weak_state.lock();
            if (state == nullptr) {
              return;
            }
            {
              std::lock_guard lock(state->gate);
              state->closed = true;
              ++state->sequence;
            }
            state->changed.notify_all();
          } catch (...) {
          }
        });
    item_closed_subscribed_ = true;
    frame_pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(
        direct3d_device_, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2,
        item_size);
    frame_arrived_token_ = frame_pool_.FrameArrived(
        [weak_state](const Direct3D11CaptureFramePool&,
                     const winrt::Windows::Foundation::IInspectable&) noexcept {
          try {
            const std::shared_ptr<FrameArrivalState> state = weak_state.lock();
            if (state == nullptr) {
              return;
            }
            {
              std::lock_guard lock(state->gate);
              if (state->closed) {
                return;
              }
              ++state->sequence;
            }
            state->changed.notify_one();
          } catch (...) {
          }
        });
    frame_arrived_subscribed_ = true;
    session_ = frame_pool_.CreateCaptureSession(item_);
    session_.StartCapture();

    target_ = target;
    fingerprint_ = resolved.fingerprint;
    adapter_ = std::move(resolved.adapter);
    return DxgiDesktopFrameResult::kOk;
  } catch (const winrt::hresult_error& error) {
    const DxgiDesktopFrameResult failure =
        MapWindowsGraphicsCaptureFailure(error.code());
    Reset();
    return failure;
  } catch (...) {
    Reset();
    return DxgiDesktopFrameResult::kDeviceFailure;
  }
}

DxgiDesktopFrameResult WindowsGraphicsCaptureFrameSource::Acquire(
    uint32_t timeout_ms, BgraFrame* frame) noexcept {
  if (frame == nullptr) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }
  *frame = {};
  if (!initialized() || timeout_ms > kMaximumAcquireTimeoutMs) {
    return DxgiDesktopFrameResult::kInvalidArgument;
  }
  if (ReadInputDesktopState() != InputDesktopState::kDefault) {
    return DxgiDesktopFrameResult::kAccessLost;
  }

  const DxgiDesktopFrameResult before = RevalidateOutput();
  if (before != DxgiDesktopFrameResult::kOk) {
    return before;
  }

  try {
    const auto deadline = std::chrono::steady_clock::now() +
                          std::chrono::milliseconds(timeout_ms);
    for (;;) {
      Direct3D11CaptureFrame captured = frame_pool_.TryGetNextFrame();
      if (captured != nullptr) {
        return CopyFrame(captured, frame);
      }

      std::unique_lock lock(arrival_state_->gate);
      if (arrival_state_->closed) {
        return DxgiDesktopFrameResult::kAccessLost;
      }
      if (arrival_state_->sequence != observed_arrival_sequence_) {
        observed_arrival_sequence_ = arrival_state_->sequence;
        continue;
      }
      if (!arrival_state_->changed.wait_until(lock, deadline, [&]() {
            return arrival_state_->closed ||
                   arrival_state_->sequence != observed_arrival_sequence_;
          })) {
        return DxgiDesktopFrameResult::kTimeout;
      }
      if (arrival_state_->closed) {
        return DxgiDesktopFrameResult::kAccessLost;
      }
      observed_arrival_sequence_ = arrival_state_->sequence;
    }
  } catch (const winrt::hresult_error& error) {
    return MapWindowsGraphicsCaptureFailure(error.code());
  } catch (...) {
    return DxgiDesktopFrameResult::kDeviceFailure;
  }
}

DxgiDesktopFrameResult WindowsGraphicsCaptureFrameSource::CopyFrame(
    const Direct3D11CaptureFrame& source, BgraFrame* frame) noexcept {
  try {
    const auto content_size = source.ContentSize();
    if (!MatchesFingerprintDimensions(content_size.Width, content_size.Height,
                                      fingerprint_)) {
      return DxgiDesktopFrameResult::kTopologyChanged;
    }

    const auto surface = source.Surface();
    winrt::com_ptr<Direct3DDxgiInterfaceAccess> surface_access =
        surface.as<Direct3DDxgiInterfaceAccess>();
    ComPtr<ID3D11Texture2D> desktop_texture;
    HRESULT result = surface_access->GetInterface(
        IID_PPV_ARGS(desktop_texture.GetAddressOf()));
    if (FAILED(result) || desktop_texture == nullptr) {
      return MapWindowsGraphicsCaptureFailure(result);
    }

    D3D11_TEXTURE2D_DESC description{};
    desktop_texture->GetDesc(&description);
    size_t packed_frame_size = 0;
    if (!IsSupportedBgraFormat(description.Format) ||
        description.Width != static_cast<uint32_t>(content_size.Width) ||
        description.Height != static_cast<uint32_t>(content_size.Height) ||
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
        return MapWindowsGraphicsCaptureFailure(result);
      }
      staging_description_ = staging_description;
    }

    context_->CopyResource(staging_.Get(), desktop_texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    result = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(result)) {
      return MapWindowsGraphicsCaptureFailure(result);
    }

    size_t source_size = 0;
    size_t destination_size = 0;
    const bool valid_mapping =
        mapped.pData != nullptr &&
        TryCalculateDxgiMappedFrameSizes(
            mapped.RowPitch, description.Width, description.Height,
            &source_size, &destination_size) &&
        destination_size == packed_frame_size;
    BgraFrame copied;
    bool copied_rows = false;
    if (valid_mapping) {
      copied.width = description.Width;
      copied.height = description.Height;
      copied.pixels.resize(destination_size);
      copied_rows = CopyDecodedRgb32Rows(
          static_cast<const uint8_t*>(mapped.pData), source_size,
          description.Width, description.Height,
          static_cast<ptrdiff_t>(mapped.RowPitch), copied.pixels.data(),
          copied.pixels.size());
    }
    context_->Unmap(staging_.Get(), 0);
    if (!valid_mapping || !copied_rows) {
      return DxgiDesktopFrameResult::kCopyFailure;
    }

    const DxgiDesktopFrameResult after = RevalidateOutput();
    if (after != DxgiDesktopFrameResult::kOk ||
        ReadInputDesktopState() != InputDesktopState::kDefault) {
      return after == DxgiDesktopFrameResult::kOk
                 ? DxgiDesktopFrameResult::kAccessLost
                 : after;
    }
    *frame = std::move(copied);
    return DxgiDesktopFrameResult::kOk;
  } catch (const winrt::hresult_error& error) {
    *frame = {};
    return MapWindowsGraphicsCaptureFailure(error.code());
  } catch (...) {
    *frame = {};
    return DxgiDesktopFrameResult::kCopyFailure;
  }
}

void WindowsGraphicsCaptureFrameSource::Reset() noexcept {
  const std::shared_ptr<FrameArrivalState> state = arrival_state_;
  if (state != nullptr) {
    {
      std::lock_guard lock(state->gate);
      state->closed = true;
      ++state->sequence;
    }
    state->changed.notify_all();
  }

  if (frame_arrived_subscribed_ && frame_pool_ != nullptr) {
    try {
      frame_pool_.FrameArrived(frame_arrived_token_);
    } catch (...) {
    }
  }
  frame_arrived_subscribed_ = false;
  if (item_closed_subscribed_ && item_ != nullptr) {
    try {
      item_.Closed(item_closed_token_);
    } catch (...) {
    }
  }
  item_closed_subscribed_ = false;
  if (session_ != nullptr) {
    try {
      session_.Close();
    } catch (...) {
    }
  }
  if (frame_pool_ != nullptr) {
    try {
      frame_pool_.Close();
    } catch (...) {
    }
  }

  session_ = nullptr;
  frame_pool_ = nullptr;
  item_ = nullptr;
  direct3d_device_ = nullptr;
  arrival_state_.reset();
  observed_arrival_sequence_ = 0;
  staging_.Reset();
  context_.Reset();
  device_.Reset();
  adapter_.Reset();
  staging_description_ = {};
  fingerprint_ = {};
  target_ = {};
  if (uninitialize_winrt_) {
    RoUninitialize();
    uninitialize_winrt_ = false;
  }
}

bool WindowsGraphicsCaptureFrameSource::initialized() const noexcept {
  return adapter_ != nullptr && device_ != nullptr && context_ != nullptr &&
         direct3d_device_ != nullptr && item_ != nullptr &&
         frame_pool_ != nullptr && session_ != nullptr &&
         arrival_state_ != nullptr;
}

DxgiDesktopFrameResult WindowsGraphicsCaptureFrameSource::RevalidateOutput()
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

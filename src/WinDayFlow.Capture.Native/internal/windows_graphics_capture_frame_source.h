#ifndef WINDAYFLOW_WINDOWS_GRAPHICS_CAPTURE_FRAME_SOURCE_H_
#define WINDAYFLOW_WINDOWS_GRAPHICS_CAPTURE_FRAME_SOURCE_H_

#include <Windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <cstdint>
#include <memory>
#include <string_view>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>

#include "capture_safety_core.h"
#include "dxgi_desktop_frame_source.h"
#include "dxgi_output_resolver.h"

namespace windayflow::capture {

bool IsDefaultCaptureDesktopName(std::wstring_view name) noexcept;

DxgiDesktopFrameResult MapWindowsGraphicsCaptureFailure(
    HRESULT result) noexcept;

class WindowsGraphicsCaptureFrameSource final {
 public:
  WindowsGraphicsCaptureFrameSource() = default;
  ~WindowsGraphicsCaptureFrameSource();

  WindowsGraphicsCaptureFrameSource(
      const WindowsGraphicsCaptureFrameSource&) = delete;
  WindowsGraphicsCaptureFrameSource& operator=(
      const WindowsGraphicsCaptureFrameSource&) = delete;

  DxgiDesktopFrameResult Initialize(
      const CaptureTargetIdentity& target) noexcept;
  DxgiDesktopFrameResult Acquire(uint32_t timeout_ms,
                                 BgraFrame* frame) noexcept;
  void Reset() noexcept;

  bool initialized() const noexcept;

 private:
  struct FrameArrivalState;

  DxgiDesktopFrameResult CopyFrame(
      const winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame& source,
      BgraFrame* frame) noexcept;
  DxgiDesktopFrameResult RevalidateOutput() const noexcept;

  CaptureTargetIdentity target_;
  DxgiOutputFingerprint fingerprint_;
  Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter_;
  Microsoft::WRL::ComPtr<ID3D11Device> device_;
  Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
  Microsoft::WRL::ComPtr<ID3D11Texture2D> staging_;
  D3D11_TEXTURE2D_DESC staging_description_{};
  winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice
      direct3d_device_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureItem item_{nullptr};
  winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool
      frame_pool_{nullptr};
  winrt::Windows::Graphics::Capture::GraphicsCaptureSession session_{nullptr};
  winrt::event_token frame_arrived_token_{};
  winrt::event_token item_closed_token_{};
  std::shared_ptr<FrameArrivalState> arrival_state_;
  uint64_t observed_arrival_sequence_ = 0;
  bool frame_arrived_subscribed_ = false;
  bool item_closed_subscribed_ = false;
  bool uninitialize_winrt_ = false;
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_WINDOWS_GRAPHICS_CAPTURE_FRAME_SOURCE_H_

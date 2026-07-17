// Adapted from QiDayflow windows/runner/capture_service.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256:
// FF967B90A95EFAA608BF6CDC4AD985299313A5A058D61A397A63847C3CA4E8FD. Heavily
// modified for strict display-bound WinDayFlow capture; see
// THIRD_PARTY_NOTICES.md.

#ifndef WINDAYFLOW_DXGI_DESKTOP_FRAME_SOURCE_H_
#define WINDAYFLOW_DXGI_DESKTOP_FRAME_SOURCE_H_

#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstddef>
#include <cstdint>

#include "capture_safety_core.h"
#include "dxgi_output_resolver.h"
#include "wic_bgra_scaler.h"

namespace windayflow::capture {

enum class DxgiDesktopFrameResult {
  kOk,
  kTimeout,
  kInvalidArgument,
  kOutputUnavailable,
  kTopologyChanged,
  kAccessLost,
  kUnsupportedFormat,
  kDeviceFailure,
  kCopyFailure,
};

inline constexpr uint64_t kMaximumDxgiFramePixels =
    uint64_t{7'680} * uint64_t{4'320};
inline constexpr size_t kMaximumDxgiFrameBgraBytes =
    static_cast<size_t>(kMaximumDxgiFramePixels * uint64_t{4});

DxgiDesktopFrameResult MapDesktopDuplicationFailure(HRESULT result) noexcept;
DxgiDesktopFrameResult MapDesktopTextureMapFailure(HRESULT result) noexcept;

bool TryCalculateDxgiFrameBgraBytes(uint32_t width, uint32_t height,
                                    size_t* bytes) noexcept;
bool TryCalculateDxgiMappedFrameSizes(uint32_t row_pitch, uint32_t width,
                                      uint32_t height, size_t* source_size,
                                      size_t* destination_size) noexcept;

DxgiDesktopFrameResult ValidateDxgiOutputFingerprint(
    const DxgiOutputFingerprint& expected,
    DxgiOutputResolveResult resolve_result,
    const DxgiOutputFingerprint& current) noexcept;
DxgiDesktopFrameResult ValidateDxgiFrameDimensions(
    uint32_t width, uint32_t height,
    const DxgiOutputFingerprint& fingerprint) noexcept;

using DxgiCleanupCallback = HRESULT (*)(void* context) noexcept;

class ScopedDxgiCleanupAction final {
 public:
  ScopedDxgiCleanupAction(void* context, DxgiCleanupCallback callback) noexcept;
  ~ScopedDxgiCleanupAction();

  ScopedDxgiCleanupAction(const ScopedDxgiCleanupAction&) = delete;
  ScopedDxgiCleanupAction& operator=(const ScopedDxgiCleanupAction&) = delete;

  void Arm() noexcept;
  HRESULT RunNow() noexcept;

 private:
  void* context_ = nullptr;
  DxgiCleanupCallback callback_ = nullptr;
  bool armed_ = false;
};

bool RotateBgraFrame(const BgraFrame& source, DXGI_MODE_ROTATION rotation,
                     BgraFrame* destination) noexcept;

class DxgiDesktopFrameSource final {
 public:
  DxgiDesktopFrameSource() = default;
  ~DxgiDesktopFrameSource() = default;

  DxgiDesktopFrameSource(const DxgiDesktopFrameSource&) = delete;
  DxgiDesktopFrameSource& operator=(const DxgiDesktopFrameSource&) = delete;

  DxgiDesktopFrameResult Initialize(
      const CaptureTargetIdentity& target) noexcept;
  DxgiDesktopFrameResult Acquire(uint32_t timeout_ms,
                                 BgraFrame* frame) noexcept;
  void Reset() noexcept;

  bool initialized() const noexcept;
  const DxgiOutputFingerprint& fingerprint() const noexcept;

 private:
  DxgiDesktopFrameResult RevalidateOutput() const noexcept;

  CaptureTargetIdentity target_;
  DxgiOutputFingerprint fingerprint_;
  Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter_;
  Microsoft::WRL::ComPtr<IDXGIOutput1> output_;
  Microsoft::WRL::ComPtr<ID3D11Device> device_;
  Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
  Microsoft::WRL::ComPtr<IDXGIOutputDuplication> duplication_;
  Microsoft::WRL::ComPtr<ID3D11Texture2D> staging_;
  D3D11_TEXTURE2D_DESC staging_description_{};
};

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_DXGI_DESKTOP_FRAME_SOURCE_H_

#ifndef WINDAYFLOW_DXGI_OUTPUT_RESOLVER_H_
#define WINDAYFLOW_DXGI_OUTPUT_RESOLVER_H_

#include <Windows.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstddef>
#include <span>
#include <string>
#include <string_view>

namespace windayflow::capture {

struct DxgiOutputFingerprint {
  LUID adapter_luid = {};
  HMONITOR monitor = nullptr;
  std::wstring canonical_device_name;
  RECT desktop_coordinates = {};
  DXGI_MODE_ROTATION rotation = DXGI_MODE_ROTATION_UNSPECIFIED;
};

struct DxgiOutputCandidate {
  DxgiOutputFingerprint fingerprint;
  bool attached_to_desktop = false;
};

enum class DxgiOutputCatalogState {
  kComplete,
  kFailed,
};

enum class DxgiOutputResolveResult {
  kResolved,
  kInvalidArgument,
  kInvalidTarget,
  kEnumerationFailed,
  kInvalidTopology,
  kNotFound,
  kAmbiguous,
  kUnsupportedOutput,
};

inline constexpr size_t kNoDxgiOutputIndex = static_cast<size_t>(-1);

struct DxgiOutputSelection {
  DxgiOutputResolveResult result = DxgiOutputResolveResult::kNotFound;
  size_t candidate_index = kNoDxgiOutputIndex;
};

DxgiOutputSelection SelectUniqueDxgiOutput(
    std::span<const DxgiOutputCandidate> candidates,
    DxgiOutputCatalogState catalog_state, HMONITOR target_monitor,
    std::wstring_view target_device_name) noexcept;

bool SameDxgiOutputFingerprint(const DxgiOutputFingerprint& left,
                               const DxgiOutputFingerprint& right) noexcept;

struct DxgiResolverObject {
  Microsoft::WRL::ComPtr<IUnknown> value;

  explicit operator bool() const noexcept { return value != nullptr; }
  void Reset() noexcept { value.Reset(); }
};

class IDxgiOutputResolverApi {
 public:
  virtual ~IDxgiOutputResolverApi() = default;

  virtual HRESULT CreateFactory(DxgiResolverObject* factory) noexcept = 0;
  virtual bool IsFactoryCurrent(const DxgiResolverObject& factory) noexcept = 0;
  virtual HRESULT EnumAdapter(const DxgiResolverObject& factory,
                              UINT adapter_index,
                              DxgiResolverObject* adapter) noexcept = 0;
  virtual HRESULT GetAdapterDescription(
      const DxgiResolverObject& adapter,
      DXGI_ADAPTER_DESC1* description) noexcept = 0;
  virtual HRESULT EnumOutput(const DxgiResolverObject& adapter,
                             UINT output_index,
                             DxgiResolverObject* output) noexcept = 0;
  virtual HRESULT GetOutputDescription(
      const DxgiResolverObject& output,
      DXGI_OUTPUT_DESC* description) noexcept = 0;
  virtual HRESULT QueryOutput1(const DxgiResolverObject& output,
                               DxgiResolverObject* output1) noexcept = 0;
};

struct DxgiResolverBinding {
  DxgiResolverObject adapter;
  DxgiResolverObject output1;
  DxgiOutputFingerprint fingerprint;

  void Reset() noexcept;
};

DxgiOutputResolveResult ResolveDxgiOutputWithApi(
    IDxgiOutputResolverApi& api, HMONITOR target_monitor,
    std::wstring_view target_device_name,
    DxgiResolverBinding* binding) noexcept;

struct ResolvedDxgiOutput {
  Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
  Microsoft::WRL::ComPtr<IDXGIOutput1> output;
  DxgiOutputFingerprint fingerprint;

  void Reset() noexcept;
};

DxgiOutputResolveResult ResolveDxgiOutput(
    HMONITOR target_monitor, std::wstring_view target_device_name,
    ResolvedDxgiOutput* resolved_output) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_DXGI_OUTPUT_RESOLVER_H_

#include "dxgi_output_resolver.h"

#include <array>
#include <limits>
#include <utility>
#include <vector>

namespace windayflow::capture {
namespace {

using Microsoft::WRL::ComPtr;

struct EnumeratedDxgiOutput {
  DxgiOutputCandidate candidate;
  DxgiResolverObject adapter;
  DxgiResolverObject output;
};

bool IsValidDeviceName(std::wstring_view device_name) noexcept {
  if (device_name.empty() || device_name.size() >= CCHDEVICENAME) {
    return false;
  }

  std::array<WORD, CCHDEVICENAME - 1> character_types{};
  if (GetStringTypeW(CT_CTYPE1, device_name.data(),
                     static_cast<int>(device_name.size()),
                     character_types.data()) == 0) {
    return false;
  }

  bool all_whitespace = true;
  for (size_t index = 0; index < device_name.size(); ++index) {
    if (device_name[index] == L'\0' ||
        (character_types[index] & C1_CNTRL) != 0) {
      return false;
    }
    all_whitespace = all_whitespace && (character_types[index] & C1_SPACE) != 0;
  }
  return !all_whitespace;
}

bool DeviceNamesEqual(std::wstring_view left,
                      std::wstring_view right) noexcept {
  if (left.empty() || right.empty() ||
      left.size() > static_cast<size_t>(std::numeric_limits<int>::max()) ||
      right.size() > static_cast<size_t>(std::numeric_limits<int>::max())) {
    return false;
  }
  return CompareStringOrdinal(left.data(), static_cast<int>(left.size()),
                              right.data(), static_cast<int>(right.size()),
                              TRUE) == CSTR_EQUAL;
}

bool IsValidRotation(DXGI_MODE_ROTATION rotation) noexcept {
  switch (rotation) {
    case DXGI_MODE_ROTATION_IDENTITY:
    case DXGI_MODE_ROTATION_ROTATE90:
    case DXGI_MODE_ROTATION_ROTATE180:
    case DXGI_MODE_ROTATION_ROTATE270:
      return true;
    case DXGI_MODE_ROTATION_UNSPECIFIED:
    default:
      return false;
  }
}

bool IsValidDesktopCoordinates(const RECT& coordinates) noexcept {
  return coordinates.right > coordinates.left &&
         coordinates.bottom > coordinates.top;
}

bool IsValidDisplayTarget(HMONITOR monitor,
                          std::wstring_view device_name) noexcept {
  return monitor != nullptr && IsValidDeviceName(device_name);
}

bool IsValidAttachedCandidate(const DxgiOutputCandidate& candidate) noexcept {
  const DxgiOutputFingerprint& fingerprint = candidate.fingerprint;
  return fingerprint.monitor != nullptr &&
         IsValidDeviceName(fingerprint.canonical_device_name) &&
         IsValidDesktopCoordinates(fingerprint.desktop_coordinates) &&
         IsValidRotation(fingerprint.rotation);
}

size_t BoundedDeviceNameLength(
    const wchar_t (&device_name)[CCHDEVICENAME]) noexcept {
  size_t length = 0;
  while (length < CCHDEVICENAME && device_name[length] != L'\0') {
    ++length;
  }
  return length;
}

DxgiOutputCandidate CandidateFromDescriptions(
    const DXGI_ADAPTER_DESC1& adapter_description,
    const DXGI_OUTPUT_DESC& output_description) {
  const size_t device_name_length =
      BoundedDeviceNameLength(output_description.DeviceName);
  DxgiOutputCandidate candidate;
  candidate.attached_to_desktop = output_description.AttachedToDesktop != FALSE;
  candidate.fingerprint.adapter_luid = adapter_description.AdapterLuid;
  candidate.fingerprint.monitor = output_description.Monitor;
  candidate.fingerprint.canonical_device_name.assign(
      output_description.DeviceName, device_name_length);
  candidate.fingerprint.desktop_coordinates =
      output_description.DesktopCoordinates;
  candidate.fingerprint.rotation = output_description.Rotation;
  return candidate;
}

class WindowsDxgiOutputResolverApi final : public IDxgiOutputResolverApi {
 public:
  HRESULT CreateFactory(DxgiResolverObject* factory) noexcept override {
    if (factory == nullptr) {
      return E_POINTER;
    }
    factory->Reset();
    ComPtr<IDXGIFactory1> typed_factory;
    HRESULT result =
        CreateDXGIFactory1(IID_PPV_ARGS(typed_factory.GetAddressOf()));
    if (FAILED(result) || typed_factory == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_factory.As(&factory->value);
  }

  bool IsFactoryCurrent(const DxgiResolverObject& factory) noexcept override {
    if (!factory) {
      return false;
    }
    ComPtr<IDXGIFactory1> typed_factory;
    return SUCCEEDED(factory.value.As(&typed_factory)) &&
           typed_factory != nullptr && typed_factory->IsCurrent() != FALSE;
  }

  HRESULT EnumAdapter(const DxgiResolverObject& factory, UINT adapter_index,
                      DxgiResolverObject* adapter) noexcept override {
    if (adapter == nullptr) {
      return E_POINTER;
    }
    adapter->Reset();
    if (!factory) {
      return E_NOINTERFACE;
    }
    ComPtr<IDXGIFactory1> typed_factory;
    HRESULT result = factory.value.As(&typed_factory);
    if (FAILED(result) || typed_factory == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    ComPtr<IDXGIAdapter1> typed_adapter;
    result = typed_factory->EnumAdapters1(adapter_index,
                                          typed_adapter.GetAddressOf());
    if (FAILED(result) || typed_adapter == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_adapter.As(&adapter->value);
  }

  HRESULT GetAdapterDescription(
      const DxgiResolverObject& adapter,
      DXGI_ADAPTER_DESC1* description) noexcept override {
    if (description == nullptr) {
      return E_POINTER;
    }
    *description = {};
    if (!adapter) {
      return E_NOINTERFACE;
    }
    ComPtr<IDXGIAdapter1> typed_adapter;
    const HRESULT result = adapter.value.As(&typed_adapter);
    if (FAILED(result) || typed_adapter == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_adapter->GetDesc1(description);
  }

  HRESULT EnumOutput(const DxgiResolverObject& adapter, UINT output_index,
                     DxgiResolverObject* output) noexcept override {
    if (output == nullptr) {
      return E_POINTER;
    }
    output->Reset();
    if (!adapter) {
      return E_NOINTERFACE;
    }
    ComPtr<IDXGIAdapter1> typed_adapter;
    HRESULT result = adapter.value.As(&typed_adapter);
    if (FAILED(result) || typed_adapter == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    ComPtr<IDXGIOutput> typed_output;
    result =
        typed_adapter->EnumOutputs(output_index, typed_output.GetAddressOf());
    if (FAILED(result) || typed_output == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_output.As(&output->value);
  }

  HRESULT GetOutputDescription(
      const DxgiResolverObject& output,
      DXGI_OUTPUT_DESC* description) noexcept override {
    if (description == nullptr) {
      return E_POINTER;
    }
    *description = {};
    if (!output) {
      return E_NOINTERFACE;
    }
    ComPtr<IDXGIOutput> typed_output;
    const HRESULT result = output.value.As(&typed_output);
    if (FAILED(result) || typed_output == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_output->GetDesc(description);
  }

  HRESULT QueryOutput1(const DxgiResolverObject& output,
                       DxgiResolverObject* output1) noexcept override {
    if (output1 == nullptr) {
      return E_POINTER;
    }
    output1->Reset();
    if (!output) {
      return E_NOINTERFACE;
    }
    ComPtr<IDXGIOutput> typed_output;
    HRESULT result = output.value.As(&typed_output);
    if (FAILED(result) || typed_output == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    ComPtr<IDXGIOutput1> typed_output1;
    result = typed_output.As(&typed_output1);
    if (FAILED(result) || typed_output1 == nullptr) {
      return FAILED(result) ? result : E_NOINTERFACE;
    }
    return typed_output1.As(&output1->value);
  }
};

DxgiOutputResolveResult EnumerateDxgiOutputs(
    IDxgiOutputResolverApi& api, DxgiResolverObject* factory,
    std::vector<EnumeratedDxgiOutput>* outputs) {
  if (factory == nullptr || outputs == nullptr) {
    return DxgiOutputResolveResult::kInvalidArgument;
  }
  factory->Reset();
  outputs->clear();

  HRESULT result = api.CreateFactory(factory);
  if (FAILED(result) || !*factory) {
    return DxgiOutputResolveResult::kEnumerationFailed;
  }

  UINT adapter_index = 0;
  for (;;) {
    DxgiResolverObject adapter;
    result = api.EnumAdapter(*factory, adapter_index, &adapter);
    if (result == DXGI_ERROR_NOT_FOUND) {
      break;
    }
    if (FAILED(result) || !adapter) {
      return DxgiOutputResolveResult::kEnumerationFailed;
    }

    DXGI_ADAPTER_DESC1 adapter_description = {};
    result = api.GetAdapterDescription(adapter, &adapter_description);
    if (FAILED(result)) {
      return DxgiOutputResolveResult::kEnumerationFailed;
    }

    UINT output_index = 0;
    for (;;) {
      DxgiResolverObject output;
      result = api.EnumOutput(adapter, output_index, &output);
      if (result == DXGI_ERROR_NOT_FOUND) {
        break;
      }
      if (FAILED(result) || !output) {
        return DxgiOutputResolveResult::kEnumerationFailed;
      }

      DXGI_OUTPUT_DESC output_description = {};
      result = api.GetOutputDescription(output, &output_description);
      if (FAILED(result)) {
        return DxgiOutputResolveResult::kEnumerationFailed;
      }

      outputs->push_back(EnumeratedDxgiOutput{
          CandidateFromDescriptions(adapter_description, output_description),
          adapter, output});

      if (output_index == std::numeric_limits<UINT>::max()) {
        return DxgiOutputResolveResult::kEnumerationFailed;
      }
      ++output_index;
    }

    if (adapter_index == std::numeric_limits<UINT>::max()) {
      return DxgiOutputResolveResult::kEnumerationFailed;
    }
    ++adapter_index;
  }

  return DxgiOutputResolveResult::kResolved;
}

}  // namespace

DxgiOutputSelection SelectUniqueDxgiOutput(
    std::span<const DxgiOutputCandidate> candidates,
    DxgiOutputCatalogState catalog_state, HMONITOR target_monitor,
    std::wstring_view target_device_name) noexcept {
  if (!IsValidDisplayTarget(target_monitor, target_device_name)) {
    return {DxgiOutputResolveResult::kInvalidTarget, kNoDxgiOutputIndex};
  }
  if (catalog_state != DxgiOutputCatalogState::kComplete) {
    return {DxgiOutputResolveResult::kEnumerationFailed, kNoDxgiOutputIndex};
  }

  size_t selected_index = kNoDxgiOutputIndex;
  for (size_t index = 0; index < candidates.size(); ++index) {
    const DxgiOutputCandidate& candidate = candidates[index];
    if (!candidate.attached_to_desktop) {
      continue;
    }
    if (!IsValidAttachedCandidate(candidate)) {
      return {DxgiOutputResolveResult::kInvalidTopology, kNoDxgiOutputIndex};
    }

    const DxgiOutputFingerprint& fingerprint = candidate.fingerprint;
    if (fingerprint.monitor != target_monitor ||
        !DeviceNamesEqual(fingerprint.canonical_device_name,
                          target_device_name)) {
      continue;
    }
    if (selected_index != kNoDxgiOutputIndex) {
      return {DxgiOutputResolveResult::kAmbiguous, kNoDxgiOutputIndex};
    }
    selected_index = index;
  }

  return selected_index == kNoDxgiOutputIndex
             ? DxgiOutputSelection{DxgiOutputResolveResult::kNotFound,
                                   kNoDxgiOutputIndex}
             : DxgiOutputSelection{DxgiOutputResolveResult::kResolved,
                                   selected_index};
}

bool SameDxgiOutputFingerprint(const DxgiOutputFingerprint& left,
                               const DxgiOutputFingerprint& right) noexcept {
  return left.adapter_luid.HighPart == right.adapter_luid.HighPart &&
         left.adapter_luid.LowPart == right.adapter_luid.LowPart &&
         left.monitor == right.monitor &&
         DeviceNamesEqual(left.canonical_device_name,
                          right.canonical_device_name) &&
         left.desktop_coordinates.left == right.desktop_coordinates.left &&
         left.desktop_coordinates.top == right.desktop_coordinates.top &&
         left.desktop_coordinates.right == right.desktop_coordinates.right &&
         left.desktop_coordinates.bottom == right.desktop_coordinates.bottom &&
         left.rotation == right.rotation;
}

void DxgiResolverBinding::Reset() noexcept {
  adapter.Reset();
  output1.Reset();
  fingerprint = {};
}

DxgiOutputResolveResult ResolveDxgiOutputWithApi(
    IDxgiOutputResolverApi& api, HMONITOR target_monitor,
    std::wstring_view target_device_name,
    DxgiResolverBinding* binding) noexcept {
  if (binding == nullptr) {
    return DxgiOutputResolveResult::kInvalidArgument;
  }
  binding->Reset();
  if (!IsValidDisplayTarget(target_monitor, target_device_name)) {
    return DxgiOutputResolveResult::kInvalidTarget;
  }

  try {
    DxgiResolverObject factory;
    std::vector<EnumeratedDxgiOutput> enumerated_outputs;
    const DxgiOutputResolveResult enumeration_result =
        EnumerateDxgiOutputs(api, &factory, &enumerated_outputs);
    if (enumeration_result != DxgiOutputResolveResult::kResolved) {
      return enumeration_result;
    }

    std::vector<DxgiOutputCandidate> candidates;
    candidates.reserve(enumerated_outputs.size());
    for (const EnumeratedDxgiOutput& output : enumerated_outputs) {
      candidates.push_back(output.candidate);
    }

    const DxgiOutputSelection selection =
        SelectUniqueDxgiOutput(candidates, DxgiOutputCatalogState::kComplete,
                               target_monitor, target_device_name);
    if (selection.result != DxgiOutputResolveResult::kResolved) {
      return selection.result;
    }
    if (selection.candidate_index >= enumerated_outputs.size()) {
      return DxgiOutputResolveResult::kInvalidTopology;
    }
    if (!api.IsFactoryCurrent(factory)) {
      return DxgiOutputResolveResult::kInvalidTopology;
    }

    const EnumeratedDxgiOutput& selected =
        enumerated_outputs[selection.candidate_index];
    DXGI_ADAPTER_DESC1 current_adapter_description = {};
    DXGI_OUTPUT_DESC current_output_description = {};
    if (FAILED(api.GetAdapterDescription(selected.adapter,
                                         &current_adapter_description)) ||
        FAILED(api.GetOutputDescription(selected.output,
                                        &current_output_description))) {
      return DxgiOutputResolveResult::kEnumerationFailed;
    }
    if (!api.IsFactoryCurrent(factory)) {
      return DxgiOutputResolveResult::kInvalidTopology;
    }

    const DxgiOutputCandidate current_candidate = CandidateFromDescriptions(
        current_adapter_description, current_output_description);
    if (!current_candidate.attached_to_desktop ||
        !IsValidAttachedCandidate(current_candidate) ||
        !SameDxgiOutputFingerprint(selected.candidate.fingerprint,
                                   current_candidate.fingerprint)) {
      return DxgiOutputResolveResult::kInvalidTopology;
    }

    DxgiResolverObject output1;
    const HRESULT query_result = api.QueryOutput1(selected.output, &output1);
    if (FAILED(query_result) || !output1) {
      return DxgiOutputResolveResult::kUnsupportedOutput;
    }

    binding->adapter = selected.adapter;
    binding->output1 = std::move(output1);
    binding->fingerprint = current_candidate.fingerprint;
    return DxgiOutputResolveResult::kResolved;
  } catch (...) {
    binding->Reset();
    return DxgiOutputResolveResult::kEnumerationFailed;
  }
}

void ResolvedDxgiOutput::Reset() noexcept {
  adapter.Reset();
  output.Reset();
  fingerprint = {};
}

DxgiOutputResolveResult ResolveDxgiOutput(
    HMONITOR target_monitor, std::wstring_view target_device_name,
    ResolvedDxgiOutput* resolved_output) noexcept {
  if (resolved_output == nullptr) {
    return DxgiOutputResolveResult::kInvalidArgument;
  }
  resolved_output->Reset();
  if (!IsValidDisplayTarget(target_monitor, target_device_name)) {
    return DxgiOutputResolveResult::kInvalidTarget;
  }

  try {
    WindowsDxgiOutputResolverApi api;
    DxgiResolverBinding binding;
    const DxgiOutputResolveResult result = ResolveDxgiOutputWithApi(
        api, target_monitor, target_device_name, &binding);
    if (result != DxgiOutputResolveResult::kResolved) {
      return result;
    }

    if (!binding.adapter || !binding.output1) {
      return DxgiOutputResolveResult::kInvalidTopology;
    }
    HRESULT conversion_result =
        binding.adapter.value.As(&resolved_output->adapter);
    if (FAILED(conversion_result) || resolved_output->adapter == nullptr) {
      resolved_output->Reset();
      return DxgiOutputResolveResult::kInvalidTopology;
    }
    conversion_result = binding.output1.value.As(&resolved_output->output);
    if (FAILED(conversion_result) || resolved_output->output == nullptr) {
      resolved_output->Reset();
      return DxgiOutputResolveResult::kUnsupportedOutput;
    }
    resolved_output->fingerprint = std::move(binding.fingerprint);
    return DxgiOutputResolveResult::kResolved;
  } catch (...) {
    resolved_output->Reset();
    return DxgiOutputResolveResult::kEnumerationFailed;
  }
}

}  // namespace windayflow::capture

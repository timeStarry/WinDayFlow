#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <iostream>
#include <limits>
#include <string>
#include <utility>
#include <vector>

#include "dxgi_output_resolver.h"

namespace {

using windayflow::capture::DxgiOutputCandidate;
using windayflow::capture::DxgiOutputCatalogState;
using windayflow::capture::DxgiOutputResolveResult;
using windayflow::capture::DxgiOutputSelection;
using windayflow::capture::DxgiResolverBinding;
using windayflow::capture::DxgiResolverObject;
using windayflow::capture::IDxgiOutputResolverApi;

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

HMONITOR TestMonitor(uintptr_t value) {
  return reinterpret_cast<HMONITOR>(value);
}

LUID TestLuid(LONG high_part, DWORD low_part) {
  LUID luid = {};
  luid.HighPart = high_part;
  luid.LowPart = low_part;
  return luid;
}

class FakeUnknown final : public IUnknown {
 public:
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interface_id,
                                           void** object) override {
    if (object == nullptr) {
      return E_POINTER;
    }
    *object = nullptr;
    if (!IsEqualIID(interface_id, __uuidof(IUnknown))) {
      return E_NOINTERFACE;
    }
    *object = static_cast<IUnknown*>(this);
    AddRef();
    return S_OK;
  }

  ULONG STDMETHODCALLTYPE AddRef() override {
    return references_.fetch_add(1, std::memory_order_relaxed) + 1;
  }

  ULONG STDMETHODCALLTYPE Release() override {
    const ULONG remaining =
        references_.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (remaining == 0) {
      delete this;
    }
    return remaining;
  }

 private:
  std::atomic<ULONG> references_{1};
};

DxgiResolverObject TestObject() {
  DxgiResolverObject object;
  object.value.Attach(new FakeUnknown());
  return object;
}

DXGI_OUTPUT_DESC OutputDescription(
    uintptr_t monitor, std::wstring_view device_name,
    RECT coordinates = RECT{0, 0, 1920, 1080},
    DXGI_MODE_ROTATION rotation = DXGI_MODE_ROTATION_IDENTITY,
    bool attached_to_desktop = true) {
  DXGI_OUTPUT_DESC description = {};
  const size_t copied =
      (std::min)(device_name.size(), static_cast<size_t>(CCHDEVICENAME - 1));
  std::copy_n(device_name.data(), copied, description.DeviceName);
  description.DeviceName[copied] = L'\0';
  description.DesktopCoordinates = coordinates;
  description.AttachedToDesktop = attached_to_desktop ? TRUE : FALSE;
  description.Rotation = rotation;
  description.Monitor = TestMonitor(monitor);
  return description;
}

class FakeDxgiOutputResolverApi final : public IDxgiOutputResolverApi {
 public:
  struct OutputState {
    DxgiResolverObject output = TestObject();
    DxgiResolverObject output1 = TestObject();
    DXGI_OUTPUT_DESC initial_description = {};
    DXGI_OUTPUT_DESC current_description = {};
    HRESULT initial_description_result = S_OK;
    HRESULT current_description_result = S_OK;
    HRESULT query_result = S_OK;
    int description_calls = 0;
    int query_calls = 0;
  };

  struct AdapterState {
    DxgiResolverObject adapter = TestObject();
    DXGI_ADAPTER_DESC1 initial_description = {};
    DXGI_ADAPTER_DESC1 current_description = {};
    HRESULT initial_description_result = S_OK;
    HRESULT current_description_result = S_OK;
    int description_calls = 0;
    std::vector<OutputState> outputs;
    UINT output_failure_index = std::numeric_limits<UINT>::max();
    HRESULT output_failure_result = E_FAIL;
  };

  FakeDxgiOutputResolverApi() : factory_(TestObject()) {}

  size_t AddAdapter(LUID luid) {
    AdapterState adapter;
    adapter.initial_description.AdapterLuid = luid;
    adapter.current_description = adapter.initial_description;
    adapters_.push_back(std::move(adapter));
    return adapters_.size() - 1;
  }

  size_t AddOutput(size_t adapter_index, const DXGI_OUTPUT_DESC& description) {
    OutputState output;
    output.initial_description = description;
    output.current_description = description;
    adapters_.at(adapter_index).outputs.push_back(std::move(output));
    return adapters_.at(adapter_index).outputs.size() - 1;
  }

  AdapterState& Adapter(size_t index) { return adapters_.at(index); }
  OutputState& Output(size_t adapter_index, size_t output_index) {
    return adapters_.at(adapter_index).outputs.at(output_index);
  }

  IUnknown* AdapterIdentity(size_t index) const {
    return adapters_.at(index).adapter.value.Get();
  }
  IUnknown* Output1Identity(size_t adapter_index, size_t output_index) const {
    return adapters_.at(adapter_index)
        .outputs.at(output_index)
        .output1.value.Get();
  }

  HRESULT create_result = S_OK;
  UINT adapter_failure_index = std::numeric_limits<UINT>::max();
  HRESULT adapter_failure_result = E_FAIL;
  std::vector<bool> factory_current_results{true, true};
  int create_calls = 0;
  int factory_current_calls = 0;

  HRESULT CreateFactory(DxgiResolverObject* factory) noexcept override {
    ++create_calls;
    if (factory == nullptr) {
      return E_POINTER;
    }
    factory->Reset();
    if (FAILED(create_result)) {
      return create_result;
    }
    *factory = factory_;
    return S_OK;
  }

  bool IsFactoryCurrent(const DxgiResolverObject& factory) noexcept override {
    if (factory.value.Get() != factory_.value.Get()) {
      return false;
    }
    const size_t index = static_cast<size_t>(factory_current_calls++);
    return index < factory_current_results.size()
               ? factory_current_results[index]
               : factory_current_results.back();
  }

  HRESULT EnumAdapter(const DxgiResolverObject& factory, UINT adapter_index,
                      DxgiResolverObject* adapter) noexcept override {
    if (adapter == nullptr || factory.value.Get() != factory_.value.Get()) {
      return E_INVALIDARG;
    }
    adapter->Reset();
    if (adapter_index == adapter_failure_index) {
      return adapter_failure_result;
    }
    if (adapter_index >= adapters_.size()) {
      return DXGI_ERROR_NOT_FOUND;
    }
    *adapter = adapters_[adapter_index].adapter;
    return S_OK;
  }

  HRESULT GetAdapterDescription(
      const DxgiResolverObject& adapter,
      DXGI_ADAPTER_DESC1* description) noexcept override {
    if (description == nullptr) {
      return E_POINTER;
    }
    for (AdapterState& state : adapters_) {
      if (state.adapter.value.Get() != adapter.value.Get()) {
        continue;
      }
      const bool current = state.description_calls++ > 0;
      const HRESULT result = current ? state.current_description_result
                                     : state.initial_description_result;
      if (FAILED(result)) {
        return result;
      }
      *description =
          current ? state.current_description : state.initial_description;
      return S_OK;
    }
    return E_INVALIDARG;
  }

  HRESULT EnumOutput(const DxgiResolverObject& adapter, UINT output_index,
                     DxgiResolverObject* output) noexcept override {
    if (output == nullptr) {
      return E_POINTER;
    }
    output->Reset();
    for (AdapterState& state : adapters_) {
      if (state.adapter.value.Get() != adapter.value.Get()) {
        continue;
      }
      if (output_index == state.output_failure_index) {
        return state.output_failure_result;
      }
      if (output_index >= state.outputs.size()) {
        return DXGI_ERROR_NOT_FOUND;
      }
      *output = state.outputs[output_index].output;
      return S_OK;
    }
    return E_INVALIDARG;
  }

  HRESULT GetOutputDescription(
      const DxgiResolverObject& output,
      DXGI_OUTPUT_DESC* description) noexcept override {
    if (description == nullptr) {
      return E_POINTER;
    }
    for (AdapterState& adapter : adapters_) {
      for (OutputState& state : adapter.outputs) {
        if (state.output.value.Get() != output.value.Get()) {
          continue;
        }
        const bool current = state.description_calls++ > 0;
        const HRESULT result = current ? state.current_description_result
                                       : state.initial_description_result;
        if (FAILED(result)) {
          return result;
        }
        *description =
            current ? state.current_description : state.initial_description;
        return S_OK;
      }
    }
    return E_INVALIDARG;
  }

  HRESULT QueryOutput1(const DxgiResolverObject& output,
                       DxgiResolverObject* output1) noexcept override {
    if (output1 == nullptr) {
      return E_POINTER;
    }
    output1->Reset();
    for (AdapterState& adapter : adapters_) {
      for (OutputState& state : adapter.outputs) {
        if (state.output.value.Get() != output.value.Get()) {
          continue;
        }
        ++state.query_calls;
        if (FAILED(state.query_result)) {
          return state.query_result;
        }
        *output1 = state.output1;
        return S_OK;
      }
    }
    return E_INVALIDARG;
  }

 private:
  DxgiResolverObject factory_;
  std::vector<AdapterState> adapters_;
};

DxgiOutputCandidate Candidate(
    uintptr_t monitor, std::wstring device_name,
    LUID adapter_luid = TestLuid(1, 1),
    RECT coordinates = RECT{0, 0, 1920, 1080},
    DXGI_MODE_ROTATION rotation = DXGI_MODE_ROTATION_IDENTITY,
    bool attached_to_desktop = true) {
  DxgiOutputCandidate candidate;
  candidate.attached_to_desktop = attached_to_desktop;
  candidate.fingerprint.adapter_luid = adapter_luid;
  candidate.fingerprint.monitor = TestMonitor(monitor);
  candidate.fingerprint.canonical_device_name = std::move(device_name);
  candidate.fingerprint.desktop_coordinates = coordinates;
  candidate.fingerprint.rotation = rotation;
  return candidate;
}

DxgiOutputSelection Select(
    const std::span<const DxgiOutputCandidate> candidates,
    HMONITOR monitor = TestMonitor(7),
    std::wstring_view device_name = LR"(\\.\DISPLAY7)",
    DxgiOutputCatalogState catalog_state = DxgiOutputCatalogState::kComplete) {
  return windayflow::capture::SelectUniqueDxgiOutput(candidates, catalog_state,
                                                     monitor, device_name);
}

bool TestUniqueExactMatchAndCanonicalFingerprint() {
  const std::array candidates = {
      Candidate(1, LR"(\\.\DISPLAY1)", TestLuid(1, 10)),
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(2, 20), RECT{-1920, 0, 0, 1080},
                DXGI_MODE_ROTATION_ROTATE90),
      Candidate(9, LR"(\\.\DISPLAY9)", TestLuid(3, 30)),
  };
  const DxgiOutputSelection selection =
      Select(candidates, TestMonitor(7), LR"(\\.\display7)");
  const auto& fingerprint = candidates[1].fingerprint;
  return Expect(selection.result == DxgiOutputResolveResult::kResolved,
                "unique exact output was not resolved") &&
         Expect(selection.candidate_index == 1,
                "resolver depended on the first enumerated output") &&
         Expect(fingerprint.adapter_luid.HighPart == 2 &&
                    fingerprint.adapter_luid.LowPart == 20,
                "selected fingerprint lost owning adapter LUID") &&
         Expect(fingerprint.monitor == TestMonitor(7),
                "selected fingerprint lost HMONITOR") &&
         Expect(fingerprint.canonical_device_name == LR"(\\.\DISPLAY7)",
                "selected fingerprint did not retain canonical device name") &&
         Expect(fingerprint.desktop_coordinates.left == -1920 &&
                    fingerprint.desktop_coordinates.right == 0,
                "selected fingerprint lost desktop coordinates") &&
         Expect(fingerprint.rotation == DXGI_MODE_ROTATION_ROTATE90,
                "selected fingerprint lost rotation");
}

bool TestNoPrimaryOrPartialKeyFallback() {
  const std::array candidates = {
      Candidate(1, LR"(\\.\DISPLAY1)"),
      Candidate(7, LR"(\\.\DISPLAY8)"),
      Candidate(8, LR"(\\.\DISPLAY7)"),
  };
  const DxgiOutputSelection selection = Select(candidates);
  return Expect(selection.result == DxgiOutputResolveResult::kNotFound,
                "monitor-only or key-only candidate was selected") &&
         Expect(selection.candidate_index ==
                    windayflow::capture::kNoDxgiOutputIndex,
                "not-found selection retained a candidate index");
}

bool TestAmbiguousExactMatchesFailClosed() {
  const std::array candidates = {
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1)),
      Candidate(7, LR"(\\.\display7)", TestLuid(2, 2),
                RECT{1920, 0, 3840, 1080}),
  };
  const DxgiOutputSelection selection = Select(candidates);
  return Expect(selection.result == DxgiOutputResolveResult::kAmbiguous,
                "two exact outputs did not fail as ambiguous") &&
         Expect(selection.candidate_index ==
                    windayflow::capture::kNoDxgiOutputIndex,
                "ambiguous selection retained a candidate index");
}

bool TestIncompleteEnumerationRejectsPartialExactMatch() {
  const std::array candidates = {
      Candidate(7, LR"(\\.\DISPLAY7)"),
  };
  const DxgiOutputSelection selection =
      Select(candidates, TestMonitor(7), LR"(\\.\DISPLAY7)",
             DxgiOutputCatalogState::kFailed);
  return Expect(selection.result == DxgiOutputResolveResult::kEnumerationFailed,
                "partial output catalog was treated as complete") &&
         Expect(selection.candidate_index ==
                    windayflow::capture::kNoDxgiOutputIndex,
                "failed enumeration retained a candidate index");
}

bool TestInvalidTargetsFailBeforeSelection() {
  const std::array candidates = {
      Candidate(7, LR"(\\.\DISPLAY7)"),
  };
  const std::wstring embedded_null(
      LR"(\\.\DIS)"
      L"\0"
      L"PLAY7",
      13);
  const std::wstring too_long(32, L'A');
  const std::wstring control =
      LR"(\\.\DISPLAY)"
      L"\x7f";
  const std::wstring whitespace = L"   ";
  return Expect(Select(candidates, nullptr).result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "null target monitor was accepted") &&
         Expect(Select(candidates, TestMonitor(7), L"").result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "empty target device name was accepted") &&
         Expect(Select(candidates, TestMonitor(7), embedded_null).result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "embedded-null target device name was accepted") &&
         Expect(Select(candidates, TestMonitor(7), too_long).result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "overlength target device name was accepted") &&
         Expect(Select(candidates, TestMonitor(7), control).result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "control character in target device name was accepted") &&
         Expect(Select(candidates, TestMonitor(7), whitespace).result ==
                    DxgiOutputResolveResult::kInvalidTarget,
                "whitespace-only target device name was accepted");
}

bool TestAttachedAndTopologyValidation() {
  const std::array detached = {
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1), RECT{0, 0, 1920, 1080},
                DXGI_MODE_ROTATION_IDENTITY, false),
  };
  if (!Expect(Select(detached).result == DxgiOutputResolveResult::kNotFound,
              "detached output was selected")) {
    return false;
  }

  const std::array invalid_candidates = {
      Candidate(0, LR"(\\.\DISPLAY7)"),
      Candidate(7, L""),
      Candidate(7, L"   "),
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1), RECT{0, 0, 0, 1080}),
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1), RECT{0, 1080, 1920, 0}),
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1), RECT{0, 0, 1920, 1080},
                DXGI_MODE_ROTATION_UNSPECIFIED),
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1), RECT{0, 0, 1920, 1080},
                static_cast<DXGI_MODE_ROTATION>(99)),
  };
  for (const DxgiOutputCandidate& candidate : invalid_candidates) {
    const std::array one = {candidate};
    if (!Expect(Select(one).result == DxgiOutputResolveResult::kInvalidTopology,
                "invalid attached output topology was accepted")) {
      return false;
    }
  }
  return true;
}

bool TestAllSupportedRotationsResolve() {
  const std::array rotations = {
      DXGI_MODE_ROTATION_IDENTITY,
      DXGI_MODE_ROTATION_ROTATE90,
      DXGI_MODE_ROTATION_ROTATE180,
      DXGI_MODE_ROTATION_ROTATE270,
  };
  for (const DXGI_MODE_ROTATION rotation : rotations) {
    const std::array candidates = {
        Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(1, 1),
                  RECT{-1080, -1920, 0, 0}, rotation),
    };
    if (!Expect(Select(candidates).result == DxgiOutputResolveResult::kResolved,
                "supported rotation was rejected")) {
      return false;
    }
  }
  return true;
}

bool TestFingerprintIncludesEveryTopologyField() {
  const auto base =
      Candidate(7, LR"(\\.\DISPLAY7)", TestLuid(10, 20),
                RECT{-1920, 0, 0, 1080}, DXGI_MODE_ROTATION_ROTATE90)
          .fingerprint;
  auto changed = base;
  changed.canonical_device_name = LR"(\\.\display7)";
  if (!Expect(windayflow::capture::SameDxgiOutputFingerprint(base, changed),
              "fingerprint device comparison was not ordinal-ignore-case")) {
    return false;
  }

  changed = base;
  changed.adapter_luid.LowPart++;
  const bool luid_changed =
      !windayflow::capture::SameDxgiOutputFingerprint(base, changed);
  changed = base;
  changed.monitor = TestMonitor(8);
  const bool monitor_changed =
      !windayflow::capture::SameDxgiOutputFingerprint(base, changed);
  changed = base;
  changed.canonical_device_name = LR"(\\.\DISPLAY8)";
  const bool name_changed =
      !windayflow::capture::SameDxgiOutputFingerprint(base, changed);
  changed = base;
  changed.desktop_coordinates.left--;
  const bool rectangle_changed =
      !windayflow::capture::SameDxgiOutputFingerprint(base, changed);
  changed = base;
  changed.rotation = DXGI_MODE_ROTATION_ROTATE270;
  const bool rotation_changed =
      !windayflow::capture::SameDxgiOutputFingerprint(base, changed);
  return Expect(luid_changed && monitor_changed && name_changed &&
                    rectangle_changed && rotation_changed,
                "fingerprint omitted a topology identity field");
}

bool TestInjectedApiResolvesAndPreservesSelectedOwnership() {
  FakeDxgiOutputResolverApi api;
  const size_t first_adapter = api.AddAdapter(TestLuid(1, 10));
  api.AddOutput(first_adapter, OutputDescription(1, LR"(\\.\DISPLAY1)"));
  const size_t selected_adapter = api.AddAdapter(TestLuid(2, 20));
  const size_t selected_output = api.AddOutput(
      selected_adapter,
      OutputDescription(7, LR"(\\.\DISPLAY7)", RECT{-1920, 0, 0, 1080},
                        DXGI_MODE_ROTATION_ROTATE90));

  DxgiResolverBinding binding;
  const DxgiOutputResolveResult result =
      windayflow::capture::ResolveDxgiOutputWithApi(
          api, TestMonitor(7), LR"(\\.\display7)", &binding);
  return Expect(result == DxgiOutputResolveResult::kResolved,
                "injected API could not resolve the unique output") &&
         Expect(binding.adapter.value.Get() ==
                    api.AdapterIdentity(selected_adapter),
                "resolved binding did not preserve the owning adapter") &&
         Expect(binding.output1.value.Get() ==
                    api.Output1Identity(selected_adapter, selected_output),
                "resolved binding did not preserve the selected output1") &&
         Expect(binding.fingerprint.adapter_luid.HighPart == 2 &&
                    binding.fingerprint.adapter_luid.LowPart == 20 &&
                    binding.fingerprint.monitor == TestMonitor(7) &&
                    binding.fingerprint.canonical_device_name ==
                        LR"(\\.\DISPLAY7)" &&
                    binding.fingerprint.desktop_coordinates.left == -1920 &&
                    binding.fingerprint.rotation == DXGI_MODE_ROTATION_ROTATE90,
                "resolved binding lost its complete fingerprint") &&
         Expect(
             api.factory_current_calls == 2,
             "resolver did not check factory currency around revalidation") &&
         Expect(api.Adapter(selected_adapter).description_calls == 2 &&
                    api.Output(selected_adapter, selected_output)
                            .description_calls == 2,
                "resolver did not reread the selected adapter and output") &&
         Expect(
             api.Output(first_adapter, 0).query_calls == 0 &&
                 api.Output(selected_adapter, selected_output).query_calls == 1,
             "resolver queried output1 for the wrong candidate");
}

bool TestInjectedApiFailsClosedAtEveryEnumerationStage() {
  DxgiResolverBinding binding;

  FakeDxgiOutputResolverApi create_failure;
  create_failure.create_result = E_FAIL;
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  create_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kEnumerationFailed,
              "factory creation failure did not fail closed")) {
    return false;
  }

  FakeDxgiOutputResolverApi adapter_failure;
  adapter_failure.adapter_failure_index = 0;
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  adapter_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kEnumerationFailed,
              "adapter enumeration failure did not fail closed")) {
    return false;
  }

  FakeDxgiOutputResolverApi adapter_description_failure;
  const size_t adapter = adapter_description_failure.AddAdapter(TestLuid(1, 1));
  adapter_description_failure.Adapter(adapter).initial_description_result =
      E_FAIL;
  if (!Expect(
          windayflow::capture::ResolveDxgiOutputWithApi(
              adapter_description_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
              &binding) == DxgiOutputResolveResult::kEnumerationFailed,
          "adapter description failure did not fail closed")) {
    return false;
  }

  FakeDxgiOutputResolverApi partial_output_failure;
  const size_t partial_adapter =
      partial_output_failure.AddAdapter(TestLuid(1, 1));
  const size_t partial_output = partial_output_failure.AddOutput(
      partial_adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  partial_output_failure.Adapter(partial_adapter).output_failure_index = 1;
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  partial_output_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kEnumerationFailed &&
                  partial_output_failure.Output(partial_adapter, partial_output)
                          .query_calls == 0,
              "partial output catalog authorized an enumerated candidate")) {
    return false;
  }

  FakeDxgiOutputResolverApi output_description_failure;
  const size_t output_adapter =
      output_description_failure.AddAdapter(TestLuid(1, 1));
  const size_t output = output_description_failure.AddOutput(
      output_adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  output_description_failure.Output(output_adapter, output)
      .initial_description_result = E_FAIL;
  if (!Expect(
          windayflow::capture::ResolveDxgiOutputWithApi(
              output_description_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
              &binding) == DxgiOutputResolveResult::kEnumerationFailed,
          "output description failure did not fail closed")) {
    return false;
  }

  FakeDxgiOutputResolverApi query_failure;
  const size_t query_adapter = query_failure.AddAdapter(TestLuid(1, 1));
  const size_t query_output = query_failure.AddOutput(
      query_adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  query_failure.Output(query_adapter, query_output).query_result =
      E_NOINTERFACE;
  return Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                    query_failure, TestMonitor(7), LR"(\\.\DISPLAY7)",
                    &binding) == DxgiOutputResolveResult::kUnsupportedOutput,
                "IDXGIOutput1 query failure did not fail closed");
}

bool TestInjectedApiRejectsStaleFactoryAndDescriptionRaces() {
  DxgiResolverBinding binding;

  FakeDxgiOutputResolverApi stale_factory;
  const size_t stale_adapter = stale_factory.AddAdapter(TestLuid(1, 1));
  stale_factory.AddOutput(stale_adapter,
                          OutputDescription(7, LR"(\\.\DISPLAY7)"));
  stale_factory.factory_current_results = {false};
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  stale_factory, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kInvalidTopology,
              "stale factory after enumeration was accepted")) {
    return false;
  }

  FakeDxgiOutputResolverApi stale_during_revalidation;
  const size_t changing_adapter =
      stale_during_revalidation.AddAdapter(TestLuid(1, 1));
  stale_during_revalidation.AddOutput(changing_adapter,
                                      OutputDescription(7, LR"(\\.\DISPLAY7)"));
  stale_during_revalidation.factory_current_results = {true, false};
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  stale_during_revalidation, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kInvalidTopology,
              "factory change during description revalidation was accepted")) {
    return false;
  }

  FakeDxgiOutputResolverApi changed_luid;
  const size_t luid_adapter = changed_luid.AddAdapter(TestLuid(1, 1));
  changed_luid.AddOutput(luid_adapter,
                         OutputDescription(7, LR"(\\.\DISPLAY7)"));
  changed_luid.Adapter(luid_adapter).current_description.AdapterLuid =
      TestLuid(2, 2);
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  changed_luid, TestMonitor(7), LR"(\\.\DISPLAY7)", &binding) ==
                  DxgiOutputResolveResult::kInvalidTopology,
              "adapter LUID change after selection was accepted")) {
    return false;
  }

  FakeDxgiOutputResolverApi changed_output;
  const size_t output_adapter = changed_output.AddAdapter(TestLuid(1, 1));
  const size_t output_index = changed_output.AddOutput(
      output_adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  changed_output.Output(output_adapter, output_index)
      .current_description.DesktopCoordinates.left = -1;
  if (!Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                  changed_output, TestMonitor(7), LR"(\\.\DISPLAY7)",
                  &binding) == DxgiOutputResolveResult::kInvalidTopology,
              "output fingerprint change after selection was accepted")) {
    return false;
  }

  FakeDxgiOutputResolverApi detached_output;
  const size_t detached_adapter = detached_output.AddAdapter(TestLuid(1, 1));
  const size_t detached_index = detached_output.AddOutput(
      detached_adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  detached_output.Output(detached_adapter, detached_index)
      .current_description.AttachedToDesktop = FALSE;
  return Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                    detached_output, TestMonitor(7), LR"(\\.\DISPLAY7)",
                    &binding) == DxgiOutputResolveResult::kInvalidTopology,
                "selected output detachment was accepted");
}

bool TestInjectedApiRereadFailuresClearTheBinding() {
  FakeDxgiOutputResolverApi api;
  const size_t adapter = api.AddAdapter(TestLuid(1, 1));
  const size_t output =
      api.AddOutput(adapter, OutputDescription(7, LR"(\\.\DISPLAY7)"));
  api.Output(adapter, output).current_description_result = E_FAIL;
  DxgiResolverBinding binding;
  binding.adapter = TestObject();
  binding.output1 = TestObject();
  binding.fingerprint.monitor = TestMonitor(99);
  return Expect(windayflow::capture::ResolveDxgiOutputWithApi(
                    api, TestMonitor(7), LR"(\\.\DISPLAY7)", &binding) ==
                    DxgiOutputResolveResult::kEnumerationFailed,
                "selected output reread failure did not fail closed") &&
         Expect(!binding.adapter && !binding.output1 &&
                    binding.fingerprint.monitor == nullptr,
                "failed injected resolution retained a prior binding");
}

bool TestProductionEntryRejectsInvalidInputAndClearsOutput() {
  windayflow::capture::ResolvedDxgiOutput resolved;
  resolved.fingerprint.monitor = TestMonitor(99);
  resolved.fingerprint.canonical_device_name = LR"(\\.\PRIVATE)";
  const auto result = windayflow::capture::ResolveDxgiOutput(
      nullptr, LR"(\\.\DISPLAY7)", &resolved);
  return Expect(result == DxgiOutputResolveResult::kInvalidTarget,
                "production resolver accepted an invalid target") &&
         Expect(resolved.adapter == nullptr && resolved.output == nullptr,
                "failed production resolution retained COM ownership") &&
         Expect(resolved.fingerprint.monitor == nullptr &&
                    resolved.fingerprint.canonical_device_name.empty(),
                "failed production resolution retained a fingerprint") &&
         Expect(windayflow::capture::ResolveDxgiOutput(
                    TestMonitor(7), LR"(\\.\DISPLAY7)", nullptr) ==
                    DxgiOutputResolveResult::kInvalidArgument,
                "production resolver accepted a null destination");
}

}  // namespace

int main() {
  const bool passed = TestUniqueExactMatchAndCanonicalFingerprint() &&
                      TestNoPrimaryOrPartialKeyFallback() &&
                      TestAmbiguousExactMatchesFailClosed() &&
                      TestIncompleteEnumerationRejectsPartialExactMatch() &&
                      TestInvalidTargetsFailBeforeSelection() &&
                      TestAttachedAndTopologyValidation() &&
                      TestAllSupportedRotationsResolve() &&
                      TestFingerprintIncludesEveryTopologyField() &&
                      TestInjectedApiResolvesAndPreservesSelectedOwnership() &&
                      TestInjectedApiFailsClosedAtEveryEnumerationStage() &&
                      TestInjectedApiRejectsStaleFactoryAndDescriptionRaces() &&
                      TestInjectedApiRereadFailuresClearTheBinding() &&
                      TestProductionEntryRejectsInvalidInputAndClearsOutput();
  if (!passed) {
    return 1;
  }
  std::cout << "dxgi output resolver tests passed\n";
  return 0;
}

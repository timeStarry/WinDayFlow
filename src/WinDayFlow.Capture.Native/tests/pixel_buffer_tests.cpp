// Derived from QiDayflow windows/runner/capture_pixel_buffer_test.cpp at commit
// 8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e.
// Original SHA-256: 5D7D15389F0FB83CDA0B775225A47225A4B1D58A0A2C62EF8E48464FEC0016BF.
// Derived and modified for WinDayFlow; see THIRD_PARTY_NOTICES.md.

#include "pixel_buffer.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <limits>

namespace {

bool Expect(bool condition, const char* message) {
  if (condition) {
    return true;
  }
  std::cerr << message << '\n';
  return false;
}

bool TestTopDownRows() {
  constexpr std::array<uint8_t, 16> source = {
      0, 0, 255, 255, 0, 0, 255, 255,
      255, 0, 0, 255, 255, 0, 0, 255,
  };
  std::array<uint8_t, source.size()> destination{};
  return Expect(windayflow::capture::CopyTopDownBgraRows(
                    source.data(), source.size(), 2, 2, destination.data(),
                    destination.size()),
                "valid top-down frame was rejected") &&
         Expect(destination == source, "top-down rows were inverted");
}

bool TestPaddedPositiveStride() {
  constexpr ptrdiff_t stride = 12;
  constexpr size_t row_bytes = 8;
  constexpr std::array<uint8_t, 20> source = {
      1, 2, 3, 4, 5, 6, 7, 8, 90, 91, 92, 93,
      11, 12, 13, 14, 15, 16, 17, 18,
  };
  static_assert(source.size() == static_cast<size_t>(stride) + row_bytes);
  constexpr std::array<uint8_t, 16> expected = {
      1, 2, 3, 4, 5, 6, 7, 8,
      11, 12, 13, 14, 15, 16, 17, 18,
  };
  std::array<uint8_t, expected.size()> destination{};
  return Expect(windayflow::capture::CopyDecodedRgb32Rows(
                    source.data(), source.size(), 2, 2, stride,
                    destination.data(), destination.size()),
                "positive padded stride was rejected") &&
         Expect(destination == expected,
                "positive padded stride copied padding or inverted rows");
}

bool TestSingleRowDoesNotAdvanceByStride() {
  constexpr std::array<uint8_t, 8> source = {1, 2, 3, 4, 5, 6, 7, 8};
  std::array<uint8_t, source.size()> destination{};
  const auto copy_with_stride = [&](ptrdiff_t stride, const char* message) {
    destination.fill(0);
    return Expect(windayflow::capture::CopyDecodedRgb32Rows(
                      source.data(), source.size(), 2, 1, stride,
                      destination.data(), destination.size()),
                  message) &&
           Expect(destination == source, "single row pixels were not copied");
  };

  return copy_with_stride(12, "single row positive stride was rejected") &&
         copy_with_stride(-12, "single row negative stride was rejected") &&
         copy_with_stride(std::numeric_limits<ptrdiff_t>::min(),
                          "single row minimum ptrdiff stride was rejected");
}

bool TestNegativeStride() {
  constexpr std::array<uint8_t, 20> source = {
      11, 12, 13, 14, 15, 16, 17, 18, 90, 91, 92, 93,
      1, 2, 3, 4, 5, 6, 7, 8,
  };
  constexpr std::array<uint8_t, 16> expected = {
      1, 2, 3, 4, 5, 6, 7, 8,
      11, 12, 13, 14, 15, 16, 17, 18,
  };
  std::array<uint8_t, expected.size()> destination{};
  return Expect(windayflow::capture::CopyDecodedRgb32Rows(
                    source.data(), source.size(), 2, 2, -12,
                    destination.data(), destination.size()),
                "negative stride was rejected") &&
         Expect(destination == expected, "negative stride row order was wrong");
}

bool TestInvalidInputs() {
  std::array<uint8_t, 16> source{};
  std::array<uint8_t, 16> destination{};
  return Expect(!windayflow::capture::CopyTopDownBgraRows(
                    nullptr, source.size(), 2, 2, destination.data(),
                    destination.size()),
                "null source was accepted") &&
         Expect(!windayflow::capture::CopyTopDownBgraRows(
                    source.data(), source.size() - 1U, 2, 2,
                    destination.data(), destination.size()),
                "short tightly packed source was accepted") &&
         Expect(!windayflow::capture::CopyDecodedRgb32Rows(
                    source.data(), source.size(), 2, 2, 0,
                    destination.data(), destination.size()),
                "zero stride was accepted") &&
         Expect(!windayflow::capture::CopyDecodedRgb32Rows(
                    source.data(), source.size(), 2, 2,
                    std::numeric_limits<ptrdiff_t>::min(), destination.data(),
                    destination.size()),
                "minimum ptrdiff stride overflow was accepted") &&
         Expect(!windayflow::capture::CopyDecodedRgb32Rows(
                    source.data(), source.size(),
                    std::numeric_limits<uint32_t>::max(), 2, 8,
                    destination.data(), destination.size()),
                "overflowing width was accepted");
}

}  // namespace

int main() {
  if (!TestTopDownRows() || !TestPaddedPositiveStride() ||
      !TestSingleRowDoesNotAdvanceByStride() || !TestNegativeStride() ||
      !TestInvalidInputs()) {
    return 1;
  }
  std::cout << "pixel buffer tests passed\n";
  return 0;
}

#ifndef WINDAYFLOW_WINDOWS_CAPTURE_WORKER_BACKEND_H_
#define WINDAYFLOW_WINDOWS_CAPTURE_WORKER_BACKEND_H_

#include <memory>
#include <string>
#include <string_view>

#include "capture_worker.h"

namespace windayflow::capture {

bool TryConvertCaptureOutputDirectory(std::string_view utf8,
                                      std::wstring* utf16) noexcept;

std::unique_ptr<CaptureWorkerBackend> CreateWindowsCaptureWorkerBackend(
    std::wstring output_root) noexcept;

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_WINDOWS_CAPTURE_WORKER_BACKEND_H_

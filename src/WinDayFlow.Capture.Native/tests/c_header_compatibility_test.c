#include "windayflow_capture.h"

#include <stdint.h>
#include <stdio.h>

int main(void) {
  wdf_capture_capabilities capabilities = 0;

  if (wdf_capture_get_abi_version() != WDF_CAPTURE_ABI_VERSION) {
    fputs("C translation unit observed an unexpected ABI version\n", stderr);
    return 1;
  }

  if (wdf_capture_get_capabilities(&capabilities) != WDF_CAPTURE_RESULT_OK) {
    fputs("C translation unit could not call the capture DLL\n", stderr);
    return 1;
  }

  if ((capabilities & WDF_CAPTURE_CAPABILITY_PRIVACY_GUARD) == 0 ||
      (capabilities & WDF_CAPTURE_CAPABILITY_EVENT_QUEUE) == 0) {
    fputs("C translation unit observed incomplete foundation capabilities\n",
          stderr);
    return 1;
  }

  puts("C header and DLL compatibility test passed");
  return 0;
}

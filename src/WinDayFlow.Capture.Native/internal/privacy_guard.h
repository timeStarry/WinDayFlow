#ifndef WINDAYFLOW_CAPTURE_PRIVACY_GUARD_H_
#define WINDAYFLOW_CAPTURE_PRIVACY_GUARD_H_

#include <cstdint>

#include "windayflow_capture.h"

namespace windayflow::capture {

struct PrivacyContext {
  wdf_capture_policy_decision consent_granted = WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision session_unlocked = WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision secure_desktop_clear = WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision remote_session_allowed =
      WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision presentation_allowed =
      WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision application_allowed =
      WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision window_allowed = WDF_CAPTURE_POLICY_UNKNOWN;
  wdf_capture_policy_decision storage_available = WDF_CAPTURE_POLICY_UNKNOWN;
  uint64_t policy_revision = 0;

  bool operator==(const PrivacyContext&) const = default;
};

struct PrivacyDecision {
  bool allowed = false;
  wdf_capture_reason reason = WDF_CAPTURE_REASON_POLICY_BLOCKED;
};

bool IsValidPolicyDecision(wdf_capture_policy_decision decision);
bool IsValidPrivacyContext(const PrivacyContext& context);
PrivacyDecision EvaluatePrivacyContext(const PrivacyContext& context);

}  // namespace windayflow::capture

#endif  // WINDAYFLOW_CAPTURE_PRIVACY_GUARD_H_

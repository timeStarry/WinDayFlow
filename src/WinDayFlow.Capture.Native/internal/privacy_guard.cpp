#include "privacy_guard.h"

namespace windayflow::capture {
namespace {

PrivacyDecision RequireAllowed(wdf_capture_policy_decision decision,
                               wdf_capture_reason reason) {
  return decision == WDF_CAPTURE_POLICY_ALLOW
             ? PrivacyDecision{true, WDF_CAPTURE_REASON_NONE}
             : PrivacyDecision{false, reason};
}

}  // namespace

bool IsValidPolicyDecision(wdf_capture_policy_decision decision) {
  return decision == WDF_CAPTURE_POLICY_UNKNOWN ||
         decision == WDF_CAPTURE_POLICY_ALLOW ||
         decision == WDF_CAPTURE_POLICY_BLOCK;
}

bool IsValidPrivacyContext(const PrivacyContext& context) {
  return IsValidPolicyDecision(context.consent_granted) &&
         IsValidPolicyDecision(context.session_unlocked) &&
         IsValidPolicyDecision(context.secure_desktop_clear) &&
         IsValidPolicyDecision(context.remote_session_allowed) &&
         IsValidPolicyDecision(context.presentation_allowed) &&
         IsValidPolicyDecision(context.application_allowed) &&
         IsValidPolicyDecision(context.window_allowed) &&
         IsValidPolicyDecision(context.storage_available) &&
         context.policy_revision > 0;
}

PrivacyDecision EvaluatePrivacyContext(const PrivacyContext& context) {
  const PrivacyDecision consent = RequireAllowed(
      context.consent_granted, WDF_CAPTURE_REASON_CONSENT_REQUIRED);
  if (!consent.allowed) {
    return consent;
  }
  const PrivacyDecision session = RequireAllowed(
      context.session_unlocked, WDF_CAPTURE_REASON_SESSION_LOCKED);
  if (!session.allowed) {
    return session;
  }
  const PrivacyDecision secure_desktop = RequireAllowed(
      context.secure_desktop_clear, WDF_CAPTURE_REASON_SECURE_DESKTOP);
  if (!secure_desktop.allowed) {
    return secure_desktop;
  }
  const PrivacyDecision remote = RequireAllowed(
      context.remote_session_allowed, WDF_CAPTURE_REASON_REMOTE_SESSION);
  if (!remote.allowed) {
    return remote;
  }
  const PrivacyDecision presentation = RequireAllowed(
      context.presentation_allowed, WDF_CAPTURE_REASON_PRESENTATION_MODE);
  if (!presentation.allowed) {
    return presentation;
  }
  const PrivacyDecision application = RequireAllowed(
      context.application_allowed, WDF_CAPTURE_REASON_EXCLUDED_APPLICATION);
  if (!application.allowed) {
    return application;
  }
  const PrivacyDecision window = RequireAllowed(
      context.window_allowed, WDF_CAPTURE_REASON_EXCLUDED_WINDOW);
  if (!window.allowed) {
    return window;
  }
  return RequireAllowed(
      context.storage_available, WDF_CAPTURE_REASON_STORAGE_CONSTRAINED);
}

}  // namespace windayflow::capture

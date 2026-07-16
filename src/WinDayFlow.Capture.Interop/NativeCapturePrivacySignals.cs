using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public enum NativeCaptureConditionState
{
    Unknown = 0,
    Inactive = 1,
    Active = 2,
}

public sealed record NativeCapturePrivacySignals(
    NativeCapturePolicyDecision SessionUnlocked,
    NativeCapturePolicyDecision SecureDesktopClear,
    NativeCaptureConditionState RemoteSession,
    NativeCaptureConditionState PresentationMode,
    NativeCapturePolicyDecision ApplicationAllowed,
    NativeCapturePolicyDecision WindowAllowed,
    NativeCapturePolicyDecision StorageAvailable,
    NativeCaptureIdentitySnapshot? CaptureIdentity = null)
{
    public NativeCapturePolicyDecision SessionUnlocked { get; } =
        ValidateDecision(SessionUnlocked, nameof(SessionUnlocked));

    public NativeCapturePolicyDecision SecureDesktopClear { get; } =
        ValidateDecision(SecureDesktopClear, nameof(SecureDesktopClear));

    public NativeCaptureConditionState RemoteSession { get; } =
        ValidateCondition(RemoteSession, nameof(RemoteSession));

    public NativeCaptureConditionState PresentationMode { get; } =
        ValidateCondition(PresentationMode, nameof(PresentationMode));

    public NativeCapturePolicyDecision ApplicationAllowed { get; } =
        ValidateDecision(ApplicationAllowed, nameof(ApplicationAllowed));

    public NativeCapturePolicyDecision WindowAllowed { get; } =
        ValidateDecision(WindowAllowed, nameof(WindowAllowed));

    public NativeCapturePolicyDecision StorageAvailable { get; } =
        ValidateDecision(StorageAvailable, nameof(StorageAvailable));

    public NativeCaptureIdentitySnapshot CaptureIdentity { get; } =
        CaptureIdentity ?? NativeCaptureIdentitySnapshot.Unknown;

    public static NativeCapturePrivacySignals FailClosed { get; } = new(
        NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision.Unknown,
        NativeCaptureConditionState.Unknown,
        NativeCaptureConditionState.Unknown,
        NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision.Unknown);

    private static NativeCapturePolicyDecision ValidateDecision(
        NativeCapturePolicyDecision decision,
        string parameterName)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                decision,
                "The native privacy signal decision is not defined.");
        }

        return decision;
    }

    private static NativeCaptureConditionState ValidateCondition(
        NativeCaptureConditionState condition,
        string parameterName)
    {
        if (!Enum.IsDefined(condition))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                condition,
                "The native privacy condition state is not defined.");
        }

        return condition;
    }
}

public static class NativeCapturePrivacyPolicy
{
    public static NativeCapturePrivacyContext Compose(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        ulong runtimePolicyRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signals);

        var consentGranted = settings.CaptureEnabled
            && HasCurrentRecordingConsent(settings)
                ? NativeCapturePolicyDecision.Allow
                : NativeCapturePolicyDecision.Block;
        var remoteSessionAllowed = settings.CapturePrivacy.PauseInRemoteSessions
            ? RequireInactive(signals.RemoteSession)
            : NativeCapturePolicyDecision.Allow;
        var presentationAllowed = settings.CapturePrivacy.PauseDuringScreenSharing
            ? RequireInactive(signals.PresentationMode)
            : NativeCapturePolicyDecision.Allow;
        var exclusion = NativeCaptureExclusionRuleMatcher.Evaluate(
            settings.CapturePrivacy.ExclusionRules,
            signals.CaptureIdentity);
        var applicationAllowed = CombineExclusionPolicies(
            settings.CapturePrivacy.ExcludeSensitiveApplications,
            signals.ApplicationAllowed,
            exclusion.Application.HasEnabledRules,
            exclusion.Application.Decision);
        var windowAllowed = CombineExclusionPolicies(
            settings.CapturePrivacy.ExcludeSensitiveApplications,
            signals.WindowAllowed,
            exclusion.Window.HasEnabledRules,
            exclusion.Window.Decision);

        return new NativeCapturePrivacyContext(
            consentGranted,
            signals.SessionUnlocked,
            signals.SecureDesktopClear,
            remoteSessionAllowed,
            presentationAllowed,
            applicationAllowed,
            windowAllowed,
            signals.StorageAvailable,
            runtimePolicyRevision);
    }

    private static bool HasCurrentRecordingConsent(AppSettings settings)
    {
        return settings.RecordingConsent is { } consent
            && consent.PolicyVersion == AppSettingsService.CurrentRecordingConsentVersion
            && consent.PrivacyRevision == settings.CapturePrivacy.Revision;
    }

    private static NativeCapturePolicyDecision RequireInactive(
        NativeCaptureConditionState condition)
    {
        return condition switch
        {
            NativeCaptureConditionState.Inactive => NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Active => NativeCapturePolicyDecision.Block,
            _ => NativeCapturePolicyDecision.Unknown,
        };
    }

    private static NativeCapturePolicyDecision CombineExclusionPolicies(
        bool includeBuiltInPolicy,
        NativeCapturePolicyDecision builtInDecision,
        bool includeUserRules,
        NativeCapturePolicyDecision userRuleDecision)
    {
        var effectiveBuiltInDecision = includeBuiltInPolicy
            ? builtInDecision
            : NativeCapturePolicyDecision.Allow;
        var effectiveUserRuleDecision = includeUserRules
            ? userRuleDecision
            : NativeCapturePolicyDecision.Allow;

        return MostRestrictive(
            effectiveBuiltInDecision,
            effectiveUserRuleDecision);
    }

    private static NativeCapturePolicyDecision MostRestrictive(
        NativeCapturePolicyDecision left,
        NativeCapturePolicyDecision right)
    {
        if (left == NativeCapturePolicyDecision.Block
            || right == NativeCapturePolicyDecision.Block)
        {
            return NativeCapturePolicyDecision.Block;
        }

        return left == NativeCapturePolicyDecision.Unknown
            || right == NativeCapturePolicyDecision.Unknown
                ? NativeCapturePolicyDecision.Unknown
                : NativeCapturePolicyDecision.Allow;
    }
}

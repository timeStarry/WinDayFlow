using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCapturePrivacyPolicyTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailClosedSignalsRemainUnknownWhileMissingConsentIsExplicitlyBlocked()
    {
        var context = NativeCapturePrivacyPolicy.Compose(
            AppSettings.Default,
            NativeCapturePrivacySignals.FailClosed,
            runtimePolicyRevision: 1);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.ConsentGranted);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.SecureDesktopClear);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.RemoteSessionAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.PresentationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.WindowAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.StorageAvailable);
        Assert.Equal<ulong>(1, context.RuntimePolicyRevision);
    }

    [Fact]
    public void CurrentConsentAndKnownClearSignalsProduceAnAllowedContext()
    {
        var privacy = CapturePrivacySettings.Default;
        var settings = CreateEnabledSettings(privacy);
        var signals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow);

        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            signals,
            runtimePolicyRevision: 2);

        Assert.All(
            new[]
            {
                context.ConsentGranted,
                context.SessionUnlocked,
                context.SecureDesktopClear,
                context.RemoteSessionAllowed,
                context.PresentationAllowed,
                context.ApplicationAllowed,
                context.WindowAllowed,
                context.StorageAvailable,
            },
            decision => Assert.Equal(NativeCapturePolicyDecision.Allow, decision));
        Assert.Equal<ulong>(2, context.RuntimePolicyRevision);
    }

    [Fact]
    public void DisabledOptionalPoliciesIgnoreTheirDynamicSignals()
    {
        var privacy = new CapturePrivacySettings(
            EvidenceRetentionDays: 30,
            ExcludeSensitiveApplications: false,
            PauseInRemoteSessions: false,
            PauseDuringScreenSharing: false,
            Revision: 7);
        var settings = CreateEnabledSettings(privacy);
        var signals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Active,
            NativeCaptureConditionState.Unknown,
            NativeCapturePolicyDecision.Block,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Allow);

        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            signals,
            runtimePolicyRevision: 1);

        Assert.Equal(NativeCapturePolicyDecision.Allow, context.RemoteSessionAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.PresentationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.WindowAllowed);
        Assert.Equal<ulong>(1, context.RuntimePolicyRevision);
        Assert.Equal(7, settings.CapturePrivacy.Revision);
    }

    [Theory]
    [InlineData(false, AppSettingsService.CurrentRecordingConsentVersion, 1)]
    [InlineData(true, AppSettingsService.CurrentRecordingConsentVersion + 1, 1)]
    [InlineData(true, AppSettingsService.CurrentRecordingConsentVersion, 2)]
    public void DisabledOrStaleConsentIsExplicitlyBlocked(
        bool captureEnabled,
        int consentVersion,
        long consentPrivacyRevision)
    {
        var privacy = CapturePrivacySettings.Default;
        var settings = new AppSettings(
            AppThemePreference.System,
            captureEnabled,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                consentVersion,
                ConsentTime,
                consentPrivacyRevision),
            privacy);

        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            CreateAllowedSignals(),
            runtimePolicyRevision: 9);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.ConsentGranted);
    }

    [Fact]
    public void InvalidConditionStatesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            (NativeCaptureConditionState)99,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow));
    }

    private static AppSettings CreateEnabledSettings(CapturePrivacySettings privacy)
    {
        return new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion,
                ConsentTime,
                privacy.Revision),
            privacy);
    }

    private static NativeCapturePrivacySignals CreateAllowedSignals()
    {
        return new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow);
    }
}

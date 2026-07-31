using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCapturePrivacyPolicyTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingConsentAndUnknownSystemSignalsRemainFailClosed()
    {
        var context = NativeCapturePrivacyPolicy.Compose(
            AppSettings.Default,
            NativeCapturePrivacySignals.FailClosed,
            runtimePolicyRevision: 1);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.ConsentGranted);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.SecureDesktopClear);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, context.StorageAvailable);
        Assert.Equal<ulong>(1, context.RuntimePolicyRevision);
    }

    [Fact]
    public void OnlyRecordingIntentAllowsNativeCapture()
    {
        var signals = CreateAllowedSignals();

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePrivacyPolicy.Compose(
                CreateSettings(CaptureIntent.Recording),
                signals,
                runtimePolicyRevision: 1).ConsentGranted);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            NativeCapturePrivacyPolicy.Compose(
                CreateSettings(CaptureIntent.Paused),
                signals,
                runtimePolicyRevision: 2).ConsentGranted);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            NativeCapturePrivacyPolicy.Compose(
                CreateSettings(CaptureIntent.Stopped),
                signals,
                runtimePolicyRevision: 3).ConsentGranted);
    }

    [Fact]
    public void OutdatedConsentCannotAuthorizeRecording()
    {
        var settings = new AppSettings(
            AppThemePreference.System,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion - 1,
                ConsentTime),
            EvidenceSettings.Default,
            CaptureIntervalSeconds: 10,
            CaptureIntent.Recording);

        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            CreateAllowedSignals(),
            runtimePolicyRevision: 4);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.ConsentGranted);
    }

    [Fact]
    public void SendRulesAndForegroundApplicationNeverGateLocalCapture()
    {
        var blockedForegroundSignals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Active,
            NativeCaptureConditionState.Active,
            NativeCapturePolicyDecision.Block,
            NativeCapturePolicyDecision.Block,
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                "WinDayFlow.App.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "WinDayFlow"));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateSettings(CaptureIntent.Recording),
            blockedForegroundSignals,
            runtimePolicyRevision: 5);

        Assert.Equal(NativeCapturePolicyDecision.Allow, context.RemoteSessionAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.PresentationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.WindowAllowed);
    }

    [Fact]
    public void LockAppIdentityDoesNotOverridePositiveSystemGates()
    {
        var signals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                "LockApp.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateSettings(CaptureIntent.Recording),
            signals,
            runtimePolicyRevision: 6);

        Assert.Equal(NativeCapturePolicyDecision.Allow, context.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.SecureDesktopClear);
    }

    [Fact]
    public void LockedSessionRemainsAHardCaptureGate()
    {
        var signals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Block,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                "explorer.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateSettings(CaptureIntent.Recording),
            signals,
            runtimePolicyRevision: 7);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.SecureDesktopClear);
    }

    [Fact]
    public void InvalidSignalStatesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeCapturePrivacySignals(
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                (NativeCaptureConditionState)99,
                NativeCaptureConditionState.Inactive,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow));
    }

    private static AppSettings CreateSettings(CaptureIntent intent) => new(
        AppThemePreference.System,
        new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            ConsentTime),
        EvidenceSettings.Default,
        CaptureIntervalSeconds: 10,
        intent);

    private static NativeCapturePrivacySignals CreateAllowedSignals() => new(
        NativeCapturePolicyDecision.Allow,
        NativeCapturePolicyDecision.Allow,
        NativeCaptureConditionState.Inactive,
        NativeCaptureConditionState.Inactive,
        NativeCapturePolicyDecision.Allow,
        NativeCapturePolicyDecision.Allow,
        NativeCapturePolicyDecision.Allow);
}

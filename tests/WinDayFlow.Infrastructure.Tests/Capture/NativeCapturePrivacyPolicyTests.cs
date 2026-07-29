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
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                executableName: "notepad.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Untitled - Notepad"));

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

    [Fact]
    public void UserApplicationRuleBlocksWhenBuiltInProtectionIsDisabled()
    {
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Excluded editor",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe");
        var privacy = CreatePrivacySettings(
            excludeSensitiveApplications: false,
            new CaptureExclusionRuleSet([rule]));
        var signals = CopySignals(
            CreateAllowedSignals(),
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "EDITOR.EXE",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 10);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            context.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            context.WindowAllowed);
    }

    [Fact]
    public void DefaultWinDayFlowRuleBlocksSelfCapture()
    {
        var signals = CopySignals(
            CreateAllowedSignals(),
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "WINDAYFLOW.APP.EXE",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "WinDayFlow"));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(CapturePrivacySettings.Default),
            signals,
            runtimePolicyRevision: 10);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            context.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            context.WindowAllowed);
    }

    [Fact]
    public void UnknownUserWindowRuleRemainsFailClosedWhenBuiltInProtectionIsDisabled()
    {
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Private editor window",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe",
            WindowTitleMatchKind.Contains,
            "private");
        var privacy = CreatePrivacySettings(
            excludeSensitiveApplications: false,
            new CaptureExclusionRuleSet([rule]));
        var signals = CopySignals(
            CreateAllowedSignals(),
            captureIdentity: NativeCaptureIdentitySnapshot.Unknown);

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 11);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            context.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            context.WindowAllowed);
    }

    [Fact]
    public void BuiltInBlockStillAppliesWhenUserRulesDoNotMatchAndProtectionIsEnabled()
    {
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Different editor",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "different.exe");
        var privacy = CreatePrivacySettings(
            excludeSensitiveApplications: true,
            new CaptureExclusionRuleSet([rule]));
        var signals = CopySignals(
            CreateAllowedSignals(),
            applicationAllowed: NativeCapturePolicyDecision.Block,
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 12);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            context.ApplicationAllowed);
    }

    [Fact]
    public void DisabledBuiltInProtectionBypassesSignalsWhenNoUserRulesExist()
    {
        var privacy = CreatePrivacySettings(
            excludeSensitiveApplications: false,
            CaptureExclusionRuleSet.Empty);
        var signals = CopySignals(
            CreateAllowedSignals(),
            applicationAllowed: NativeCapturePolicyDecision.Block,
            windowAllowed: NativeCapturePolicyDecision.Unknown,
            captureIdentity: NativeCaptureIdentitySnapshot.Unknown);

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 13);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            context.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            context.WindowAllowed);
    }

    [Fact]
    public void AllowAllApplicationsSuspendsApplicationAndWindowExclusions()
    {
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Private editor",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe",
            WindowTitleMatchKind.Contains,
            "private");
        var privacy = CreatePrivacySettings(
                excludeSensitiveApplications: true,
                new CaptureExclusionRuleSet([rule]))
            .ChangeApplicationPrivacyMode(
                CaptureApplicationPrivacyMode.AllowAllApplications);
        var signals = CopySignals(
            CreateAllowedSignals(),
            applicationAllowed: NativeCapturePolicyDecision.Block,
            windowAllowed: NativeCapturePolicyDecision.Block,
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Private document"));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 14);

        Assert.Equal(NativeCapturePolicyDecision.Allow, context.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.WindowAllowed);
    }

    [Fact]
    public void AllowAllApplicationsStillBlocksLockScreenAndEnabledSystemPolicies()
    {
        var privacy = CapturePrivacySettings.Default.ChangeApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        var signals = CopySignals(
            CreateAllowedSignals(),
            applicationAllowed: NativeCapturePolicyDecision.Allow,
            windowAllowed: NativeCapturePolicyDecision.Allow,
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "LockApp.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));
        signals = new NativeCapturePrivacySignals(
            signals.SessionUnlocked,
            signals.SecureDesktopClear,
            NativeCaptureConditionState.Active,
            NativeCaptureConditionState.Active,
            signals.ApplicationAllowed,
            signals.WindowAllowed,
            signals.StorageAvailable,
            signals.CaptureIdentity);

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 15);

        Assert.Equal(NativeCapturePolicyDecision.Block, context.SecureDesktopClear);
        Assert.Equal(NativeCapturePolicyDecision.Block, context.RemoteSessionAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Block, context.PresentationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, context.WindowAllowed);
    }

    [Fact]
    public void UserApplicationAndWindowRuleDecisionsRemainIndependentInPolicy()
    {
        var windowRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Packaged application",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.App_123456789abcd"),
            CaptureExclusionRule.Create(
                windowRuleId,
                "Private editor window",
                enabled: true,
                CaptureExclusionRuleScope.Window,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe",
                WindowTitleMatchKind.Contains,
                "private"),
        ]);
        var privacy = CreatePrivacySettings(
            excludeSensitiveApplications: false,
            rules);
        var signals = CopySignals(
            CreateAllowedSignals(),
            captureIdentity: new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "PRIVATE document"));

        var context = NativeCapturePrivacyPolicy.Compose(
            CreateEnabledSettings(privacy),
            signals,
            runtimePolicyRevision: 14);

        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            context.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            context.WindowAllowed);
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

    private static CapturePrivacySettings CreatePrivacySettings(
        bool excludeSensitiveApplications,
        CaptureExclusionRuleSet exclusionRules)
    {
        return new CapturePrivacySettings(
            EvidenceRetentionDays: 30,
            excludeSensitiveApplications,
            PauseInRemoteSessions: true,
            PauseDuringScreenSharing: true,
            Revision: 7,
            exclusionRules);
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
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                executableName: "notepad.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Untitled - Notepad"));
    }

    private static NativeCapturePrivacySignals CopySignals(
        NativeCapturePrivacySignals source,
        NativeCapturePolicyDecision? applicationAllowed = null,
        NativeCapturePolicyDecision? windowAllowed = null,
        NativeCaptureIdentitySnapshot? captureIdentity = null)
    {
        return new NativeCapturePrivacySignals(
            source.SessionUnlocked,
            source.SecureDesktopClear,
            source.RemoteSession,
            source.PresentationMode,
            applicationAllowed ?? source.ApplicationAllowed,
            windowAllowed ?? source.WindowAllowed,
            source.StorageAvailable,
            captureIdentity ?? source.CaptureIdentity);
    }
}

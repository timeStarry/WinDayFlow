using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class CaptureRuleObservationBufferTests
{
    [Fact]
    public void StoresOnlyRuleEvaluationMetadataAndSupportsInvalidation()
    {
        var applicationRule = CaptureExclusionRule.Create(
            Guid.Parse("01bcc1d3-2342-4386-b96f-a65811ff8b5f"),
            "Browser",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe");
        var windowRule = CaptureExclusionRule.Create(
            Guid.Parse("cd171e84-5406-403a-bdb5-000f25b68a4d"),
            "Credential page",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            "API key");
        var settings = new AppSettings(
            AppThemePreference.System,
            RecordingConsent: null,
            new EvidenceSettings(
                EvidenceSettings.DefaultRetentionDays,
                RulesRevision: 9,
                new CaptureExclusionRuleSet([applicationRule, windowRule])),
            CaptureIntervalSeconds: 10,
            CaptureIntent.Stopped);
        var observedAt = new DateTimeOffset(
            2026,
            7,
            30,
            12,
            0,
            0,
            TimeSpan.Zero);
        var buffer = new CaptureRuleObservationBuffer();

        buffer.Observe(
            observedAt,
            settings,
            CreateSignals(new NativeCaptureIdentitySnapshot(
                executableName: "browser.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Settings - API key")));
        var evaluation = buffer.FindAt(observedAt.AddSeconds(1));

        Assert.NotNull(evaluation);
        Assert.Equal(9, evaluation.RuleSetRevision);
        Assert.True(evaluation.ApplicationContextAvailable);
        Assert.True(evaluation.WindowContextAvailable);
        Assert.Equal(
            [applicationRule.Id, windowRule.Id],
            evaluation.RuleMatches
                .Select(static match => match.RuleId)
                .Order()
                .ToArray());

        buffer.Invalidate(observedAt.AddSeconds(2), settings);
        var invalidated = buffer.FindAt(observedAt.AddSeconds(3));

        Assert.NotNull(invalidated);
        Assert.False(invalidated.ApplicationContextAvailable);
        Assert.False(invalidated.WindowContextAvailable);
        Assert.Empty(invalidated.RuleMatches);
    }

    [Fact]
    public void CompleteNonMatchingObservationProvesRulesWereEvaluated()
    {
        var rule = CaptureExclusionRule.Create(
            Guid.Parse("4dddfc43-e0ec-476a-9fda-e4f5f6cd83f5"),
            "Credential page",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            "API key");
        var settings = new AppSettings(
            AppThemePreference.System,
            RecordingConsent: null,
            new EvidenceSettings(
                EvidenceSettings.DefaultRetentionDays,
                RulesRevision: 2,
                new CaptureExclusionRuleSet([rule])),
            CaptureIntervalSeconds: 10,
            CaptureIntent.Stopped);
        var buffer = new CaptureRuleObservationBuffer();
        var observedAt = DateTimeOffset.UtcNow;

        buffer.Observe(
            observedAt,
            settings,
            CreateSignals(new NativeCaptureIdentitySnapshot(
                executableName: "browser.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Project plan")));

        var evaluation = buffer.FindAt(observedAt);

        Assert.NotNull(evaluation);
        Assert.True(evaluation.WindowContextAvailable);
        Assert.Empty(evaluation.RuleMatches);
    }

    private static NativeCapturePrivacySignals CreateSignals(
        NativeCaptureIdentitySnapshot identity) => new(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            identity,
            NativeCaptureTargetIdentity.Unknown);
}

using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureExclusionRuleMatcherTests
{
    [Fact]
    public void NoEnabledRulesAllowAnUnknownIdentity()
    {
        var disabled = CreateApplicationRule(
            Guid.NewGuid(),
            "Disabled browser",
            enabled: false,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe");

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([disabled]),
            NativeCaptureIdentitySnapshot.Unknown);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            evaluation.Application.Decision);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            evaluation.Window.Decision);
        Assert.Null(evaluation.Application.MatchedRuleId);
        Assert.Null(evaluation.Window.MatchedRuleId);
        Assert.False(evaluation.Application.HasEnabledRules);
        Assert.False(evaluation.Window.HasEnabledRules);
    }

    [Fact]
    public void ApplicationAndWindowScopesMatchIndependentlyWithTypedCaseInsensitiveIdentity()
    {
        var windowRuleId = Guid.NewGuid();
        var applicationRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CreateWindowRule(
                windowRuleId,
                "Private browser window",
                enabled: true,
                ApplicationIdentityKind.PackageFamilyName,
                "Publisher.Browser_abc123def4567",
                WindowTitleMatchKind.StartsWith,
                "PRIVATE"),
            CreateApplicationRule(
                applicationRuleId,
                "Browser executable",
                enabled: true,
                ApplicationIdentityKind.ExecutableName,
                "browser.exe"),
        ]);
        var identity = new NativeCaptureIdentitySnapshot(
            executableName: "BROWSER.EXE",
            packageFamilyName: "publisher.browser_ABC123DEF4567",
            publisherCertificateSha256: null,
            windowTitle: "private session");

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            identity);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Application.Decision);
        Assert.Equal(applicationRuleId, evaluation.Application.MatchedRuleId);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Window.Decision);
        Assert.Equal(windowRuleId, evaluation.Window.MatchedRuleId);
        Assert.True(evaluation.Application.HasEnabledRules);
        Assert.True(evaluation.Window.HasEnabledRules);
    }

    [Fact]
    public void IdentityKindsNeverMatchAcrossTypedBoundaries()
    {
        var rule = CreateApplicationRule(
            Guid.NewGuid(),
            "Packaged app",
            enabled: true,
            ApplicationIdentityKind.PackageFamilyName,
            "Contoso.App_123456789abcd");
        var identity = new NativeCaptureIdentitySnapshot(
            executableName: "Contoso.App_123456789abcd",
            packageFamilyName: "Different.Package_abc123def4567",
            publisherCertificateSha256: null,
            windowTitle: null);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([rule]),
            identity);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            evaluation.Application.Decision);
        Assert.Null(evaluation.Application.MatchedRuleId);
    }

    [Fact]
    public void MissingTypedIdentityMakesTheFirstPotentialRuleUnknown()
    {
        var unresolvedRule = CreateApplicationRule(
            Guid.NewGuid(),
            "Packaged app",
            enabled: true,
            ApplicationIdentityKind.PackageFamilyName,
            "Contoso.App_123456789abcd");
        var laterMatch = CreateApplicationRule(
            Guid.NewGuid(),
            "Executable fallback",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "contoso.exe");
        var identity = new NativeCaptureIdentitySnapshot(
            executableName: "contoso.exe",
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle: null);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([unresolvedRule, laterMatch]),
            identity);

        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            evaluation.Application.Decision);
        Assert.Null(evaluation.Application.MatchedRuleId);
    }

    [Fact]
    public void AbsentPackageAndCertificateIdentitiesSkipTheirRulesAndAllowALaterMatch()
    {
        var executableRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CreateApplicationRule(
                Guid.NewGuid(),
                "Packaged app",
                enabled: true,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.App_123456789abcd"),
            CreateApplicationRule(
                Guid.NewGuid(),
                "Signed app",
                enabled: true,
                ApplicationIdentityKind.PublisherCertificateSha256,
                new string('A', CaptureExclusionRule.PublisherCertificateSha256Length)),
            CreateApplicationRule(
                executableRuleId,
                "Executable fallback",
                enabled: true,
                ApplicationIdentityKind.ExecutableName,
                "contoso.exe"),
        ]);
        var identity = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("contoso.exe"),
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(rules, identity);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Application.Decision);
        Assert.Equal(executableRuleId, evaluation.Application.MatchedRuleId);
        Assert.Equal(
            NativeCaptureObservationState.Absent,
            identity.PackageFamilyNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Absent,
            identity.PublisherCertificateSha256Observation.State);
    }

    [Fact]
    public void PresentApplicationIdentitiesUseDomainNormalizationAndMalformedValuesBecomeUnknown()
    {
        var normalized = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("  EDITOR.EXE  "),
            NativeCaptureObservation.Present("  Contoso.App_123456789abcd  "),
            NativeCaptureObservation.Present(
                new string('a', CaptureExclusionRule.PublisherCertificateSha256Length)),
            NativeCaptureObservation.Present("  private title  "));
        var malformed = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present(@"C:\private\editor.exe"),
            NativeCaptureObservation.Present("not-a-package-family"),
            NativeCaptureObservation.Present(
                new string('a', CaptureExclusionRule.PublisherCertificateSha256Length - 1)),
            NativeCaptureObservation.Absent);
        var rule = CreateApplicationRule(
            Guid.NewGuid(),
            "Editor",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe");

        var malformedEvaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([rule]),
            malformed);

        Assert.Equal("EDITOR.EXE", normalized.ExecutableName);
        Assert.Equal("Contoso.App_123456789abcd", normalized.PackageFamilyName);
        Assert.Equal(
            new string('A', CaptureExclusionRule.PublisherCertificateSha256Length),
            normalized.PublisherCertificateSha256);
        Assert.Equal("  private title  ", normalized.WindowTitle);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            malformed.ExecutableNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            malformed.PackageFamilyNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            malformed.PublisherCertificateSha256Observation.State);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            malformedEvaluation.Application.Decision);
    }

    [Fact]
    public void LegacyNullValuesRemainUnknown()
    {
        var identity = new NativeCaptureIdentitySnapshot(
            executableName: null,
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle: null);

        Assert.Equal(NativeCaptureIdentitySnapshot.Unknown, identity);
        Assert.All(
            new[]
            {
                identity.ExecutableNameObservation,
                identity.PackageFamilyNameObservation,
                identity.PublisherCertificateSha256Observation,
                identity.WindowTitleObservation,
            },
            observation => Assert.Equal(
                NativeCaptureObservationState.Unknown,
                observation.State));
    }

    [Theory]
    [InlineData(WindowTitleMatchKind.Exact, "PRIVATE REPORT", "private report")]
    [InlineData(WindowTitleMatchKind.StartsWith, "PRIVATE", "private report - July")]
    [InlineData(WindowTitleMatchKind.Contains, "REPORT", "July private report")]
    public void WindowRulesSupportBoundedCaseInsensitiveTitleMatching(
        WindowTitleMatchKind matchKind,
        string pattern,
        string observedTitle)
    {
        var ruleId = Guid.NewGuid();
        var rule = CreateWindowRule(
            ruleId,
            "Bounded window",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "reports.exe",
            matchKind,
            pattern);
        var identity = new NativeCaptureIdentitySnapshot(
            executableName: "REPORTS.EXE",
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle: observedTitle);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([rule]),
            identity);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Window.Decision);
        Assert.Equal(ruleId, evaluation.Window.MatchedRuleId);
    }

    [Fact]
    public void UnknownWindowTitleFailsClosedOnlyAfterItsApplicationAnchorMatches()
    {
        var rule = CreateWindowRule(
            Guid.NewGuid(),
            "Private editor window",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe",
            WindowTitleMatchKind.Contains,
            "private");
        var rules = new CaptureExclusionRuleSet([rule]);

        var matchingApplication = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));
        var differentApplication = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            new NativeCaptureIdentitySnapshot(
                executableName: "terminal.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: null));

        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            matchingApplication.Window.Decision);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            differentApplication.Window.Decision);
    }

    [Fact]
    public void AbsentWindowTitleConcludesNoMatchWhileUnknownRemainsFailClosed()
    {
        var rule = CreateWindowRule(
            Guid.NewGuid(),
            "Private editor window",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe",
            WindowTitleMatchKind.Contains,
            "private");
        var rules = new CaptureExclusionRuleSet([rule]);
        var absentTitle = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("editor.exe"),
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent);
        var unknownTitle = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("editor.exe"),
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Unknown);

        var absentEvaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            absentTitle);
        var unknownEvaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            unknownTitle);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            absentEvaluation.Window.Decision);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            unknownEvaluation.Window.Decision);
    }

    [Theory]
    [InlineData(WindowTitleMatchKind.Exact, " private ", " private ")]
    [InlineData(WindowTitleMatchKind.StartsWith, " private", " private document")]
    public void WindowPatternBoundaryWhitespaceIsMatchedWithoutNormalization(
        WindowTitleMatchKind matchKind,
        string pattern,
        string matchingTitle)
    {
        var rule = CreateWindowRule(
            Guid.NewGuid(),
            "Whitespace-sensitive title",
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe",
            matchKind,
            pattern);
        var rules = new CaptureExclusionRuleSet([rule]);
        var matching = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("editor.exe"),
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Present(matchingTitle));
        var withoutBoundaryWhitespace = NativeCaptureIdentitySnapshot.FromObservations(
            NativeCaptureObservation.Present("editor.exe"),
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Present(matchingTitle.Trim()));

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            NativeCaptureExclusionRuleMatcher.Evaluate(rules, matching).Window.Decision);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            NativeCaptureExclusionRuleMatcher.Evaluate(
                rules,
                withoutBoundaryWhitespace).Window.Decision);
    }

    [Fact]
    public void UnknownApplicationRuleDoesNotHideAnIndependentWindowMatch()
    {
        var windowRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CreateApplicationRule(
                Guid.NewGuid(),
                "Packaged application",
                enabled: true,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.App_123456789abcd"),
            CreateWindowRule(
                windowRuleId,
                "Private executable window",
                enabled: true,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe",
                WindowTitleMatchKind.Contains,
                "private"),
        ]);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "PRIVATE document"));

        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            evaluation.Application.Decision);
        Assert.Null(evaluation.Application.MatchedRuleId);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Window.Decision);
        Assert.Equal(windowRuleId, evaluation.Window.MatchedRuleId);
    }

    [Fact]
    public void ApplicationMatchDoesNotTurnAnUnknownWindowRuleIntoAllow()
    {
        var applicationRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CreateApplicationRule(
                applicationRuleId,
                "Editor application",
                enabled: true,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe"),
            CreateWindowRule(
                Guid.NewGuid(),
                "Packaged private window",
                enabled: true,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.App_123456789abcd",
                WindowTitleMatchKind.Contains,
                "private"),
        ]);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "private document"));

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Application.Decision);
        Assert.Equal(applicationRuleId, evaluation.Application.MatchedRuleId);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            evaluation.Window.Decision);
        Assert.Null(evaluation.Window.MatchedRuleId);
    }

    [Fact]
    public void FirstConclusiveMatchWinsWithinItsScope()
    {
        var firstRuleId = Guid.NewGuid();
        var secondRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CreateApplicationRule(
                firstRuleId,
                "Packaged editor",
                enabled: true,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.Editor_123456789abcd"),
            CreateApplicationRule(
                secondRuleId,
                "Editor executable",
                enabled: true,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe"),
        ]);

        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            new NativeCaptureIdentitySnapshot(
                executableName: "editor.exe",
                packageFamilyName: "CONTOSO.EDITOR_123456789ABCD",
                publisherCertificateSha256: null,
                windowTitle: null));

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            evaluation.Application.Decision);
        Assert.Equal(firstRuleId, evaluation.Application.MatchedRuleId);
        Assert.NotEqual(secondRuleId, evaluation.Application.MatchedRuleId);
    }

    [Fact]
    public void EvaluationNeverReturnsTheRuleNameOrObservedWindowTitle()
    {
        const string ruleName = "Confidential payroll rule";
        const string observedTitle = "Payroll - Employee 0042";
        var ruleId = Guid.NewGuid();
        var rule = CreateWindowRule(
            ruleId,
            ruleName,
            enabled: true,
            ApplicationIdentityKind.ExecutableName,
            "payroll.exe",
            WindowTitleMatchKind.StartsWith,
            "Payroll");

        var identity = new NativeCaptureIdentitySnapshot(
            executableName: "payroll.exe",
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle: observedTitle);
        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            new CaptureExclusionRuleSet([rule]),
            identity);
        var rendered = evaluation.ToString();
        var renderedRule = rule.ToString();
        var renderedObservation = identity.WindowTitleObservation.ToString();
        var renderedIdentity = identity.ToString();
        var renderedSignals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            identity).ToString();

        Assert.Equal(ruleId, evaluation.Window.MatchedRuleId);
        Assert.DoesNotContain(ruleName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(observedTitle, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(ruleName, renderedRule, StringComparison.Ordinal);
        Assert.DoesNotContain("payroll.exe", renderedRule, StringComparison.Ordinal);
        Assert.DoesNotContain("Payroll", renderedRule, StringComparison.Ordinal);
        Assert.DoesNotContain(observedTitle, renderedObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("payroll.exe", renderedIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(observedTitle, renderedIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("payroll.exe", renderedSignals, StringComparison.Ordinal);
        Assert.DoesNotContain(observedTitle, renderedSignals, StringComparison.Ordinal);
        Assert.Contains("ExecutableNameState = Present", renderedIdentity, StringComparison.Ordinal);
        Assert.Contains("WindowTitleState = Present", renderedIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(
            evaluation.GetType().GetProperties(),
            static property => property.Name.Contains(
                "Title",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            evaluation.GetType().GetProperties(),
            static property => property.Name.Contains(
                "Name",
                StringComparison.OrdinalIgnoreCase));
    }

    private static CaptureExclusionRule CreateApplicationRule(
        Guid id,
        string name,
        bool enabled,
        ApplicationIdentityKind identityKind,
        string identityValue)
    {
        return CaptureExclusionRule.Create(
            id,
            name,
            enabled,
            CaptureExclusionRuleScope.Application,
            identityKind,
            identityValue);
    }

    private static CaptureExclusionRule CreateWindowRule(
        Guid id,
        string name,
        bool enabled,
        ApplicationIdentityKind identityKind,
        string identityValue,
        WindowTitleMatchKind matchKind,
        string pattern)
    {
        return CaptureExclusionRule.Create(
            id,
            name,
            enabled,
            CaptureExclusionRuleScope.Window,
            identityKind,
            identityValue,
            matchKind,
            pattern);
    }
}

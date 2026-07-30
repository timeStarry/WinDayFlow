using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Settings;

public sealed class CaptureExclusionRuleTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RulesNormalizeSupportedIdentitiesAndRejectUnsafeSelectors()
    {
        var publisher = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "  Signed app  ",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.PublisherCertificateSha256,
            new string('a', CaptureExclusionRule.PublisherCertificateSha256Length));
        var package = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Calculator",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.PackageFamilyName,
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe");

        Assert.Equal("Signed app", publisher.Name);
        Assert.Equal(
            new string('A', CaptureExclusionRule.PublisherCertificateSha256Length),
            publisher.IdentityValue);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", package.IdentityValue);
        Assert.Throws<ArgumentException>(
            () => CreateApplicationRule("C:private.exe"));
        Assert.Throws<ArgumentException>(
            () => CreateApplicationRule("private"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Unsafe\nname",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "private.exe"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Bad package",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.PackageFamilyName,
                "not-a-package-family"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateWindowRule("x"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Application with title",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "private.exe",
                WindowTitleMatchKind.Contains,
                "Private"));
    }

    [Fact]
    public void TextValidationRejectsControlsBeforeTrimmingAndPreservesWindowPattern()
    {
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "\nHidden control",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "private.exe"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Unsafe identity",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "\tprivate.exe"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Unsafe pattern",
                enabled: true,
                CaptureExclusionRuleScope.Window,
                ApplicationIdentityKind.ExecutableName,
                "private.exe",
                WindowTitleMatchKind.Exact,
                "\nprivate"));
        Assert.Throws<ArgumentException>(
            () => CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Whitespace pattern",
                enabled: true,
                CaptureExclusionRuleScope.Window,
                ApplicationIdentityKind.ExecutableName,
                "private.exe",
                WindowTitleMatchKind.Exact,
                "  "));

        var exact = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Boundary whitespace",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "private.exe",
            WindowTitleMatchKind.Exact,
            " private ");

        Assert.Equal(" private ", exact.Pattern);
    }

    [Fact]
    public void RuleToStringRedactsConfiguredText()
    {
        const string ruleName = "Confidential payroll";
        const string identity = "payroll.exe";
        const string pattern = "Employee 0042";
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            ruleName,
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            identity,
            WindowTitleMatchKind.Contains,
            pattern);

        var rendered = rule.ToString();

        Assert.DoesNotContain(ruleName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(identity, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(pattern, rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleSetIsOrderedDefensiveAndRejectsDuplicateBoundaries()
    {
        var first = CreateApplicationRule("first.exe");
        var second = CreateWindowRule("Private");
        var source = new[] { first, second };
        var rules = new CaptureExclusionRuleSet(source);
        source[0] = CreateApplicationRule("replacement.exe");

        Assert.Equal(first, rules[0]);
        Assert.Equal(new CaptureExclusionRuleSet([first, second]), rules);
        Assert.NotEqual(new CaptureExclusionRuleSet([second, first]), rules);
        Assert.Throws<ArgumentException>(
            () => new CaptureExclusionRuleSet([first, first]));
        Assert.Throws<ArgumentException>(
            () => new CaptureExclusionRuleSet(
            [
                first,
                CaptureExclusionRule.Create(
                    Guid.NewGuid(),
                    "Duplicate matcher",
                    enabled: false,
                    CaptureExclusionRuleScope.Application,
                    ApplicationIdentityKind.ExecutableName,
                    "FIRST.EXE"),
            ]));
    }

    [Fact]
    public void EffectivePolicyIgnoresDisabledRulesAndNamesButTracksEnabledOrder()
    {
        var first = CreateApplicationRule("first.exe");
        var second = CreateApplicationRule("second.exe");
        var renamedFirst = new CaptureExclusionRule(
            first.Id,
            "Renamed",
            first.Enabled,
            first.Scope,
            first.ApplicationIdentityKind,
            first.IdentityValue.ToUpperInvariant(),
            first.WindowTitleMatchKind,
            first.Pattern,
            revision: 2);
        var disabled = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Draft",
            enabled: false,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "draft.exe");

        Assert.True(
            CaptureExclusionRuleSet.Empty.HasSameEffectivePolicy(
                new CaptureExclusionRuleSet([disabled])));
        Assert.True(
            new CaptureExclusionRuleSet([first, second]).HasSameEffectivePolicy(
                new CaptureExclusionRuleSet([renamedFirst, second])));
        Assert.False(
            new CaptureExclusionRuleSet([first, second]).HasSameEffectivePolicy(
                new CaptureExclusionRuleSet([second, renamedFirst])));
    }

    [Fact]
    public void EffectivePolicyUsesIndependentScopeOrderAndStableRuleIdentifiers()
    {
        var application = CreateApplicationRule("private.exe");
        var window = CreateWindowRule("Private");
        var replacement = new CaptureExclusionRule(
            Guid.NewGuid(),
            application.Name,
            application.Enabled,
            application.Scope,
            application.ApplicationIdentityKind,
            application.IdentityValue,
            application.WindowTitleMatchKind,
            application.Pattern,
            revision: 1);

        Assert.True(
            new CaptureExclusionRuleSet([application, window])
                .HasSameEffectivePolicy(
                    new CaptureExclusionRuleSet([window, application])));
        Assert.False(
            new CaptureExclusionRuleSet([application])
                .HasSameEffectivePolicy(
                    new CaptureExclusionRuleSet([replacement])));
    }

    [Fact]
    public async Task SendRuleChangesDoNotInvalidateConsentOrStopCapture()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureEnabledAsync(enabled: true);
        var draft = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Draft",
            enabled: false,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "draft.exe");

        await service.AddCaptureExclusionRuleAsync(draft);
        var renamed = await service.UpdateCaptureExclusionRuleAsync(
            draft.Id,
            expectedRevision: 1,
            "Renamed draft",
            draft.Scope,
            draft.ApplicationIdentityKind,
            draft.IdentityValue,
            null,
            null);

        Assert.True(service.Current.CaptureEnabled);
        Assert.True(service.HasValidRecordingConsent);
        Assert.Equal(1, service.Current.Evidence.RulesRevision);
        Assert.Equal(2, renamed.Revision);
        await Assert.ThrowsAsync<CaptureExclusionRuleRevisionConflictException>(
            () => service.SetCaptureExclusionRuleEnabledAsync(
                draft.Id,
                expectedRevision: 1,
                enabled: true));

        var enabled = await service.SetCaptureExclusionRuleEnabledAsync(
            draft.Id,
            renamed.Revision,
            enabled: true);

        Assert.True(service.Current.CaptureEnabled);
        Assert.True(service.HasValidRecordingConsent);
        Assert.Equal(2, service.Current.Evidence.RulesRevision);
        Assert.Equal(3, enabled.Revision);
    }

    [Fact]
    public async Task EnabledRuleMoveAndDeleteAdvanceSendRuleRevisions()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        var first = CreateApplicationRule("first.exe");
        var second = CreateApplicationRule("second.exe");
        await service.AddCaptureExclusionRuleAsync(first);
        await service.AddCaptureExclusionRuleAsync(second);
        Assert.Equal(3, service.Current.Evidence.RulesRevision);
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureEnabledAsync(enabled: true);

        var moved = await service.MoveCaptureExclusionRuleAsync(
            second.Id,
            expectedRevision: 1,
            newIndex: 0);

        Assert.Equal(2, moved.Revision);
        Assert.Equal(second.Id, service.Current.Evidence.SendRules[0].Id);
        Assert.Equal(4, service.Current.Evidence.RulesRevision);
        Assert.True(service.Current.CaptureEnabled);
        await Assert.ThrowsAsync<CaptureExclusionRuleRevisionConflictException>(
            () => service.DeleteCaptureExclusionRuleAsync(second.Id, expectedRevision: 1));
        await service.DeleteCaptureExclusionRuleAsync(second.Id, moved.Revision);
        Assert.Equal(5, service.Current.Evidence.RulesRevision);
    }

    private static CaptureExclusionRule CreateApplicationRule(string executableName)
    {
        return CaptureExclusionRule.Create(
            Guid.NewGuid(),
            executableName,
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            executableName);
    }

    private static CaptureExclusionRule CreateWindowRule(string pattern)
    {
        return CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Private window",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            pattern);
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        private AppSettings _settings = AppSettings.Default;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_settings != expected)
            {
                throw new AppSettingsConcurrencyException();
            }

            _settings = proposed;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Settings;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public async Task InitializeLoadsV13Settings()
    {
        var expected = AppSettings.Default;
        var repository = new InMemorySettingsRepository(expected);
        using var service = new AppSettingsService(repository);

        await service.InitializeAsync();

        Assert.Equal(expected, service.Current);
        Assert.False(service.HasValidRecordingConsent);
        Assert.Equal(CaptureIntent.Stopped, service.Current.CaptureIntent);
        Assert.Equal(10, service.Current.CaptureIntervalSeconds);
    }

    [Fact]
    public async Task RecordingRequiresCurrentConsent()
    {
        using var service = await CreateInitializedAsync();

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.SetCaptureIntentAsync(CaptureIntent.Recording));

        Assert.Equal(CaptureIntent.Stopped, service.Current.CaptureIntent);
    }

    [Fact]
    public async Task PauseAndStopArePersistedUserIntent()
    {
        var repository = new InMemorySettingsRepository(AppSettings.Default);
        using var service = new AppSettingsService(repository);
        await service.InitializeAsync();
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureIntentAsync(CaptureIntent.Recording);
        await service.SetCaptureIntentAsync(CaptureIntent.Paused);

        Assert.Equal(CaptureIntent.Paused, service.Current.CaptureIntent);
        Assert.False(service.Current.CaptureEnabled);
        Assert.Equal(CaptureIntent.Paused, repository.Current.CaptureIntent);

        await service.SetCaptureIntentAsync(CaptureIntent.Stopped);

        Assert.Equal(CaptureIntent.Stopped, repository.Current.CaptureIntent);
    }

    [Fact]
    public async Task RevokingConsentAlwaysStopsCapture()
    {
        using var service = await CreateConsentedAsync(CaptureIntent.Recording);

        await service.RevokeRecordingConsentAsync();

        Assert.Null(service.Current.RecordingConsent);
        Assert.Equal(CaptureIntent.Stopped, service.Current.CaptureIntent);
        Assert.False(service.HasValidRecordingConsent);
    }

    [Fact]
    public async Task OutdatedConsentDisarmsPersistedRecordingIntentOnInitialize()
    {
        var outdated = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion - 1,
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        var loaded = new AppSettings(
            AppThemePreference.Dark,
            outdated,
            EvidenceSettings.Default,
            CaptureIntervalSeconds: 15,
            CaptureIntent.Recording);
        var repository = new InMemorySettingsRepository(loaded);
        using var service = new AppSettingsService(repository);

        await service.InitializeAsync();

        Assert.Equal(CaptureIntent.Stopped, service.Current.CaptureIntent);
        Assert.Equal(CaptureIntent.Stopped, repository.Current.CaptureIntent);
        Assert.False(service.HasValidRecordingConsent);
    }

    [Fact]
    public async Task SendRuleMutationNeverChangesCaptureIntentOrConsent()
    {
        using var service = await CreateConsentedAsync(CaptureIntent.Recording);
        var consent = service.Current.RecordingConsent;
        var originalRevision = service.Current.Evidence.RulesRevision;
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Password manager",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "password-manager.exe");

        await service.AddCaptureExclusionRuleAsync(rule);

        Assert.Equal(CaptureIntent.Recording, service.Current.CaptureIntent);
        Assert.Equal(consent, service.Current.RecordingConsent);
        Assert.True(service.HasValidRecordingConsent);
        Assert.Equal(originalRevision + 1, service.Current.Evidence.RulesRevision);
    }

    [Fact]
    public async Task SendRuleUpdatesUseExpectedRevision()
    {
        using var service = await CreateInitializedAsync();
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Editor",
            enabled: false,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe");
        await service.AddCaptureExclusionRuleAsync(rule);
        var updated = await service.SetCaptureExclusionRuleEnabledAsync(
            rule.Id,
            expectedRevision: 1,
            enabled: true);

        Assert.Equal(2, updated.Revision);
        await Assert.ThrowsAsync<CaptureExclusionRuleRevisionConflictException>(
            () => service.DeleteCaptureExclusionRuleAsync(rule.Id, expectedRevision: 1));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task SupportedCaptureIntervalsArePersisted(int intervalSeconds)
    {
        var repository = new InMemorySettingsRepository(AppSettings.Default);
        using var service = new AppSettingsService(repository);
        await service.InitializeAsync();

        await service.SetCaptureIntervalSecondsAsync(intervalSeconds);

        Assert.Equal(intervalSeconds, repository.Current.CaptureIntervalSeconds);
    }

    [Fact]
    public async Task SettingsChangedReportsCommittedSnapshots()
    {
        using var service = await CreateInitializedAsync();
        AppSettingsChangedEventArgs? observed = null;
        service.SettingsChanged += (_, args) => observed = args;

        await service.SetThemeAsync(AppThemePreference.Dark);

        Assert.NotNull(observed);
        Assert.Equal(AppThemePreference.System, observed.Previous.Theme);
        Assert.Equal(AppThemePreference.Dark, observed.Current.Theme);
    }

    private static async Task<AppSettingsService> CreateInitializedAsync()
    {
        var service = new AppSettingsService(
            new InMemorySettingsRepository(AppSettings.Default));
        await service.InitializeAsync();
        return service;
    }

    private static async Task<AppSettingsService> CreateConsentedAsync(
        CaptureIntent intent)
    {
        var service = await CreateInitializedAsync();
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureIntentAsync(intent);
        return service;
    }

    private sealed class InMemorySettingsRepository(AppSettings initial)
        : IAppSettingsRepository
    {
        public AppSettings Current { get; private set; } = initial;

        public Task<AppSettings> GetAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current != expected)
            {
                throw new InvalidOperationException("Concurrent settings update.");
            }

            Current = proposed;
            return Task.CompletedTask;
        }
    }
}

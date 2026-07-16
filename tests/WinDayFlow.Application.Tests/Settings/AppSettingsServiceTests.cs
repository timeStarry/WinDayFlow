using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Settings;

public sealed class AppSettingsServiceTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultSettingsAreLocalAndCaptureSafe()
    {
        var settings = AppSettings.Default;

        Assert.Equal(AppThemePreference.System, settings.Theme);
        Assert.False(settings.CaptureEnabled);
        Assert.False(settings.CloudAnalysisEnabled);
        Assert.Null(settings.RecordingConsent);
        Assert.Equal(CapturePrivacySettings.Default, settings.CapturePrivacy);
    }

    [Fact]
    public void SettingsRejectInvalidPersistableValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppSettings(
                (AppThemePreference)99,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                RecordingConsent: null));
        Assert.Throws<ArgumentException>(
            () => new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                RecordingConsent: null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingConsent(0, ConsentTime));
        Assert.Throws<ArgumentException>(
            () => new RecordingConsent(
                1,
                ConsentTime.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingConsent(2, ConsentTime, PrivacyRevision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturePrivacySettings(
                EvidenceRetentionDays: 0,
                ExcludeSensitiveApplications: true,
                PauseInRemoteSessions: true,
                PauseDuringScreenSharing: true,
                Revision: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturePrivacySettings(
                CapturePrivacySettings.DefaultRetentionDays,
                ExcludeSensitiveApplications: true,
                PauseInRemoteSessions: true,
                PauseDuringScreenSharing: true,
                Revision: 0));
    }

    [Fact]
    public async Task InitializeAsyncLoadsSettingsAndRaisesChangeEvent()
    {
        var stored = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);
        var repository = new TestSettingsRepository(stored);
        using var service = new AppSettingsService(repository);
        AppSettingsChangedEventArgs? change = null;
        object? sender = null;
        service.SettingsChanged += (source, args) =>
        {
            sender = source;
            change = args;
        };

        await service.InitializeAsync();

        Assert.Same(stored, service.Current);
        Assert.Same(service, sender);
        Assert.NotNull(change);
        Assert.Same(AppSettings.Default, change.Previous);
        Assert.Same(stored, change.Current);
        Assert.Equal(1, repository.GetCallCount);
        Assert.Empty(repository.SavedSettings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task InitializeAsyncDisablesCaptureWhenStoredConsentIsOutdated(
        int storedPolicyVersion)
    {
        var staleConsent = new RecordingConsent(
            storedPolicyVersion,
            ConsentTime,
            storedPolicyVersion >= AppSettingsService.CurrentRecordingConsentVersion
                ? CapturePrivacySettings.Default.Revision
                : null);
        var stored = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: true,
            staleConsent);
        var repository = new TestSettingsRepository(stored);
        using var service = new AppSettingsService(repository);

        await service.InitializeAsync();

        Assert.False(service.Current.CaptureEnabled);
        Assert.True(service.Current.CloudAnalysisEnabled);
        Assert.Same(staleConsent, service.Current.RecordingConsent);
        Assert.False(service.HasValidRecordingConsent);
        Assert.Single(repository.SavedSettings);
        Assert.Equal(service.Current, repository.SavedSettings[0]);
    }

    [Fact]
    public async Task SetThemeAsyncSavesAndPublishesTheNewSnapshot()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(repository);
        var eventCount = 0;
        service.SettingsChanged += (_, _) => eventCount++;

        await service.SetThemeAsync(AppThemePreference.Dark);

        Assert.Equal(AppThemePreference.Dark, service.Current.Theme);
        Assert.Single(repository.SavedSettings);
        Assert.Equal(service.Current, repository.SavedSettings[0]);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task GrantRecordingConsentAsyncRecordsCurrentVersionAndUtcTime()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));

        await service.GrantRecordingConsentAsync();

        var consent = Assert.IsType<RecordingConsent>(service.Current.RecordingConsent);
        Assert.Equal(AppSettingsService.CurrentRecordingConsentVersion, consent.PolicyVersion);
        Assert.Equal(ConsentTime, consent.AcceptedAtUtc);
        Assert.Equal(TimeSpan.Zero, consent.AcceptedAtUtc.Offset);
        Assert.Equal(service.Current.CapturePrivacy.Revision, consent.PrivacyRevision);
        Assert.True(service.HasValidRecordingConsent);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task PrivacyChangeIncrementsRevisionDisablesCaptureAndInvalidatesConsent()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureEnabledAsync(enabled: true);
        var acceptedRevision = service.Current.RecordingConsent?.PrivacyRevision;

        await service.SetCapturePrivacyAsync(
            evidenceRetentionDays: 90,
            excludeSensitiveApplications: true,
            pauseInRemoteSessions: true,
            pauseDuringScreenSharing: false);

        Assert.False(service.Current.CaptureEnabled);
        Assert.False(service.HasValidRecordingConsent);
        Assert.Equal(90, service.Current.CapturePrivacy.EvidenceRetentionDays);
        Assert.False(service.Current.CapturePrivacy.PauseDuringScreenSharing);
        Assert.Equal(2, service.Current.CapturePrivacy.Revision);
        Assert.Equal(1, acceptedRevision);
        Assert.Equal(acceptedRevision, service.Current.RecordingConsent?.PrivacyRevision);
        Assert.Equal(3, repository.SavedSettings.Count);
    }

    [Fact]
    public async Task ReapplyingIdenticalPrivacyChoicesDoesNotPersistOrChangeRevision()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(repository);
        var privacy = service.Current.CapturePrivacy;

        await service.SetCapturePrivacyAsync(
            privacy.EvidenceRetentionDays,
            privacy.ExcludeSensitiveApplications,
            privacy.PauseInRemoteSessions,
            privacy.PauseDuringScreenSharing);

        Assert.Same(privacy, service.Current.CapturePrivacy);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task SetCaptureEnabledAsyncRejectsMissingOrOutdatedConsent()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(repository);

        var missing = await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.SetCaptureEnabledAsync(enabled: true));

        Assert.Equal(RecordingConsentRequiredException.ErrorMessage, missing.Message);
        Assert.False(service.Current.CaptureEnabled);
        Assert.Empty(repository.SavedSettings);

        var stale = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion + 1,
                ConsentTime));
        var staleRepository = new TestSettingsRepository(stale);
        using var staleService = new AppSettingsService(staleRepository);
        await staleService.InitializeAsync();

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => staleService.SetCaptureEnabledAsync(enabled: true));
        Assert.False(staleService.Current.CaptureEnabled);
        Assert.Empty(staleRepository.SavedSettings);
    }

    [Fact]
    public async Task RevokeRecordingConsentAsyncAlsoDisablesCapture()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureEnabledAsync(enabled: true);

        await service.RevokeRecordingConsentAsync();

        Assert.False(service.Current.CaptureEnabled);
        Assert.Null(service.Current.RecordingConsent);
        Assert.False(service.HasValidRecordingConsent);
        Assert.Equal(3, repository.SavedSettings.Count);
        Assert.Equal(service.Current, repository.SavedSettings[^1]);
    }

    [Fact]
    public async Task ConcurrentWritesAreSerialized()
    {
        var repository = new TestSettingsRepository
        {
            BlockFirstSave = true,
        };
        using var service = new AppSettingsService(repository);

        var first = service.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted;
        var second = service.SetThemeAsync(AppThemePreference.Light);

        Assert.Equal(1, repository.SaveCallCount);
        Assert.False(second.IsCompleted);

        repository.ReleaseFirstSave();
        await Task.WhenAll(first, second);

        Assert.Equal(2, repository.SaveCallCount);
        Assert.Equal(1, repository.MaximumConcurrentSaves);
        Assert.Equal(AppThemePreference.Light, service.Current.Theme);
    }

    [Fact]
    public async Task CancellationWhileWaitingForWritePreventsPersistence()
    {
        var repository = new TestSettingsRepository
        {
            BlockFirstSave = true,
        };
        using var service = new AppSettingsService(repository);

        var first = service.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted;
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.SetThemeAsync(
            AppThemePreference.Light,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        repository.ReleaseFirstSave();
        await first;

        Assert.Equal(1, repository.SaveCallCount);
        Assert.Equal(AppThemePreference.Dark, service.Current.Theme);
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _firstSaveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeSaves;
        private int _maximumConcurrentSaves;
        private int _saveCallCount;
        private AppSettings _settings;

        public TestSettingsRepository(AppSettings? settings = null)
        {
            _settings = settings ?? AppSettings.Default;
        }

        public bool BlockFirstSave { get; init; }

        public int GetCallCount { get; private set; }

        public int SaveCallCount => Volatile.Read(ref _saveCallCount);

        public int MaximumConcurrentSaves => Volatile.Read(ref _maximumConcurrentSaves);

        public List<AppSettings> SavedSettings { get; } = [];

        public Task FirstSaveStarted => _firstSaveStarted.Task;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCallCount++;
            return Task.FromResult(_settings);
        }

        public async Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var call = Interlocked.Increment(ref _saveCallCount);
            var active = Interlocked.Increment(ref _activeSaves);
            SetMaximumConcurrentSaves(active);
            try
            {
                if (call == 1)
                {
                    _firstSaveStarted.TrySetResult();
                    if (BlockFirstSave)
                    {
                        await _releaseFirstSave.Task.WaitAsync(cancellationToken);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                _settings = settings;
                SavedSettings.Add(settings);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSaves);
            }
        }

        public void ReleaseFirstSave()
        {
            _releaseFirstSave.TrySetResult();
        }

        private void SetMaximumConcurrentSaves(int candidate)
        {
            var current = Volatile.Read(ref _maximumConcurrentSaves);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrentSaves,
                    candidate,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

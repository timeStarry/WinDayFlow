using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Settings;

public sealed class AppSettingsServiceTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 5, 30, 0, TimeSpan.Zero);
    private static readonly string[] PrepareCommittedCalls = ["prepare", "committed"];
    private static readonly string[] PrepareAbortedCalls = ["prepare", "aborted"];
    private static readonly string[] PrepareCommittedAbortedCalls =
        ["prepare", "committed", "aborted"];

    [Fact]
    public void DefaultSettingsAreLocalAndCaptureSafe()
    {
        var settings = AppSettings.Default;

        Assert.Equal(AppThemePreference.System, settings.Theme);
        Assert.False(settings.CaptureEnabled);
        Assert.False(settings.CloudAnalysisEnabled);
        Assert.Null(settings.RecordingConsent);
        Assert.Equal(CapturePrivacySettings.Default, settings.CapturePrivacy);
        Assert.Equal(10, settings.CaptureIntervalSeconds);
        Assert.Equal(
            CaptureApplicationPrivacyMode.ProtectByForegroundApplication,
            settings.CapturePrivacy.ApplicationPrivacyMode);
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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturePrivacySettings(
                CapturePrivacySettings.DefaultRetentionDays,
                ExcludeSensitiveApplications: true,
                PauseInRemoteSessions: true,
                PauseDuringScreenSharing: true,
                Revision: 1,
                ApplicationPrivacyMode: (CaptureApplicationPrivacyMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                RecordingConsent: null,
                CapturePrivacySettings.Default,
                CaptureIntervalSeconds: 7));
    }

    [Fact]
    public async Task CaptureIntervalPersistsWithoutChangingOtherSettings()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(repository);
        await service.InitializeAsync();

        await service.SetCaptureIntervalSecondsAsync(30);

        Assert.Equal(30, service.Current.CaptureIntervalSeconds);
        Assert.Equal(AppThemePreference.System, service.Current.Theme);
        Assert.False(service.Current.CaptureEnabled);
        Assert.Equal(service.Current, Assert.Single(repository.SavedSettings));
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
        var barrier = new TestCommitBarrier();
        using var service = new AppSettingsService(
            repository,
            commitBarrier: barrier);
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
        Assert.Equal(PrepareCommittedCalls, barrier.Calls);
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
    public async Task ApplicationPrivacyModeChangeInvalidatesConsentAndDisablesCapture()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await service.GrantRecordingConsentAsync();
        await service.SetCaptureEnabledAsync(enabled: true);
        var consent = Assert.IsType<RecordingConsent>(service.Current.RecordingConsent);

        await service.SetCaptureApplicationPrivacyModeAsync(
            CaptureApplicationPrivacyMode.AllowAllApplications);

        Assert.False(service.Current.CaptureEnabled);
        Assert.False(service.HasValidRecordingConsent);
        Assert.Equal(
            CaptureApplicationPrivacyMode.AllowAllApplications,
            service.Current.CapturePrivacy.ApplicationPrivacyMode);
        Assert.Equal(2, service.Current.CapturePrivacy.Revision);
        Assert.Equal(1, consent.PrivacyRevision);
        Assert.Equal(consent, service.Current.RecordingConsent);
        Assert.Equal(3, repository.SavedSettings.Count);
    }

    [Fact]
    public async Task ReapplyingApplicationPrivacyModeDoesNotPersist()
    {
        var repository = new TestSettingsRepository();
        using var service = new AppSettingsService(repository);

        await service.SetCaptureApplicationPrivacyModeAsync(
            CaptureApplicationPrivacyMode.ProtectByForegroundApplication);

        Assert.Equal(CapturePrivacySettings.Default, service.Current.CapturePrivacy);
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
    public async Task RestrictivePrepareCompletesBeforeSettingsAreSaved()
    {
        var privacy = CapturePrivacySettings.Default;
        var consent = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            ConsentTime,
            privacy.Revision);
        var stored = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent,
            privacy);
        var repository = new TestSettingsRepository(stored);
        var barrier = new TestCommitBarrier();
        using var service = new AppSettingsService(
            repository,
            commitBarrier: barrier);
        await service.InitializeAsync();
        barrier.ResetObservations();
        barrier.BlockNextPrepare();

        var revoke = service.RevokeRecordingConsentAsync();
        await barrier.PrepareStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, repository.SaveCallCount);
        Assert.True(service.Current.CaptureEnabled);
        Assert.NotNull(service.Current.RecordingConsent);

        barrier.ReleasePrepare();
        await revoke;

        Assert.Equal(1, repository.SaveCallCount);
        Assert.False(service.Current.CaptureEnabled);
        Assert.Null(service.Current.RecordingConsent);
        Assert.Equal(PrepareCommittedCalls, barrier.Calls);
    }

    [Fact]
    public async Task SaveFailureCallsAbortAndAbortFailureDoesNotMaskOriginalFailure()
    {
        var privacy = CapturePrivacySettings.Default;
        var consent = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            ConsentTime,
            privacy.Revision);
        var stored = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent,
            privacy);
        var repository = new TestSettingsRepository(stored);
        var barrier = new TestCommitBarrier();
        using var service = new AppSettingsService(
            repository,
            commitBarrier: barrier);
        await service.InitializeAsync();
        barrier.ResetObservations();
        var saveFailure = new InvalidOperationException("save failed");
        var abortFailure = new InvalidOperationException("abort failed");
        repository.SaveException = saveFailure;
        barrier.AbortException = abortFailure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RevokeRecordingConsentAsync());

        Assert.Same(saveFailure, thrown);
        Assert.Same(saveFailure, barrier.AbortedFailure);
        Assert.False(barrier.AbortedSettingsApplied);
        Assert.True(service.Current.CaptureEnabled);
        Assert.Same(consent, service.Current.RecordingConsent);
        Assert.Equal(PrepareAbortedCalls, barrier.Calls);
    }

    [Fact]
    public async Task CommitFailureKeepsPersistedCurrentAndPublishesTheAppliedChange()
    {
        var repository = new TestSettingsRepository();
        var barrier = new TestCommitBarrier();
        var commitFailure = new InvalidOperationException("commit failed");
        barrier.CommitException = commitFailure;
        using var service = new AppSettingsService(
            repository,
            commitBarrier: barrier);
        var eventCount = 0;
        service.SettingsChanged += (_, _) => eventCount++;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetThemeAsync(AppThemePreference.Dark));

        Assert.Same(commitFailure, thrown);
        Assert.Equal(AppThemePreference.Dark, service.Current.Theme);
        Assert.Equal(service.Current, Assert.Single(repository.SavedSettings));
        Assert.True(barrier.AbortedSettingsApplied);
        Assert.Same(commitFailure, barrier.AbortedFailure);
        Assert.Equal(PrepareCommittedAbortedCalls, barrier.Calls);
        Assert.Equal(1, eventCount);
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

        public Exception? SaveException { get; set; }

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
            AppSettings expected,
            AppSettings proposed,
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
                if (SaveException is { } saveException)
                {
                    throw saveException;
                }

                if (_settings != expected)
                {
                    throw new AppSettingsConcurrencyException();
                }

                _settings = proposed;
                SavedSettings.Add(proposed);
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

    private sealed class TestCommitBarrier : IAppSettingsCommitBarrier
    {
        private TaskCompletionSource _prepareStarted = CreateCompletionSource();
        private TaskCompletionSource _releasePrepare = CreateCompletionSource();
        private bool _blockNextPrepare;

        public List<string> Calls { get; } = [];

        public Exception? CommitException { get; set; }

        public Exception? AbortException { get; set; }

        public Exception? AbortedFailure { get; private set; }

        public bool AbortedSettingsApplied { get; private set; }

        public Task PrepareStarted => _prepareStarted.Task;

        public async Task PrepareAsync(
            AppSettings previous,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("prepare");
            if (!_blockNextPrepare)
            {
                return;
            }

            _blockNextPrepare = false;
            _prepareStarted.TrySetResult();
            await _releasePrepare.Task.WaitAsync(cancellationToken);
        }

        public Task CommittedAsync(
            AppSettings previous,
            AppSettings current,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("committed");
            return CommitException is { } exception
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }

        public Task AbortedAsync(
            AppSettings previous,
            AppSettings proposed,
            bool settingsApplied,
            Exception failure,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("aborted");
            AbortedSettingsApplied = settingsApplied;
            AbortedFailure = failure;
            return AbortException is { } exception
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }

        public void BlockNextPrepare()
        {
            _prepareStarted = CreateCompletionSource();
            _releasePrepare = CreateCompletionSource();
            _blockNextPrepare = true;
        }

        public void ReleasePrepare()
        {
            _releasePrepare.TrySetResult();
        }

        public void ResetObservations()
        {
            Calls.Clear();
            AbortedFailure = null;
            AbortedSettingsApplied = false;
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

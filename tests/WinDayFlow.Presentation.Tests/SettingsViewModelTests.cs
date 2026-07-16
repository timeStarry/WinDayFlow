using System.ComponentModel;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class SettingsViewModelTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 5, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CaptureState.Unavailable, "原生录制组件尚未接入。", false)]
    [InlineData(CaptureState.BlockedByConsent, "需要先查看并同意录制说明。", true)]
    [InlineData(CaptureState.Recording, "正在将屏幕活动记录到本地。", true)]
    [InlineData(CaptureState.Paused, "录制已暂停。", true)]
    [InlineData(CaptureState.Faulted, "录制组件发生错误。", true)]
    [InlineData(CaptureState.Stopped, "录制组件已就绪。", true)]
    public async Task InitialStateProjectsSettingsAndCaptureStatus(
        CaptureState state,
        string expectedAvailabilityText,
        bool expectedBackendAvailable)
    {
        var stored = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);
        var repository = new TestSettingsRepository(stored);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(state);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        Assert.False(viewModel.CaptureEnabled);
        Assert.True(viewModel.CloudAnalysisEnabled);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.Equal(expectedBackendAvailable, viewModel.IsCaptureBackendAvailable);
        Assert.False(viewModel.CanChangeCapture);
        Assert.True(viewModel.CanGrantConsent);
        Assert.False(viewModel.CanRevokeConsent);
        Assert.Equal("尚未同意屏幕活动录制", viewModel.ConsentStatusText);
        Assert.Equal("录制保持关闭；你仍可使用手工时间线。", viewModel.ConsentDetailText);
        Assert.Equal(expectedAvailabilityText, viewModel.CaptureAvailabilityText);
    }

    [Fact]
    public async Task ThemeAndConsentChangesPersistAndRefreshProjection()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(
            repository,
            new FixedTimeProvider(ConsentTime));
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);
        var changedProperties = ObserveChanges(viewModel);

        Assert.True(await viewModel.SetThemeAsync(AppThemePreference.Dark));
        Assert.True(await viewModel.GrantRecordingConsentAsync());

        Assert.Equal(AppThemePreference.Dark, settings.Current.Theme);
        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        var consent = Assert.IsType<RecordingConsent>(
            settings.Current.RecordingConsent);
        Assert.Equal(
            AppSettingsService.CurrentRecordingConsentVersion,
            consent.PolicyVersion);
        Assert.Equal(ConsentTime, consent.AcceptedAtUtc);
        Assert.True(viewModel.HasValidRecordingConsent);
        Assert.True(viewModel.CanChangeCapture);
        Assert.False(viewModel.CanGrantConsent);
        Assert.True(viewModel.CanRevokeConsent);
        Assert.Equal("已同意当前录制说明", viewModel.ConsentStatusText);
        Assert.StartsWith(
            $"版本 {AppSettingsService.CurrentRecordingConsentVersion}",
            viewModel.ConsentDetailText,
            StringComparison.Ordinal);
        Assert.Contains(nameof(SettingsViewModel.Theme), changedProperties);
        Assert.Contains(
            nameof(SettingsViewModel.HasValidRecordingConsent),
            changedProperties);
        Assert.Contains(nameof(SettingsViewModel.CanChangeCapture), changedProperties);
        Assert.Equal(2, repository.SavedSettings.Count);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task GrantFailureUsesStableErrorAndCanBeCleared()
    {
        var repository = new TestSettingsRepository
        {
            SaveException = new InvalidOperationException("Sensitive storage detail."),
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.GrantRecordingConsentAsync());

        Assert.False(settings.HasValidRecordingConsent);
        Assert.True(viewModel.HasError);
        Assert.Equal("无法保存设置，请稍后重试。", viewModel.ErrorMessage);

        viewModel.ClearError();

        Assert.False(viewModel.HasError);
        Assert.Empty(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task RevokeStopsActiveCaptureBeforeClearingConsent()
    {
        var consent = CreateConsent();
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                consent));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                Assert.Same(consent, settings.Current.RecordingConsent);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.RevokeRecordingConsentAsync());

        Assert.Equal(1, capture.StopCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Null(settings.Current.RecordingConsent);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.False(viewModel.CaptureEnabled);
        Assert.True(viewModel.CanGrantConsent);
        Assert.False(viewModel.CanRevokeConsent);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task RevokeStopFailurePreservesConsentAndReportsCaptureError()
    {
        var consent = CreateConsent();
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                consent));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException(
                "Sensitive capture detail."),
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.RevokeRecordingConsentAsync());

        Assert.Equal(1, capture.StopCount);
        Assert.True(settings.Current.CaptureEnabled);
        Assert.Same(consent, settings.Current.RecordingConsent);
        Assert.Empty(repository.SavedSettings);
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureEnableAndDisableUseSafeOperationOrder()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                return Task.CompletedTask;
            },
            StopOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                return Task.CompletedTask;
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: true));
        Assert.True(settings.Current.CaptureEnabled);
        Assert.Equal(CaptureState.Recording, capture.CurrentStatus.State);

        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: false));
        Assert.False(settings.Current.CaptureEnabled);
        Assert.Equal(CaptureState.Stopped, capture.CurrentStatus.State);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(2, repository.SavedSettings.Count);
    }

    [Fact]
    public async Task CaptureStartFailureRollsBackPersistedEnablement()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = _ =>
            {
                Assert.True(settings.Current.CaptureEnabled);
                throw new InvalidOperationException("Sensitive capture detail.");
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.False(await viewModel.SetCaptureEnabledAsync(enabled: true));

        Assert.Equal(1, capture.StartCount);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.CaptureEnabled);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.True(saved.CaptureEnabled),
            saved => Assert.False(saved.CaptureEnabled));
        Assert.Equal("无法更改录制状态，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CaptureStartCancellationRollsBackBeforePropagating()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var startEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TestCaptureService(CaptureState.Stopped)
        {
            StartOperation = async token =>
            {
                startEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        using var viewModel = new SettingsViewModel(settings, capture);
        using var cancellation = new CancellationTokenSource();

        var mutation = viewModel.SetCaptureEnabledAsync(
            enabled: true,
            cancellation.Token);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasError);
        Assert.Collection(
            repository.SavedSettings,
            saved => Assert.True(saved.CaptureEnabled),
            saved => Assert.False(saved.CaptureEnabled));
    }

    [Fact]
    public async Task EnabledCaptureCanBeDisabledWhenBackendBecomesUnavailable()
    {
        var repository = new TestSettingsRepository(
            new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: true,
                CloudAnalysisEnabled: false,
                CreateConsent()));
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Unavailable);
        using var viewModel = new SettingsViewModel(settings, capture);

        Assert.True(viewModel.CanChangeCapture);
        Assert.True(await viewModel.SetCaptureEnabledAsync(enabled: false));

        Assert.False(viewModel.CaptureEnabled);
        Assert.False(viewModel.CanChangeCapture);
        Assert.Equal(0, capture.StopCount);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task ConcurrentMutationIsRejectedWithoutResettingBusyState()
    {
        var repository = new TestSettingsRepository
        {
            BlockFirstSave = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);

        var first = viewModel.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await viewModel.GrantRecordingConsentAsync();

        Assert.False(second);
        Assert.True(viewModel.IsBusy);
        Assert.Equal("另一项设置操作正在进行，请稍候。", viewModel.ErrorMessage);

        repository.ReleaseFirstSave();
        Assert.True(await first);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(AppThemePreference.Dark, viewModel.Theme);
        Assert.False(viewModel.HasValidRecordingConsent);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndResetsBusyState()
    {
        var repository = new TestSettingsRepository
        {
            WaitForFirstSaveCancellation = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        using var viewModel = new SettingsViewModel(settings, capture);
        using var cancellation = new CancellationTokenSource();

        var mutation = viewModel.SetThemeAsync(
            AppThemePreference.Dark,
            cancellation.Token);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(AppThemePreference.System, viewModel.Theme);
        Assert.False(viewModel.HasError);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task DisposeCancelsMutationDetachesEventsAndRejectsFurtherWrites()
    {
        var repository = new TestSettingsRepository
        {
            WaitForFirstSaveCancellation = true,
        };
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var viewModel = new SettingsViewModel(settings, capture);
        var changedProperties = ObserveChanges(viewModel);

        var mutation = viewModel.SetThemeAsync(AppThemePreference.Dark);
        await repository.FirstSaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, capture.SubscriptionCount);

        viewModel.Dispose();

        Assert.False(await mutation);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(0, capture.SubscriptionCount);
        changedProperties.Clear();

        Assert.False(await viewModel.GrantRecordingConsentAsync());
        await settings.SetThemeAsync(AppThemePreference.Light);
        capture.TransitionTo(CaptureState.Unavailable);

        Assert.Empty(changedProperties);
        Assert.Equal(AppThemePreference.Light, viewModel.Theme);
    }

    [Fact]
    public async Task DisposeDropsCaptureUpdateAlreadyQueuedForUiDispatch()
    {
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var capture = new TestCaptureService(CaptureState.Stopped);
        var dispatchContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(dispatchContext);
            viewModel = new SettingsViewModel(settings, capture);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var changedProperties = ObserveChanges(viewModel);
        await Task.Run(() => capture.TransitionTo(CaptureState.Unavailable));
        await dispatchContext.Posted.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();
        dispatchContext.RunPostedCallback();

        Assert.Empty(changedProperties);
        Assert.Equal("原生录制组件尚未接入。", viewModel.CaptureAvailabilityText);
    }

    private static RecordingConsent CreateConsent()
    {
        return new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            ConsentTime);
    }

    private static HashSet<string> ObserveChanges(INotifyPropertyChanged source)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                properties.Add(args.PropertyName);
            }
        };
        return properties;
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _firstSaveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AppSettings _settings;
        private int _saveCallCount;

        public TestSettingsRepository(AppSettings? settings = null)
        {
            _settings = settings ?? AppSettings.Default;
        }

        public bool BlockFirstSave { get; init; }

        public bool WaitForFirstSaveCancellation { get; init; }

        public Exception? SaveException { get; init; }

        public Task FirstSaveStarted => _firstSaveStarted.Task;

        public List<AppSettings> SavedSettings { get; } = [];

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public async Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _saveCallCount);
            if (call == 1)
            {
                _firstSaveStarted.TrySetResult();
                if (WaitForFirstSaveCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                if (BlockFirstSave)
                {
                    await _releaseFirstSave.Task.WaitAsync(cancellationToken);
                }
            }

            if (SaveException is not null)
            {
                throw SaveException;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _settings = settings;
            SavedSettings.Add(settings);
        }

        public void ReleaseFirstSave()
        {
            _releaseFirstSave.TrySetResult();
        }
    }

    private sealed class TestCaptureService : ICaptureService
    {
        private static readonly DateTimeOffset StatusTime =
            new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;

        public TestCaptureService(CaptureState initialState)
        {
            CurrentStatus = CreateStatus(initialState);
        }

        public CaptureStatus CurrentStatus { get; private set; }

        public Func<CancellationToken, Task>? StartOperation { get; init; }

        public Func<CancellationToken, Task>? StopOperation { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int SubscriptionCount { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add
            {
                _statusChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                _statusChanged -= value;
                SubscriptionCount--;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (StartOperation is not null)
            {
                await StartOperation(cancellationToken);
            }

            TransitionTo(CaptureState.Recording);
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Paused);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Recording);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (StopOperation is not null)
            {
                await StopOperation(cancellationToken);
            }

            TransitionTo(CaptureState.Stopped);
        }

        public void TransitionTo(CaptureState state)
        {
            var previous = CurrentStatus;
            CurrentStatus = CreateStatus(state);
            _statusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(previous, CurrentStatus));
        }

        private static CaptureStatus CreateStatus(CaptureState state)
        {
            return new CaptureStatus(
                state,
                StatusTime,
                Reason: state switch
                {
                    CaptureState.Unavailable => CaptureReasonCode.BackendUnavailable,
                    CaptureState.Faulted => CaptureReasonCode.BackendFault,
                    _ => CaptureReasonCode.None,
                },
                ErrorCode: state == CaptureState.Faulted
                    ? CaptureErrorCode.Unknown
                    : CaptureErrorCode.None);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource _posted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SendOrPostCallback? _callback;
        private object? _state;

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
            _posted.TrySetResult();
        }

        public void RunPostedCallback()
        {
            Assert.NotNull(_callback);
            _callback(_state);
        }
    }
}

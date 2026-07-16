using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Capture;

public sealed class ConsentGatedCaptureServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAndResumeNeverReachBackendWithoutCurrentConsent(
        bool useOutdatedConsent)
    {
        using var settings = await CreateSettingsAsync(useOutdatedConsent);
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        using var service = new ConsentGatedCaptureService(backend, settings);

        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.StartAsync());
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());

        Assert.Equal(0, backend.StartCount);
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task CurrentConsentDelegatesEveryLifecycleCommand()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        using var service = new ConsentGatedCaptureService(backend, settings);

        await service.StartAsync();
        await service.PauseAsync();
        await service.ResumeAsync();
        await service.StopAsync();

        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.PauseCount);
        Assert.Equal(1, backend.ResumeCount);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.Stopped, service.CurrentStatus.State);
    }

    [Fact]
    public async Task RevokingConsentStopsBackendBeforePublishingBlockedStatus()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Paused);
        using var service = new ConsentGatedCaptureService(backend, settings);
        var transitions = new List<CaptureStatusChangedEventArgs>();
        service.StatusChanged += (_, eventArgs) => transitions.Add(eventArgs);

        await settings.RevokeRecordingConsentAsync();
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        var transition = Assert.Single(transitions);
        Assert.Equal(CaptureState.Paused, transition.Previous.State);
        Assert.Equal(CaptureState.BlockedByConsent, transition.Current.State);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task PauseAndStopDelegateEvenWhenConsentIsMissing()
    {
        using var settings = await CreateSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped)
        {
            TransitionOnCommands = false,
        };
        using var service = new ConsentGatedCaptureService(backend, settings);

        await service.PauseAsync();
        await service.StopAsync();

        Assert.Equal(1, backend.PauseCount);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);
    }

    [Fact]
    public async Task BackendUnavailableAndFaultedStatesTakePriorityOverConsent()
    {
        using var settings = await CreateSettingsAsync();
        var releaseStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Unavailable)
        {
            StopOperation = token => releaseStop.Task.WaitAsync(token),
        };
        using var service = new ConsentGatedCaptureService(backend, settings);

        Assert.Equal(CaptureState.Unavailable, service.CurrentStatus.State);

        backend.TransitionTo(CaptureState.Faulted, "Backend failure");
        await backend.StopStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.Faulted, service.CurrentStatus.State);
        Assert.Equal("Backend failure", service.CurrentStatus.Detail);

        releaseStop.TrySetResult();
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);
    }

    [Fact]
    public async Task BackendEventsAreMappedFromTheCachedProjectedStatus()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        using var service = new ConsentGatedCaptureService(backend, settings);
        var transitions = new List<CaptureStatusChangedEventArgs>();
        service.StatusChanged += (_, eventArgs) => transitions.Add(eventArgs);

        backend.TransitionTo(CaptureState.Recording, "Recording display 1");

        var transition = Assert.Single(transitions);
        Assert.Equal(CaptureState.Stopped, transition.Previous.State);
        Assert.Equal(CaptureState.Recording, transition.Current.State);
        Assert.Equal("Recording display 1", transition.Current.Detail);
        Assert.Equal(1UL, transition.Current.Sequence);
    }

    [Fact]
    public async Task ActiveBackendStateIsVisibleUntilConsentStopCompletes()
    {
        using var settings = await CreateSettingsAsync();
        var releaseStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Stopped)
        {
            StopOperation = token => releaseStop.Task.WaitAsync(token),
        };
        using var service = new ConsentGatedCaptureService(backend, settings);
        var transitions = new List<CaptureStatusChangedEventArgs>();
        service.StatusChanged += (_, eventArgs) => transitions.Add(eventArgs);

        backend.TransitionTo(CaptureState.Recording);
        await backend.StopStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.Recording, service.CurrentStatus.State);

        backend.TransitionTo(CaptureState.Paused);

        Assert.Equal(CaptureState.Paused, service.CurrentStatus.State);
        Assert.Collection(
            transitions,
            transition =>
            {
                Assert.Equal(CaptureState.BlockedByConsent, transition.Previous.State);
                Assert.Equal(CaptureState.Recording, transition.Current.State);
            },
            transition =>
            {
                Assert.Equal(CaptureState.Recording, transition.Previous.State);
                Assert.Equal(CaptureState.Paused, transition.Current.State);
            });

        releaseStop.TrySetResult();
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task ConsentStopFailureNeverHidesActualRecording()
    {
        using var settings = await CreateSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped)
        {
            StopOperation = _ => throw new InvalidOperationException("stop failed"),
        };
        using var service = new ConsentGatedCaptureService(backend, settings);

        backend.TransitionTo(CaptureState.Recording);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.Recording, service.CurrentStatus.State);
        Assert.Equal(
            "录制授权已失效，但自动停止失败。请立即使用停止操作。",
            service.CurrentStatus.Detail);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task RevocationDuringStartQueuesStopBehindLifecycleOperation()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Stopped)
        {
            StartOperation = token => releaseStart.Task.WaitAsync(token),
        };
        using var service = new ConsentGatedCaptureService(backend, settings);

        var start = service.StartAsync();
        await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await settings.RevokeRecordingConsentAsync();

        Assert.Equal(0, backend.StopCount);

        releaseStart.TrySetResult();
        await start;
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);
    }

    [Fact]
    public async Task DisposeUnsubscribesFromBackendAndSettingsChanges()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        var service = new ConsentGatedCaptureService(backend, settings);
        var original = service.CurrentStatus;
        var eventCount = 0;
        service.StatusChanged += (_, _) => eventCount++;

        service.Dispose();
        backend.TransitionTo(CaptureState.Recording);
        await settings.RevokeRecordingConsentAsync();

        Assert.Equal(0, backend.SubscriberCount);
        Assert.Equal(0, eventCount);
        Assert.Equal(original, service.CurrentStatus);
    }

    private static async Task<AppSettingsService> CreateSettingsAsync(
        bool useOutdatedConsent = false)
    {
        RecordingConsent? consent = useOutdatedConsent
            ? new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion + 1,
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero))
            : null;
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            consent);
        var settings = new AppSettingsService(new InMemorySettingsRepository(initial));
        await settings.InitializeAsync();
        return settings;
    }

    private static async Task<AppSettingsService> CreateConsentedSettingsAsync()
    {
        var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        return settings;
    }

    private sealed class InMemorySettingsRepository : IAppSettingsRepository
    {
        private AppSettings _settings;

        public InMemorySettingsRepository(AppSettings settings)
        {
            _settings = settings;
        }

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCaptureBackend : ICaptureBackend
    {
        private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
        private long _statusSequence;

        public StubCaptureBackend(CaptureState state)
        {
            CurrentStatus = CreateStatus(state, null, _statusSequence);
        }

        public CaptureStatus CurrentStatus { get; private set; }

        public int StartCount { get; private set; }

        public int PauseCount { get; private set; }

        public int ResumeCount { get; private set; }

        public int StopCount { get; private set; }

        public bool TransitionOnCommands { get; init; } = true;

        public Func<CancellationToken, Task>? StartOperation { get; init; }

        public Func<CancellationToken, Task>? StopOperation { get; init; }

        public Task StartStarted => _startStarted.Task;

        public Task StopStarted => _stopStarted.Task;

        public Task StopCompleted => _stopCompleted.Task;

        public int SubscriberCount => _statusChanged?.GetInvocationList().Length ?? 0;

        private readonly TaskCompletionSource _startStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add => _statusChanged += value;
            remove => _statusChanged -= value;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            _startStarted.TrySetResult();
            if (StartOperation is not null)
            {
                await StartOperation(cancellationToken);
            }

            if (TransitionOnCommands)
            {
                TransitionTo(CaptureState.Recording);
            }
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseCount++;
            if (TransitionOnCommands)
            {
                TransitionTo(CaptureState.Paused);
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCount++;
            if (TransitionOnCommands)
            {
                TransitionTo(CaptureState.Recording);
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            _stopStarted.TrySetResult();
            try
            {
                if (StopOperation is not null)
                {
                    await StopOperation(cancellationToken);
                }

                if (TransitionOnCommands)
                {
                    TransitionTo(CaptureState.Stopped);
                }
            }
            finally
            {
                _stopCompleted.TrySetResult();
            }
        }

        public void TransitionTo(CaptureState state, string? detail = null)
        {
            var previous = CurrentStatus;
            _statusSequence++;
            CurrentStatus = CreateStatus(state, detail, _statusSequence);
            _statusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(previous, CurrentStatus));
        }

        private static CaptureStatus CreateStatus(
            CaptureState state,
            string? detail,
            long sequence)
        {
            return new CaptureStatus(
                state,
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero)
                    .AddSeconds(sequence),
                detail,
                Sequence: checked((ulong)sequence),
                Reason: state == CaptureState.Faulted
                    ? CaptureReasonCode.BackendFault
                    : CaptureReasonCode.None,
                ErrorCode: state == CaptureState.Faulted
                    ? CaptureErrorCode.Unknown
                    : CaptureErrorCode.None);
        }
    }
}

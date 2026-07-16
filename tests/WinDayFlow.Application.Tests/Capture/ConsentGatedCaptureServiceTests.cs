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
        using var service = CreateService(backend, settings);

        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.StartAsync());
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());

        Assert.Equal(0, backend.StartCount);
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task CurrentConsentStillRequiresCaptureToBeEnabled()
    {
        using var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        using var service = CreateService(backend, settings);

        Assert.Equal(CaptureState.Stopped, service.CurrentStatus.State);

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
        using var service = CreateService(backend, settings);

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
    public async Task RuntimeAuthorizationBlocksStartAndResumeBeforeTheyReachBackend()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        using var service = new ConsentGatedCaptureService(
            backend,
            settings,
            new TestRuntimeAuthorization(isCaptureAuthorized: false));

        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.StartAsync());
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());

        Assert.Equal(0, backend.StartCount);
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task RevokingConsentStopsBackendBeforePublishingBlockedStatus()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Paused);
        using var service = CreateService(backend, settings);
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
    public async Task PauseIsAllowedAndStopIsIdempotentWhenConsentIsMissing()
    {
        using var settings = await CreateSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped)
        {
            TransitionOnCommands = false,
        };
        using var service = CreateService(backend, settings);

        await service.PauseAsync();
        await service.StopAsync();

        Assert.Equal(1, backend.PauseCount);
        Assert.Equal(0, backend.StopCount);
        Assert.Equal(CaptureState.BlockedByConsent, service.CurrentStatus.State);
    }

    [Fact]
    public async Task DisablingCaptureStopsBackendAndBlocksResumeWithoutRevokingConsent()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Recording);
        using var service = CreateService(backend, settings);

        await settings.SetCaptureEnabledAsync(enabled: false);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(settings.Current.CaptureEnabled);
        Assert.True(settings.HasValidRecordingConsent);
        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.Stopped, service.CurrentStatus.State);
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task DisableStopFailureKeepsActualRecordingVisibleAndBlocksResume()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException("stop failed"),
        };
        using var service = CreateService(backend, settings);

        await settings.SetCaptureEnabledAsync(enabled: false);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(settings.Current.CaptureEnabled);
        Assert.True(settings.HasValidRecordingConsent);
        Assert.Equal(CaptureState.Recording, service.CurrentStatus.State);
        Assert.Equal(
            "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。",
            service.CurrentStatus.Detail);
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(
            () => service.ResumeAsync());
        Assert.Equal(0, backend.ResumeCount);
    }

    [Fact]
    public async Task ExplicitStopFailureDoesNotEraseAutomaticStopWarning()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException("stop failed"),
        };
        using var service = CreateService(backend, settings);

        await settings.SetCaptureEnabledAsync(enabled: false);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync());

        Assert.Equal(2, backend.StopCount);
        Assert.Equal(CaptureState.Recording, service.CurrentStatus.State);
        Assert.Equal(
            "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。",
            service.CurrentStatus.Detail);
    }

    [Fact]
    public async Task StopWarningPreservesLatestStateFromUnsequencedBackend()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            StopOperation = _ => throw new InvalidOperationException("stop failed"),
        };
        using var service = CreateService(backend, settings);

        await settings.SetCaptureEnabledAsync(enabled: false);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        backend.TransitionTo(
            CaptureState.Paused,
            detail: null,
            incrementSequence: false);

        Assert.Equal(0UL, service.CurrentStatus.Sequence);
        Assert.Equal(CaptureState.Paused, service.CurrentStatus.State);
        Assert.Equal(
            "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。",
            service.CurrentStatus.Detail);
    }

    [Fact]
    public async Task ExplicitStopQueuesBehindAutomaticStopWithoutCallingBackendTwice()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var releaseStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            StopOperation = token => releaseStop.Task.WaitAsync(token),
        };
        using var service = CreateService(backend, settings);

        await settings.SetCaptureEnabledAsync(enabled: false);
        await backend.StopStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var explicitStop = service.StopAsync();

        releaseStop.TrySetResult();
        await explicitStop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.StopCount);
        Assert.Equal(CaptureState.Stopped, service.CurrentStatus.State);
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
        using var service = CreateService(backend, settings);

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
        using var service = CreateService(backend, settings);
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
        using var service = CreateService(backend, settings);
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
        using var service = CreateService(backend, settings);

        backend.TransitionTo(CaptureState.Recording);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CaptureState.Recording, service.CurrentStatus.State);
        Assert.Equal(
            "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。",
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
        using var service = CreateService(backend, settings);

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
    public async Task RevocationLatchBlocksAQueuedStartBeforeSettingsArePersisted()
    {
        var privacy = CapturePrivacySettings.Default;
        var consent = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            privacy.Revision);
        var initial = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            consent,
            privacy);
        var runtimeAuthorization = new LatchingRuntimeAuthorization();
        using var settings = new AppSettingsService(
            new InMemorySettingsRepository(initial),
            commitBarrier: runtimeAuthorization);
        await settings.InitializeAsync();
        runtimeAuthorization.BlockNextRestrictivePrepare();
        var releasePause = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            TransitionOnCommands = false,
            PauseOperation = token => releasePause.Task.WaitAsync(token),
        };
        using var service = new ConsentGatedCaptureService(
            backend,
            settings,
            runtimeAuthorization);

        var pause = service.PauseAsync();
        await backend.PauseStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var start = service.StartAsync();
        var revoke = settings.RevokeRecordingConsentAsync();
        await runtimeAuthorization.BlockStarted.WaitAsync(TimeSpan.FromSeconds(5));

        releasePause.TrySetResult();
        await pause;
        await Assert.ThrowsAsync<RecordingConsentRequiredException>(() => start);

        Assert.True(settings.Current.CaptureEnabled);
        Assert.True(settings.HasValidRecordingConsent);
        Assert.Equal(0, backend.StartCount);

        runtimeAuthorization.ReleaseBlock();
        await revoke;
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(settings.Current.CaptureEnabled);
        Assert.False(settings.HasValidRecordingConsent);
    }

    [Fact]
    public async Task RuntimeInvalidationStopsActiveCaptureEvenWhenAuthorizationRecovers()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var runtimeAuthorization = new MutableRuntimeAuthorization(
            isCaptureAuthorized: true);
        var releasePause = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            TransitionOnCommands = false,
            PauseOperation = token => releasePause.Task.WaitAsync(token),
        };
        using var service = new ConsentGatedCaptureService(
            backend,
            settings,
            runtimeAuthorization);
        var pause = service.PauseAsync();
        await backend.PauseStarted.WaitAsync(TimeSpan.FromSeconds(5));

        runtimeAuthorization.SetCaptureAuthorized(authorized: false);
        runtimeAuthorization.SetCaptureAuthorized(authorized: true);
        releasePause.TrySetResult();

        await pause.WaitAsync(TimeSpan.FromSeconds(5));
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runtimeAuthorization.IsCaptureAuthorized);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task ConstructorObservesAnInvalidationThatPrecededSubscription()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var runtimeAuthorization = new MutableRuntimeAuthorization(
            isCaptureAuthorized: true);
        runtimeAuthorization.SetCaptureAuthorized(authorized: false);
        runtimeAuthorization.SetCaptureAuthorized(authorized: true);
        var backend = new StubCaptureBackend(CaptureState.Recording);

        using var service = new ConsentGatedCaptureService(
            backend,
            settings,
            runtimeAuthorization);
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, runtimeAuthorization.InvalidationGeneration);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task DisposeUnsubscribesRuntimeAuthorizationAndCancelsBackgroundStop()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var runtimeAuthorization = new MutableRuntimeAuthorization(
            isCaptureAuthorized: true);
        var backend = new StubCaptureBackend(CaptureState.Recording)
        {
            StopOperation = static token => Task.Delay(Timeout.Infinite, token),
        };
        var service = new ConsentGatedCaptureService(
            backend,
            settings,
            runtimeAuthorization);
        runtimeAuthorization.SetCaptureAuthorized(authorized: false);
        await backend.StopStarted.WaitAsync(TimeSpan.FromSeconds(5));

        service.Dispose();
        await backend.StopCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        runtimeAuthorization.SetCaptureAuthorized(authorized: true);
        runtimeAuthorization.SetCaptureAuthorized(authorized: false);

        Assert.Equal(0, runtimeAuthorization.SubscriberCount);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task DisposeUnsubscribesFromBackendAndSettingsChanges()
    {
        using var settings = await CreateConsentedSettingsAsync();
        var backend = new StubCaptureBackend(CaptureState.Stopped);
        var service = CreateService(backend, settings);
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
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                CapturePrivacySettings.Default.Revision)
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

    private static ConsentGatedCaptureService CreateService(
        ICaptureBackend backend,
        AppSettingsService settings)
    {
        return new ConsentGatedCaptureService(
            backend,
            settings,
            new TestRuntimeAuthorization(isCaptureAuthorized: true));
    }

    private static async Task<AppSettingsService> CreateConsentedSettingsAsync()
    {
        var settings = await CreateSettingsAsync();
        await settings.GrantRecordingConsentAsync();
        await settings.SetCaptureEnabledAsync(enabled: true);
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

        public Func<CancellationToken, Task>? PauseOperation { get; init; }

        public Func<CancellationToken, Task>? StopOperation { get; init; }

        public Task StartStarted => _startStarted.Task;

        public Task PauseStarted => _pauseStarted.Task;

        public Task StopStarted => _stopStarted.Task;

        public Task StopCompleted => _stopCompleted.Task;

        public int SubscriberCount => _statusChanged?.GetInvocationList().Length ?? 0;

        private readonly TaskCompletionSource _startStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _pauseStarted =
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

        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseCount++;
            _pauseStarted.TrySetResult();
            if (PauseOperation is not null)
            {
                await PauseOperation(cancellationToken);
            }

            if (TransitionOnCommands)
            {
                TransitionTo(CaptureState.Paused);
            }
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

        public void TransitionTo(
            CaptureState state,
            string? detail = null,
            bool incrementSequence = true)
        {
            var previous = CurrentStatus;
            if (incrementSequence)
            {
                _statusSequence++;
            }

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

    private sealed class TestRuntimeAuthorization(bool isCaptureAuthorized)
        : ICaptureRuntimeAuthorization
    {
        public bool IsCaptureAuthorized { get; } = isCaptureAuthorized;

        public long InvalidationGeneration => 0;

        public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? AuthorizationChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class LatchingRuntimeAuthorization
        : IAppSettingsCommitBarrier, ICaptureRuntimeAuthorization
    {
        private readonly TaskCompletionSource _blockStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlock = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _authorized = 1;
        private int _blockNextRestrictivePrepare;
        private long _invalidationGeneration;

        public bool IsCaptureAuthorized => Volatile.Read(ref _authorized) != 0;

        public long InvalidationGeneration =>
            Volatile.Read(ref _invalidationGeneration);

        public Task BlockStarted => _blockStarted.Task;

        public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? AuthorizationChanged;

        public async Task PrepareAsync(
            AppSettings previous,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blockNextRestrictivePrepare, 0) == 0
                || !previous.CaptureEnabled
                || proposed.CaptureEnabled)
            {
                return;
            }

            Volatile.Write(ref _authorized, 0);
            var generation = Interlocked.Increment(ref _invalidationGeneration);
            AuthorizationChanged?.Invoke(
                this,
                new CaptureRuntimeAuthorizationChangedEventArgs(
                    isCaptureAuthorized: false,
                    generation));
            _blockStarted.TrySetResult();
            await _releaseBlock.Task.WaitAsync(cancellationToken);
        }

        public Task CommittedAsync(
            AppSettings previous,
            AppSettings current,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AbortedAsync(
            AppSettings previous,
            AppSettings proposed,
            bool settingsApplied,
            Exception failure,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void BlockNextRestrictivePrepare()
        {
            Interlocked.Exchange(ref _blockNextRestrictivePrepare, 1);
        }

        public void ReleaseBlock()
        {
            _releaseBlock.TrySetResult();
        }
    }

    private sealed class MutableRuntimeAuthorization(bool isCaptureAuthorized)
        : ICaptureRuntimeAuthorization
    {
        private EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>?
            _authorizationChanged;
        private int _authorized = isCaptureAuthorized ? 1 : 0;
        private long _invalidationGeneration;

        public bool IsCaptureAuthorized => Volatile.Read(ref _authorized) != 0;

        public long InvalidationGeneration =>
            Volatile.Read(ref _invalidationGeneration);

        public int SubscriberCount =>
            _authorizationChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>?
            AuthorizationChanged
        {
            add => _authorizationChanged += value;
            remove => _authorizationChanged -= value;
        }

        public void SetCaptureAuthorized(bool authorized)
        {
            var previous = Interlocked.Exchange(
                ref _authorized,
                authorized ? 1 : 0) != 0;
            if (previous == authorized)
            {
                return;
            }

            if (!authorized)
            {
                Interlocked.Increment(ref _invalidationGeneration);
            }

            _authorizationChanged?.Invoke(
                this,
                new CaptureRuntimeAuthorizationChangedEventArgs(
                    authorized,
                    InvalidationGeneration));
        }
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Settings;
using WinDayFlow.Presentation.Settings;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class AiProviderSettingsViewModelTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("794d15de-3e83-418a-925c-ea5aa43161c7");

    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulWorkflowProjectsValidationCloudAndSaveState()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var provider = new TestAnalysisProvider(initial.Profile);
        using var configuration = CreateConfiguration(store, provider, settings);
        await configuration.InitializeAsync();
        using var viewModel = new AiProviderSettingsViewModel(configuration, settings);

        Assert.Equal(initial.Profile.DisplayName, viewModel.DisplayName);
        Assert.False(viewModel.IsValidated);
        Assert.False(viewModel.CanChangeCloudAnalysis);

        Assert.True(await viewModel.TestConnectionAsync());
        Assert.True(viewModel.IsValidated);
        Assert.True(viewModel.CanChangeCloudAnalysis);
        Assert.False(viewModel.HasError);
        Assert.Contains("合成图像", viewModel.NoticeMessage, StringComparison.Ordinal);

        Assert.True(await viewModel.SetCloudAnalysisEnabledAsync(true));
        Assert.True(viewModel.CloudAnalysisEnabled);
        Assert.Contains("新证据", viewModel.CloudAnalysisStatusText, StringComparison.Ordinal);

        Assert.True(await viewModel.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-v2",
            requestTimeoutSeconds: 45,
            replacementApiKey: null));
        Assert.Equal("vision-v2", viewModel.Model);
        Assert.Equal(45, viewModel.RequestTimeoutSeconds);
        Assert.False(viewModel.IsValidated);
        Assert.False(viewModel.CloudAnalysisEnabled);
        Assert.False(viewModel.CanChangeCloudAnalysis);
        Assert.Contains("已保存", viewModel.NoticeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailureIsMappedToActionableErrorState()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var provider = new TestAnalysisProvider(initial.Profile)
        {
            Failure = new AiProviderException(
                AiProviderErrorCode.AuthenticationFailed,
                "authentication failed",
                Guid.NewGuid(),
                isRetryable: false),
        };
        using var configuration = CreateConfiguration(store, provider, settings);
        await configuration.InitializeAsync();
        using var viewModel = new AiProviderSettingsViewModel(configuration, settings);

        Assert.False(await viewModel.TestConnectionAsync());

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
        Assert.Equal("API 密钥未通过提供方验证。", viewModel.ErrorMessage);
        Assert.False(viewModel.HasNotice);
        Assert.False(viewModel.IsValidated);
    }

    [Fact]
    public async Task StoreFailureUsesSaveFallbackAndLeavesCloudOff()
    {
        var initial = CreateSnapshot(revision: 2, validated: true);
        var store = new TestProfileStore(initial)
        {
            SaveException = new InvalidOperationException("save failed"),
        };
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var configuration = CreateConfiguration(
            store,
            new TestAnalysisProvider(initial.Profile),
            settings);
        await configuration.InitializeAsync();
        using var viewModel = new AiProviderSettingsViewModel(configuration, settings);

        Assert.False(await viewModel.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-failing",
            requestTimeoutSeconds: 30,
            replacementApiKey: null));

        Assert.Equal("无法保存提供方配置，请检查地址、模型和 API 密钥。", viewModel.ErrorMessage);
        Assert.False(viewModel.HasNotice);
        Assert.False(viewModel.CloudAnalysisEnabled);
        Assert.Same(initial, configuration.Current);
    }

    [Fact]
    public async Task ConcurrentMutationIsRejectedAndFirstSuccessClearsBusyError()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var provider = new TestAnalysisProvider(initial.Profile)
        {
            BlockAnalysis = true,
        };
        using var configuration = CreateConfiguration(store, provider, settings);
        await configuration.InitializeAsync();
        using var viewModel = new AiProviderSettingsViewModel(configuration, settings);

        var first = viewModel.TestConnectionAsync();
        await provider.AnalysisStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(viewModel.IsBusy);
            Assert.False(await viewModel.SaveAsync(
                initial.Profile.DisplayName,
                initial.Profile.BaseEndpoint.AbsoluteUri,
                "vision-v2",
                requestTimeoutSeconds: 30,
                replacementApiKey: null));
            Assert.Equal("另一项 AI 设置操作正在进行，请稍候。", viewModel.ErrorMessage);
        }
        finally
        {
            provider.ReleaseAnalysis();
        }

        Assert.True(await first);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasError);
        Assert.Contains("连接测试通过", viewModel.NoticeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundMutationRaisesPropertiesThroughCapturedContext()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var configuration = CreateConfiguration(
            store,
            new TestAnalysisProvider(initial.Profile),
            settings);
        await configuration.InitializeAsync();
        var context = new ImmediateRecordingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        AiProviderSettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = new AiProviderSettingsViewModel(configuration, settings);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            var raisedOnCapturedContext = new ConcurrentBag<bool>();
            viewModel.PropertyChanged += (_, _) =>
                raisedOnCapturedContext.Add(SynchronizationContext.Current == context);

            Assert.True(await Task.Run(() => viewModel.TestConnectionAsync()));

            Assert.True(context.PostCount > 0);
            Assert.NotEmpty(raisedOnCapturedContext);
            Assert.DoesNotContain(false, raisedOnCapturedContext);
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.IsValidated);
        }
    }

    [Fact]
    public async Task DisposeCancelsInFlightMutationWithoutDisposingItsGate()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial)
        {
            WaitForSaveCancellation = true,
        };
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var configuration = CreateConfiguration(
            store,
            new TestAnalysisProvider(initial.Profile),
            settings);
        await configuration.InitializeAsync();
        var viewModel = new AiProviderSettingsViewModel(configuration, settings);

        var operation = viewModel.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-blocked",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);
        await store.SaveStarted.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();

        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(store.SaveCancellationObserved);
        viewModel.Dispose();
        Assert.False(await viewModel.TestConnectionAsync());
    }

    [Fact]
    public async Task DisposeDropsProviderUpdateAlreadyQueuedForUiDispatch()
    {
        var initial = CreateSnapshot(revision: 1, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository();
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var configuration = CreateConfiguration(
            store,
            new TestAnalysisProvider(initial.Profile),
            settings);
        await configuration.InitializeAsync();
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        AiProviderSettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = new AiProviderSettingsViewModel(configuration, settings);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var changedProperties = ObserveChanges(viewModel);
        _ = await Task.Run(() => configuration.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-v2",
            requestTimeoutSeconds: 30,
            replacementApiKey: null));
        await context.Posted.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();
        context.RunAllPostedCallbacks();

        Assert.Empty(changedProperties);
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

    private static AiProviderConfigurationService CreateConfiguration(
        TestProfileStore store,
        TestAnalysisProvider provider,
        AppSettingsService settings)
    {
        return new AiProviderConfigurationService(
            store,
            new TestProviderFactory(provider),
            settings,
            new FixedTimeProvider(Now));
    }

    private static AiProviderProfileSnapshot CreateSnapshot(
        long revision,
        bool validated)
    {
        return new AiProviderProfileSnapshot(
            CreateProfile(),
            revision,
            hasApiKey: true,
            validated ? revision : null,
            validated ? Now.AddMinutes(-1) : null);
    }

    private static AiProviderProfile CreateProfile()
    {
        return new AiProviderProfile(
            ProfileId,
            "Primary provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://api.example.com/v1/"),
            "vision-v1",
            TimeSpan.FromSeconds(30));
    }

    private sealed class TestProfileStore(AiProviderProfileSnapshot? current)
        : IAiProviderProfileStore
    {
        private readonly TaskCompletionSource _saveStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public AiProviderProfileSnapshot? Current { get; private set; } = current;

        public Exception? SaveException { get; init; }

        public bool WaitForSaveCancellation { get; init; }

        public Task SaveStarted => _saveStarted.Task;

        public bool SaveCancellationObserved { get; private set; }

        public Task<AiProviderProfileSnapshot?> GetActiveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public async Task<AiProviderProfileSnapshot> SaveActiveAsync(
            AiProviderProfile profile,
            long? expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _ = changedAtUtc;
            cancellationToken.ThrowIfCancellationRequested();
            _saveStarted.TrySetResult();
            if (WaitForSaveCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SaveCancellationObserved = true;
                    throw;
                }
            }

            if (SaveException is not null)
            {
                throw SaveException;
            }

            if (expectedRevision != Current?.Revision)
            {
                throw new AiProviderConfigurationConflictException();
            }

            var hasApiKey = credentialUpdate.Kind switch
            {
                AiProviderCredentialUpdateKind.Preserve => Current?.HasApiKey == true,
                AiProviderCredentialUpdateKind.Replace => true,
                AiProviderCredentialUpdateKind.Clear => false,
                _ => throw new InvalidOperationException("Unexpected credential update."),
            };
            Current = new AiProviderProfileSnapshot(
                profile,
                (Current?.Revision ?? 0) + 1,
                hasApiKey,
                validatedRevision: null,
                validatedAtUtc: null);
            return Current;
        }

        public Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
            Guid profileId,
            long expectedRevision,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current?.Profile.Id != profileId || Current.Revision != expectedRevision)
            {
                return Task.FromResult<AiProviderProfileSnapshot?>(null);
            }

            Current = new AiProviderProfileSnapshot(
                Current.Profile,
                Current.Revision,
                Current.HasApiKey,
                Current.Revision,
                validatedAtUtc);
            return Task.FromResult<AiProviderProfileSnapshot?>(Current);
        }
    }

    private sealed class TestProviderFactory(TestAnalysisProvider provider)
        : IAiAnalysisProviderFactory
    {
        public Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            _ = snapshot;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAiAnalysisProvider>(provider);
        }
    }

    private sealed class TestAnalysisProvider(AiProviderProfile profile)
        : IAiAnalysisProvider, IDisposable
    {
        private readonly TaskCompletionSource _analysisStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAnalysis = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public AiProviderProfile Profile { get; } = profile;

        public AiProviderCapabilities Capabilities =>
            AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;

        public Exception? Failure { get; init; }

        public bool BlockAnalysis { get; init; }

        public Task AnalysisStarted => _analysisStarted.Task;

        public async Task<AiAnalysisResponse> AnalyzeAsync(
            AiAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            _analysisStarted.TrySetResult();
            if (BlockAnalysis)
            {
                await _releaseAnalysis.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new AiAnalysisResponse(
                "synthetic-request",
                Profile.Model,
                AiAnalysisContract.CurrentSchemaVersion,
                activities: []);
        }

        public void ReleaseAnalysis()
        {
            _releaseAnalysis.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        public TestSettingsRepository(bool cloudAnalysisEnabled = false)
        {
            Current = new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                cloudAnalysisEnabled,
                RecordingConsent: null);
        }

        public AppSettings Current { get; private set; }

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
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
                throw new AppSettingsConcurrencyException();
            }

            Current = proposed;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ImmediateRecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)>
            _callbacks = new();
        private readonly TaskCompletionSource _posted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _posted.TrySetResult();
        }

        public void RunAllPostedCallbacks()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }
}

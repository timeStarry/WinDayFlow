using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Settings;
using Xunit;

namespace WinDayFlow.Application.Tests.Ai;

public sealed class AiProviderConfigurationServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("7607a847-ea84-4d83-a7e9-cb89122b490f");

    private static readonly DateTimeOffset ChangedAt =
        new(2026, 7, 23, 18, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task InitializeDisablesCloudAnalysisForUnvalidatedProfile()
    {
        var snapshot = CreateSnapshot(revision: 4, validated: false);
        var store = new TestProfileStore(snapshot);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);

        await service.InitializeAsync();

        Assert.Same(snapshot, service.Current);
        Assert.False(service.IsCloudAnalysisEnabled);
        Assert.False(repository.Current.CloudAnalysisEnabled);
        Assert.Single(repository.SavedSettings);
    }

    [Fact]
    public async Task ChangedSaveDisablesCloudBeforeStoreAndClearsValidation()
    {
        var initial = CreateSnapshot(revision: 7, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        store.BeforeSave = () => Assert.False(settings.Current.CloudAnalysisEnabled);
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-v2",
            requestTimeoutSeconds: 45,
            replacementApiKey: null);

        Assert.Equal(8, saved.Revision);
        Assert.Equal(ProfileId, saved.Profile.Id);
        Assert.Equal("vision-v2", saved.Profile.Model);
        Assert.True(saved.HasApiKey);
        Assert.False(saved.IsValidated);
        Assert.Null(saved.ValidatedAtUtc);
        Assert.Equal(ChangedAt.ToUniversalTime(), store.LastChangedAtUtc);
        Assert.False(service.IsCloudAnalysisEnabled);

        var replaced = await service.SaveAsync(
            saved.Profile.DisplayName,
            saved.Profile.BaseEndpoint.AbsoluteUri,
            saved.Profile.Model,
            requestTimeoutSeconds: 45,
            replacementApiKey: "replacement-key");

        Assert.Equal(9, replaced.Revision);
        Assert.False(replaced.IsValidated);
        Assert.Equal(AiProviderCredentialUpdateKind.Replace, store.LastCredentialUpdate?.Kind);
    }

    [Fact]
    public async Task UnchangedSaveWithPreservedCredentialIsNoOp()
    {
        var initial = CreateSnapshot(revision: 3, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            initial.Profile.Model,
            checked((int)initial.Profile.RequestTimeout.TotalSeconds),
            replacementApiKey: null);

        Assert.Same(initial, saved);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(repository.SavedSettings);
        Assert.True(service.IsCloudAnalysisEnabled);
        Assert.True(saved.IsValidated);
    }

    [Fact]
    public async Task SameOriginEndpointChangeWithoutReplacementPreservesCredential()
    {
        var initial = CreateSnapshot(revision: 3, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            initial.Profile.DisplayName,
            "https://API.EXAMPLE.COM:443/compatible/v1",
            initial.Profile.Model,
            checked((int)initial.Profile.RequestTimeout.TotalSeconds),
            replacementApiKey: null);

        Assert.Equal(4, saved.Revision);
        Assert.Equal("/compatible/v1/", saved.Profile.BaseEndpoint.AbsolutePath);
        Assert.True(saved.HasApiKey);
        Assert.Equal(
            AiProviderCredentialUpdateKind.Preserve,
            store.LastCredentialUpdate?.Kind);
        Assert.False(service.IsCloudAnalysisEnabled);
    }

    [Fact]
    public async Task ChangedRemoteOriginWithoutReplacementIsRejectedWithoutChangingState()
    {
        var initial = CreateSnapshot(revision: 3, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            service.SaveAsync(
                initial.Profile.DisplayName,
                "https://api.other.example/v1",
                initial.Profile.Model,
                checked((int)initial.Profile.RequestTimeout.TotalSeconds),
                replacementApiKey: null));

        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, exception.ErrorCode);
        Assert.Same(initial, service.Current);
        Assert.Same(initial, store.Current);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(store.LastCredentialUpdate);
        Assert.True(service.IsCloudAnalysisEnabled);
        Assert.Empty(repository.SavedSettings);
    }

    [Fact]
    public async Task SwitchingToLoopbackWithoutReplacementClearsCredential()
    {
        var initial = CreateSnapshot(revision: 3, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            "Local provider",
            "http://127.0.0.1:11434/v1",
            "local-vision",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);

        Assert.Equal(4, saved.Revision);
        Assert.True(saved.Profile.IsLoopback);
        Assert.False(saved.HasApiKey);
        Assert.Equal(
            AiProviderCredentialUpdateKind.Clear,
            store.LastCredentialUpdate?.Kind);
        Assert.False(service.IsCloudAnalysisEnabled);
    }

    [Fact]
    public async Task ChangedRemoteOriginWithReplacementUsesOnlyNewCredential()
    {
        var initial = CreateSnapshot(revision: 3, validated: true);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            "Replacement provider",
            "https://api.other.example/v1",
            "other-vision",
            requestTimeoutSeconds: 30,
            replacementApiKey: "new-provider-key");

        Assert.Equal(4, saved.Revision);
        Assert.True(saved.HasApiKey);
        Assert.Equal(
            AiProviderCredentialUpdateKind.Replace,
            store.LastCredentialUpdate?.Kind);
        Assert.Equal(
            "new-provider-key",
            store.LastCredentialUpdate?.GetReplacement());
        Assert.False(service.IsCloudAnalysisEnabled);
    }

    [Fact]
    public async Task InitialLoopbackSaveWithoutCredentialUsesClearUpdate()
    {
        var store = new TestProfileStore(current: null);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var saved = await service.SaveAsync(
            "Local provider",
            "http://localhost:11434/v1",
            "local-vision",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);

        Assert.False(saved.HasApiKey);
        Assert.Equal(
            AiProviderCredentialUpdateKind.Clear,
            store.LastCredentialUpdate?.Kind);
    }

    [Fact]
    public async Task FailedStoreSaveLeavesCloudAnalysisDisabledAndCurrentUnchanged()
    {
        var initial = CreateSnapshot(revision: 2, validated: true);
        var store = new TestProfileStore(initial)
        {
            SaveException = new InvalidOperationException("store failed"),
        };
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: true);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        store.BeforeSave = () => Assert.False(settings.Current.CloudAnalysisEnabled);
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-failing",
            requestTimeoutSeconds: 30,
            replacementApiKey: null));

        Assert.Same(initial, service.Current);
        Assert.Same(initial, store.Current);
        Assert.False(service.IsCloudAnalysisEnabled);
        Assert.False(repository.Current.CloudAnalysisEnabled);
    }

    [Fact]
    public async Task TestConnectionUsesOnlySyntheticEvidenceAndDoesNotEnableCloud()
    {
        var initial = CreateSnapshot(revision: 5, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        var provider = new TestAnalysisProvider(initial.Profile);
        var factory = new TestProviderFactory(provider);
        using var service = new AiProviderConfigurationService(
            store,
            factory,
            settings,
            new FixedTimeProvider(ChangedAt));
        await service.InitializeAsync();

        var validated = await service.TestConnectionAsync();

        Assert.Equal(initial.Revision, validated.Revision);
        Assert.True(validated.IsValidated);
        Assert.Equal(ChangedAt.ToUniversalTime(), validated.ValidatedAtUtc);
        Assert.False(service.IsCloudAnalysisEnabled);
        Assert.Empty(repository.SavedSettings);
        Assert.Equal(1, factory.CreateCount);
        Assert.Same(initial, factory.LastSnapshot);
        Assert.True(provider.Disposed);

        var request = Assert.Single(provider.Requests);
        Assert.Equal("connection-test", request.CaptureChunkId);
        Assert.Equal("synthetic/connection-test.jpg", request.ArtifactPath);
        Assert.Equal("connection-test-v1", request.PromptVersion);
        Assert.Equal("zh-CN", request.Locale);
        Assert.Equal(ChangedAt.ToUniversalTime(), request.Range.Start);
        Assert.Equal(request.Range.Start.AddSeconds(1), request.Range.End);
        Assert.Empty(request.Context);
        var image = Assert.Single(request.Images);
        Assert.Equal("synthetic-frame", image.FrameId);
        Assert.Equal(request.Range.Start, image.CapturedAt);
        Assert.Equal(631, image.JpegBytes.Length);
        Assert.Equal(0xff, image.JpegBytes.Span[0]);
        Assert.Equal(0xd8, image.JpegBytes.Span[1]);
        Assert.Equal(0xff, image.JpegBytes.Span[^2]);
        Assert.Equal(0xd9, image.JpegBytes.Span[^1]);
    }

    [Fact]
    public async Task CloudAnalysisRequiresValidationOfCurrentRevision()
    {
        var initial = CreateSnapshot(revision: 10, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = new AiProviderConfigurationService(
            store,
            new TestProviderFactory(new TestAnalysisProvider(initial.Profile)),
            settings,
            new FixedTimeProvider(ChangedAt));
        await service.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetCloudAnalysisEnabledAsync(true));
        Assert.False(service.IsCloudAnalysisEnabled);

        _ = await service.TestConnectionAsync();
        await service.SetCloudAnalysisEnabledAsync(true);
        Assert.True(service.IsCloudAnalysisEnabled);

        var changed = await service.SaveAsync(
            initial.Profile.DisplayName,
            initial.Profile.BaseEndpoint.AbsoluteUri,
            "vision-next",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);
        Assert.False(changed.IsValidated);
        Assert.False(service.IsCloudAnalysisEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetCloudAnalysisEnabledAsync(true));
    }

    [Fact]
    public async Task FailedConnectionDoesNotMarkRevisionValidated()
    {
        var initial = CreateSnapshot(revision: 6, validated: false);
        var store = new TestProfileStore(initial);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
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
        using var service = new AiProviderConfigurationService(
            store,
            new TestProviderFactory(provider),
            settings,
            new FixedTimeProvider(ChangedAt));
        await service.InitializeAsync();

        var exception = await Assert.ThrowsAsync<AiProviderException>(
            () => service.TestConnectionAsync());

        Assert.Equal(AiProviderErrorCode.AuthenticationFailed, exception.ErrorCode);
        Assert.Equal(0, store.MarkValidatedCount);
        Assert.Same(initial, service.Current);
        Assert.False(service.Current!.IsValidated);
        Assert.False(service.IsCloudAnalysisEnabled);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task ConfigurationSubscriberFailureDoesNotMaskCommittedSave()
    {
        var store = new TestProfileStore(current: null);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();
        var laterSubscriberCalls = 0;
        service.ConfigurationChanged += (_, _) =>
            throw new InvalidOperationException("subscriber failed");
        service.ConfigurationChanged += (_, _) => laterSubscriberCalls++;

        var saved = await service.SaveAsync(
            "Local provider",
            "http://localhost:11434/v1",
            "local-vision",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);

        Assert.Same(saved, service.Current);
        Assert.Same(saved, store.Current);
        Assert.Equal(1, laterSubscriberCalls);
    }

    [Fact]
    public async Task RemoteProfileCannotBeSavedWithoutCredential()
    {
        var store = new TestProfileStore(current: null);
        var repository = new TestSettingsRepository(cloudAnalysisEnabled: false);
        using var settings = new AppSettingsService(repository);
        await settings.InitializeAsync();
        using var service = CreateService(store, settings);
        await service.InitializeAsync();

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            service.SaveAsync(
                "Remote provider",
                "https://api.example.com/v1",
                "vision",
                requestTimeoutSeconds: 30,
                replacementApiKey: null));

        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, exception.ErrorCode);
        Assert.Equal(0, store.SaveCount);
    }

    private static AiProviderConfigurationService CreateService(
        TestProfileStore store,
        AppSettingsService settings)
    {
        var profile = store.Current?.Profile ?? CreateProfile();
        return new AiProviderConfigurationService(
            store,
            new TestProviderFactory(new TestAnalysisProvider(profile)),
            settings,
            new FixedTimeProvider(ChangedAt));
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
            validated ? ChangedAt.AddMinutes(-5) : null);
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
        public AiProviderProfileSnapshot? Current { get; private set; } = current;

        public Action? BeforeSave { get; set; }

        public Exception? SaveException { get; init; }

        public int SaveCount { get; private set; }

        public int MarkValidatedCount { get; private set; }

        public DateTimeOffset? LastChangedAtUtc { get; private set; }

        public AiProviderCredentialUpdate? LastCredentialUpdate { get; private set; }

        public Task<AiProviderProfileSnapshot?> GetActiveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task<AiProviderProfileSnapshot> SaveActiveAsync(
            AiProviderProfile profile,
            long? expectedRevision,
            AiProviderCredentialUpdate credentialUpdate,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            BeforeSave?.Invoke();
            if (SaveException is not null)
            {
                return Task.FromException<AiProviderProfileSnapshot>(SaveException);
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
            LastChangedAtUtc = changedAtUtc;
            LastCredentialUpdate = credentialUpdate;
            return Task.FromResult(Current);
        }

        public Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
            Guid profileId,
            long expectedRevision,
            DateTimeOffset validatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkValidatedCount++;
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
        public int CreateCount { get; private set; }

        public AiProviderProfileSnapshot? LastSnapshot { get; private set; }

        public Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            LastSnapshot = snapshot;
            return Task.FromResult<IAiAnalysisProvider>(provider);
        }
    }

    private sealed class TestAnalysisProvider(AiProviderProfile profile)
        : IAiAnalysisProvider, IDisposable
    {
        public AiProviderProfile Profile { get; } = profile;

        public AiProviderCapabilities Capabilities =>
            AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;

        public Exception? Failure { get; init; }

        public List<AiAnalysisRequest> Requests { get; } = [];

        public bool Disposed { get; private set; }

        public Task<AiAnalysisResponse> AnalyzeAsync(
            AiAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Failure is null
                ? Task.FromResult(new AiAnalysisResponse(
                    "synthetic-request",
                    Profile.Model,
                    AiAnalysisContract.CurrentSchemaVersion,
                    activities: []))
                : Task.FromException<AiAnalysisResponse>(Failure);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        public TestSettingsRepository(bool cloudAnalysisEnabled)
        {
            Current = new AppSettings(
                AppThemePreference.System,
                CaptureEnabled: false,
                cloudAnalysisEnabled,
                RecordingConsent: null);
        }

        public AppSettings Current { get; private set; }

        public List<AppSettings> SavedSettings { get; } = [];

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
            SavedSettings.Add(proposed);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

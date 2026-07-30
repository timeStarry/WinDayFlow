using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Infrastructure.Ai;
using WinDayFlow.Infrastructure.Persistence;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Analysis;

public sealed class AnalysisPipelineEndToEndTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task ProfileCreationDoesNotChooseAStageForTheUser()
    {
        using var database = new TemporaryDatabase();
        var (routing, _, _) = await CreateRoutingAsync(database);

        var profile = await routing.CreateProfileAsync(
            "Local compatible provider",
            "http://127.0.0.1:11434/v1",
            "vision-model",
            requestTimeoutSeconds: 30,
            apiKey: null);
        var bindings = await routing.ListBindingsAsync();

        Assert.NotEqual(Guid.Empty, profile.Profile.Id);
        Assert.All(bindings, binding =>
        {
            Assert.False(binding.Enabled);
            Assert.Null(binding.ProviderProfileId);
        });
    }

    [Fact]
    public async Task OneProfileCanBeValidatedAndBoundIndependentlyToBothStages()
    {
        using var database = new TemporaryDatabase();
        var (routing, _, providerFactory) = await CreateRoutingAsync(database);
        var profile = await routing.CreateProfileAsync(
            "Local compatible provider",
            "http://127.0.0.1:11434/v1",
            "vision-model",
            requestTimeoutSeconds: 30,
            apiKey: null);
        var initial = await routing.ListBindingsAsync();

        var privacy = await routing.SaveBindingAsync(
            AnalysisStage.PrivacyInspection,
            enabled: true,
            profile.Profile.Id,
            initial.Single(binding => binding.Stage == AnalysisStage.PrivacyInspection)
                .RouteRevision,
            new PrivacyStageOptions(
                PrivacyMatchAction.RedactAndContinue,
                PrivacyFailureAction.Hold));
        var timeline = await routing.SaveBindingAsync(
            AnalysisStage.TimelineAnalysis,
            enabled: true,
            profile.Profile.Id,
            initial.Single(binding => binding.Stage == AnalysisStage.TimelineAnalysis)
                .RouteRevision,
            privacyOptions: null);

        Assert.True(privacy.Enabled);
        Assert.True(timeline.Enabled);
        Assert.Equal(profile.Profile.Id, privacy.ProviderProfileId);
        Assert.Equal(profile.Profile.Id, timeline.ProviderProfileId);
        Assert.NotNull(await routing.GetStageValidationAsync(
            profile.Profile.Id,
            profile.Revision,
            AnalysisStage.PrivacyInspection));
        Assert.NotNull(await routing.GetStageValidationAsync(
            profile.Profile.Id,
            profile.Revision,
            AnalysisStage.TimelineAnalysis));
        Assert.Equal(1, providerFactory.Provider.PrivacyCalls);
        Assert.Equal(1, providerFactory.Provider.TimelineCalls);

        var error = await Assert.ThrowsAsync<AiProviderProfileInUseException>(
            () => routing.DeleteProfileAsync(profile.Profile.Id, profile.Revision));
        Assert.Equal(
            [AnalysisStage.PrivacyInspection, AnalysisStage.TimelineAnalysis],
            error.Stages);
    }

    [Fact]
    public async Task FailedStageValidationDoesNotEnableTheBinding()
    {
        using var database = new TemporaryDatabase();
        var (routing, _, providerFactory) = await CreateRoutingAsync(database);
        providerFactory.Provider.TimelineFailure = new AiProviderException(
            AiProviderErrorCode.InvalidResponse,
            "invalid structured response",
            Guid.NewGuid(),
            isRetryable: false);
        var profile = await routing.CreateProfileAsync(
            "Local compatible provider",
            "http://127.0.0.1:11434/v1",
            "vision-model",
            requestTimeoutSeconds: 30,
            apiKey: null);
        var initial = (await routing.ListBindingsAsync())
            .Single(binding => binding.Stage == AnalysisStage.TimelineAnalysis);

        await Assert.ThrowsAsync<AiProviderException>(() =>
            routing.SaveBindingAsync(
                AnalysisStage.TimelineAnalysis,
                enabled: true,
                profile.Profile.Id,
                initial.RouteRevision,
                privacyOptions: null));

        var persisted = (await routing.ListBindingsAsync())
            .Single(binding => binding.Stage == AnalysisStage.TimelineAnalysis);
        Assert.False(persisted.Enabled);
        Assert.Null(persisted.ProviderProfileId);
        Assert.Equal(initial.RouteRevision, persisted.RouteRevision);
    }

    [Fact]
    public async Task StageBindingUsesExpectedRouteRevision()
    {
        using var database = new TemporaryDatabase();
        var (routing, _, _) = await CreateRoutingAsync(database);
        var profile = await routing.CreateProfileAsync(
            "Local compatible provider",
            "http://127.0.0.1:11434/v1",
            "vision-model",
            requestTimeoutSeconds: 30,
            apiKey: null);
        var initial = (await routing.ListBindingsAsync())
            .Single(binding => binding.Stage == AnalysisStage.TimelineAnalysis);
        _ = await routing.SaveBindingAsync(
            AnalysisStage.TimelineAnalysis,
            enabled: true,
            profile.Profile.Id,
            initial.RouteRevision,
            privacyOptions: null);

        await Assert.ThrowsAsync<AnalysisStageBindingConflictException>(() =>
            routing.SaveBindingAsync(
                AnalysisStage.TimelineAnalysis,
                enabled: false,
                providerProfileId: null,
                initial.RouteRevision,
                privacyOptions: null));
    }

    [Fact]
    public async Task ProfileUpdateRequiresExpectedRevisionAndInvalidatesStageValidation()
    {
        using var database = new TemporaryDatabase();
        var (routing, _, _) = await CreateRoutingAsync(database);
        var profile = await routing.CreateProfileAsync(
            "Local compatible provider",
            "http://127.0.0.1:11434/v1",
            "vision-model",
            requestTimeoutSeconds: 30,
            apiKey: null);
        _ = await routing.ValidateStageAsync(
            profile.Profile.Id,
            AnalysisStage.TimelineAnalysis);

        await Assert.ThrowsAsync<AiProviderConfigurationConflictException>(() =>
            routing.UpdateProfileAsync(
                profile.Profile.Id,
                expectedRevision: profile.Revision + 1,
                profile.Profile.DisplayName,
                profile.Profile.BaseEndpoint.ToString(),
                profile.Profile.Model,
                requestTimeoutSeconds: 31,
                replacementApiKey: null,
                clearApiKey: false));

        var updated = await routing.UpdateProfileAsync(
            profile.Profile.Id,
            expectedRevision: profile.Revision,
            profile.Profile.DisplayName,
            profile.Profile.BaseEndpoint.ToString(),
            profile.Profile.Model,
            requestTimeoutSeconds: 31,
            replacementApiKey: null,
            clearApiKey: false);
        Assert.Equal(profile.Revision + 1, updated.Revision);
        Assert.Null(await routing.GetStageValidationAsync(
            profile.Profile.Id,
            updated.Revision,
            AnalysisStage.TimelineAnalysis));
    }

    private static async Task<(
        AiProviderRoutingService Routing,
        SqliteConnectionFactory ConnectionFactory,
        TestProviderFactory ProviderFactory)> CreateRoutingAsync(
        TemporaryDatabase database)
    {
        var connectionFactory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(
            connectionFactory,
            new FixedTimeProvider(Now)).InitializeAsync();
        var providerFactory = new TestProviderFactory();
        var routing = new AiProviderRoutingService(
            new SqliteAiProviderProfileStore(connectionFactory),
            new SqliteAnalysisStageBindingStore(connectionFactory),
            providerFactory,
            new FixedTimeProvider(Now));
        return (routing, connectionFactory, providerFactory);
    }

    private sealed class TestProviderFactory : IAiAnalysisProviderFactory
    {
        public TestProvider Provider { get; } = new();

        public Task<IAiAnalysisProvider> CreateAsync(
            AiProviderProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Provider.ProfileValue = snapshot.Profile;
            return Task.FromResult<IAiAnalysisProvider>(Provider);
        }
    }

    private sealed class TestProvider : IAiAnalysisProvider, IPrivacyInspectionProvider
    {
        public AiProviderProfile Profile => ProfileValue
            ?? throw new InvalidOperationException("Provider has not been created.");

        public AiProviderProfile? ProfileValue { get; set; }

        public AiProviderCapabilities Capabilities =>
            AiProviderCapabilities.VisionAnalysis
            | AiProviderCapabilities.StructuredOutput;

        public int TimelineCalls { get; private set; }

        public int PrivacyCalls { get; private set; }

        public Exception? TimelineFailure { get; set; }

        public Task<AiAnalysisResponse> AnalyzeAsync(
            AiAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimelineCalls++;
            if (TimelineFailure is not null)
            {
                throw TimelineFailure;
            }
            return Task.FromResult(new AiAnalysisResponse(
                providerRequestId: "timeline-validation",
                Profile.Model,
                AiAnalysisContract.CurrentSchemaVersion,
                activities: []));
        }

        public Task<PrivacyInspectionResponse> InspectPrivacyAsync(
            PrivacyInspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrivacyCalls++;
            return Task.FromResult(new PrivacyInspectionResponse(
                new PrivacyScreeningResult(
                    PrivacyScreeningResult.CurrentSchemaVersion,
                    PrivacyScreeningVerdict.Clear,
                    Findings: []),
                TokenUsage: null,
                ProviderRequestId: "privacy-validation"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "windayflow.db");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

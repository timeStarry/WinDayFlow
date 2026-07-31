using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Ai;

public sealed class AiProviderRoutingService
{
    private const string ValidationChunkId = "provider-validation";
    private const string ValidationFingerprint =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly byte[] ValidationJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQ"
        + "DQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQU"
        + "FBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEB"
        + "AQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKB"
        + "kaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1"
        + "dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl"
        + "5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcF"
        + "BAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5"
        + "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0"
        + "tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD9U6KKKAP/"
        + "2Q==");

    private readonly IAiProviderProfileStore _profileStore;
    private readonly IAnalysisStageBindingStore _bindingStore;
    private readonly IAiAnalysisProviderFactory _providerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AnalysisProviderSendGate _sendGate;
    private readonly IAnalysisPipelineScheduler? _scheduler;

    public AiProviderRoutingService(
        IAiProviderProfileStore profileStore,
        IAnalysisStageBindingStore bindingStore,
        IAiAnalysisProviderFactory providerFactory,
        TimeProvider? timeProvider = null,
        AnalysisProviderSendGate? sendGate = null,
        IAnalysisPipelineScheduler? scheduler = null)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sendGate = sendGate ?? new AnalysisProviderSendGate();
        _scheduler = scheduler;
    }

    public Task<IReadOnlyList<AiProviderProfileSnapshot>> ListProfilesAsync(
        CancellationToken cancellationToken = default) =>
        _profileStore.ListAsync(cancellationToken);

    public Task<IReadOnlyList<AnalysisStageBinding>> ListBindingsAsync(
        CancellationToken cancellationToken = default) =>
        _bindingStore.ListAsync(cancellationToken);

    public Task<ProviderStageValidation?> GetStageValidationAsync(
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        CancellationToken cancellationToken = default) =>
        _bindingStore.GetValidationAsync(
            profileId,
            profileRevision,
            stage,
            cancellationToken);

    public async Task<AiProviderProfileSnapshot> CreateProfileAsync(
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        string? apiKey,
        int maximumConcurrency = 1,
        CancellationToken cancellationToken = default)
    {
        var profile = CreateProfile(
            Guid.NewGuid(),
            displayName,
            baseEndpoint,
            model,
            requestTimeoutSeconds,
            maximumConcurrency);
        var credential = string.IsNullOrEmpty(apiKey)
            ? AiProviderCredentialUpdate.Clear
            : AiProviderCredentialUpdate.Replace(apiKey);
        EnsureComplete(profile, credential.Kind == AiProviderCredentialUpdateKind.Replace);
        return await _profileStore.CreateAsync(
                profile,
                credential,
                UtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AiProviderProfileSnapshot> UpdateProfileAsync(
        Guid profileId,
        long expectedRevision,
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        string? replacementApiKey,
        bool clearApiKey,
        int maximumConcurrency = 1,
        CancellationToken cancellationToken = default)
    {
        if (clearApiKey && !string.IsNullOrEmpty(replacementApiKey))
        {
            throw new ArgumentException(
                "An API key cannot be replaced and cleared in one update.",
                nameof(replacementApiKey));
        }

        var current = await _profileStore.GetAsync(profileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AiProviderConfigurationConflictException();
        if (current.Revision != expectedRevision)
        {
            throw new AiProviderConfigurationConflictException();
        }

        var profile = CreateProfile(
            profileId,
            displayName,
            baseEndpoint,
            model,
            requestTimeoutSeconds,
            maximumConcurrency);
        var credential = clearApiKey
            ? AiProviderCredentialUpdate.Clear
            : string.IsNullOrEmpty(replacementApiKey)
                ? AiProviderCredentialUpdate.Preserve
                : AiProviderCredentialUpdate.Replace(replacementApiKey);
        var hasApiKey = credential.Kind switch
        {
            AiProviderCredentialUpdateKind.Replace => true,
            AiProviderCredentialUpdateKind.Clear => false,
            _ => current.HasApiKey,
        };
        EnsureComplete(profile, hasApiKey);
        var preservedValidations = credential.Kind == AiProviderCredentialUpdateKind.Preserve
            && HasEquivalentValidatedConfiguration(current.Profile, profile)
                ? await GetExistingValidationsAsync(current, cancellationToken)
                    .ConfigureAwait(false)
                : [];
        AiProviderProfileSnapshot updated;
        using (await _sendGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            updated = await _profileStore.UpdateAsync(
                    profile,
                    expectedRevision,
                    credential,
                    UtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var validation in preservedValidations)
            {
                _ = await _bindingStore.MarkValidatedAsync(
                        updated.Profile.Id,
                        updated.Revision,
                        validation.Stage,
                        validation.ValidatedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        _scheduler?.RequestRun();
        return updated;
    }

    public async Task DeleteProfileAsync(
        Guid profileId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var bindings = await _bindingStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var stages = bindings
            .Where(binding => binding.ProviderProfileId == profileId)
            .Select(static binding => binding.Stage)
            .ToArray();
        if (stages.Length != 0)
        {
            throw new AiProviderProfileInUseException(profileId, stages);
        }

        try
        {
            using (await _sendGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                await _profileStore.DeleteAsync(profileId, expectedRevision, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (AiProviderProfileInUseException)
        {
            bindings = await _bindingStore.ListAsync(cancellationToken).ConfigureAwait(false);
            stages = bindings
                .Where(binding => binding.ProviderProfileId == profileId)
                .Select(static binding => binding.Stage)
                .ToArray();
            throw new AiProviderProfileInUseException(profileId, stages);
        }
        _scheduler?.RequestRun();
    }

    public async Task<ProviderStageValidation> ValidateStageAsync(
        Guid profileId,
        AnalysisStage stage,
        CancellationToken cancellationToken = default)
    {
        var profile = await _profileStore.GetAsync(profileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AiProviderConfigurationConflictException();
        if (!profile.IsComplete)
        {
            throw new AiProviderException(
                AiProviderErrorCode.InvalidConfiguration,
                "The provider profile is incomplete.",
                Guid.Empty,
                isRetryable: false);
        }

        var provider = await _providerFactory.CreateAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var capturedAt = UtcNow();
            var image = new AiEvidenceImage("validation-frame", capturedAt, ValidationJpeg);
            if (stage == AnalysisStage.PrivacyInspection)
            {
                if (provider is not IPrivacyInspectionProvider privacyProvider)
                {
                    throw new AiProviderException(
                        AiProviderErrorCode.UnsupportedCapability,
                        "The provider does not support privacy inspection.",
                        Guid.Empty,
                        isRetryable: false);
                }

                _ = await privacyProvider.InspectPrivacyAsync(
                        new PrivacyInspectionRequest(
                            Guid.NewGuid(),
                            ValidationChunkId,
                            ValidationFingerprint,
                            [image]),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (stage == AnalysisStage.TimelineAnalysis)
            {
                var required = AiProviderCapabilities.VisionAnalysis
                    | AiProviderCapabilities.StructuredOutput;
                if ((provider.Capabilities & required) != required)
                {
                    throw new AiProviderException(
                        AiProviderErrorCode.UnsupportedCapability,
                        "The provider does not support structured vision analysis.",
                        Guid.Empty,
                        isRetryable: false);
                }

                var range = new TimeRange(capturedAt, capturedAt.AddSeconds(1));
                _ = await provider.AnalyzeAsync(
                        new AiAnalysisRequest(
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            attempt: 1,
                            ValidationChunkId,
                            "synthetic/provider-validation.jpg",
                            range,
                            "provider-validation-v1",
                            AiAnalysisContract.CurrentSchemaVersion,
                            "zh-CN",
                            [image],
                            context: []),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(stage));
            }
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }

        return await _bindingStore.MarkValidatedAsync(
                profile.Profile.Id,
                profile.Revision,
                stage,
                UtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AnalysisStageBinding> SaveBindingAsync(
        AnalysisStage stage,
        bool enabled,
        Guid? providerProfileId,
        long expectedRouteRevision,
        PrivacyStageOptions? privacyOptions,
        CancellationToken cancellationToken = default)
    {
        if (enabled && providerProfileId.HasValue)
        {
            var profile = await _profileStore.GetAsync(
                    providerProfileId.Value,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new AiProviderConfigurationConflictException();
            var validation = await _bindingStore.GetValidationAsync(
                    profile.Profile.Id,
                    profile.Revision,
                    stage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validation is null)
            {
                _ = await ValidateStageAsync(profile.Profile.Id, stage, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        AnalysisStageBinding saved;
        using (await _sendGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            saved = await _bindingStore.SaveAsync(
                    stage,
                    enabled,
                    providerProfileId,
                    expectedRouteRevision,
                    privacyOptions,
                    UtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        _scheduler?.RequestRun();
        return saved;
    }

    private static AiProviderProfile CreateProfile(
        Guid id,
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        int maximumConcurrency)
    {
        if (!Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The provider endpoint is invalid.", nameof(baseEndpoint));
        }

        return new AiProviderProfile(
            id,
            displayName,
            AiProviderKind.OpenAiCompatible,
            endpoint,
            model,
            TimeSpan.FromSeconds(requestTimeoutSeconds),
            maximumConcurrency);
    }

    private static void EnsureComplete(AiProviderProfile profile, bool hasApiKey)
    {
        if (!profile.IsLoopback && !hasApiKey)
        {
            throw new AiProviderException(
                AiProviderErrorCode.InvalidConfiguration,
                "A remote provider requires an API key.",
                Guid.Empty,
                isRetryable: false);
        }
    }

    private async Task<IReadOnlyList<ProviderStageValidation>> GetExistingValidationsAsync(
        AiProviderProfileSnapshot profile,
        CancellationToken cancellationToken)
    {
        var validations = new List<ProviderStageValidation>(2);
        foreach (var stage in new[]
                 {
                     AnalysisStage.PrivacyInspection,
                     AnalysisStage.TimelineAnalysis,
                 })
        {
            var validation = await _bindingStore.GetValidationAsync(
                    profile.Profile.Id,
                    profile.Revision,
                    stage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validation is not null)
            {
                validations.Add(validation);
            }
        }

        return validations;
    }

    private static bool HasEquivalentValidatedConfiguration(
        AiProviderProfile current,
        AiProviderProfile proposed) =>
        current.Id == proposed.Id
        && current.Kind == proposed.Kind
        && current.BaseEndpoint == proposed.BaseEndpoint
        && string.Equals(current.Model, proposed.Model, StringComparison.Ordinal)
        && current.RequestTimeout == proposed.RequestTimeout;

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}

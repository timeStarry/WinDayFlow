using System.Diagnostics;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Ai;

public sealed class AiProviderConfigurationService : IDisposable
{
    private const string ConnectionTestChunkId = "connection-test";
    private const string ConnectionTestPromptVersion = "connection-test-v1";
    private const string ConnectionTestArtifactPath = "synthetic/connection-test.jpg";

    private static readonly byte[] ConnectionTestJpeg = Convert.FromBase64String(
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

    private readonly IAiProviderProfileStore _store;
    private readonly IAiAnalysisProviderFactory _providerFactory;
    private readonly AppSettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private int _disposed;

    public AiProviderConfigurationService(
        IAiProviderProfileStore store,
        IAiAnalysisProviderFactory providerFactory,
        AppSettingsService settings,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerFactory = providerFactory
            ?? throw new ArgumentNullException(nameof(providerFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AiProviderProfileSnapshot? Current { get; private set; }

    public bool IsCloudAnalysisEnabled => _settings.Current.CloudAnalysisEnabled;

    public event EventHandler<AiProviderConfigurationChangedEventArgs>?
        ConfigurationChanged;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AiProviderProfileSnapshot? previous = null;
        AiProviderProfileSnapshot? current = null;
        var changed = false;
        try
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            previous = Current;
            current = await _store
                .GetActiveAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_settings.Current.CloudAnalysisEnabled
                && (current is null || !current.IsComplete || !current.IsValidated))
            {
                await _settings
                    .SetCloudAnalysisEnabledAsync(false, cancellationToken)
                    .ConfigureAwait(false);
            }

            Current = current;
            _initialized = true;
            changed = previous != current;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            OnConfigurationChanged(previous, current);
        }
    }

    public async Task<AiProviderProfileSnapshot> SaveAsync(
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        string? replacementApiKey,
        bool clearApiKey = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException(
                "The AI provider endpoint is invalid.",
                nameof(baseEndpoint));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AiProviderProfileSnapshot? previous;
        AiProviderProfileSnapshot saved;
        try
        {
            ThrowIfReadyForUse();
            previous = Current;
            var profile = new AiProviderProfile(
                previous?.Profile.Id ?? Guid.NewGuid(),
                displayName,
                AiProviderKind.OpenAiCompatible,
                endpoint,
                model,
                TimeSpan.FromSeconds(requestTimeoutSeconds));
            var credentialUpdate = CreateCredentialUpdate(
                replacementApiKey,
                clearApiKey);
            var willHaveApiKey = credentialUpdate.Kind switch
            {
                AiProviderCredentialUpdateKind.Preserve => previous?.HasApiKey == true,
                AiProviderCredentialUpdateKind.Replace => true,
                AiProviderCredentialUpdateKind.Clear => false,
                _ => false,
            };
            if (!profile.IsLoopback && !willHaveApiKey)
            {
                throw new AiProviderException(
                    AiProviderErrorCode.InvalidConfiguration,
                    "A remote AI provider requires an API key.",
                    Guid.Empty,
                    isRetryable: false);
            }

            if (previous is not null
                && previous.Profile == profile
                && credentialUpdate.Kind == AiProviderCredentialUpdateKind.Preserve)
            {
                return previous;
            }

            await DisableCloudAnalysisAsync(cancellationToken).ConfigureAwait(false);
            saved = await _store
                .SaveActiveAsync(
                    profile,
                    previous?.Revision,
                    credentialUpdate,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    cancellationToken)
                .ConfigureAwait(false);
            Current = saved;
        }
        finally
        {
            _gate.Release();
        }

        OnConfigurationChanged(previous, saved);
        return saved;
    }

    public async Task<AiProviderProfileSnapshot> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AiProviderProfileSnapshot previous;
        AiProviderProfileSnapshot validated;
        try
        {
            ThrowIfReadyForUse();
            previous = Current
                ?? throw new InvalidOperationException(
                    "An AI provider must be configured before testing it.");
            if (!previous.IsComplete)
            {
                throw new AiProviderException(
                    AiProviderErrorCode.InvalidConfiguration,
                    "The AI provider configuration is incomplete.",
                    Guid.Empty,
                    isRetryable: false);
            }

            var provider = await _providerFactory
                .CreateAsync(previous, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                _ = await provider
                    .AnalyzeAsync(CreateConnectionTestRequest(), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (provider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            validated = await _store
                .MarkValidatedAsync(
                    previous.Profile.Id,
                    previous.Revision,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new AiProviderConfigurationConflictException();
            Current = validated;
        }
        finally
        {
            _gate.Release();
        }

        OnConfigurationChanged(previous, validated);
        return validated;
    }

    public async Task SetCloudAnalysisEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfReadyForUse();
            if (enabled
                && (Current is null || !Current.IsComplete || !Current.IsValidated))
            {
                throw new InvalidOperationException(
                    "The current AI provider must pass a connection test before cloud analysis can be enabled.");
            }

            await _settings
                .SetCloudAnalysisEnabledAsync(enabled, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ConfigurationChanged = null;
    }

    private static AiProviderCredentialUpdate CreateCredentialUpdate(
        string? replacementApiKey,
        bool clearApiKey)
    {
        if (clearApiKey && !string.IsNullOrEmpty(replacementApiKey))
        {
            throw new ArgumentException(
                "An API key cannot be replaced and cleared in the same operation.",
                nameof(replacementApiKey));
        }

        if (clearApiKey)
        {
            return AiProviderCredentialUpdate.Clear;
        }

        return string.IsNullOrEmpty(replacementApiKey)
            ? AiProviderCredentialUpdate.Preserve
            : AiProviderCredentialUpdate.Replace(replacementApiKey);
    }

    private AiAnalysisRequest CreateConnectionTestRequest()
    {
        var start = _timeProvider.GetUtcNow().ToUniversalTime();
        var range = new TimeRange(start, start.AddSeconds(1));
        return new AiAnalysisRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            attempt: 1,
            ConnectionTestChunkId,
            ConnectionTestArtifactPath,
            range,
            ConnectionTestPromptVersion,
            AiAnalysisContract.CurrentSchemaVersion,
            "zh-CN",
            [new AiEvidenceImage("synthetic-frame", start, ConnectionTestJpeg)],
            context: []);
    }

    private async Task DisableCloudAnalysisAsync(CancellationToken cancellationToken)
    {
        if (_settings.Current.CloudAnalysisEnabled)
        {
            await _settings
                .SetCloudAnalysisEnabledAsync(false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ThrowIfReadyForUse()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "The AI provider configuration service has not been initialized.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void OnConfigurationChanged(
        AiProviderProfileSnapshot? previous,
        AiProviderProfileSnapshot? current)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var handler = ConfigurationChanged;
        if (handler is null)
        {
            return;
        }

        var eventArgs = new AiProviderConfigurationChangedEventArgs(previous, current);
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<AiProviderConfigurationChangedEventArgs>)subscriber)(
                    this,
                    eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"An AI provider configuration subscriber failed: {exception.GetType().Name}");
            }
        }
    }
}

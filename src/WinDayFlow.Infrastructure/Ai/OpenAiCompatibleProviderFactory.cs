using System.Security.Cryptography;
using WinDayFlow.Application.Ai;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class OpenAiCompatibleProviderFactory : IAiAnalysisProviderFactory
{
    private readonly SqliteAiProviderProfileStore _profileStore;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly TimeProvider _timeProvider;

    public OpenAiCompatibleProviderFactory(SqliteAiProviderProfileStore profileStore)
    {
        _profileStore = profileStore
            ?? throw new ArgumentNullException(nameof(profileStore));
        _timeProvider = TimeProvider.System;
    }

    public OpenAiCompatibleProviderFactory(
        SqliteAiProviderProfileStore profileStore,
        Func<HttpMessageHandler> handlerFactory,
        TimeProvider? timeProvider)
    {
        _profileStore = profileStore
            ?? throw new ArgumentNullException(nameof(profileStore));
        _handlerFactory = handlerFactory
            ?? throw new ArgumentNullException(nameof(handlerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IAiAnalysisProvider> CreateAsync(
        AiProviderProfileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        string? apiKey;
        try
        {
            apiKey = await _profileStore
                .ReadApiKeyAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CryptographicException exception)
        {
            throw new AiProviderException(
                AiProviderErrorCode.InvalidConfiguration,
                "The stored AI provider credential could not be decrypted.",
                Guid.Empty,
                isRetryable: false,
                innerException: exception);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var handler = _handlerFactory?.Invoke();
        if (_handlerFactory is not null && handler is null)
        {
            throw new InvalidOperationException(
                "The AI provider HTTP handler factory returned null.");
        }

        try
        {
            return new OpenAiCompatibleProvider(
                snapshot.Profile,
                apiKey,
                handler,
                _timeProvider);
        }
        catch
        {
            handler?.Dispose();
            throw;
        }
    }
}

namespace WinDayFlow.Application.Ai;

public sealed record AiProviderProfileSnapshot
{
    public AiProviderProfileSnapshot(
        AiProviderProfile profile,
        long revision,
        bool hasApiKey,
        long? validatedRevision,
        DateTimeOffset? validatedAtUtc)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        if (validatedRevision.HasValue != validatedAtUtc.HasValue
            || validatedRevision is <= 0
            || validatedRevision > revision)
        {
            throw new ArgumentException(
                "AI provider validation metadata is inconsistent.",
                nameof(validatedRevision));
        }

        Revision = revision;
        HasApiKey = hasApiKey;
        ValidatedRevision = validatedRevision;
        ValidatedAtUtc = validatedAtUtc?.ToUniversalTime();
    }

    public AiProviderProfile Profile { get; }

    public long Revision { get; }

    public bool HasApiKey { get; }

    public long? ValidatedRevision { get; }

    public DateTimeOffset? ValidatedAtUtc { get; }

    public bool IsComplete => Profile.IsLoopback || HasApiKey;

    public bool IsValidated => ValidatedRevision == Revision;
}

public enum AiProviderCredentialUpdateKind
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed class AiProviderCredentialUpdate
{
    private const int MaximumApiKeyLength = 8 * 1024;
    private readonly string? _replacement;

    private AiProviderCredentialUpdate(
        AiProviderCredentialUpdateKind kind,
        string? replacement)
    {
        Kind = kind;
        _replacement = replacement;
    }

    public static AiProviderCredentialUpdate Preserve { get; } = new(
        AiProviderCredentialUpdateKind.Preserve,
        replacement: null);

    public static AiProviderCredentialUpdate Clear { get; } = new(
        AiProviderCredentialUpdateKind.Clear,
        replacement: null);

    public AiProviderCredentialUpdateKind Kind { get; }

    public static AiProviderCredentialUpdate Replace(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (apiKey.Length > MaximumApiKeyLength
            || !string.Equals(apiKey, apiKey.Trim(), StringComparison.Ordinal)
            || apiKey.Any(char.IsWhiteSpace)
            || apiKey.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The AI provider API key is invalid.",
                nameof(apiKey));
        }

        return new AiProviderCredentialUpdate(
            AiProviderCredentialUpdateKind.Replace,
            apiKey);
    }

    public string GetReplacement()
    {
        if (Kind != AiProviderCredentialUpdateKind.Replace
            || _replacement is null)
        {
            throw new InvalidOperationException(
                "This credential update does not contain a replacement value.");
        }

        return _replacement;
    }

    public override string ToString() => Kind.ToString();
}

public interface IAiProviderProfileStore
{
    Task<AiProviderProfileSnapshot?> GetActiveAsync(
        CancellationToken cancellationToken = default);

    Task<AiProviderProfileSnapshot> SaveActiveAsync(
        AiProviderProfile profile,
        long? expectedRevision,
        AiProviderCredentialUpdate credentialUpdate,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AiProviderProfileSnapshot?> MarkValidatedAsync(
        Guid profileId,
        long expectedRevision,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IAiAnalysisProviderFactory
{
    Task<IAiAnalysisProvider> CreateAsync(
        AiProviderProfileSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public sealed class AiProviderConfigurationConflictException : Exception
{
    public AiProviderConfigurationConflictException()
        : base("The AI provider configuration changed during the operation.")
    {
    }
}

public sealed class AiProviderConfigurationChangedEventArgs : EventArgs
{
    public AiProviderConfigurationChangedEventArgs(
        AiProviderProfileSnapshot? previous,
        AiProviderProfileSnapshot? current)
    {
        Previous = previous;
        Current = current;
    }

    public AiProviderProfileSnapshot? Previous { get; }

    public AiProviderProfileSnapshot? Current { get; }
}

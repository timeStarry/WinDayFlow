namespace WinDayFlow.Application.Ai;

[Flags]
public enum AiProviderCapabilities
{
    None = 0,
    VisionAnalysis = 1 << 0,
    StructuredOutput = 1 << 1,
}

public interface IAiAnalysisProvider
{
    AiProviderProfile Profile { get; }

    AiProviderCapabilities Capabilities { get; }

    Task<AiAnalysisResponse> AnalyzeAsync(
        AiAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public enum AiProviderErrorCode
{
    InvalidConfiguration = 0,
    AuthenticationFailed = 1,
    AccessDenied = 2,
    ModelNotFound = 3,
    UnsupportedCapability = 4,
    RequestRejected = 5,
    RequestTooLarge = 6,
    ContentRejected = 7,
    RateLimited = 8,
    NetworkUnavailable = 9,
    Timeout = 10,
    ProviderUnavailable = 11,
    InvalidResponse = 12,
    Unknown = 255,
}

public sealed class AiProviderException : Exception
{
    public AiProviderException(
        AiProviderErrorCode errorCode,
        string message,
        Guid correlationId,
        bool isRetryable,
        TimeSpan? retryAfter = null,
        int? transportStatusCode = null,
        string? providerRequestId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(errorCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(errorCode),
                errorCode,
                "The AI provider error code is not defined.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                retryAfter,
                "The provider retry delay cannot be negative.");
        }

        if (transportStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportStatusCode),
                transportStatusCode,
                "The transport status code must be a valid HTTP status code.");
        }

        ErrorCode = errorCode;
        CorrelationId = correlationId;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
        TransportStatusCode = transportStatusCode;
        ProviderRequestId = NormalizeProviderRequestId(providerRequestId);
    }

    public AiProviderErrorCode ErrorCode { get; }

    public Guid CorrelationId { get; }

    public bool IsRetryable { get; }

    public TimeSpan? RetryAfter { get; }

    public int? TransportStatusCode { get; }

    public string? ProviderRequestId { get; }

    private static string? NormalizeProviderRequestId(string? providerRequestId)
    {
        if (string.IsNullOrWhiteSpace(providerRequestId)
            || providerRequestId.Length > 128
            || providerRequestId.Any(char.IsControl))
        {
            return null;
        }

        return providerRequestId;
    }
}

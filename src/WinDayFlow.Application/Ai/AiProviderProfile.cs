namespace WinDayFlow.Application.Ai;

public enum AiProviderKind
{
    OpenAiCompatible = 0,
}

public sealed record AiProviderProfile
{
    public const int MaximumEndpointLength = 4096;
    public const int MaximumConcurrencyLimit = 16;

    public static readonly TimeSpan MinimumRequestTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromMinutes(10);

    public AiProviderProfile(
        Guid id,
        string displayName,
        AiProviderKind kind,
        Uri baseEndpoint,
        string model,
        TimeSpan requestTimeout,
        int maximumConcurrency = 1)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "An AI provider profile requires a non-empty identifier.",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!string.Equals(displayName, displayName.Trim(), StringComparison.Ordinal)
            || displayName.Length > 80
            || displayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The AI provider display name must be trimmed, contain no control characters, and be no longer than 80 characters.",
                nameof(displayName));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The AI provider kind is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!string.Equals(model, model.Trim(), StringComparison.Ordinal)
            || model.Length > 200
            || model.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The AI model name must be trimmed, contain no control characters, and be no longer than 200 characters.",
                nameof(model));
        }

        if (requestTimeout < MinimumRequestTimeout
            || requestTimeout > MaximumRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                requestTimeout,
                $"The AI request timeout must be between {MinimumRequestTimeout} and {MaximumRequestTimeout}.");
        }

        if (maximumConcurrency is < 1 or > MaximumConcurrencyLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                maximumConcurrency,
                $"The AI provider maximum concurrency must be between 1 and {MaximumConcurrencyLimit}.");
        }

        Id = id;
        DisplayName = displayName;
        Kind = kind;
        BaseEndpoint = ValidateAndNormalizeEndpoint(baseEndpoint);
        Model = model;
        RequestTimeout = requestTimeout;
        MaximumConcurrency = maximumConcurrency;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public AiProviderKind Kind { get; }

    public Uri BaseEndpoint { get; }

    public string Model { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumConcurrency { get; }

    public bool IsLoopback => BaseEndpoint.IsLoopback;

    public Uri ChatCompletionsEndpoint => new(BaseEndpoint, "chat/completions");

    private static Uri ValidateAndNormalizeEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException(
                "The AI provider endpoint must be an absolute HTTP or HTTPS URI.",
                nameof(endpoint));
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The AI provider endpoint must use HTTP or HTTPS.",
                nameof(endpoint));
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "Plain HTTP is permitted only for loopback AI provider endpoints.",
                nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException(
                "The AI provider endpoint cannot contain user information.",
                nameof(endpoint));
        }

        if (endpoint.OriginalString.Contains('?')
            || endpoint.OriginalString.Contains('#')
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The AI provider endpoint cannot contain a query or fragment.",
                nameof(endpoint));
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.EndsWith('/')
                ? endpoint.AbsolutePath
                : endpoint.AbsolutePath + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var normalized = builder.Uri;
        if (normalized.AbsoluteUri.Length > MaximumEndpointLength)
        {
            throw new ArgumentException(
                $"The AI provider endpoint cannot exceed {MaximumEndpointLength} characters.",
                nameof(endpoint));
        }

        return normalized;
    }
}

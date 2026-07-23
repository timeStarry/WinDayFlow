using System.Collections.ObjectModel;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Ai;

public static class AiAnalysisContract
{
    public const string CurrentSchemaVersion = "1";
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumRequestImageBytes = 12 * 1024 * 1024;
    public const int MaximumImages = 32;
    public const int MaximumContextSlices = 2_048;
    public const int MaximumActivities = 32;
}

public sealed class AiEvidenceImage
{
    public const string MediaType = "image/jpeg";

    private readonly byte[] _jpegBytes;

    public AiEvidenceImage(
        string frameId,
        DateTimeOffset capturedAt,
        ReadOnlyMemory<byte> jpegBytes)
    {
        ValidateIdentifier(frameId, nameof(frameId), 128);
        if (jpegBytes.Length < 4 || jpegBytes.Length > AiAnalysisContract.MaximumImageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jpegBytes),
                jpegBytes.Length,
                $"A JPEG evidence image must contain between 4 and {AiAnalysisContract.MaximumImageBytes} bytes.");
        }

        var jpegSpan = jpegBytes.Span;
        if (jpegSpan[0] != 0xff
            || jpegSpan[1] != 0xd8
            || jpegSpan[^2] != 0xff
            || jpegSpan[^1] != 0xd9)
        {
            throw new ArgumentException(
                "The evidence image does not have JPEG start and end markers.",
                nameof(jpegBytes));
        }

        FrameId = frameId;
        CapturedAt = capturedAt;
        _jpegBytes = jpegBytes.ToArray();
    }

    public string FrameId { get; }

    public DateTimeOffset CapturedAt { get; }

    public ReadOnlyMemory<byte> JpegBytes => _jpegBytes;

    internal static void ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The identifier must be trimmed, contain no control characters, and be no longer than {maximumLength} characters.",
                parameterName);
        }
    }
}

public sealed record AiAnalysisContextSlice
{
    public AiAnalysisContextSlice(
        TimeRange range,
        string applicationId,
        string applicationDisplayName)
    {
        ArgumentNullException.ThrowIfNull(range);
        AiEvidenceImage.ValidateIdentifier(applicationId, nameof(applicationId), 256);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDisplayName);
        if (!string.Equals(
                applicationDisplayName,
                applicationDisplayName.Trim(),
                StringComparison.Ordinal)
            || applicationDisplayName.Length > 160
            || applicationDisplayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The application display name must be trimmed, contain no control characters, and be no longer than 160 characters.",
                nameof(applicationDisplayName));
        }

        Range = range;
        ApplicationId = applicationId;
        ApplicationDisplayName = applicationDisplayName;
    }

    public TimeRange Range { get; }

    public string ApplicationId { get; }

    public string ApplicationDisplayName { get; }
}

public sealed class AiAnalysisRequest
{
    public AiAnalysisRequest(
        Guid correlationId,
        Guid jobId,
        int attempt,
        string captureChunkId,
        string artifactPath,
        TimeRange range,
        string promptVersion,
        string schemaVersion,
        string locale,
        IReadOnlyList<AiEvidenceImage> images,
        IReadOnlyList<AiAnalysisContextSlice> context)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An AI analysis request requires a non-empty correlation identifier.",
                nameof(correlationId));
        }

        if (jobId == Guid.Empty)
        {
            throw new ArgumentException(
                "An AI analysis request requires a non-empty job identifier.",
                nameof(jobId));
        }

        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt,
                "An AI analysis attempt must be positive.");
        }

        AiEvidenceImage.ValidateIdentifier(captureChunkId, nameof(captureChunkId), 256);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(range);
        AiEvidenceImage.ValidateIdentifier(promptVersion, nameof(promptVersion), 64);
        AiEvidenceImage.ValidateIdentifier(schemaVersion, nameof(schemaVersion), 32);
        if (!string.Equals(
                schemaVersion,
                AiAnalysisContract.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The AI analysis request schema version is not supported.",
                nameof(schemaVersion));
        }

        AiEvidenceImage.ValidateIdentifier(locale, nameof(locale), 35);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(context);

        if (images.Count is 0 or > AiAnalysisContract.MaximumImages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(images),
                images.Count,
                $"An AI analysis request must contain between 1 and {AiAnalysisContract.MaximumImages} evidence images.");
        }

        var imageCopy = images.ToArray();
        if (imageCopy.Any(static image => image is null))
        {
            throw new ArgumentException(
                "AI analysis evidence cannot contain null images.",
                nameof(images));
        }

        if (imageCopy.Select(static image => image.FrameId).Distinct(StringComparer.Ordinal).Count()
            != imageCopy.Length)
        {
            throw new ArgumentException(
                "AI analysis evidence frame identifiers must be unique.",
                nameof(images));
        }

        var totalImageBytes = imageCopy.Sum(static image => (long)image.JpegBytes.Length);
        if (totalImageBytes > AiAnalysisContract.MaximumRequestImageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(images),
                totalImageBytes,
                $"The total JPEG evidence payload cannot exceed {AiAnalysisContract.MaximumRequestImageBytes} bytes.");
        }

        if (imageCopy.Any(image => !range.Contains(image.CapturedAt)))
        {
            throw new ArgumentException(
                "Every evidence image timestamp must fall within the analyzed range.",
                nameof(images));
        }

        var contextCopy = context.ToArray();
        if (contextCopy.Length > AiAnalysisContract.MaximumContextSlices)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                contextCopy.Length,
                $"An AI analysis request cannot contain more than {AiAnalysisContract.MaximumContextSlices} context slices.");
        }

        if (contextCopy.Any(static slice => slice is null))
        {
            throw new ArgumentException(
                "AI analysis context cannot contain null slices.",
                nameof(context));
        }

        if (contextCopy.Any(slice => slice.Range.Start < range.Start || slice.Range.End > range.End))
        {
            throw new ArgumentException(
                "Every application context slice must fall within the analyzed range.",
                nameof(context));
        }

        var inconsistentApplication = contextCopy
            .GroupBy(static slice => slice.ApplicationId, StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(static slice => slice.ApplicationDisplayName)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any());
        if (inconsistentApplication is not null)
        {
            throw new ArgumentException(
                "An application identifier must have one stable display name within an analysis request.",
                nameof(context));
        }

        foreach (var applicationGroup in contextCopy.GroupBy(
                     static slice => slice.ApplicationId,
                     StringComparer.Ordinal))
        {
            DateTimeOffset? previousEnd = null;
            foreach (var slice in applicationGroup.OrderBy(static slice => slice.Range.Start))
            {
                if (previousEnd > slice.Range.Start)
                {
                    throw new ArgumentException(
                        "Context slices for one application cannot overlap.",
                        nameof(context));
                }

                previousEnd = slice.Range.End;
            }
        }

        CorrelationId = correlationId;
        JobId = jobId;
        Attempt = attempt;
        CaptureChunkId = captureChunkId;
        ArtifactPath = artifactPath;
        Range = range;
        PromptVersion = promptVersion;
        SchemaVersion = schemaVersion;
        Locale = locale;
        Images = Array.AsReadOnly(imageCopy);
        Context = Array.AsReadOnly(contextCopy);
    }

    public Guid CorrelationId { get; }

    public Guid JobId { get; }

    public int Attempt { get; }

    public string CaptureChunkId { get; }

    public string ArtifactPath { get; }

    public TimeRange Range { get; }

    public string PromptVersion { get; }

    public string SchemaVersion { get; }

    public string Locale { get; }

    public ReadOnlyCollection<AiEvidenceImage> Images { get; }

    public ReadOnlyCollection<AiAnalysisContextSlice> Context { get; }
}

public sealed record AiActivityCandidate(
    long StartOffsetMilliseconds,
    long EndOffsetMilliseconds,
    string Title,
    string Summary,
    string Category,
    string Productivity,
    IReadOnlyList<string> ApplicationIds,
    IReadOnlyList<string> Tags,
    double Confidence,
    IReadOnlyList<string> EvidenceFrameIds);

public sealed record AiTokenUsage(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens);

public sealed record AiAnalysisResponse
{
    public AiAnalysisResponse(
        string? providerRequestId,
        string model,
        string schemaVersion,
        IReadOnlyList<AiActivityCandidate> activities,
        AiTokenUsage? tokenUsage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        AiEvidenceImage.ValidateIdentifier(schemaVersion, nameof(schemaVersion), 32);
        ArgumentNullException.ThrowIfNull(activities);
        if (activities.Any(static activity => activity is null))
        {
            throw new ArgumentException(
                "An AI analysis response cannot contain null activity candidates.",
                nameof(activities));
        }

        ProviderRequestId = providerRequestId;
        Model = model;
        SchemaVersion = schemaVersion;
        Activities = Array.AsReadOnly(activities.ToArray());
        TokenUsage = tokenUsage;
    }

    public string? ProviderRequestId { get; }

    public string Model { get; }

    public string SchemaVersion { get; }

    public ReadOnlyCollection<AiActivityCandidate> Activities { get; }

    public AiTokenUsage? TokenUsage { get; }
}

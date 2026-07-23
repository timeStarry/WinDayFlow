using WinDayFlow.Application.Ai;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public sealed class AiAnalysisValidationException : Exception
{
    public AiAnalysisValidationException(string message)
        : base(message)
    {
    }

    public AiAnalysisValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class AiAnalysisResponseValidator
{
    private const int MaximumTitleLength = 160;
    private const int MaximumSummaryLength = 2_000;
    private const int MaximumTags = 12;
    private const int MaximumTagLength = 64;
    private const int MaximumApplications = 16;

    public static IReadOnlyList<Activity> Validate(
        AiAnalysisRequest request,
        AiAnalysisResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (!string.Equals(
                response.SchemaVersion,
                request.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new AiAnalysisValidationException(
                "The AI response schema version does not match the request.");
        }

        if (response.Activities.Count > AiAnalysisContract.MaximumActivities)
        {
            throw new AiAnalysisValidationException(
                "The AI response contains too many activity candidates.");
        }

        ValidateTokenUsage(response.TokenUsage);
        var frameIds = request.Images
            .Select(static image => image.FrameId)
            .ToHashSet(StringComparer.Ordinal);
        var contextByApplication = request.Context
            .GroupBy(static slice => slice.ApplicationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, StringComparer.Ordinal);
        var activities = new List<Activity>(response.Activities.Count);
        long previousEndOffset = 0;

        for (var index = 0; index < response.Activities.Count; index++)
        {
            var candidate = response.Activities[index];
            ValidateCandidate(candidate, index, request, previousEndOffset, frameIds);

            TimeRange range;
            try
            {
                range = new TimeRange(
                    request.Range.Start.AddMilliseconds(candidate.StartOffsetMilliseconds),
                    request.Range.Start.AddMilliseconds(candidate.EndOffsetMilliseconds));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new AiAnalysisValidationException(
                    $"Activity candidate {index} contains an invalid time range.",
                    exception);
            }

            var apps = BuildApplicationUsage(candidate, index, range, contextByApplication);
            activities.Add(new Activity(
                range,
                candidate.Title,
                candidate.Summary,
                MapCategory(candidate.Category),
                MapProductivity(candidate.Productivity),
                apps,
                candidate.Tags,
                candidate.Confidence,
                new EvidenceReference(request.CaptureChunkId, request.ArtifactPath)));
            previousEndOffset = candidate.EndOffsetMilliseconds;
        }

        return activities.AsReadOnly();
    }

    private static void ValidateCandidate(
        AiActivityCandidate candidate,
        int index,
        AiAnalysisRequest request,
        long previousEndOffset,
        HashSet<string> frameIds)
    {
        if (candidate.StartOffsetMilliseconds < 0
            || candidate.EndOffsetMilliseconds <= candidate.StartOffsetMilliseconds
            || candidate.EndOffsetMilliseconds > request.Range.Duration.TotalMilliseconds)
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} falls outside the analyzed range.");
        }

        if (index > 0 && candidate.StartOffsetMilliseconds < previousEndOffset)
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} overlaps a preceding candidate.");
        }

        ValidateText(candidate.Title, MaximumTitleLength, allowEmpty: false, index, "title");
        ValidateText(candidate.Summary, MaximumSummaryLength, allowEmpty: true, index, "summary");
        ValidateText(candidate.Category, 64, allowEmpty: false, index, "category");
        ValidateText(candidate.Productivity, 64, allowEmpty: false, index, "productivity");
        if (candidate.Confidence is < 0 or > 1 || double.IsNaN(candidate.Confidence))
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} has an invalid confidence value.");
        }

        ValidateStringSet(
            candidate.Tags,
            MaximumTags,
            MaximumTagLength,
            allowEmpty: true,
            index,
            "tags");
        ValidateStringSet(
            candidate.ApplicationIds,
            MaximumApplications,
            256,
            allowEmpty: true,
            index,
            "application identifiers");
        ValidateStringSet(
            candidate.EvidenceFrameIds,
            AiAnalysisContract.MaximumImages,
            128,
            allowEmpty: false,
            index,
            "evidence frame identifiers");
        if (candidate.EvidenceFrameIds.Any(frameId => !frameIds.Contains(frameId)))
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} references evidence outside the request.");
        }
    }

    private static List<AppUsage> BuildApplicationUsage(
        AiActivityCandidate candidate,
        int index,
        TimeRange activityRange,
        Dictionary<string, IGrouping<string, AiAnalysisContextSlice>> contextByApplication)
    {
        var apps = new List<AppUsage>(candidate.ApplicationIds.Count);
        foreach (var applicationId in candidate.ApplicationIds)
        {
            if (!contextByApplication.TryGetValue(applicationId, out var contextSlices))
            {
                throw new AiAnalysisValidationException(
                    $"Activity candidate {index} references an application outside the request context.");
            }

            long durationTicks = 0;
            foreach (var slice in contextSlices)
            {
                var start = slice.Range.Start > activityRange.Start
                    ? slice.Range.Start
                    : activityRange.Start;
                var end = slice.Range.End < activityRange.End
                    ? slice.Range.End
                    : activityRange.End;
                if (end > start)
                {
                    durationTicks = checked(durationTicks + (end - start).Ticks);
                }
            }

            if (durationTicks == 0)
            {
                throw new AiAnalysisValidationException(
                    $"Activity candidate {index} references an application with no usage in its range.");
            }

            apps.Add(new AppUsage(
                applicationId,
                contextSlices.First().ApplicationDisplayName,
                TimeSpan.FromTicks(durationTicks)));
        }

        return apps;
    }

    private static void ValidateText(
        string? value,
        int maximumLength,
        bool allowEmpty,
        int index,
        string fieldName)
    {
        if (value is null
            || (!allowEmpty && value.Length == 0)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} has an invalid {fieldName}.");
        }
    }

    private static void ValidateStringSet(
        IReadOnlyList<string>? values,
        int maximumCount,
        int maximumLength,
        bool allowEmpty,
        int index,
        string fieldName)
    {
        if (values is null
            || (!allowEmpty && values.Count == 0)
            || values.Count > maximumCount)
        {
            throw new AiAnalysisValidationException(
                $"Activity candidate {index} has invalid {fieldName}.");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > maximumLength
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Any(char.IsControl)
                || !unique.Add(value))
            {
                throw new AiAnalysisValidationException(
                    $"Activity candidate {index} has invalid {fieldName}.");
            }
        }
    }

    private static void ValidateTokenUsage(AiTokenUsage? tokenUsage)
    {
        if (tokenUsage is null)
        {
            return;
        }

        long expectedTotal;
        try
        {
            expectedTotal = checked(tokenUsage.PromptTokens + tokenUsage.CompletionTokens);
        }
        catch (OverflowException exception)
        {
            throw new AiAnalysisValidationException(
                "The AI response contains invalid token usage metadata.",
                exception);
        }

        if (tokenUsage.PromptTokens < 0
            || tokenUsage.CompletionTokens < 0
            || tokenUsage.TotalTokens != expectedTotal)
        {
            throw new AiAnalysisValidationException(
                "The AI response contains invalid token usage metadata.");
        }
    }

    private static ActivityCategory MapCategory(string category)
    {
        return category switch
        {
            "focused_work" => ActivityCategory.FocusedWork,
            "communication" => ActivityCategory.Communication,
            "meeting" => ActivityCategory.Meeting,
            "planning" => ActivityCategory.Planning,
            "research" => ActivityCategory.Research,
            "administration" => ActivityCategory.Administration,
            "learning" => ActivityCategory.Learning,
            "break" => ActivityCategory.Break,
            "personal" => ActivityCategory.Personal,
            _ => ActivityCategory.Unknown,
        };
    }

    private static ProductivityKind MapProductivity(string productivity)
    {
        return productivity switch
        {
            "focused" => ProductivityKind.Focused,
            "neutral" => ProductivityKind.Neutral,
            "distracting" => ProductivityKind.Distracting,
            "break" => ProductivityKind.Break,
            _ => ProductivityKind.Unknown,
        };
    }
}

using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class AiAnalysisResponseValidatorTests
{
    [Fact]
    public void ValidateMapsAProviderNeutralCandidateToDomainActivity()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var response = CreateResponse(CreateCandidate());

        var activity = Assert.Single(AiAnalysisResponseValidator.Validate(request, response));

        Assert.Equal("Implement provider adapter", activity.Title);
        Assert.Equal(ActivityCategory.FocusedWork, activity.Category);
        Assert.Equal(ProductivityKind.Focused, activity.Productivity);
        Assert.Equal(0.9, activity.Confidence);
        Assert.Equal(request.CaptureChunkId, activity.Evidence.CaptureChunkId);
        Assert.Equal(request.ArtifactPath, activity.Evidence.ArtifactPath);
        var app = Assert.Single(activity.Apps);
        Assert.Equal("editor.exe", app.ApplicationId);
        Assert.Equal(TimeSpan.FromMinutes(1), app.Duration);
    }

    [Fact]
    public void ValidateRejectsUnrecognizedClassificationLabels()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var candidate = CreateCandidate() with
        {
            Category = "provider_specific_category",
            Productivity = "provider_specific_productivity",
        };

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse(candidate)));
    }

    [Fact]
    public void ValidateAcceptsExplicitUnknownClassificationLabels()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var candidate = CreateCandidate() with
        {
            Category = "unknown",
            Productivity = "unknown",
        };

        var activity = Assert.Single(AiAnalysisResponseValidator.Validate(
            request,
            CreateResponse(candidate)));

        Assert.Equal(ActivityCategory.Unknown, activity.Category);
        Assert.Equal(ProductivityKind.Unknown, activity.Productivity);
    }

    [Fact]
    public void ValidateAcceptsUnknownActivityWithoutFrameReferencesForZeroFrameRequest()
    {
        var request = CreateZeroFrameRequest();
        var candidate = CreateCandidate() with
        {
            Category = "unknown",
            Productivity = "unknown",
            EvidenceFrameIds = [],
        };

        var activity = Assert.Single(AiAnalysisResponseValidator.Validate(
            request,
            CreateResponse(candidate)));

        Assert.Equal(ActivityCategory.Unknown, activity.Category);
        Assert.Equal(ProductivityKind.Unknown, activity.Productivity);
        Assert.Equal(request.Range, activity.Range);
        Assert.Equal(request.CaptureChunkId, activity.Evidence.CaptureChunkId);
    }

    [Theory]
    [InlineData("focused_work", "unknown")]
    [InlineData("unknown", "focused")]
    public void ValidateRejectsInferredClassificationForZeroFrameRequest(
        string category,
        string productivity)
    {
        var request = CreateZeroFrameRequest();
        var candidate = CreateCandidate() with
        {
            Category = category,
            Productivity = productivity,
            EvidenceFrameIds = [],
        };

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse(candidate)));
    }

    [Fact]
    public void ValidateAcceptsContiguousCandidatesThatCoverTheEntireRange()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var first = CreateCandidate() with
        {
            EndOffsetMilliseconds = 20_000,
        };
        var second = CreateCandidate() with
        {
            StartOffsetMilliseconds = 20_000,
        };

        var activities = AiAnalysisResponseValidator.Validate(
            request,
            CreateResponse(first, second));

        Assert.Equal(2, activities.Count);
        Assert.Equal(request.Range.Start, activities[0].Range.Start);
        Assert.Equal(activities[0].Range.End, activities[1].Range.Start);
        Assert.Equal(request.Range.End, activities[1].Range.End);
    }

    [Fact]
    public void ValidateRejectsAnEmptyActivityList()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse()));
    }

    [Theory]
    [InlineData(1L, 60_000L, null, null)]
    [InlineData(0L, 59_999L, null, null)]
    [InlineData(0L, 20_000L, 30_000L, 60_000L)]
    public void ValidateRejectsLeadingTrailingAndInternalCoverageGaps(
        long firstStart,
        long firstEnd,
        long? secondStart,
        long? secondEnd)
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        AiActivityCandidate[] candidates = secondStart is null
            ? [CreateCandidate() with
            {
                StartOffsetMilliseconds = firstStart,
                EndOffsetMilliseconds = firstEnd,
            }]
            : new[]
            {
                CreateCandidate() with
                {
                    StartOffsetMilliseconds = firstStart,
                    EndOffsetMilliseconds = firstEnd,
                },
                CreateCandidate() with
                {
                    StartOffsetMilliseconds = secondStart.Value,
                    EndOffsetMilliseconds = secondEnd!.Value,
                },
            };

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse(candidates)));
    }

    [Fact]
    public void ValidateRejectsOverlapsAndUnknownEvidenceReferences()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var first = CreateCandidate() with { EndOffsetMilliseconds = 40_000 };
        var overlapping = CreateCandidate() with
        {
            StartOffsetMilliseconds = 30_000,
            EndOffsetMilliseconds = 60_000,
        };
        var unknownFrame = CreateCandidate() with
        {
            EvidenceFrameIds = ["not-in-request"],
        };

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse(first, overlapping)));
        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, CreateResponse(unknownFrame)));
    }

    [Fact]
    public void ValidateRejectsInconsistentTokenUsage()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var response = new AiAnalysisResponse(
            "request-1",
            "vision-test-model",
            AiAnalysisContract.CurrentSchemaVersion,
            [CreateCandidate()],
            new AiTokenUsage(10, 5, 14));

        Assert.Throws<AiAnalysisValidationException>(() =>
            AiAnalysisResponseValidator.Validate(request, response));
    }

    private static AiAnalysisResponse CreateResponse(params AiActivityCandidate[] candidates)
    {
        return new AiAnalysisResponse(
            "request-1",
            "vision-test-model",
            AiAnalysisContract.CurrentSchemaVersion,
            candidates,
            new AiTokenUsage(10, 5, 15));
    }

    private static AiActivityCandidate CreateCandidate()
    {
        return new AiActivityCandidate(
            StartOffsetMilliseconds: 0,
            EndOffsetMilliseconds: 60_000,
            "Implement provider adapter",
            "Build and test the OpenAI-compatible boundary.",
            "focused_work",
            "focused",
            ["editor.exe"],
            ["coding"],
            Confidence: 0.9,
            ["frame-1"]);
    }

    private static AiAnalysisRequest CreateZeroFrameRequest()
    {
        var populated = Ai.AiAnalysisContractsTests.CreateRequest();
        return new AiAnalysisRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            attempt: 1,
            populated.EvidenceReferences,
            populated.Range,
            populated.PromptVersion,
            populated.SchemaVersion,
            populated.Locale,
            images: [],
            populated.Context,
            existingEntries: []);
    }
}

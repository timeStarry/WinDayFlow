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
    public void ValidateFallsBackToUnknownForUnrecognizedLabels()
    {
        var request = Ai.AiAnalysisContractsTests.CreateRequest();
        var candidate = CreateCandidate() with
        {
            Category = "provider_specific_category",
            Productivity = "provider_specific_productivity",
        };

        var activity = Assert.Single(AiAnalysisResponseValidator.Validate(
            request,
            CreateResponse(candidate)));

        Assert.Equal(ActivityCategory.Unknown, activity.Category);
        Assert.Equal(ProductivityKind.Unknown, activity.Productivity);
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
}

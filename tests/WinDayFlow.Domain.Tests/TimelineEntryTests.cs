using Xunit;

namespace WinDayFlow.Domain.Tests;

public sealed class TimelineEntryTests
{
    private static readonly DateTimeOffset OriginalStart =
        new(2026, 7, 15, 9, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset EditedAt =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void ApplyReanalysisPreservesEveryUserEditedField()
    {
        var original = TimelineEntry.FromActivity(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            CreateActivity("Original title", "Original summary", OriginalStart, "chunk-original"),
            "analysis-v1");
        var editedRange = new TimeRange(OriginalStart.AddMinutes(5), OriginalStart.AddMinutes(50));
        var edited = original.ApplyUserEdit(new TimelineEntryEdit(
            EditedAt,
            editedRange,
            "User title",
            "User summary",
            ActivityCategory.Meeting,
            ProductivityKind.Neutral,
            ["user-tag"]));

        var reanalyzed = edited.ApplyReanalysis(
            CreateActivity(
                "Replacement title",
                "Replacement summary",
                OriginalStart.AddHours(1),
                "chunk-reanalyzed"),
            "analysis-v2");

        Assert.Equal(editedRange, reanalyzed.Range);
        Assert.Equal("User title", reanalyzed.Title);
        Assert.Equal("User summary", reanalyzed.Summary);
        Assert.Equal(ActivityCategory.Meeting, reanalyzed.Category);
        Assert.Equal(ProductivityKind.Neutral, reanalyzed.Productivity);
        Assert.Equal(["user-tag"], reanalyzed.Tags);
        Assert.True(reanalyzed.HasUserEdits);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.RangeEditedAt);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.TitleEditedAt);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.SummaryEditedAt);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.CategoryEditedAt);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.ProductivityEditedAt);
        Assert.Equal(EditedAt, reanalyzed.UserEdits.TagsEditedAt);

        Assert.Equal("analysis-v2", reanalyzed.AnalysisVersion);
        Assert.NotNull(reanalyzed.Evidence);
        Assert.Equal("chunk-reanalyzed", reanalyzed.Evidence!.CaptureChunkId);
        Assert.Equal("reanalyzed.app", Assert.Single(reanalyzed.Apps).ApplicationId);
        Assert.Equal(0.95, reanalyzed.Confidence);
    }

    [Fact]
    public void ApplyReanalysisUpdatesFieldsThatHaveNoUserProvenance()
    {
        var original = TimelineEntry.FromActivity(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            CreateActivity("Original title", "Original summary", OriginalStart, "chunk-original"),
            "analysis-v1");
        var replacement = CreateActivity(
            "Replacement title",
            "Replacement summary",
            OriginalStart.AddHours(1),
            "chunk-reanalyzed");

        var reanalyzed = original.ApplyReanalysis(replacement, "analysis-v2");

        Assert.Equal(replacement.Range, reanalyzed.Range);
        Assert.Equal(replacement.Title, reanalyzed.Title);
        Assert.Equal(replacement.Summary, reanalyzed.Summary);
        Assert.Equal(replacement.Category, reanalyzed.Category);
        Assert.Equal(replacement.Productivity, reanalyzed.Productivity);
        Assert.Equal(replacement.Tags, reanalyzed.Tags);
        Assert.False(reanalyzed.HasUserEdits);
    }

    [Fact]
    public void ApplyUserEditDoesNotMutateGeneratedEntry()
    {
        var original = TimelineEntry.FromActivity(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            CreateActivity("Original title", "Original summary", OriginalStart, "chunk-original"),
            "analysis-v1");

        var edited = original.ApplyUserEdit(new TimelineEntryEdit(EditedAt, title: "User title"));

        Assert.Equal("Original title", original.Title);
        Assert.False(original.HasUserEdits);
        Assert.Equal("User title", edited.Title);
        Assert.True(edited.HasUserEdits);
    }

    [Fact]
    public void CreateManualDoesNotInventAnalysisEvidence()
    {
        var createdAt = EditedAt;
        var range = new TimeRange(OriginalStart, OriginalStart.AddMinutes(45));

        var entry = TimelineEntry.CreateManual(
            Guid.Parse("00000000-0000-0000-0000-000000000004"),
            range,
            "Write release notes",
            "Summarize the completed work.",
            ActivityCategory.Administration,
            ProductivityKind.Focused,
            ["release", "writing"],
            createdAt);

        Assert.Equal(TimelineEntryOrigin.Manual, entry.Origin);
        Assert.Null(entry.Confidence);
        Assert.Null(entry.Evidence);
        Assert.Null(entry.AnalysisVersion);
        Assert.Empty(entry.Apps);
        Assert.True(entry.HasUserEdits);
        Assert.False(entry.HasEvidence);
        Assert.Equal(createdAt, entry.UserEdits.RangeEditedAt);
        Assert.Equal(createdAt, entry.UserEdits.TitleEditedAt);
        Assert.Equal(createdAt, entry.UserEdits.SummaryEditedAt);
        Assert.Equal(createdAt, entry.UserEdits.CategoryEditedAt);
        Assert.Equal(createdAt, entry.UserEdits.ProductivityEditedAt);
        Assert.Equal(createdAt, entry.UserEdits.TagsEditedAt);
    }

    [Fact]
    public void ManualEntryCannotBeSilentlyReanalyzed()
    {
        var entry = TimelineEntry.CreateManual(
            Guid.Parse("00000000-0000-0000-0000-000000000005"),
            new TimeRange(OriginalStart, OriginalStart.AddMinutes(30)),
            "Manual activity",
            string.Empty,
            ActivityCategory.Unknown,
            ProductivityKind.Unknown,
            [],
            EditedAt);

        var exception = Assert.Throws<InvalidOperationException>(
            () => entry.ApplyReanalysis(
                CreateActivity(
                    "Replacement title",
                    "Replacement summary",
                    OriginalStart,
                    "chunk-reanalyzed"),
                "analysis-v2"));

        Assert.Equal("Manual timeline entries cannot be reanalyzed.", exception.Message);
    }

    [Fact]
    public void EntryRejectsRangesThatCrossALocalCalendarDay()
    {
        var start = new DateTimeOffset(2026, 7, 15, 23, 30, 0, TimeSpan.FromHours(8));

        var exception = Assert.Throws<ArgumentException>(() => TimelineEntry.CreateManual(
            Guid.Parse("00000000-0000-0000-0000-000000000006"),
            new TimeRange(start, start.AddHours(1)),
            "Cross midnight",
            string.Empty,
            ActivityCategory.Unknown,
            ProductivityKind.Unknown,
            [],
            EditedAt));

        Assert.Equal("range", exception.ParamName);
    }

    [Fact]
    public void EntryRejectsUnknownOriginAndNegativeRevision()
    {
        var range = new TimeRange(OriginalStart, OriginalStart.AddMinutes(30));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineEntry(
            Guid.Parse("00000000-0000-0000-0000-000000000007"),
            range,
            "Invalid origin",
            string.Empty,
            ActivityCategory.Unknown,
            ProductivityKind.Unknown,
            [],
            [],
            null,
            null,
            null,
            origin: (TimelineEntryOrigin)999));

        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineEntry(
            Guid.Parse("00000000-0000-0000-0000-000000000008"),
            range,
            "Invalid revision",
            string.Empty,
            ActivityCategory.Unknown,
            ProductivityKind.Unknown,
            [],
            [],
            null,
            null,
            null,
            origin: TimelineEntryOrigin.Manual,
            revision: -1));
    }

    private static Activity CreateActivity(
        string title,
        string summary,
        DateTimeOffset start,
        string chunkId)
    {
        var isReplacement = chunkId == "chunk-reanalyzed";

        return new Activity(
            new TimeRange(start, start.AddMinutes(60)),
            title,
            summary,
            isReplacement ? ActivityCategory.Research : ActivityCategory.FocusedWork,
            isReplacement ? ProductivityKind.Distracting : ProductivityKind.Focused,
            [new AppUsage(isReplacement ? "reanalyzed.app" : "original.app", "Editor", TimeSpan.FromMinutes(60))],
            [isReplacement ? "reanalyzed-tag" : "original-tag"],
            isReplacement ? 0.95 : 0.75,
            new EvidenceReference(chunkId, $"evidence/{chunkId}.mp4"));
    }
}

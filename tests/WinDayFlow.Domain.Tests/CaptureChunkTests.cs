using Xunit;

namespace WinDayFlow.Domain.Tests;

public sealed class CaptureChunkTests
{
    [Fact]
    public void PreservesCommittedEvidenceMetadataAndNormalizesAuditTimestamps()
    {
        var start = new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.FromHours(8));
        var chunk = CreateChunk(
            start,
            persistenceGeneration: ulong.MaxValue,
            targetEpoch: ulong.MaxValue - 1);

        Assert.Equal(start, chunk.Range.Start);
        Assert.Equal(ulong.MaxValue, chunk.PersistenceGeneration);
        Assert.Equal(ulong.MaxValue - 1, chunk.TargetEpoch);
        Assert.Equal(TimeSpan.Zero, chunk.CommittedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, chunk.IngestedAtUtc.Offset);
        Assert.Equal(CaptureChunkAvailability.Available, chunk.Availability);
    }

    [Theory]
    [InlineData("../chunks/chunk-safe/capture.mp4")]
    [InlineData("/chunks/chunk-safe/capture.mp4")]
    [InlineData("chunks\\chunk-safe\\capture.mp4")]
    [InlineData("C:chunks/chunk-safe/capture.mp4")]
    [InlineData("chunks/./capture.mp4")]
    [InlineData("chunks/CON/capture.mp4")]
    [InlineData("chunks/chunk-safe/capture.mp4/")]
    public void RejectsUnsafeEvidenceRelativePaths(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EvidenceRelativePath(value));
    }

    [Fact]
    public void RejectsEvidencePathsForAnotherChunk()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new CaptureChunk(
            "chunk-safe",
            new EvidenceRelativePath("chunks/chunk-other/capture.mp4"),
            new EvidenceRelativePath("chunks/chunk-other/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            1,
            1920,
            1080,
            1,
            10,
            1024,
            1,
            1,
            start,
            start));
    }

    [Theory]
    [InlineData("Chunk-upper")]
    [InlineData("chunk space")]
    [InlineData("chunk/path")]
    [InlineData("")]
    public void RejectsNonCanonicalChunkIdentifiers(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => CaptureChunk.ValidateIdentifier(value));
    }

    private static CaptureChunk CreateChunk(
        DateTimeOffset start,
        ulong persistenceGeneration,
        ulong targetEpoch)
    {
        const string id = "chunk-safe";
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/capture.mp4"),
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            frameCount: 6,
            videoWidth: 1920,
            videoHeight: 1080,
            frameRateNumerator: 1,
            frameRateDenominator: 10,
            videoByteCount: 4096,
            persistenceGeneration,
            targetEpoch,
            committedAtUtc: start.AddMinutes(1).ToOffset(TimeSpan.FromHours(-4)),
            ingestedAtUtc: start.AddMinutes(2));
    }
}

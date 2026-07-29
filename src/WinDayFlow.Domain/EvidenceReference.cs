namespace WinDayFlow.Domain;

public sealed record EvidenceReference
{
    public EvidenceReference(
        string captureChunkId,
        string artifactPath,
        TimeRange? contributionRange = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureChunkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        CaptureChunkId = captureChunkId;
        ArtifactPath = artifactPath;
        ContributionRange = contributionRange;
    }

    public string CaptureChunkId { get; }

    public string ArtifactPath { get; }

    public TimeRange? ContributionRange { get; }
}

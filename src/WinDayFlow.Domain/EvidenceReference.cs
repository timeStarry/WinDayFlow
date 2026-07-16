namespace WinDayFlow.Domain;

public sealed record EvidenceReference
{
    public EvidenceReference(string captureChunkId, string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureChunkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        CaptureChunkId = captureChunkId;
        ArtifactPath = artifactPath;
    }

    public string CaptureChunkId { get; }

    public string ArtifactPath { get; }
}

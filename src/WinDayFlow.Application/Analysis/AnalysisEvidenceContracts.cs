using System.Collections.ObjectModel;
using WinDayFlow.Application.Ai;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public enum AnalysisEvidenceExtractionFailureKind
{
    EvidenceNotFound,
    UnsafeEvidence,
    EvidenceTooLarge,
    EvidenceChanged,
    IoFailure,
    CryptoFailure,
    InvalidEvidence,
    DecoderFailure,
    EvidenceConflict,
    NativeContractFailure,
}

public sealed class AnalysisEvidenceExtractionException : InvalidOperationException
{
    public AnalysisEvidenceExtractionException(
        AnalysisEvidenceExtractionFailureKind failureKind,
        int resultCode)
        : base($"Analysis evidence extraction failed: {failureKind}.")
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        FailureKind = failureKind;
        ResultCode = resultCode;
    }

    public AnalysisEvidenceExtractionFailureKind FailureKind { get; }

    public int ResultCode { get; }
}

public sealed class AnalysisEvidenceBatch
{
    private readonly ReadOnlyCollection<AiEvidenceImage> _images;
    private readonly ReadOnlyCollection<AiAnalysisContextSlice> _context;

    public AnalysisEvidenceBatch(
        string artifactPath,
        CaptureChunkFingerprint sourceFingerprint,
        IReadOnlyList<AiEvidenceImage> images,
        IReadOnlyList<AiAnalysisContextSlice> context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(artifactPath, artifactPath.Trim(), StringComparison.Ordinal)
            || artifactPath.Length > 4_096
            || artifactPath.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The analysis evidence artifact path is invalid.",
                nameof(artifactPath));
        }

        var imageCopy = images.ToArray();
        var contextCopy = context.ToArray();
        if (imageCopy.Any(static image => image is null))
        {
            throw new ArgumentException(
                "Analysis evidence cannot contain null images.",
                nameof(images));
        }

        if (contextCopy.Any(static slice => slice is null))
        {
            throw new ArgumentException(
                "Analysis evidence cannot contain null context slices.",
                nameof(context));
        }

        ArtifactPath = artifactPath;
        SourceFingerprint = sourceFingerprint;
        _images = Array.AsReadOnly(imageCopy);
        _context = Array.AsReadOnly(contextCopy);
    }

    public string ArtifactPath { get; }

    public CaptureChunkFingerprint SourceFingerprint { get; }

    public IReadOnlyList<AiEvidenceImage> Images => _images;

    public IReadOnlyList<AiAnalysisContextSlice> Context => _context;
}

public interface IAnalysisEvidenceExtractor
{
    Task<AnalysisEvidenceBatch> ExtractAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint expectedSourceFingerprint,
        CancellationToken cancellationToken = default);
}

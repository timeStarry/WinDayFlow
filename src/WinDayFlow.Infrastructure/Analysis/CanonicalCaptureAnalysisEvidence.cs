using System.Security.Cryptography;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Capture;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class CanonicalCaptureChunkFingerprintProvider(
    CanonicalCaptureFrameArchive archive) : ICaptureChunkFingerprintProvider
{
    private readonly CanonicalCaptureFrameArchive _archive = archive
        ?? throw new ArgumentNullException(nameof(archive));

    public async Task<CaptureChunkFingerprint> ComputeAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var frames = await _archive.ListFramesAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("WinDayFlow/canonical-frames-v1\0"u8);
        var manifest = await _archive.ReadManifestBytesAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        AppendLength(hash, manifest.Length);
        hash.AppendData(manifest);
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await _archive.ReadFrameBytesAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            AppendLength(hash, bytes.Length);
            hash.AppendData(bytes);
        }

        return new CaptureChunkFingerprint(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendLength(IncrementalHash hash, int length)
    {
        Span<byte> encoded = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            encoded,
            checked((ulong)length));
        hash.AppendData(encoded);
    }
}

public sealed class CanonicalFrameAnalysisEvidenceExtractor(
    ICaptureFrameArchive archive,
    ICaptureChunkFingerprintProvider fingerprintProvider)
    : IAnalysisEvidenceExtractor
{
    private readonly ICaptureFrameArchive _archive = archive
        ?? throw new ArgumentNullException(nameof(archive));
    private readonly ICaptureChunkFingerprintProvider _fingerprintProvider =
        fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));

    public async Task<AnalysisEvidenceBatch> ExtractAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint expectedSourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(expectedSourceFingerprint);
        var frames = await _archive.ListFramesAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        var selected = SelectEvenly(frames, AiAnalysisContract.MaximumImages);
        var images = new List<AiEvidenceImage>(selected.Count);
        var totalBytes = 0;
        foreach (var frame in selected)
        {
            var bytes = await _archive.ReadFrameBytesAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            if (totalBytes + bytes.Length > AiAnalysisContract.MaximumRequestImageBytes)
            {
                continue;
            }
            totalBytes += bytes.Length;
            images.Add(new AiEvidenceImage(
                $"frame-{frame.Index:D6}",
                frame.CapturedAt,
                bytes));
        }

        var current = await _fingerprintProvider.ComputeAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                current.Value,
                expectedSourceFingerprint.Value,
                StringComparison.Ordinal))
        {
            throw new AnalysisEvidenceExtractionException(
                AnalysisEvidenceExtractionFailureKind.EvidenceChanged,
                resultCode: 0);
        }

        IReadOnlyList<AiAnalysisContextSlice> context = chunk.ProcessTelemetry is { } telemetry
            ? [new AiAnalysisContextSlice(
                chunk.Range,
                $"process:{telemetry.ProcessName.ToLowerInvariant()}",
                telemetry.ProcessName)]
            : [];
        return new AnalysisEvidenceBatch(
            chunk.ManifestPath.Value,
            expectedSourceFingerprint,
            images,
            context);
    }

    private static IReadOnlyList<CaptureFrameDescriptor> SelectEvenly(
        IReadOnlyList<CaptureFrameDescriptor> frames,
        int maximum)
    {
        if (frames.Count <= maximum)
        {
            return frames;
        }

        var selected = new CaptureFrameDescriptor[maximum];
        for (var index = 0; index < maximum; index++)
        {
            var source = (int)Math.Round(
                index * (frames.Count - 1d) / (maximum - 1d),
                MidpointRounding.AwayFromZero);
            selected[index] = frames[source];
        }
        return selected;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Domain;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class PrivacyAwareAnalysisEvidenceExtractor : IAnalysisEvidenceExtractor
{
    private const int MaximumManifestBytes = 128 * 1024;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly string _dataRootPrefix;
    private readonly CanonicalFrameAnalysisEvidenceExtractor _original;
    private readonly ICaptureChunkFingerprintProvider _fingerprintProvider;
    private readonly IPrivacyScreeningStore _screeningStore;
    private readonly ICaptureContextStore _contextStore;

    public PrivacyAwareAnalysisEvidenceExtractor(
        string dataRoot,
        CanonicalFrameAnalysisEvidenceExtractor original,
        ICaptureChunkFingerprintProvider fingerprintProvider,
        IPrivacyScreeningStore screeningStore,
        ICaptureContextStore contextStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _dataRootPrefix = _dataRoot + Path.DirectorySeparatorChar;
        _original = original ?? throw new ArgumentNullException(nameof(original));
        _fingerprintProvider = fingerprintProvider
            ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _screeningStore = screeningStore ?? throw new ArgumentNullException(nameof(screeningStore));
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
    }

    public async Task<AnalysisEvidenceBatch> ExtractAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint expectedSourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(expectedSourceFingerprint);
        var originalFingerprint = await _fingerprintProvider
            .ComputeAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        if (originalFingerprint == expectedSourceFingerprint)
        {
            var batch = await _original
                .ExtractAsync(chunk, expectedSourceFingerprint, cancellationToken)
                .ConfigureAwait(false);
            return new AnalysisEvidenceBatch(
                batch.ArtifactPath,
                batch.SourceFingerprint,
                batch.Images,
                await ReadContextAsync(chunk, cancellationToken).ConfigureAwait(false));
        }

        var screening = await _screeningStore.FindByOutputAsync(
                chunk.Id,
                expectedSourceFingerprint.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (screening is not { State: PrivacyScreeningState.Redacted }
            || screening.DerivativeManifestPath is null)
        {
            throw new AnalysisEvidenceExtractionException(
                AnalysisEvidenceExtractionFailureKind.EvidenceConflict,
                resultCode: 0);
        }

        var manifestPath = Resolve(screening.DerivativeManifestPath);
        var manifestBytes = await ReadBoundedAsync(
                manifestPath,
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
        {
            MaxDepth = 8,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || !string.Equals(
                root.GetProperty("screeningId").GetString(),
                screening.Id.ToString("D"),
                StringComparison.Ordinal)
            || !string.Equals(
                root.GetProperty("captureChunkId").GetString(),
                chunk.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                root.GetProperty("sourceFingerprint").GetString(),
                screening.InputFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("mask").GetString(), "opaque-black", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The privacy derivative manifest is inconsistent.");
        }

        var allImages = new List<AiEvidenceImage>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("WinDayFlow/privacy-evidence-v1\0"u8);
        hash.AppendData(manifestBytes);
        foreach (var item in root.GetProperty("frames").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()
                ?? throw new InvalidDataException("A derivative frame has no identifier.");
            var index = item.GetProperty("index").GetUInt32();
            var expectedId = $"frame-{index:D6}";
            var relative = item.GetProperty("path").GetString();
            if (!string.Equals(id, expectedId, StringComparison.Ordinal)
                || !string.Equals(relative, $"frames/{expectedId}.jpg", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A derivative frame path is invalid.");
            }
            var framePath = Resolve(new EvidenceRelativePath(
                $"screenings/{screening.Id:D}/{relative}"));
            var bytes = await ReadBoundedAsync(framePath, MaximumFrameBytes, cancellationToken)
                .ConfigureAwait(false);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.Length != item.GetProperty("byteCount").GetInt32()
                || !string.Equals(
                    sha256,
                    item.GetProperty("sha256").GetString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("A derivative frame failed integrity validation.");
            }
            hash.AppendData(bytes);
            allImages.Add(new AiEvidenceImage(
                id,
                chunk.Range.Start.AddMilliseconds(
                    item.GetProperty("offsetMilliseconds").GetUInt64()),
                bytes));
        }

        var observedFingerprint = new CaptureChunkFingerprint(
            Convert.ToHexString(hash.GetHashAndReset()));
        if (observedFingerprint != expectedSourceFingerprint)
        {
            throw new AnalysisEvidenceExtractionException(
                AnalysisEvidenceExtractionFailureKind.EvidenceChanged,
                resultCode: 0);
        }

        var selected = SelectWithinBudget(allImages);
        return new AnalysisEvidenceBatch(
            screening.DerivativeManifestPath.Value,
            expectedSourceFingerprint,
            selected,
            await ReadContextAsync(chunk, cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<AiAnalysisContextSlice>> ReadContextAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken)
    {
        var samples = await _contextStore.ListAsync(chunk.Id, cancellationToken)
            .ConfigureAwait(false);
        var slices = new List<AiAnalysisContextSlice>();
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.Application is not { } application)
            {
                continue;
            }
            var end = index + 1 < samples.Count
                ? samples[index + 1].SampledAt
                : chunk.Range.End;
            if (end <= sample.SampledAt)
            {
                continue;
            }
            slices.Add(new AiAnalysisContextSlice(
                new TimeRange(sample.SampledAt, end),
                application.ApplicationId,
                application.DisplayName));
        }
        return slices.Take(AiAnalysisContract.MaximumContextSlices).ToArray();
    }

    private static List<AiEvidenceImage> SelectWithinBudget(
        List<AiEvidenceImage> images)
    {
        if (images.Count == 0)
        {
            return [];
        }
        var target = Math.Min(images.Count, AiAnalysisContract.MaximumImages);
        var selected = new List<AiEvidenceImage>(target);
        var bytes = 0L;
        for (var ordinal = 0; ordinal < target; ordinal++)
        {
            var index = target == 1
                ? 0
                : checked((int)((long)ordinal * (images.Count - 1) / (target - 1)));
            var image = images[index];
            if (bytes + image.JpegBytes.Length > AiAnalysisContract.MaximumRequestImageBytes)
            {
                continue;
            }
            selected.Add(image);
            bytes += image.JpegBytes.Length;
        }
        return selected;
    }

    private string Resolve(EvidenceRelativePath relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(
            _dataRoot,
            relativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_dataRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The derivative path escapes the data root.");
        }
        return full;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("The privacy derivative is unavailable.", path);
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != info.Length)
        {
            throw new InvalidDataException("The privacy derivative changed while reading.");
        }
        return bytes;
    }
}

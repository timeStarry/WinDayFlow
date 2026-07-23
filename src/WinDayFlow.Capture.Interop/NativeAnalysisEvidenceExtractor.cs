using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;

namespace WinDayFlow.Capture.Interop;

internal interface INativeAnalysisEvidenceApi
{
    NativeCaptureResult Extract(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        ulong expectedVideoByteCount,
        uint expectedFrameCount,
        uint expectedVideoWidth,
        uint expectedVideoHeight,
        ulong expectedDurationMilliseconds,
        byte[] expectedSourceFingerprintUtf8,
        uint expectedSourceFingerprintUtf8Length,
        byte[] manifestUtf8,
        uint manifestUtf8Capacity,
        out uint manifestUtf8Required);

    NativeCaptureResult ReadFrame(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        byte[] canonicalSourceFingerprintUtf8,
        uint canonicalSourceFingerprintUtf8Length,
        uint frameIndex,
        byte[] frameBytes,
        uint frameBytesCapacity,
        out uint frameBytesRequired);
}

internal sealed class PInvokeNativeAnalysisEvidenceApi : INativeAnalysisEvidenceApi
{
    internal static PInvokeNativeAnalysisEvidenceApi Instance { get; } = new();

    private PInvokeNativeAnalysisEvidenceApi()
    {
    }

    public NativeCaptureResult Extract(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        ulong expectedVideoByteCount,
        uint expectedFrameCount,
        uint expectedVideoWidth,
        uint expectedVideoHeight,
        ulong expectedDurationMilliseconds,
        byte[] expectedSourceFingerprintUtf8,
        uint expectedSourceFingerprintUtf8Length,
        byte[] manifestUtf8,
        uint manifestUtf8Capacity,
        out uint manifestUtf8Required) =>
        NativeCaptureMethods.wdf_capture_extract_analysis_evidence(
            dataRootUtf8,
            dataRootUtf8Length,
            canonicalChunkIdUtf8,
            canonicalChunkIdUtf8Length,
            expectedVideoByteCount,
            expectedFrameCount,
            expectedVideoWidth,
            expectedVideoHeight,
            expectedDurationMilliseconds,
            expectedSourceFingerprintUtf8,
            expectedSourceFingerprintUtf8Length,
            manifestUtf8,
            manifestUtf8Capacity,
            out manifestUtf8Required);

    public NativeCaptureResult ReadFrame(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        byte[] canonicalSourceFingerprintUtf8,
        uint canonicalSourceFingerprintUtf8Length,
        uint frameIndex,
        byte[] frameBytes,
        uint frameBytesCapacity,
        out uint frameBytesRequired) =>
        NativeCaptureMethods.wdf_capture_read_analysis_evidence_frame(
            dataRootUtf8,
            dataRootUtf8Length,
            canonicalChunkIdUtf8,
            canonicalChunkIdUtf8Length,
            canonicalSourceFingerprintUtf8,
            canonicalSourceFingerprintUtf8Length,
            frameIndex,
            frameBytes,
            frameBytesCapacity,
            out frameBytesRequired);
}

public sealed class NativeAnalysisEvidenceExtractor : IAnalysisEvidenceExtractor
{
    internal const int ManifestUtf8Capacity = 64 * 1024 + 1;
    private const int MaximumDataRootUtf8Bytes = 32_767;
    private const uint MaximumSourceFrames = 14_400;
    private const uint MaximumVideoWidth = 7_680;
    private const uint MaximumVideoHeight = 4_320;
    private const ulong MaximumDurationMilliseconds = 3_600_000;
    private const string PolicyVersion = "evidence-v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _dataRootUtf8;
    private readonly INativeAnalysisEvidenceApi _nativeApi;

    public NativeAnalysisEvidenceExtractor(string dataRootPath)
        : this(dataRootPath, PInvokeNativeAnalysisEvidenceApi.Instance)
    {
    }

    internal NativeAnalysisEvidenceExtractor(
        string dataRootPath,
        INativeAnalysisEvidenceApi nativeApi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));

        if (!Path.IsPathFullyQualified(dataRootPath))
        {
            throw new ArgumentException(
                "The capture evidence root must be an absolute local Windows path.",
                nameof(dataRootPath));
        }

        var fullPath = Path.GetFullPath(dataRootPath);
        var root = Path.GetPathRoot(fullPath);
        if (!Path.IsPathFullyQualified(fullPath)
            || string.IsNullOrEmpty(root)
            || root.StartsWith("\\\\", StringComparison.Ordinal)
            || root.Length < 3
            || root[1] != ':'
            || root[2] is not ('\\' or '/'))
        {
            throw new ArgumentException(
                "The capture evidence root must be an absolute local Windows path.",
                nameof(dataRootPath));
        }

        _dataRootUtf8 = StrictUtf8.GetBytes(fullPath);
        if (_dataRootUtf8.Length is 0 or > MaximumDataRootUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataRootPath),
                "The UTF-8 capture evidence root exceeds the native path limit.");
        }
    }

    public Task<AnalysisEvidenceBatch> ExtractAsync(
        CaptureChunk chunk,
        CaptureChunkFingerprint expectedSourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(expectedSourceFingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateChunkBounds(chunk);
        var durationMilliseconds = GetDurationMilliseconds(chunk.Range.Duration);
        var chunkIdUtf8 = StrictUtf8.GetBytes(chunk.Id);
        var fingerprintUtf8 = Encoding.ASCII.GetBytes(expectedSourceFingerprint.Value);
        var manifestBuffer = new byte[ManifestUtf8Capacity];
        var result = _nativeApi.Extract(
            _dataRootUtf8,
            checked((uint)_dataRootUtf8.Length),
            chunkIdUtf8,
            checked((uint)chunkIdUtf8.Length),
            checked((ulong)chunk.VideoByteCount),
            chunk.FrameCount,
            chunk.VideoWidth,
            chunk.VideoHeight,
            durationMilliseconds,
            fingerprintUtf8,
            checked((uint)fingerprintUtf8.Length),
            manifestBuffer,
            checked((uint)manifestBuffer.Length),
            out var manifestRequired);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfNativeFailure(result);

        if (manifestRequired is < 2 or > ManifestUtf8Capacity
            || manifestBuffer[manifestRequired - 1] != 0)
        {
            throw NativeContractFailure(result);
        }

        var manifestJson = StrictUtf8.GetString(
            manifestBuffer,
            0,
            checked((int)manifestRequired - 1));
        var manifest = ParseManifest(
            manifestJson,
            chunk,
            expectedSourceFingerprint,
            durationMilliseconds);
        var images = new List<AiEvidenceImage>(manifest.Frames.Count);
        var totalBytes = 0;
        foreach (var frame in manifest.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameBytes = new byte[frame.ByteCount];
            result = _nativeApi.ReadFrame(
                _dataRootUtf8,
                checked((uint)_dataRootUtf8.Length),
                chunkIdUtf8,
                checked((uint)chunkIdUtf8.Length),
                fingerprintUtf8,
                checked((uint)fingerprintUtf8.Length),
                frame.Index,
                frameBytes,
                checked((uint)frameBytes.Length),
                out var frameRequired);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfNativeFailure(result);
            if (frameRequired != frame.ByteCount
                || frameBytes.Length < 4
                || frameBytes[0] != 0xff
                || frameBytes[1] != 0xd8
                || frameBytes[^2] != 0xff
                || frameBytes[^1] != 0xd9
                || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(frameBytes)),
                    frame.Sha256,
                    StringComparison.Ordinal))
            {
                throw NativeContractFailure(result);
            }

            totalBytes = checked(totalBytes + frameBytes.Length);
            if (totalBytes > AiAnalysisContract.MaximumRequestImageBytes)
            {
                throw NativeContractFailure(result);
            }

            var capturedAt = chunk.Range.Start.AddMilliseconds(frame.OffsetMilliseconds);
            if (capturedAt < chunk.Range.Start || capturedAt >= chunk.Range.End)
            {
                throw NativeContractFailure(result);
            }

            images.Add(new AiEvidenceImage(frame.Id, capturedAt, frameBytes));
        }

        return Task.FromResult(new AnalysisEvidenceBatch(
            manifest.ArtifactPath,
            expectedSourceFingerprint,
            images,
            []));
    }

    private static void ValidateChunkBounds(CaptureChunk chunk)
    {
        if (chunk.FrameCount is 0 or > MaximumSourceFrames
            || chunk.VideoWidth is < 2 or > MaximumVideoWidth
            || chunk.VideoHeight is < 2 or > MaximumVideoHeight
            || (chunk.VideoWidth & 1U) != 0
            || (chunk.VideoHeight & 1U) != 0
            || chunk.VideoByteCount is <= 0 or > CaptureChunk.MaximumVideoByteCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunk),
                "The capture chunk exceeds the native evidence extraction bounds.");
        }
    }

    private static ulong GetDurationMilliseconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero
            || duration.TotalMilliseconds > MaximumDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "The capture chunk duration exceeds the native evidence extraction bounds.");
        }

        return checked((ulong)Math.Ceiling(duration.TotalMilliseconds));
    }

    private static ParsedManifest ParseManifest(
        string json,
        CaptureChunk chunk,
        CaptureChunkFingerprint fingerprint,
        ulong durationMilliseconds)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            RequireExactProperties(
                root,
                "schemaVersion",
                "policyVersion",
                "chunkId",
                "sourceFingerprint",
                "artifactPath",
                "frames");
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(
                    root.GetProperty("policyVersion").GetString(),
                    PolicyVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("chunkId").GetString(),
                    chunk.Id,
                    StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("sourceFingerprint").GetString(),
                    fingerprint.Value,
                    StringComparison.Ordinal))
            {
                throw new JsonException("The evidence manifest identity is invalid.");
            }

            var expectedArtifactPath =
                $"evidence/{PolicyVersion}/{chunk.Id}/{fingerprint.Value}/manifest.json";
            var artifactPath = root.GetProperty("artifactPath").GetString();
            if (!string.Equals(artifactPath, expectedArtifactPath, StringComparison.Ordinal))
            {
                throw new JsonException("The evidence artifact path is invalid.");
            }

            var framesElement = root.GetProperty("frames");
            if (framesElement.ValueKind != JsonValueKind.Array
                || framesElement.GetArrayLength() is < 1 or > AiAnalysisContract.MaximumImages)
            {
                throw new JsonException("The evidence frame count is invalid.");
            }

            var frames = new List<ParsedFrame>(framesElement.GetArrayLength());
            var totalBytes = 0;
            ulong previousOffset = 0;
            foreach (var element in framesElement.EnumerateArray())
            {
                RequireExactProperties(
                    element,
                    "id",
                    "index",
                    "offsetMilliseconds",
                    "byteCount",
                    "sha256");
                var index = element.GetProperty("index").GetUInt32();
                var id = element.GetProperty("id").GetString();
                var offset = element.GetProperty("offsetMilliseconds").GetUInt64();
                var byteCount = element.GetProperty("byteCount").GetInt32();
                var sha256 = element.GetProperty("sha256").GetString();
                if (index != frames.Count
                    || !string.Equals(id, $"frame-{index:D4}", StringComparison.Ordinal)
                    || offset >= durationMilliseconds
                    || (frames.Count != 0 && offset < previousOffset)
                    || byteCount is < 4 or > AiAnalysisContract.MaximumImageBytes
                    || sha256 is null
                    || sha256.Length != CaptureChunkFingerprint.HexLength
                    || sha256.Any(static character =>
                        character is not (>= '0' and <= '9')
                            and not (>= 'A' and <= 'F')))
                {
                    throw new JsonException("An evidence frame record is invalid.");
                }

                totalBytes = checked(totalBytes + byteCount);
                if (totalBytes > AiAnalysisContract.MaximumRequestImageBytes)
                {
                    throw new JsonException("The evidence frame aggregate is too large.");
                }

                frames.Add(new ParsedFrame(id!, index, offset, byteCount, sha256));
                previousOffset = offset;
            }

            return new ParsedManifest(artifactPath!, frames);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException)
        {
            throw new BadImageFormatException(
                "The native analysis evidence manifest is malformed.",
                exception);
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An evidence manifest object was expected.");
        }

        var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw new JsonException("The evidence manifest has unknown or duplicate fields.");
            }
        }

        if (observed.Count != expected.Count)
        {
            throw new JsonException("The evidence manifest is missing required fields.");
        }
    }

    private static void ThrowIfNativeFailure(NativeCaptureResult result)
    {
        if (result == NativeCaptureResult.Ok)
        {
            return;
        }

        throw result switch
        {
            NativeCaptureResult.EvidenceNotFound => Failure(
                AnalysisEvidenceExtractionFailureKind.EvidenceNotFound,
                result),
            NativeCaptureResult.UnsafeEvidence => Failure(
                AnalysisEvidenceExtractionFailureKind.UnsafeEvidence,
                result),
            NativeCaptureResult.EvidenceTooLarge => Failure(
                AnalysisEvidenceExtractionFailureKind.EvidenceTooLarge,
                result),
            NativeCaptureResult.EvidenceChanged => Failure(
                AnalysisEvidenceExtractionFailureKind.EvidenceChanged,
                result),
            NativeCaptureResult.IoFailure => Failure(
                AnalysisEvidenceExtractionFailureKind.IoFailure,
                result),
            NativeCaptureResult.CryptoFailure => Failure(
                AnalysisEvidenceExtractionFailureKind.CryptoFailure,
                result),
            NativeCaptureResult.EvidenceInvalid => Failure(
                AnalysisEvidenceExtractionFailureKind.InvalidEvidence,
                result),
            NativeCaptureResult.DecoderFailure => Failure(
                AnalysisEvidenceExtractionFailureKind.DecoderFailure,
                result),
            NativeCaptureResult.EvidenceConflict => Failure(
                AnalysisEvidenceExtractionFailureKind.EvidenceConflict,
                result),
            _ => NativeContractFailure(result),
        };
    }

    private static AnalysisEvidenceExtractionException Failure(
        AnalysisEvidenceExtractionFailureKind kind,
        NativeCaptureResult result) => new(kind, (int)result);

    private static AnalysisEvidenceExtractionException NativeContractFailure(
        NativeCaptureResult result) =>
        Failure(AnalysisEvidenceExtractionFailureKind.NativeContractFailure, result);

    private sealed record ParsedManifest(
        string ArtifactPath,
        IReadOnlyList<ParsedFrame> Frames);

    private sealed record ParsedFrame(
        string Id,
        uint Index,
        ulong OffsetMilliseconds,
        int ByteCount,
        string Sha256);
}

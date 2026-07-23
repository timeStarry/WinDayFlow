using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeAnalysisEvidenceExtractorTests
{
    private const string Fingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task ExtractsStrictManifestAndReadsOnlyCanonicalFrames()
    {
        var nativeApi = new FakeEvidenceApi();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinDayFlow-evidence-root");
        var extractor = new NativeAnalysisEvidenceExtractor(dataRoot, nativeApi);
        var chunk = CreateChunk("chunk-20260723", videoByteCount: 4_096);

        var batch = await extractor.ExtractAsync(
            chunk,
            new CaptureChunkFingerprint(Fingerprint));

        Assert.Equal(Path.GetFullPath(dataRoot), nativeApi.DataRoot);
        Assert.Equal(chunk.Id, nativeApi.ChunkId);
        Assert.Equal((ulong)chunk.VideoByteCount, nativeApi.ExpectedVideoByteCount);
        Assert.Equal(chunk.FrameCount, nativeApi.ExpectedFrameCount);
        Assert.Equal(chunk.VideoWidth, nativeApi.ExpectedWidth);
        Assert.Equal(chunk.VideoHeight, nativeApi.ExpectedHeight);
        Assert.Equal(60_000UL, nativeApi.ExpectedDurationMilliseconds);
        Assert.Equal(Fingerprint, nativeApi.ExpectedFingerprint);
        Assert.Equal((uint)NativeAnalysisEvidenceExtractor.ManifestUtf8Capacity,
            nativeApi.ManifestCapacity);
        Assert.Equal(Fingerprint, batch.SourceFingerprint.Value);
        Assert.Equal(
            $"evidence/evidence-v1/{chunk.Id}/{Fingerprint}/manifest.json",
            batch.ArtifactPath);
        var image = Assert.Single(batch.Images);
        Assert.Equal("frame-0000", image.FrameId);
        Assert.Equal(chunk.Range.Start.AddMilliseconds(250), image.CapturedAt);
        Assert.Equal(nativeApi.Frames[0], image.JpegBytes.ToArray());
        Assert.Empty(batch.Context);
        Assert.Equal([0U], nativeApi.ReadIndices);
    }

    [Theory]
    [InlineData((int)NativeCaptureResult.EvidenceNotFound,
        AnalysisEvidenceExtractionFailureKind.EvidenceNotFound)]
    [InlineData((int)NativeCaptureResult.UnsafeEvidence,
        AnalysisEvidenceExtractionFailureKind.UnsafeEvidence)]
    [InlineData((int)NativeCaptureResult.EvidenceTooLarge,
        AnalysisEvidenceExtractionFailureKind.EvidenceTooLarge)]
    [InlineData((int)NativeCaptureResult.EvidenceChanged,
        AnalysisEvidenceExtractionFailureKind.EvidenceChanged)]
    [InlineData((int)NativeCaptureResult.IoFailure,
        AnalysisEvidenceExtractionFailureKind.IoFailure)]
    [InlineData((int)NativeCaptureResult.CryptoFailure,
        AnalysisEvidenceExtractionFailureKind.CryptoFailure)]
    [InlineData((int)NativeCaptureResult.EvidenceInvalid,
        AnalysisEvidenceExtractionFailureKind.InvalidEvidence)]
    [InlineData((int)NativeCaptureResult.DecoderFailure,
        AnalysisEvidenceExtractionFailureKind.DecoderFailure)]
    [InlineData((int)NativeCaptureResult.EvidenceConflict,
        AnalysisEvidenceExtractionFailureKind.EvidenceConflict)]
    public async Task MapsStableNativeFailures(
        int resultCode,
        AnalysisEvidenceExtractionFailureKind expectedKind)
    {
        var nativeApi = new FakeEvidenceApi
        {
            ExtractResult = (NativeCaptureResult)resultCode,
        };
        var extractor = CreateExtractor(nativeApi);

        var exception = await Assert.ThrowsAsync<AnalysisEvidenceExtractionException>(
            () => extractor.ExtractAsync(
                CreateChunk("failure"),
                new CaptureChunkFingerprint(Fingerprint)));

        Assert.Equal(expectedKind, exception.FailureKind);
        Assert.Equal(resultCode, exception.ResultCode);
        Assert.DoesNotContain("failure", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnknownDuplicateOrMismatchedManifestFields()
    {
        var chunk = CreateChunk("malformed");
        var valid = FakeEvidenceApi.CreateManifest(chunk.Id, [FakeEvidenceApi.Jpeg]);
        var malformed = new[]
        {
            valid.Replace("\"frames\":", "\"unknown\":1,\"frames\":",
                StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal),
            valid.Replace(Fingerprint, new string('A', 64), StringComparison.Ordinal),
            valid.Replace("frame-0000", "frame-0001", StringComparison.Ordinal),
            valid.Replace("\"sha256\":\"", "\"sha256\":\"a", StringComparison.Ordinal),
        };

        foreach (var manifest in malformed)
        {
            var extractor = CreateExtractor(new FakeEvidenceApi { Manifest = manifest });
            await Assert.ThrowsAsync<BadImageFormatException>(() =>
                extractor.ExtractAsync(chunk, new CaptureChunkFingerprint(Fingerprint)));
        }
    }

    [Fact]
    public async Task RejectsFrameBytesThatDoNotMatchManifestHash()
    {
        var nativeApi = new FakeEvidenceApi
        {
            Manifest = FakeEvidenceApi.CreateManifest("hash-mismatch", [FakeEvidenceApi.Jpeg]),
        };
        nativeApi.Frames[0] = [0xff, 0xd8, 9, 9, 0xff, 0xd9];
        var extractor = CreateExtractor(nativeApi);

        var exception = await Assert.ThrowsAsync<AnalysisEvidenceExtractionException>(
            () => extractor.ExtractAsync(
                CreateChunk("hash-mismatch"),
                new CaptureChunkFingerprint(Fingerprint)));

        Assert.Equal(
            AnalysisEvidenceExtractionFailureKind.NativeContractFailure,
            exception.FailureKind);
    }

    [Fact]
    public async Task CancellationBeforeExtractionAndBetweenFrameReadsStopsPromptly()
    {
        var beforeApi = new FakeEvidenceApi();
        var beforeExtractor = CreateExtractor(beforeApi);
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            beforeExtractor.ExtractAsync(
                CreateChunk("cancel-before"),
                new CaptureChunkFingerprint(Fingerprint),
                before.Token));
        Assert.Equal(0, beforeApi.ExtractCallCount);

        using var between = new CancellationTokenSource();
        var betweenApi = new FakeEvidenceApi
        {
            Frames =
            {
                [0] = [0xff, 0xd8, 1, 2, 0xff, 0xd9],
                [1] = [0xff, 0xd8, 3, 4, 0xff, 0xd9],
            },
            CancelAfterRead = between,
        };
        betweenApi.Manifest = FakeEvidenceApi.CreateManifest(
            "cancel-between",
            [betweenApi.Frames[0], betweenApi.Frames[1]]);
        var betweenExtractor = CreateExtractor(betweenApi);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            betweenExtractor.ExtractAsync(
                CreateChunk("cancel-between"),
                new CaptureChunkFingerprint(Fingerprint),
                between.Token));
        Assert.Equal([0U], betweenApi.ReadIndices);
    }

    [Theory]
    [InlineData("relative\\evidence")]
    [InlineData("C:evidence")]
    [InlineData("\\\\server\\share\\evidence")]
    public void RejectsRootsThatAreNotAbsoluteLocalWindowsPaths(string root)
    {
        Assert.Throws<ArgumentException>(() =>
            new NativeAnalysisEvidenceExtractor(root, new FakeEvidenceApi()));
    }

    [Fact]
    public void ResultCodesAndPInvokeEntryPointsMatchCAbi()
    {
        Assert.Equal(-21, (int)NativeCaptureResult.EvidenceInvalid);
        Assert.Equal(-22, (int)NativeCaptureResult.DecoderFailure);
        Assert.Equal(-23, (int)NativeCaptureResult.EvidenceConflict);
        foreach (var methodName in new[]
                 {
                     "wdf_capture_extract_analysis_evidence",
                     "wdf_capture_read_analysis_evidence_frame",
                 })
        {
            var method = typeof(NativeCaptureMethods).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            var import = Assert.IsType<DllImportAttribute>(
                method?.GetCustomAttribute<DllImportAttribute>());
            Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
            Assert.True(import.ExactSpelling);
        }
    }

    private static NativeAnalysisEvidenceExtractor CreateExtractor(
        INativeAnalysisEvidenceApi nativeApi) =>
        new(
            Path.Combine(Path.GetTempPath(), "WinDayFlow-evidence-root"),
            nativeApi);

    private static CaptureChunk CreateChunk(
        string id,
        long videoByteCount = 1_024)
    {
        var start = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/capture.mp4"),
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            frameCount: 30,
            videoWidth: 1280,
            videoHeight: 720,
            frameRateNumerator: 1,
            frameRateDenominator: 1,
            videoByteCount,
            persistenceGeneration: 11,
            targetEpoch: 12,
            committedAtUtc: start.AddMinutes(1),
            ingestedAtUtc: start.AddMinutes(1));
    }

    private sealed class FakeEvidenceApi : INativeAnalysisEvidenceApi
    {
        internal static readonly byte[] Jpeg = [0xff, 0xd8, 1, 2, 0xff, 0xd9];

        public FakeEvidenceApi()
        {
            Frames[0] = Jpeg.ToArray();
        }

        public NativeCaptureResult ExtractResult { get; init; } = NativeCaptureResult.Ok;

        public NativeCaptureResult ReadResult { get; init; } = NativeCaptureResult.Ok;

        public string? Manifest { get; set; }

        public Dictionary<uint, byte[]> Frames { get; } = [];

        public CancellationTokenSource? CancelAfterRead { get; init; }

        public int ExtractCallCount { get; private set; }

        public string? DataRoot { get; private set; }

        public string? ChunkId { get; private set; }

        public string? ExpectedFingerprint { get; private set; }

        public ulong ExpectedVideoByteCount { get; private set; }

        public uint ExpectedFrameCount { get; private set; }

        public uint ExpectedWidth { get; private set; }

        public uint ExpectedHeight { get; private set; }

        public ulong ExpectedDurationMilliseconds { get; private set; }

        public uint ManifestCapacity { get; private set; }

        public List<uint> ReadIndices { get; } = [];

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
            out uint manifestUtf8Required)
        {
            ExtractCallCount++;
            DataRoot = Decode(dataRootUtf8, dataRootUtf8Length);
            ChunkId = Decode(canonicalChunkIdUtf8, canonicalChunkIdUtf8Length);
            ExpectedFingerprint = Decode(
                expectedSourceFingerprintUtf8,
                expectedSourceFingerprintUtf8Length);
            ExpectedVideoByteCount = expectedVideoByteCount;
            ExpectedFrameCount = expectedFrameCount;
            ExpectedWidth = expectedVideoWidth;
            ExpectedHeight = expectedVideoHeight;
            ExpectedDurationMilliseconds = expectedDurationMilliseconds;
            ManifestCapacity = manifestUtf8Capacity;
            var manifest = Manifest ?? CreateManifest(ChunkId, [Frames[0]]);
            var encoded = Encoding.UTF8.GetBytes(manifest);
            manifestUtf8Required = checked((uint)encoded.Length + 1);
            encoded.CopyTo(manifestUtf8, 0);
            manifestUtf8[encoded.Length] = 0;
            return ExtractResult;
        }

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
            out uint frameBytesRequired)
        {
            Assert.Equal(DataRoot, Decode(dataRootUtf8, dataRootUtf8Length));
            Assert.Equal(ChunkId, Decode(canonicalChunkIdUtf8, canonicalChunkIdUtf8Length));
            Assert.Equal(ExpectedFingerprint,
                Decode(canonicalSourceFingerprintUtf8, canonicalSourceFingerprintUtf8Length));
            ReadIndices.Add(frameIndex);
            var source = Frames[frameIndex];
            frameBytesRequired = checked((uint)source.Length);
            Assert.Equal(frameBytesRequired, frameBytesCapacity);
            source.CopyTo(frameBytes, 0);
            CancelAfterRead?.Cancel();
            return ReadResult;
        }

        internal static string CreateManifest(string chunkId, IReadOnlyList<byte[]> frames)
        {
            var records = frames.Select((frame, index) =>
                $"{{\"id\":\"frame-{index:D4}\",\"index\":{index}," +
                $"\"offsetMilliseconds\":{250 + index * 250}," +
                $"\"byteCount\":{frame.Length}," +
                $"\"sha256\":\"{Convert.ToHexString(SHA256.HashData(frame))}\"}}");
            return
                $"{{\"schemaVersion\":1,\"policyVersion\":\"evidence-v1\"," +
                $"\"chunkId\":\"{chunkId}\",\"sourceFingerprint\":\"{Fingerprint}\"," +
                $"\"artifactPath\":\"evidence/evidence-v1/{chunkId}/{Fingerprint}/manifest.json\"," +
                $"\"frames\":[{string.Join(',', records)}]}}";
        }

        private static string Decode(byte[] bytes, uint length) =>
            Encoding.UTF8.GetString(bytes, 0, checked((int)length));
    }
}

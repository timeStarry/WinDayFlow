using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureChunkFingerprintProviderTests
{
    private const string ValidFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task PassesOnlyBoundRootIdentifierAndExpectedVideoLengthToNative()
    {
        var nativeApi = new FakeFingerprintApi();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinDayFlow-fingerprint-root");
        var provider = new NativeCaptureChunkFingerprintProvider(dataRoot, nativeApi);
        var chunk = CreateChunk("chunk-20260723", videoByteCount: 4_096);

        var fingerprint = await provider.ComputeAsync(chunk);

        Assert.Equal(ValidFingerprint, fingerprint.Value);
        Assert.Equal(Path.GetFullPath(dataRoot), nativeApi.DataRoot);
        Assert.Equal(chunk.Id, nativeApi.ChunkId);
        Assert.Equal((ulong)chunk.VideoByteCount, nativeApi.ExpectedVideoByteCount);
        Assert.Equal(
            NativeCaptureChunkFingerprintProvider.FingerprintUtf8Capacity,
            nativeApi.OutputCapacity);
    }

    [Theory]
    [InlineData("relative\\evidence")]
    [InlineData("C:evidence")]
    [InlineData("\\\\server\\share\\evidence")]
    public void RejectsRootsThatAreNotAbsoluteLocalWindowsPaths(string root)
    {
        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureChunkFingerprintProvider(root, new FakeFingerprintApi()));
    }

    [Fact]
    public async Task CancellationBeforeCallDoesNotEnterNative()
    {
        var nativeApi = new FakeFingerprintApi();
        var provider = CreateProvider(nativeApi);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ComputeAsync(CreateChunk("cancelled"), cancellation.Token));

        Assert.Equal(0, nativeApi.CallCount);
    }

    [Theory]
    [InlineData((int)NativeCaptureResult.EvidenceNotFound,
        CaptureChunkFingerprintFailureKind.EvidenceNotFound)]
    [InlineData((int)NativeCaptureResult.UnsafeEvidence,
        CaptureChunkFingerprintFailureKind.UnsafeEvidence)]
    [InlineData((int)NativeCaptureResult.EvidenceTooLarge,
        CaptureChunkFingerprintFailureKind.EvidenceTooLarge)]
    [InlineData((int)NativeCaptureResult.EvidenceChanged,
        CaptureChunkFingerprintFailureKind.EvidenceChanged)]
    [InlineData((int)NativeCaptureResult.IoFailure,
        CaptureChunkFingerprintFailureKind.IoFailure)]
    [InlineData((int)NativeCaptureResult.CryptoFailure,
        CaptureChunkFingerprintFailureKind.CryptoFailure)]
    public async Task MapsStableNativeEvidenceFailures(
        int resultCode,
        CaptureChunkFingerprintFailureKind expectedKind)
    {
        var result = (NativeCaptureResult)resultCode;
        var nativeApi = new FakeFingerprintApi { Result = result };
        var provider = CreateProvider(nativeApi);

        var exception = await Assert.ThrowsAsync<CaptureChunkFingerprintException>(
            () => provider.ComputeAsync(CreateChunk("failure")));

        Assert.Equal(expectedKind, exception.FailureKind);
        Assert.Equal((int)result, exception.ResultCode);
        Assert.DoesNotContain("failure", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)NativeCaptureResult.BufferTooSmall)]
    [InlineData((int)NativeCaptureResult.Ok)]
    public async Task RejectsIncompatibleNativeOutputContract(
        int resultCode)
    {
        var nativeApi = new FakeFingerprintApi
        {
            Result = (NativeCaptureResult)resultCode,
            Required = NativeCaptureChunkFingerprintProvider.FingerprintUtf8Capacity + 1,
        };
        var provider = CreateProvider(nativeApi);

        await Assert.ThrowsAsync<BadImageFormatException>(() =>
            provider.ComputeAsync(CreateChunk("bad-contract")));
    }

    [Fact]
    public async Task RejectsMalformedSuccessfulDigest()
    {
        var nativeApi = new FakeFingerprintApi
        {
            Fingerprint = ValidFingerprint.ToLowerInvariant(),
        };
        var provider = CreateProvider(nativeApi);

        await Assert.ThrowsAsync<BadImageFormatException>(() =>
            provider.ComputeAsync(CreateChunk("malformed")));
    }

    [Fact]
    public void ManagedResultCodesAndPInvokeEntryPointMatchTheCAbi()
    {
        Assert.Equal(-15, (int)NativeCaptureResult.EvidenceNotFound);
        Assert.Equal(-16, (int)NativeCaptureResult.UnsafeEvidence);
        Assert.Equal(-17, (int)NativeCaptureResult.EvidenceTooLarge);
        Assert.Equal(-18, (int)NativeCaptureResult.EvidenceChanged);
        Assert.Equal(-19, (int)NativeCaptureResult.IoFailure);
        Assert.Equal(-20, (int)NativeCaptureResult.CryptoFailure);

        var method = typeof(NativeCaptureMethods).GetMethod(
            "wdf_capture_compute_chunk_fingerprint",
            BindingFlags.Static | BindingFlags.NonPublic);
        var import = Assert.IsType<DllImportAttribute>(
            method?.GetCustomAttribute<DllImportAttribute>());
        Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
        Assert.True(import.ExactSpelling);
    }

    private static NativeCaptureChunkFingerprintProvider CreateProvider(
        INativeCaptureChunkFingerprintApi nativeApi) =>
        new(
            Path.Combine(Path.GetTempPath(), "WinDayFlow-fingerprint-root"),
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

    private sealed class FakeFingerprintApi : INativeCaptureChunkFingerprintApi
    {
        public NativeCaptureResult Result { get; init; } = NativeCaptureResult.Ok;

        public uint Required { get; init; } =
            NativeCaptureChunkFingerprintProvider.FingerprintUtf8Capacity;

        public string Fingerprint { get; init; } = ValidFingerprint;

        public int CallCount { get; private set; }

        public string? DataRoot { get; private set; }

        public string? ChunkId { get; private set; }

        public ulong ExpectedVideoByteCount { get; private set; }

        public uint OutputCapacity { get; private set; }

        public NativeCaptureResult ComputeChunkFingerprint(
            byte[] dataRootUtf8,
            uint dataRootUtf8Length,
            byte[] canonicalChunkIdUtf8,
            uint canonicalChunkIdUtf8Length,
            ulong expectedVideoByteCount,
            byte[] fingerprintUtf8,
            uint fingerprintUtf8Capacity,
            out uint fingerprintUtf8Required)
        {
            CallCount++;
            DataRoot = Encoding.UTF8.GetString(
                dataRootUtf8,
                0,
                checked((int)dataRootUtf8Length));
            ChunkId = Encoding.UTF8.GetString(
                canonicalChunkIdUtf8,
                0,
                checked((int)canonicalChunkIdUtf8Length));
            ExpectedVideoByteCount = expectedVideoByteCount;
            OutputCapacity = fingerprintUtf8Capacity;
            fingerprintUtf8Required = Required;
            var encoded = Encoding.ASCII.GetBytes(Fingerprint);
            encoded.AsSpan(0, Math.Min(encoded.Length, fingerprintUtf8.Length))
                .CopyTo(fingerprintUtf8);
            if (fingerprintUtf8.Length > encoded.Length)
            {
                fingerprintUtf8[encoded.Length] = 0;
            }
            return Result;
        }
    }
}

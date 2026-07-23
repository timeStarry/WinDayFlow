using System.Text;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;

namespace WinDayFlow.Capture.Interop;

public enum CaptureChunkFingerprintFailureKind
{
    EvidenceNotFound,
    UnsafeEvidence,
    EvidenceTooLarge,
    EvidenceChanged,
    IoFailure,
    CryptoFailure,
}

public sealed class CaptureChunkFingerprintException : InvalidOperationException
{
    internal CaptureChunkFingerprintException(
        CaptureChunkFingerprintFailureKind failureKind,
        NativeCaptureResult result)
        : base($"Capture chunk fingerprinting failed: {failureKind}.")
    {
        FailureKind = failureKind;
        ResultCode = (int)result;
    }

    public CaptureChunkFingerprintFailureKind FailureKind { get; }

    public int ResultCode { get; }
}

internal interface INativeCaptureChunkFingerprintApi
{
    NativeCaptureResult ComputeChunkFingerprint(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        ulong expectedVideoByteCount,
        byte[] fingerprintUtf8,
        uint fingerprintUtf8Capacity,
        out uint fingerprintUtf8Required);
}

internal sealed class PInvokeNativeCaptureChunkFingerprintApi
    : INativeCaptureChunkFingerprintApi
{
    internal static PInvokeNativeCaptureChunkFingerprintApi Instance { get; } = new();

    private PInvokeNativeCaptureChunkFingerprintApi()
    {
    }

    public NativeCaptureResult ComputeChunkFingerprint(
        byte[] dataRootUtf8,
        uint dataRootUtf8Length,
        byte[] canonicalChunkIdUtf8,
        uint canonicalChunkIdUtf8Length,
        ulong expectedVideoByteCount,
        byte[] fingerprintUtf8,
        uint fingerprintUtf8Capacity,
        out uint fingerprintUtf8Required) =>
        NativeCaptureMethods.wdf_capture_compute_chunk_fingerprint(
            dataRootUtf8,
            dataRootUtf8Length,
            canonicalChunkIdUtf8,
            canonicalChunkIdUtf8Length,
            expectedVideoByteCount,
            fingerprintUtf8,
            fingerprintUtf8Capacity,
            out fingerprintUtf8Required);
}

public sealed class NativeCaptureChunkFingerprintProvider
    : ICaptureChunkFingerprintProvider
{
    internal const uint FingerprintUtf8Length = CaptureChunkFingerprint.HexLength;
    internal const uint FingerprintUtf8Capacity = FingerprintUtf8Length + 1;
    private const int MaximumDataRootUtf8Bytes = 32_767;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _dataRootUtf8;
    private readonly INativeCaptureChunkFingerprintApi _nativeApi;

    public NativeCaptureChunkFingerprintProvider(string dataRootPath)
        : this(dataRootPath, PInvokeNativeCaptureChunkFingerprintApi.Instance)
    {
    }

    internal NativeCaptureChunkFingerprintProvider(
        string dataRootPath,
        INativeCaptureChunkFingerprintApi nativeApi)
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

    public Task<CaptureChunkFingerprint> ComputeAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        var chunkIdUtf8 = StrictUtf8.GetBytes(chunk.Id);
        var fingerprintUtf8 = new byte[FingerprintUtf8Capacity];
        var result = _nativeApi.ComputeChunkFingerprint(
            _dataRootUtf8,
            checked((uint)_dataRootUtf8.Length),
            chunkIdUtf8,
            checked((uint)chunkIdUtf8.Length),
            checked((ulong)chunk.VideoByteCount),
            fingerprintUtf8,
            checked((uint)fingerprintUtf8.Length),
            out var required);
        cancellationToken.ThrowIfCancellationRequested();

        if (result == NativeCaptureResult.Ok)
        {
            if (required != FingerprintUtf8Capacity
                || fingerprintUtf8[FingerprintUtf8Length] != 0)
            {
                throw new BadImageFormatException(
                    "The native capture fingerprint ABI returned an invalid output contract.");
            }

            var value = Encoding.ASCII.GetString(
                fingerprintUtf8,
                0,
                checked((int)FingerprintUtf8Length));
            if (value.Any(static character =>
                    character is not (>= '0' and <= '9')
                        and not (>= 'A' and <= 'F')))
            {
                throw new BadImageFormatException(
                    "The native capture fingerprint ABI returned malformed digest text.");
            }

            return Task.FromResult(new CaptureChunkFingerprint(value));
        }

        throw MapFailure(result);
    }

    private static Exception MapFailure(NativeCaptureResult result)
    {
        return result switch
        {
            NativeCaptureResult.EvidenceNotFound => CreateFailure(
                CaptureChunkFingerprintFailureKind.EvidenceNotFound,
                result),
            NativeCaptureResult.UnsafeEvidence => CreateFailure(
                CaptureChunkFingerprintFailureKind.UnsafeEvidence,
                result),
            NativeCaptureResult.EvidenceTooLarge => CreateFailure(
                CaptureChunkFingerprintFailureKind.EvidenceTooLarge,
                result),
            NativeCaptureResult.EvidenceChanged => CreateFailure(
                CaptureChunkFingerprintFailureKind.EvidenceChanged,
                result),
            NativeCaptureResult.IoFailure => CreateFailure(
                CaptureChunkFingerprintFailureKind.IoFailure,
                result),
            NativeCaptureResult.CryptoFailure => CreateFailure(
                CaptureChunkFingerprintFailureKind.CryptoFailure,
                result),
            NativeCaptureResult.BufferTooSmall =>
                new BadImageFormatException(
                    "The native capture fingerprint ABI requires an incompatible output buffer."),
            _ => new NativeCaptureException(
                result,
                "compute_chunk_fingerprint"),
        };
    }

    private static CaptureChunkFingerprintException CreateFailure(
        CaptureChunkFingerprintFailureKind failureKind,
        NativeCaptureResult result) =>
        new(failureKind, result);
}

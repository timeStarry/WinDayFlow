using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Domain;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinDayFlow.App.Services;

internal sealed class SystemDrawingPrivacyEvidenceRedactor : IPrivacyEvidenceRedactor
{
    private const long JpegQuality = 82;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly string _screeningsRoot;
    private readonly ICaptureFrameArchive _archive;

    public SystemDrawingPrivacyEvidenceRedactor(
        string dataRoot,
        ICaptureFrameArchive archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _screeningsRoot = Path.Combine(_dataRoot, "screenings");
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    }

    public async Task<PrivacyRedactionResult> RedactAsync(
        Guid screeningId,
        CaptureChunk chunk,
        CaptureChunkFingerprint sourceFingerprint,
        IReadOnlyList<PrivacyFinding> findings,
        CancellationToken cancellationToken = default)
    {
        if (screeningId == Guid.Empty)
        {
            throw new ArgumentException("A screening identifier is required.", nameof(screeningId));
        }
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
        {
            throw new ArgumentException("Redaction requires at least one valid region.", nameof(findings));
        }

        var frames = await _archive.ListFramesAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        var framesById = frames.ToDictionary(
            static frame => $"frame-{frame.Index:D6}",
            StringComparer.Ordinal);
        if (findings.Any(finding => !framesById.ContainsKey(finding.FrameId)))
        {
            throw new InvalidDataException("A privacy finding references an unknown frame.");
        }

        Directory.CreateDirectory(_screeningsRoot);
        var screeningName = screeningId.ToString("D");
        var finalDirectory = Path.Combine(_screeningsRoot, screeningName);
        var temporaryDirectory = Path.Combine(
            _screeningsRoot,
            $".{screeningName}.tmp-{Guid.NewGuid():N}");
        var framesDirectory = Path.Combine(temporaryDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);

        try
        {
            var manifestFrames = new List<object>(frames.Count);
            var redactedFrameCount = 0;
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceBytes = await _archive
                    .ReadFrameBytesAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
                var frameId = $"frame-{frame.Index:D6}";
                var regions = findings
                    .Where(finding => string.Equals(
                        finding.FrameId,
                        frameId,
                        StringComparison.Ordinal))
                    .Select(static finding => finding.Region)
                    .ToArray();
                var outputBytes = regions.Length == 0
                    ? sourceBytes
                    : await RedactJpegAsync(sourceBytes, regions, cancellationToken)
                        .ConfigureAwait(false);
                if (regions.Length != 0)
                {
                    redactedFrameCount++;
                }
                if (outputBytes.Length is < 4 or > MaximumFrameBytes)
                {
                    throw new InvalidDataException("A redacted JPEG exceeded the storage bound.");
                }

                var localPath = $"frames/{frameId}.jpg";
                await File.WriteAllBytesAsync(
                        Path.Combine(framesDirectory, $"{frameId}.jpg"),
                        outputBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                manifestFrames.Add(new
                {
                    id = frameId,
                    index = frame.Index,
                    path = localPath,
                    offsetMilliseconds = frame.OffsetMilliseconds,
                    byteCount = outputBytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(outputBytes)),
                });
            }

            if (redactedFrameCount == 0)
            {
                throw new InvalidDataException("No privacy region produced an opaque mask.");
            }

            var manifestPath = Path.Combine(temporaryDirectory, "manifest.json");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                screeningId = screeningName,
                captureChunkId = chunk.Id,
                sourceFingerprint = sourceFingerprint.Value,
                mask = "opaque-black",
                jpegQuality = JpegQuality,
                frames = manifestFrames,
            });
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken)
                .ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("WinDayFlow/privacy-evidence-v1\0"u8);
            hash.AppendData(manifestBytes);
            foreach (var descriptor in frames)
            {
                var bytes = await File.ReadAllBytesAsync(
                        Path.Combine(framesDirectory, $"frame-{descriptor.Index:D6}.jpg"),
                        cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(bytes);
            }
            var fingerprint = new CaptureChunkFingerprint(
                Convert.ToHexString(hash.GetHashAndReset()));

            Directory.Move(temporaryDirectory, finalDirectory);
            return new PrivacyRedactionResult(
                new EvidenceRelativePath($"screenings/{screeningName}/manifest.json"),
                fingerprint,
                redactedFrameCount);
        }
        catch
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException
                                                    or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(cleanupFailure);
            }
            throw;
        }
    }

    private static async Task<byte[]> RedactJpegAsync(
        byte[] source,
        IReadOnlyList<NormalizedPrivacyRegion> regions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(source);
            _ = await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            _ = await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream();
        }
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        using var decoded = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var width = decoded.PixelWidth;
        var height = decoded.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("A privacy source JPEG has invalid dimensions.");
        }

        var pixels = new byte[checked(width * height * 4)];
        decoded.CopyToBuffer(pixels.AsBuffer());
        foreach (var region in regions)
        {
            var left = Math.Clamp((int)Math.Floor(region.X * width), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, height - 1);
            var right = Math.Clamp(
                (int)Math.Ceiling((region.X + region.Width) * width),
                left + 1,
                width);
            var bottom = Math.Clamp(
                (int)Math.Ceiling((region.Y + region.Height) * height),
                top + 1,
                height);
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var offset = checked((y * width + x) * 4);
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                    pixels[offset + 3] = byte.MaxValue;
                }
            }
        }

        using var redacted = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var quality = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(
                JpegQuality / 100.0,
                PropertyType.Single),
        };
        await encoder.BitmapProperties.SetPropertiesAsync(quality)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.SetSoftwareBitmap(redacted);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        if (output.Size is 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("A redacted JPEG exceeded the storage bound.");
        }
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        _ = await reader.LoadAsync(checked((uint)output.Size))
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var result = new byte[checked((int)output.Size)];
        reader.ReadBytes(result);
        return result;
    }
}

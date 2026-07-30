using System.Security.Cryptography;
using System.Text.Json;
using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;

namespace WinDayFlow.Infrastructure.Capture;

public sealed class CanonicalCaptureFrameArchive : ICaptureFrameArchive
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly string _dataRootPrefix;

    public CanonicalCaptureFrameArchive(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The capture archive root must be absolute.", nameof(dataRoot));
        }

        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _dataRootPrefix = _dataRoot + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<CaptureFrameDescriptor>> ListFramesAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var manifestPath = Resolve(chunk.ManifestPath);
        var bytes = await ReadRegularFileAsync(
                manifestPath,
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });

        var root = document.RootElement;
        var schemaVersion = ReadUInt32(root, "schemaVersion");
        RequireExactProperties(
            root,
            "schemaVersion",
            "captureScope",
            "chunkId",
            "startTimeUnixMs",
            "endTimeUnixMs",
            "authorization",
            "application",
            "contextSamples",
            "frames");
        if (schemaVersion != 4
            || ReadString(root, "chunkId") != chunk.Id
            || DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64(root, "startTimeUnixMs"))
                    .ToUniversalTime() != chunk.Range.Start.ToUniversalTime()
            || DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64(root, "endTimeUnixMs"))
                    .ToUniversalTime() != chunk.Range.End.ToUniversalTime())
        {
            throw new InvalidDataException("The capture manifest identity is invalid.");
        }
        ValidateProcessTelemetry(root, schemaVersion, chunk.ProcessTelemetry);

        var frames = root.GetProperty("frames");
        RequireExactProperties(
            frames,
            "format",
            "quality",
            "sampledFrameCount",
            "blackFrameCount",
            "duplicateFrameCount",
            "retainedFrameCount",
            "width",
            "height",
            "totalByteCount",
            "items");
        var items = frames.GetProperty("items");
        if (ReadString(frames, "format") != "jpeg"
            || ReadUInt32(frames, "quality") != 82
            || ReadUInt32(frames, "sampledFrameCount") != chunk.CapturedFrameCount
            || ReadUInt32(frames, "blackFrameCount") != chunk.BlackFrameCount
            || ReadUInt32(frames, "duplicateFrameCount") != chunk.DuplicateFrameCount
            || ReadUInt32(frames, "retainedFrameCount") != chunk.FrameCount
            || ReadUInt32(frames, "width") != chunk.FrameWidth
            || ReadUInt32(frames, "height") != chunk.FrameHeight
            || ReadUInt64(frames, "totalByteCount") != checked((ulong)chunk.FrameByteCount)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() != chunk.FrameCount)
        {
            throw new InvalidDataException("The capture frame summary is invalid.");
        }

        var result = new List<CaptureFrameDescriptor>(items.GetArrayLength());
        ulong totalBytes = 0;
        ulong previousOffset = 0;
        var durationMilliseconds = checked((ulong)Math.Ceiling(chunk.Range.Duration.TotalMilliseconds));
        var ordinal = 0U;
        foreach (var item in items.EnumerateArray())
        {
            RequireExactProperties(
                item,
                "id",
                "index",
                "path",
                "offsetMilliseconds",
                "byteCount",
                "sha256");
            var offset = ReadUInt64(item, "offsetMilliseconds");
            var byteCount = ReadUInt32(item, "byteCount");
            var sha256 = ReadString(item, "sha256");
            var expectedId = $"frame-{ordinal:D6}";
            var expectedLocalPath = $"frames/{expectedId}.jpg";
            if (ReadUInt32(item, "index") != ordinal
                || ReadString(item, "id") != expectedId
                || ReadString(item, "path") != expectedLocalPath
                || byteCount is < 4 or > MaximumFrameBytes
                || offset >= durationMilliseconds
                || (ordinal > 0 && offset <= previousOffset)
                || !IsCanonicalSha256(sha256))
            {
                throw new InvalidDataException("The capture frame metadata is invalid.");
            }

            totalBytes = checked(totalBytes + byteCount);
            var relativePath = new EvidenceRelativePath(
                $"chunks/{chunk.Id}/{expectedLocalPath}");
            result.Add(new CaptureFrameDescriptor(
                chunk.Id,
                ordinal,
                chunk.Range.Start.AddMilliseconds(offset),
                offset,
                relativePath,
                byteCount,
                sha256));
            previousOffset = offset;
            ordinal++;
        }

        if (totalBytes != checked((ulong)chunk.FrameByteCount))
        {
            throw new InvalidDataException("The capture frame byte total is invalid.");
        }

        return result;
    }

    public async Task<byte[]> ReadFrameBytesAsync(
        CaptureFrameDescriptor frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var path = Resolve(frame.RelativePath);
        var bytes = await ReadRegularFileAsync(path, MaximumFrameBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length != frame.ByteCount
            || bytes[0] != 0xff
            || bytes[1] != 0xd8
            || bytes[^2] != 0xff
            || bytes[^1] != 0xd9
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                frame.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The capture frame failed integrity validation.");
        }

        return bytes;
    }

    internal async Task<byte[]> ReadManifestBytesAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return await ReadRegularFileAsync(
                Resolve(chunk.ManifestPath),
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string Resolve(EvidenceRelativePath relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            _dataRoot,
            relativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_dataRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The capture path escapes the archive root.");
        }

        return fullPath;
    }

    private async Task<byte[]> ReadRegularFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(path);
        var before = new FileInfo(path);
        if (!before.Exists || before.Length is <= 0 || before.Length > maximumBytes)
        {
            throw new FileNotFoundException("The capture artifact is unavailable.", path);
        }

        var beforeLength = before.Length;
        var beforeWrite = before.LastWriteTimeUtc;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(path);
        if (!after.Exists
            || after.Length != beforeLength
            || after.LastWriteTimeUtc != beforeWrite
            || bytes.LongLength != beforeLength)
        {
            throw new InvalidDataException("The capture artifact changed while it was read.");
        }

        return bytes;
    }

    private void EnsureNoReparsePoints(string path)
    {
        var current = _dataRoot;
        var relative = Path.GetRelativePath(_dataRoot, path);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The capture path contains a reparse point.");
            }
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A capture manifest object was expected.");
        }

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw new InvalidDataException("The capture manifest schema is invalid.");
            }
        }

        if (!observed.SetEquals(expected))
        {
            throw new InvalidDataException("The capture manifest schema is incomplete.");
        }
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.GetProperty(name).ValueKind == JsonValueKind.String
            ? parent.GetProperty(name).GetString()
                ?? throw new InvalidDataException("A manifest string is null.")
            : throw new InvalidDataException("A manifest string was expected.");

    private static long ReadInt64(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !IsCanonicalUnsignedInteger(value.GetRawText())
            || !value.TryGetInt64(out var parsed))
        {
            throw new InvalidDataException("A canonical manifest integer was expected.");
        }
        return parsed;
    }

    private static uint ReadUInt32(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !IsCanonicalUnsignedInteger(value.GetRawText())
            || !value.TryGetUInt32(out var parsed))
        {
            throw new InvalidDataException("A canonical manifest integer was expected.");
        }
        return parsed;
    }

    private static ulong ReadUInt64(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !IsCanonicalUnsignedInteger(value.GetRawText())
            || !value.TryGetUInt64(out var parsed))
        {
            throw new InvalidDataException("A canonical manifest integer was expected.");
        }
        return parsed;
    }

    private static bool IsCanonicalUnsignedInteger(string raw) =>
        raw.Length > 0
        && (raw.Length == 1 || raw[0] != '0')
        && raw.All(static character => character is >= '0' and <= '9');

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateProcessTelemetry(
        JsonElement root,
        uint schemaVersion,
        CaptureProcessTelemetry? expected)
    {
        if (schemaVersion == 2)
        {
            if (expected is not null)
            {
                throw new InvalidDataException("Legacy capture evidence cannot contain process telemetry.");
            }
            return;
        }

        var application = root.GetProperty("application");
        if (application.ValueKind == JsonValueKind.Null)
        {
            if (expected is not null)
            {
                throw new InvalidDataException("Capture process telemetry is missing.");
            }
            return;
        }
        RequireExactProperties(
            application,
            "processName",
            "processId",
            "cpuUsageBasisPoints",
            "workingSetBytes",
            "privateMemoryBytes");
        if (expected is null
            || ReadString(application, "processName") != expected.ProcessName
            || ReadUInt32(application, "processId") != expected.ProcessId
            || ReadUInt32(application, "cpuUsageBasisPoints")
                != expected.CpuUsageBasisPoints
            || ReadUInt64(application, "workingSetBytes")
                != checked((ulong)expected.WorkingSetBytes)
            || ReadUInt64(application, "privateMemoryBytes")
                != checked((ulong)expected.PrivateMemoryBytes))
        {
            throw new InvalidDataException("Capture process telemetry is invalid.");
        }
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Infrastructure.Capture;

public sealed class CaptureManifestScanner :
    ICaptureManifestScanner,
    ICaptureManifestContextSource
{
    internal const long MaximumManifestByteCount = 64L * 1024L;

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    internal const string ForegroundDisplayCaptureScope =
        "authorized-foreground-display";
    internal const string ContinuousDisplayCaptureScope =
        "authorized-display-continuous";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly string _dataRootPath;
    private readonly string _chunksRootPath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _captureTimeZone;
    private readonly Action<CaptureManifestScanCheckpoint, string>? _checkpoint;
    private readonly CanonicalCaptureFrameArchive _frameArchive;
    private readonly object _contextSync = new();
    private readonly Dictionary<string, IReadOnlyList<CaptureContextSample>>
        _contextByChunk = new(StringComparer.Ordinal);

    public CaptureManifestScanner(
        string dataRootPath,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? captureTimeZone = null)
        : this(
            dataRootPath,
            timeProvider ?? TimeProvider.System,
            null,
            captureTimeZone ?? TimeZoneInfo.Local)
    {
    }

    internal CaptureManifestScanner(
        string dataRootPath,
        TimeProvider timeProvider,
        Action<CaptureManifestScanCheckpoint, string>? checkpoint,
        TimeZoneInfo? captureTimeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dataRootPath = Path.GetFullPath(dataRootPath);
        _chunksRootPath = Path.Combine(_dataRootPath, "chunks");
        _timeProvider = timeProvider;
        _captureTimeZone = captureTimeZone ?? TimeZoneInfo.Local;
        _checkpoint = checkpoint;
        _frameArchive = new CanonicalCaptureFrameArchive(_dataRootPath);
    }

    public async Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_dataRootPath)
            || !TryReadDirectorySnapshot(_dataRootPath, out var dataRootSnapshot)
            || !Directory.Exists(_chunksRootPath)
            || !TryReadDirectorySnapshot(_chunksRootPath, out var chunksRootSnapshot))
        {
            return [];
        }

        _checkpoint?.Invoke(
            CaptureManifestScanCheckpoint.RootsInspected,
            _chunksRootPath);

        string[] candidateDirectories;
        try
        {
            candidateDirectories = Directory
                .EnumerateDirectories(
                    _chunksRootPath,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsRecoverableFileSystemFailure(exception))
        {
            return [];
        }

        if (!RootsMatch(dataRootSnapshot, chunksRootSnapshot))
        {
            return [];
        }

        var ingestedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var chunks = new List<CaptureChunk>(candidateDirectories.Length);
        foreach (var candidateDirectory in candidateDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await TryReadChunkAsync(
                    candidateDirectory,
                    ingestedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }

            if (!RootsMatch(dataRootSnapshot, chunksRootSnapshot))
            {
                return [];
            }
        }

        return chunks.ToArray();
    }

    public Task<IReadOnlyList<CaptureContextSample>> ReadContextAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_contextSync)
        {
            return Task.FromResult(
                _contextByChunk.TryGetValue(chunk.Id, out var samples)
                    ? samples
                    : (IReadOnlyList<CaptureContextSample>)[]);
        }
    }

    private async Task<CaptureChunk?> TryReadChunkAsync(
        string candidateDirectory,
        DateTimeOffset ingestedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunkId = Path.GetFileName(candidateDirectory);
            CaptureChunk.ValidateIdentifier(chunkId);

            var canonicalDirectory = Path.GetFullPath(
                Path.Combine(_chunksRootPath, chunkId));
            if (!string.Equals(
                    canonicalDirectory,
                    Path.GetFullPath(candidateDirectory),
                    PathComparison))
            {
                return null;
            }

            if (!TryReadDirectorySnapshot(candidateDirectory, out var directoryBefore))
            {
                return null;
            }

            _checkpoint?.Invoke(
                CaptureManifestScanCheckpoint.CandidateInspected,
                candidateDirectory);

            var manifestPath = Path.Combine(candidateDirectory, "manifest.json");
            var manifestRead = await TryReadStableManifestAsync(
                    manifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (manifestRead is null
                || !TryParseManifest(
                    manifestRead.Value.Content,
                    chunkId,
                    _captureTimeZone,
                    out var manifest))
            {
                return null;
            }

            var chunk = new CaptureChunk(
                chunkId,
                new EvidenceRelativePath($"chunks/{chunkId}/manifest.json"),
                new TimeRange(manifest.StartTimeUtc, manifest.EndTimeUtc),
                manifest.CapturedFrameCount,
                manifest.FrameCount,
                manifest.Width,
                manifest.Height,
                checked((long)manifest.TotalByteCount),
                manifest.PersistenceGeneration,
                manifest.TargetEpoch,
                GetCommittedAtUtc(
                    directoryBefore.LastWriteTimeFileTime,
                    manifestRead.Value.Snapshot.LastWriteTimeFileTime),
                ingestedAtUtc,
                processTelemetry: manifest.ProcessTelemetry,
                blackFrameCount: manifest.BlackFrameCount,
                duplicateFrameCount: manifest.DuplicateFrameCount);
            var frames = await _frameArchive.ListFramesAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            foreach (var frame in frames)
            {
                _ = await _frameArchive.ReadFrameBytesAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
            }
            _checkpoint?.Invoke(
                CaptureManifestScanCheckpoint.FramesInspected,
                candidateDirectory);

            if (!TryReadDirectorySnapshot(candidateDirectory, out var directoryAfter)
                || !directoryBefore.IsSameEntryAndTimestamp(directoryAfter))
            {
                return null;
            }

            lock (_contextSync)
            {
                _contextByChunk[chunk.Id] = manifest.ContextSamples;
            }

            return chunk;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            IsRecoverableFileSystemFailure(exception)
            || exception is JsonException
            || exception is InvalidDataException
            || exception is ArgumentException
            || exception is OverflowException)
        {
            return null;
        }
    }

    private async Task<StableManifestRead?> TryReadStableManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        using var handle = TryOpenPath(manifestPath, isDirectory: false, readContent: true);
        if (handle is null
            || !TryReadSnapshot(handle, out var before)
            || !before.IsRegularFile
            || before.Length is <= 0 or > MaximumManifestByteCount)
        {
            return null;
        }

        var content = new byte[checked((int)before.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await RandomAccess.ReadAsync(
                    handle,
                    content.AsMemory(offset),
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        var trailingByte = new byte[1];
        if (await RandomAccess.ReadAsync(
                handle,
                trailingByte,
                before.Length,
                cancellationToken)
            .ConfigureAwait(false) != 0)
        {
            return null;
        }

        _checkpoint?.Invoke(
            CaptureManifestScanCheckpoint.ManifestRead,
            manifestPath);

        if (!TryReadSnapshot(handle, out var after)
            || !before.IsStable(after)
            || !TryReopenAndMatch(manifestPath, before, readContent: true))
        {
            return null;
        }

        return new StableManifestRead(content, after);
    }

    private static bool TryParseManifest(
        byte[] utf8Json,
        string expectedChunkId,
        TimeZoneInfo captureTimeZone,
        out ParsedManifest manifest)
    {
        manifest = default;
        if (utf8Json.AsSpan().StartsWith(
                new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return false;
        }

        using var document = JsonDocument.Parse(utf8Json, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryReadCanonicalUInt32(root, "schemaVersion", out var schemaVersion)
            || schemaVersion != 4
            || !HasExactProperties(
                root,
                "schemaVersion",
                "captureScope",
                "chunkId",
                "startTimeUnixMs",
                "endTimeUnixMs",
                "authorization",
                "application",
                "contextSamples",
                "frames")
            || !TryReadString(root, "captureScope", out var captureScope)
            || captureScope is not (ForegroundDisplayCaptureScope
                or ContinuousDisplayCaptureScope)
            || !TryReadString(root, "chunkId", out var chunkId)
            || !string.Equals(chunkId, expectedChunkId, StringComparison.Ordinal)
            || !TryReadCanonicalInt64(root, "startTimeUnixMs", out var startUnixMs)
            || !TryReadCanonicalInt64(root, "endTimeUnixMs", out var endUnixMs)
            || startUnixMs < 0
            || endUnixMs <= startUnixMs)
        {
            return false;
        }

        CaptureProcessTelemetry? processTelemetry = null;
        if (schemaVersion == 4)
        {
            var application = root.GetProperty("application");
            if (application.ValueKind == JsonValueKind.Object)
            {
                if (!HasExactProperties(
                        application,
                        "processName",
                        "processId",
                        "cpuUsageBasisPoints",
                        "workingSetBytes",
                        "privateMemoryBytes")
                    || !TryReadString(application, "processName", out var processName)
                    || !TryReadCanonicalUInt32(application, "processId", out var processId)
                    || !TryReadCanonicalUInt32(
                        application,
                        "cpuUsageBasisPoints",
                        out var cpuUsageBasisPoints)
                    || !TryReadCanonicalUInt64(
                        application,
                        "workingSetBytes",
                        out var workingSetBytes)
                    || !TryReadCanonicalUInt64(
                        application,
                        "privateMemoryBytes",
                        out var privateMemoryBytes)
                    || workingSetBytes > long.MaxValue
                    || privateMemoryBytes > long.MaxValue)
                {
                    return false;
                }
                processTelemetry = new CaptureProcessTelemetry(
                    processName,
                    processId,
                    cpuUsageBasisPoints,
                    checked((long)workingSetBytes),
                    checked((long)privateMemoryBytes));
            }
            else if (application.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
            if (processTelemetry is not null
                && captureScope == ContinuousDisplayCaptureScope)
            {
                return false;
            }
        }

        CaptureChunk.ValidateIdentifier(chunkId);
        var authorization = root.GetProperty("authorization");
        if (authorization.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                authorization,
                "persistenceGeneration",
                "targetEpoch")
            || !TryReadCanonicalUInt64(
                authorization,
                "persistenceGeneration",
                out var persistenceGeneration)
            || persistenceGeneration == 0
            || !TryReadCanonicalUInt64(
                authorization,
                "targetEpoch",
                out var targetEpoch)
            || targetEpoch == 0)
        {
            return false;
        }

        var frames = root.GetProperty("frames");
        if (frames.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
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
                "items")
            || !TryReadExactString(frames, "format", "jpeg")
            || !TryReadCanonicalUInt32(frames, "quality", out var quality)
            || quality != 82
            || !TryReadCanonicalUInt32(
                frames,
                "sampledFrameCount",
                out var capturedFrameCount)
            || capturedFrameCount == 0
            || !TryReadCanonicalUInt32(
                frames,
                "blackFrameCount",
                out var blackFrameCount)
            || !TryReadCanonicalUInt32(
                frames,
                "duplicateFrameCount",
                out var duplicateFrameCount)
            || !TryReadCanonicalUInt32(
                frames,
                "retainedFrameCount",
                out var frameCount)
            || frameCount > capturedFrameCount
            || frameCount > CaptureChunk.MaximumFramesPerChunk
            || (ulong)blackFrameCount + duplicateFrameCount + frameCount
                != capturedFrameCount
            || !TryReadCanonicalUInt32(frames, "width", out var width)
            || width < 2
            || (width & 1U) != 0
            || !TryReadCanonicalUInt32(frames, "height", out var height)
            || height < 2
            || (height & 1U) != 0
            || !TryReadCanonicalUInt64(
                frames,
                "totalByteCount",
                out var totalByteCount)
            || totalByteCount > CaptureChunk.MaximumFrameByteCount
            || (frameCount == 0 && totalByteCount != 0)
            || (frameCount != 0 && totalByteCount == 0)
            || frames.GetProperty("items").ValueKind != JsonValueKind.Array
            || frames.GetProperty("items").GetArrayLength() != frameCount)
        {
            return false;
        }

        var startUtc = DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs);
        var endUtc = DateTimeOffset.FromUnixTimeMilliseconds(endUnixMs);
        if (!TryReadContextSamples(
                root.GetProperty("contextSamples"),
                chunkId,
                TimeZoneInfo.ConvertTime(startUtc, captureTimeZone),
                TimeZoneInfo.ConvertTime(endUtc, captureTimeZone),
                capturedFrameCount,
                out var contextSamples))
        {
            return false;
        }
        manifest = new ParsedManifest(
            TimeZoneInfo.ConvertTime(startUtc, captureTimeZone),
            TimeZoneInfo.ConvertTime(endUtc, captureTimeZone),
            capturedFrameCount,
            frameCount,
            width,
            height,
            totalByteCount,
            persistenceGeneration,
            targetEpoch,
            processTelemetry,
            blackFrameCount,
            duplicateFrameCount,
            contextSamples);
        return true;
    }

    private static bool TryReadContextSamples(
        JsonElement element,
        string chunkId,
        DateTimeOffset start,
        DateTimeOffset end,
        uint sampledFrameCount,
        out IReadOnlyList<CaptureContextSample> samples)
    {
        samples = [];
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > sampledFrameCount)
        {
            return false;
        }

        var parsed = new List<CaptureContextSample>(element.GetArrayLength());
        uint? previousIndex = null;
        ulong? previousOffset = null;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !HasExactProperties(
                    item,
                    "sampleIndex",
                    "offsetMilliseconds",
                    "application")
                || !TryReadCanonicalUInt32(item, "sampleIndex", out var sampleIndex)
                || sampleIndex >= sampledFrameCount
                || !TryReadCanonicalUInt64(
                    item,
                    "offsetMilliseconds",
                    out var offsetMilliseconds)
                || offsetMilliseconds >= (ulong)(end - start).TotalMilliseconds
                || previousIndex >= sampleIndex
                || previousOffset >= offsetMilliseconds)
            {
                return false;
            }

            CaptureContextApplication? application = null;
            var applicationElement = item.GetProperty("application");
            if (applicationElement.ValueKind == JsonValueKind.Object)
            {
                if (!HasExactProperties(
                        applicationElement,
                        "applicationId",
                        "displayName",
                        "processId",
                        "cpuUsageBasisPoints",
                        "workingSetBytes",
                        "privateMemoryBytes")
                    || !TryReadString(
                        applicationElement,
                        "applicationId",
                        out var serializedApplicationId)
                    || !TryReadString(
                        applicationElement,
                        "displayName",
                        out var displayName)
                    || !TryReadCanonicalUInt32(
                        applicationElement,
                        "processId",
                        out var processId)
                    || !TryReadCanonicalUInt32(
                        applicationElement,
                        "cpuUsageBasisPoints",
                        out var cpuUsageBasisPoints)
                    || !TryReadCanonicalUInt64(
                        applicationElement,
                        "workingSetBytes",
                        out var workingSetBytes)
                    || !TryReadCanonicalUInt64(
                        applicationElement,
                        "privateMemoryBytes",
                        out var privateMemoryBytes)
                    || workingSetBytes > long.MaxValue
                    || privateMemoryBytes > long.MaxValue)
                {
                    return false;
                }

                var identityValue = displayName;
                if (!CaptureExclusionRule.TryNormalizeApplicationIdentity(
                        ApplicationIdentityKind.ExecutableName,
                        identityValue,
                        out identityValue))
                {
                    return false;
                }
                var applicationId = $"process:{identityValue.ToLowerInvariant()}";
                if (!string.Equals(
                        serializedApplicationId,
                        applicationId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                application = new CaptureContextApplication(
                    applicationId,
                    displayName,
                    ApplicationIdentityKind.ExecutableName,
                    identityValue,
                    processId,
                    cpuUsageBasisPoints,
                    checked((long)workingSetBytes),
                    checked((long)privateMemoryBytes));
            }
            else if (applicationElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }

            parsed.Add(new CaptureContextSample(
                chunkId,
                checked((int)sampleIndex),
                start.AddMilliseconds(offsetMilliseconds),
                application));
            previousIndex = sampleIndex;
            previousOffset = offsetMilliseconds;
        }

        samples = Array.AsReadOnly(parsed.ToArray());
        return true;
    }

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expectedNames)
    {
        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)
                || !observed.Add(property.Name))
            {
                return false;
            }
        }

        return observed.SetEquals(expected);
    }

    private static bool TryReadString(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        var property = parent.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.String
            && property.GetString() is { } parsed)
        {
            value = parsed;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadExactString(
        JsonElement parent,
        string propertyName,
        string expected) =>
        TryReadString(parent, propertyName, out var actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryReadCanonicalInt64(
        JsonElement parent,
        string propertyName,
        out long value)
    {
        value = default;
        var element = parent.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Number
            && IsCanonicalUnsignedInteger(element.GetRawText())
            && element.TryGetInt64(out value);
    }

    private static bool TryReadCanonicalUInt32(
        JsonElement parent,
        string propertyName,
        out uint value)
    {
        value = default;
        var element = parent.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Number
            && IsCanonicalUnsignedInteger(element.GetRawText())
            && element.TryGetUInt32(out value);
    }

    private static bool TryReadCanonicalUInt64(
        JsonElement parent,
        string propertyName,
        out ulong value)
    {
        value = default;
        var element = parent.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Number
            && IsCanonicalUnsignedInteger(element.GetRawText())
            && element.TryGetUInt64(out value);
    }

    private static bool IsCanonicalUnsignedInteger(string rawValue)
    {
        if (rawValue.Length == 0
            || (rawValue.Length > 1 && rawValue[0] == '0'))
        {
            return false;
        }

        return rawValue.All(static character => character is >= '0' and <= '9');
    }

    private static DateTimeOffset GetCommittedAtUtc(
        long directoryLastWriteTimeFileTime,
        long manifestLastWriteTimeFileTime)
    {
        var committedFileTime = Math.Max(
            directoryLastWriteTimeFileTime,
            manifestLastWriteTimeFileTime);
        return DateTimeOffset.FromFileTime(committedFileTime).ToUniversalTime();
    }

    private static bool TryReadDirectorySnapshot(
        string path,
        out FileSnapshot snapshot)
    {
        snapshot = default;
        using var handle = TryOpenPath(path, isDirectory: true, readContent: false);
        return handle is not null
            && TryReadSnapshot(handle, out snapshot)
            && snapshot.IsDirectory;
    }

    private bool RootsMatch(
        FileSnapshot expectedDataRoot,
        FileSnapshot expectedChunksRoot) =>
        TryReadDirectorySnapshot(_dataRootPath, out var currentDataRoot)
        && expectedDataRoot.IsStable(currentDataRoot)
        && TryReadDirectorySnapshot(_chunksRootPath, out var currentChunksRoot)
        && expectedChunksRoot.IsStable(currentChunksRoot);

    private static bool TryReopenAndMatch(
        string path,
        FileSnapshot expected,
        bool readContent)
    {
        using var reopened = TryOpenPath(path, isDirectory: false, readContent);
        return reopened is not null
            && TryReadSnapshot(reopened, out var reopenedSnapshot)
            && expected.IsStable(reopenedSnapshot);
    }

    private static SafeFileHandle? TryOpenPath(
        string path,
        bool isDirectory,
        bool readContent)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }

                return File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.None);
            }

            var flags = FileFlagOpenReparsePoint;
            if (isDirectory)
            {
                flags |= FileFlagBackupSemantics;
            }

            var handle = CreateFile(
                path,
                readContent ? GenericRead : FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }
        catch (Exception exception) when (IsRecoverableFileSystemFailure(exception))
        {
            return null;
        }
    }

    private static bool TryReadSnapshot(
        SafeFileHandle handle,
        out FileSnapshot snapshot)
    {
        snapshot = default;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                return false;
            }

            var attributes = (FileAttributes)information.FileAttributes;
            var unsignedLength = ((ulong)information.FileSizeHigh << 32)
                | information.FileSizeLow;
            var unsignedLastWriteTime = ((ulong)information.LastWriteTime.High << 32)
                | information.LastWriteTime.Low;
            if (unsignedLength > long.MaxValue
                || unsignedLastWriteTime > long.MaxValue)
            {
                return false;
            }

            var length = (long)unsignedLength;
            var fileIndex = ((ulong)information.FileIndexHigh << 32)
                | information.FileIndexLow;
            var lastWriteTime = (long)unsignedLastWriteTime;
            snapshot = new FileSnapshot(
                information.VolumeSerialNumber,
                fileIndex,
                length,
                lastWriteTime,
                attributes);
            return true;
        }

        try
        {
            var length = RandomAccess.GetLength(handle);
            snapshot = new FileSnapshot(
                0,
                0,
                length,
                DateTimeOffset.UtcNow.ToFileTime(),
                FileAttributes.Normal);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static bool IsRecoverableFileSystemFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct StableManifestRead(
        byte[] Content,
        FileSnapshot Snapshot);

    private readonly record struct ParsedManifest(
        DateTimeOffset StartTimeUtc,
        DateTimeOffset EndTimeUtc,
        uint CapturedFrameCount,
        uint FrameCount,
        uint Width,
        uint Height,
        ulong TotalByteCount,
        ulong PersistenceGeneration,
        ulong TargetEpoch,
        CaptureProcessTelemetry? ProcessTelemetry,
        uint BlackFrameCount,
        uint DuplicateFrameCount,
        IReadOnlyList<CaptureContextSample> ContextSamples);

    private readonly record struct FileSnapshot(
        uint VolumeSerialNumber,
        ulong FileIndex,
        long Length,
        long LastWriteTimeFileTime,
        FileAttributes Attributes)
    {
        public bool IsDirectory =>
            (Attributes & FileAttributes.Directory) != 0
            && (Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

        public bool IsRegularFile =>
            (Attributes
                & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint
                    | FileAttributes.Device)) == 0;

        public bool IsStable(FileSnapshot other) =>
            IsSameEntryAndTimestamp(other)
            && Length == other.Length;

        public bool IsSameEntryAndTimestamp(FileSnapshot other) =>
            VolumeSerialNumber == other.VolumeSerialNumber
            && FileIndex == other.FileIndex
            && LastWriteTimeFileTime == other.LastWriteTimeFileTime
            && Attributes == other.Attributes;
    }
}

internal enum CaptureManifestScanCheckpoint
{
    RootsInspected,
    CandidateInspected,
    ManifestRead,
    FramesInspected,
}

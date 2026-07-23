using WinDayFlow.Infrastructure.Capture;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class CaptureManifestScannerTests
{
    private static readonly DateTimeOffset IngestedAt =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartupScanFindsCommittedChunksWithoutAnEvent()
    {
        using var directory = new TemporaryDirectory();
        var firstCommittedAt = new DateTimeOffset(
            2026,
            7,
            23,
            10,
            0,
            5,
            TimeSpan.Zero);
        CreateValidChunk(
            directory.Path,
            "chunk-b",
            persistenceGeneration: ulong.MaxValue,
            targetEpoch: ulong.MaxValue - 1,
            committedAtUtc: firstCommittedAt);
        CreateValidChunk(directory.Path, "chunk-a");
        var scanner = CreateScanner(directory.Path);

        var chunks = await scanner.ScanCommittedAsync();

        Assert.Equal(["chunk-a", "chunk-b"], chunks.Select(chunk => chunk.Id));
        var chunk = chunks[1];
        Assert.Equal("chunks/chunk-b/capture.mp4", chunk.VideoPath.Value);
        Assert.Equal("chunks/chunk-b/manifest.json", chunk.ManifestPath.Value);
        Assert.Equal(8, chunk.VideoByteCount);
        Assert.Equal(ulong.MaxValue, chunk.PersistenceGeneration);
        Assert.Equal(ulong.MaxValue - 1, chunk.TargetEpoch);
        Assert.Equal(firstCommittedAt, chunk.CommittedAtUtc);
        Assert.Equal(IngestedAt, chunk.IngestedAtUtc);
    }

    [Fact]
    public async Task CaptureRangeUsesLocalOffsetsWithoutChangingDstInstants()
    {
        using var directory = new TemporaryDirectory();
        var startUtc = new DateTimeOffset(
            2026,
            11,
            1,
            8,
            59,
            0,
            TimeSpan.Zero);
        var endUtc = startUtc.AddMinutes(2);
        CreateValidChunk(
            directory.Path,
            "chunk-dst",
            startTimeUnixMs: startUtc.ToUnixTimeMilliseconds(),
            endTimeUnixMs: endUtc.ToUnixTimeMilliseconds());
        var timeZone = CreatePacificTestTimeZone();

        var chunk = Assert.Single(
            await CreateScanner(
                    directory.Path,
                    captureTimeZone: timeZone)
                .ScanCommittedAsync());

        Assert.Equal(startUtc, chunk.Range.Start);
        Assert.Equal(endUtc, chunk.Range.End);
        Assert.Equal(TimeSpan.FromHours(-7), chunk.Range.Start.Offset);
        Assert.Equal(TimeSpan.FromHours(-8), chunk.Range.End.Offset);
        Assert.Equal(TimeSpan.FromMinutes(2), chunk.Range.Duration);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("schema")]
    [InlineData("scope")]
    [InlineData("traversal")]
    [InlineData("codec")]
    [InlineData("container")]
    [InlineData("fractional")]
    public async Task StrictSchemaRejectsMalformedOrNonCanonicalManifests(
        string mutation)
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateValidChunk(directory.Path, "chunk-strict");
        var manifest = File.ReadAllText(paths.ManifestPath);
        manifest = mutation switch
        {
            "malformed" => manifest[..^2],
            "duplicate" => manifest.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
                StringComparison.Ordinal),
            "unknown" => manifest.Replace(
                "\"captureScope\":",
                "\"unexpected\": true,\n  \"captureScope\":",
                StringComparison.Ordinal),
            "schema" => manifest.Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 2",
                StringComparison.Ordinal),
            "scope" => manifest.Replace(
                "authorized-foreground-display",
                "desktop",
                StringComparison.Ordinal),
            "traversal" => manifest.Replace(
                "\"path\": \"capture.mp4\"",
                "\"path\": \"../capture.mp4\"",
                StringComparison.Ordinal),
            "codec" => manifest.Replace(
                "\"codec\": \"h264\"",
                "\"codec\": \"H264\"",
                StringComparison.Ordinal),
            "container" => manifest.Replace(
                "\"container\": \"mp4\"",
                "\"container\": \"mkv\"",
                StringComparison.Ordinal),
            "fractional" => manifest.Replace(
                "\"frameCount\": 6",
                "\"frameCount\": 6.0",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        File.WriteAllText(paths.ManifestPath, manifest);

        Assert.Empty(await CreateScanner(directory.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task DuplicateOrUnknownNestedPropertiesAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var duplicate = CreateValidChunk(directory.Path, "chunk-duplicate");
        ReplaceInFile(
            duplicate.ManifestPath,
            "\"targetEpoch\": 11",
            "\"targetEpoch\": 11, \"targetEpoch\": 11");
        var unknown = CreateValidChunk(directory.Path, "chunk-unknown");
        ReplaceInFile(
            unknown.ManifestPath,
            "\"frameCount\": 6",
            "\"extra\": 1, \"frameCount\": 6");

        Assert.Empty(await CreateScanner(directory.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task DirectoryAndManifestChunkIdentifiersMustMatchExactly()
    {
        using var directory = new TemporaryDirectory();
        CreateValidChunk(directory.Path, "Chunk-upper");
        var mismatch = CreateValidChunk(directory.Path, "chunk-path");
        ReplaceInFile(
            mismatch.ManifestPath,
            "\"chunkId\": \"chunk-path\"",
            "\"chunkId\": \"chunk-other\"");

        Assert.Empty(await CreateScanner(directory.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task MissingNonRegularAndOversizedVideosAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var missing = CreateValidChunk(directory.Path, "chunk-missing");
        File.Delete(missing.VideoPath);

        var nonRegular = CreateValidChunk(directory.Path, "chunk-directory");
        File.Delete(nonRegular.VideoPath);
        Directory.CreateDirectory(nonRegular.VideoPath);

        var oversized = CreateValidChunk(directory.Path, "chunk-oversized");
        using (var stream = new FileStream(
                   oversized.VideoPath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.Read))
        {
            stream.SetLength(64L * 1024L * 1024L + 1);
        }

        Assert.Empty(await CreateScanner(directory.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task OversizedManifestIsRejectedBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateValidChunk(directory.Path, "chunk-large-manifest");
        File.WriteAllText(
            paths.ManifestPath,
            new string(' ', checked((int)CaptureManifestScanner.MaximumManifestByteCount + 1)));

        Assert.Empty(await CreateScanner(directory.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task ReparsePointCannotRedirectAChunkOutsideTheDataRoot()
    {
        using var directory = new TemporaryDirectory();
        var dataRoot = Path.Combine(directory.Path, "data");
        var chunksRoot = Path.Combine(dataRoot, "chunks");
        Directory.CreateDirectory(chunksRoot);
        var outsideDirectory = Path.Combine(directory.Path, "outside");
        CreateValidChunkDirectory(outsideDirectory, "chunk-link");
        var linkPath = Path.Combine(chunksRoot, "chunk-link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Empty(await CreateScanner(dataRoot).ScanCommittedAsync());
    }

    [Fact]
    public async Task ManifestSizeRaceIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateValidChunk(directory.Path, "chunk-manifest-race");
        var mutated = 0;
        var scanner = CreateScanner(
            directory.Path,
            (checkpoint, path) =>
            {
                if (checkpoint == CaptureManifestScanCheckpoint.ManifestRead
                    && Interlocked.Exchange(ref mutated, 1) == 0)
                {
                    File.AppendAllText(path, " ");
                }
            });

        Assert.Empty(await scanner.ScanCommittedAsync());
        Assert.Equal(1, Volatile.Read(ref mutated));
        Assert.True(new FileInfo(paths.ManifestPath).Length > 0);
    }

    [Fact]
    public async Task VideoSizeRaceIsRejected()
    {
        using var directory = new TemporaryDirectory();
        CreateValidChunk(directory.Path, "chunk-video-race");
        var mutated = 0;
        var scanner = CreateScanner(
            directory.Path,
            (checkpoint, path) =>
            {
                if (checkpoint == CaptureManifestScanCheckpoint.VideoInspected
                    && Interlocked.Exchange(ref mutated, 1) == 0)
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    stream.Position = stream.Length;
                    stream.WriteByte(0xFF);
                    stream.Flush(flushToDisk: true);
                }
            });

        Assert.Empty(await scanner.ScanCommittedAsync());
        Assert.Equal(1, Volatile.Read(ref mutated));
    }

    [Fact]
    public async Task DataRootIdentitySwapInvalidatesTheWholeScan()
    {
        using var directory = new TemporaryDirectory();
        var dataRoot = Path.Combine(directory.Path, "data");
        CreateValidChunk(dataRoot, "chunk-original");
        var swapped = 0;
        var scanner = CreateScanner(
            dataRoot,
            (checkpoint, _) =>
            {
                if (checkpoint != CaptureManifestScanCheckpoint.RootsInspected
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                Directory.Move(dataRoot, Path.Combine(directory.Path, "data-old"));
                CreateValidChunk(dataRoot, "chunk-replacement");
            });

        Assert.Empty(await scanner.ScanCommittedAsync());
        Assert.Equal(1, Volatile.Read(ref swapped));
    }

    [Fact]
    public async Task ChunksRootIdentitySwapInvalidatesTheWholeScan()
    {
        using var directory = new TemporaryDirectory();
        var dataRoot = Path.Combine(directory.Path, "data");
        CreateValidChunk(dataRoot, "chunk-original");
        var chunksRoot = Path.Combine(dataRoot, "chunks");
        var dataRootTimestamp = Directory.GetLastWriteTimeUtc(dataRoot);
        var swapped = 0;
        var scanner = CreateScanner(
            dataRoot,
            (checkpoint, _) =>
            {
                if (checkpoint != CaptureManifestScanCheckpoint.RootsInspected
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                Directory.Move(chunksRoot, Path.Combine(dataRoot, "chunks-old"));
                CreateValidChunk(dataRoot, "chunk-replacement");
                Directory.SetLastWriteTimeUtc(dataRoot, dataRootTimestamp);
            });

        Assert.Empty(await scanner.ScanCommittedAsync());
        Assert.Equal(1, Volatile.Read(ref swapped));
    }

    [Fact]
    public async Task CandidateIdentitySwapIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var dataRoot = Path.Combine(directory.Path, "data");
        var paths = CreateValidChunk(dataRoot, "chunk-swap");
        var chunksRoot = Path.Combine(dataRoot, "chunks");
        var chunksRootTimestamp = Directory.GetLastWriteTimeUtc(chunksRoot);
        var swapped = 0;
        var scanner = CreateScanner(
            dataRoot,
            (checkpoint, path) =>
            {
                if (checkpoint != CaptureManifestScanCheckpoint.CandidateInspected
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                Directory.Move(
                    path,
                    Path.Combine(chunksRoot, "chunk-swap-old"));
                CreateValidChunkDirectory(path, "chunk-swap");
                Directory.SetLastWriteTimeUtc(chunksRoot, chunksRootTimestamp);
            });

        Assert.Empty(await scanner.ScanCommittedAsync());
        Assert.Equal(1, Volatile.Read(ref swapped));
        Assert.True(File.Exists(paths.VideoPath));
    }

    [Fact]
    public async Task CancellationStopsAFullRescan()
    {
        using var directory = new TemporaryDirectory();
        CreateValidChunk(directory.Path, "chunk-cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateScanner(directory.Path).ScanCommittedAsync(cancellation.Token));
    }

    private static CaptureManifestScanner CreateScanner(
        string dataRoot,
        Action<CaptureManifestScanCheckpoint, string>? checkpoint = null,
        TimeZoneInfo? captureTimeZone = null) =>
        new(
            dataRoot,
            new FixedTimeProvider(IngestedAt),
            checkpoint,
            captureTimeZone);

    private static ChunkPaths CreateValidChunk(
        string dataRoot,
        string chunkId,
        ulong persistenceGeneration = 7,
        ulong targetEpoch = 11,
        DateTimeOffset? committedAtUtc = null,
        long startTimeUnixMs = 1_784_797_200_000,
        long endTimeUnixMs = 1_784_797_260_000)
    {
        var chunkDirectory = Path.Combine(dataRoot, "chunks", chunkId);
        return CreateValidChunkDirectory(
            chunkDirectory,
            chunkId,
            persistenceGeneration,
            targetEpoch,
            committedAtUtc,
            startTimeUnixMs,
            endTimeUnixMs);
    }

    private static ChunkPaths CreateValidChunkDirectory(
        string chunkDirectory,
        string chunkId,
        ulong persistenceGeneration = 7,
        ulong targetEpoch = 11,
        DateTimeOffset? committedAtUtc = null,
        long startTimeUnixMs = 1_784_797_200_000,
        long endTimeUnixMs = 1_784_797_260_000)
    {
        Directory.CreateDirectory(chunkDirectory);
        var videoPath = Path.Combine(chunkDirectory, "capture.mp4");
        var manifestPath = Path.Combine(chunkDirectory, "manifest.json");
        File.WriteAllBytes(videoPath, [0, 0, 0, 4, 0x66, 0x74, 0x79, 0x70]);
        File.WriteAllText(
            manifestPath,
            CreateManifest(
                chunkId,
                persistenceGeneration,
                targetEpoch,
                startTimeUnixMs,
                endTimeUnixMs));

        var committed = committedAtUtc
            ?? new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(manifestPath, committed.UtcDateTime);
        Directory.SetLastWriteTimeUtc(chunkDirectory, committed.UtcDateTime);
        return new ChunkPaths(chunkDirectory, manifestPath, videoPath);
    }

    private static string CreateManifest(
        string chunkId,
        ulong persistenceGeneration,
        ulong targetEpoch,
        long startTimeUnixMs,
        long endTimeUnixMs) => $$"""
        {
          "schemaVersion": 1,
          "captureScope": "authorized-foreground-display",
          "chunkId": "{{chunkId}}",
          "startTimeUnixMs": {{startTimeUnixMs}},
          "endTimeUnixMs": {{endTimeUnixMs}},
          "authorization": {
            "persistenceGeneration": {{persistenceGeneration}},
            "targetEpoch": {{targetEpoch}}
          },
          "video": {
            "path": "capture.mp4",
            "codec": "h264",
            "container": "mp4",
            "frameCount": 6,
            "width": 1920,
            "height": 1080,
            "frameRateNumerator": 1,
            "frameRateDenominator": 10
          }
        }
        """;

    private static TimeZoneInfo CreatePacificTestTimeZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            month: 3,
            week: 2,
            DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            month: 11,
            week: 1,
            DayOfWeek.Sunday);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "WinDayFlow-Test-Pacific",
            TimeSpan.FromHours(-8),
            "WinDayFlow Test Pacific",
            "WinDayFlow Test Pacific Standard",
            "WinDayFlow Test Pacific Daylight",
            [adjustmentRule]);
    }

    private static void ReplaceInFile(
        string path,
        string oldValue,
        string newValue)
    {
        var content = File.ReadAllText(path);
        Assert.Contains(oldValue, content, StringComparison.Ordinal);
        File.WriteAllText(
            path,
            content.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"WinDayFlow-CaptureManifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed record ChunkPaths(
        string DirectoryPath,
        string ManifestPath,
        string VideoPath);
}

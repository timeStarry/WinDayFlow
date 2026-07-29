using System.Security.Cryptography;
using WinDayFlow.Infrastructure.Capture;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class CaptureManifestScannerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(CaptureManifestScanner.ForegroundDisplayCaptureScope)]
    [InlineData(CaptureManifestScanner.ContinuousDisplayCaptureScope)]
    public async Task ScansCanonicalJpegChunk(string captureScope)
    {
        using var root = new TemporaryRoot();
        WriteChunk(root.Path, "chunk-valid", captureScope: captureScope);

        var chunk = Assert.Single(await CreateScanner(root.Path).ScanCommittedAsync());

        Assert.Equal("chunk-valid", chunk.Id);
        Assert.Equal("chunks/chunk-valid/manifest.json", chunk.ManifestPath.Value);
        Assert.Equal(2U, chunk.CapturedFrameCount);
        Assert.Equal(1U, chunk.FrameCount);
        Assert.Equal(1600U, chunk.FrameWidth);
        Assert.Equal(900U, chunk.FrameHeight);
        Assert.Equal(4, chunk.FrameByteCount);
        Assert.Equal(Start, chunk.Range.Start);
        Assert.Equal(Start.AddMinutes(1), chunk.Range.End);
    }

    [Fact]
    public async Task ScansVersionThreeProcessTelemetry()
    {
        using var root = new TemporaryRoot();
        var paths = WriteChunk(root.Path, "chunk-telemetry");
        File.WriteAllText(
            paths.ManifestPath,
            File.ReadAllText(paths.ManifestPath)
                .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal)
                .Replace(
                    "\"authorization\": {\"persistenceGeneration\": 7, \"targetEpoch\": 11},",
                    "\"authorization\": {\"persistenceGeneration\": 7, \"targetEpoch\": 11},\n  \"application\": {\"processName\":\"Code.exe\",\"processId\":4242,\"cpuUsageBasisPoints\":1250,\"workingSetBytes\":536870912,\"privateMemoryBytes\":402653184},",
                    StringComparison.Ordinal));

        var chunk = Assert.Single(await CreateScanner(root.Path).ScanCommittedAsync());
        var telemetry = Assert.IsType<WinDayFlow.Domain.CaptureProcessTelemetry>(
            chunk.ProcessTelemetry);
        Assert.Equal("Code.exe", telemetry.ProcessName);
        Assert.Equal(4242U, telemetry.ProcessId);
        Assert.Equal(12.5, telemetry.CpuUsagePercent);
        Assert.Equal(536_870_912, telemetry.WorkingSetBytes);
        Assert.Equal(402_653_184, telemetry.PrivateMemoryBytes);
    }

    [Fact]
    public async Task IgnoresPrivateStagingDirectories()
    {
        using var root = new TemporaryRoot();
        WriteChunk(Path.Combine(root.Path, ".staging"), "chunk-private");

        Assert.Empty(await CreateScanner(root.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task RejectsMissingOrCorruptFrame()
    {
        using var root = new TemporaryRoot();
        var missing = WriteChunk(root.Path, "chunk-missing");
        var corrupt = WriteChunk(root.Path, "chunk-corrupt");
        File.Delete(missing.FramePath);
        File.WriteAllBytes(corrupt.FramePath, [0xff, 0xd8, 0xfe, 0xd9]);

        Assert.Empty(await CreateScanner(root.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task RejectsLegacySchemaAndUnknownProperties()
    {
        using var root = new TemporaryRoot();
        var legacy = WriteChunk(root.Path, "chunk-legacy");
        var extra = WriteChunk(root.Path, "chunk-extra");
        File.WriteAllText(
            legacy.ManifestPath,
            File.ReadAllText(legacy.ManifestPath)
                .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal));
        File.WriteAllText(
            extra.ManifestPath,
            File.ReadAllText(extra.ManifestPath)
                .Replace("\"captureScope\":", "\"unexpected\": true,\n  \"captureScope\":", StringComparison.Ordinal));

        Assert.Empty(await CreateScanner(root.Path).ScanCommittedAsync());
    }

    [Fact]
    public async Task ReturnsChunksInCanonicalDirectoryOrder()
    {
        using var root = new TemporaryRoot();
        WriteChunk(root.Path, "chunk-b");
        WriteChunk(root.Path, "chunk-a");

        var chunks = await CreateScanner(root.Path).ScanCommittedAsync();

        Assert.Equal(["chunk-a", "chunk-b"], chunks.Select(static chunk => chunk.Id));
    }

    private static CaptureManifestScanner CreateScanner(string root) => new(
        root,
        TimeProvider.System,
        TimeZoneInfo.CreateCustomTimeZone(
            "WinDayFlow-Scanner-UTC+08",
            TimeSpan.FromHours(8),
            "UTC+08",
            "UTC+08"));

    private static ChunkPaths WriteChunk(
        string dataRoot,
        string chunkId,
        string captureScope = CaptureManifestScanner.ForegroundDisplayCaptureScope)
    {
        var chunkDirectory = Path.Combine(dataRoot, "chunks", chunkId);
        var framesDirectory = Path.Combine(chunkDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);
        var framePath = Path.Combine(framesDirectory, "frame-000000.jpg");
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        File.WriteAllBytes(framePath, jpeg);
        var manifestPath = Path.Combine(chunkDirectory, "manifest.json");
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "schemaVersion": 2,
              "captureScope": "{{captureScope}}",
              "chunkId": "{{chunkId}}",
              "startTimeUnixMs": {{Start.ToUnixTimeMilliseconds()}},
              "endTimeUnixMs": {{Start.AddMinutes(1).ToUnixTimeMilliseconds()}},
              "authorization": {"persistenceGeneration": 7, "targetEpoch": 11},
              "frames": {
                "format": "jpeg",
                "quality": 82,
                "capturedFrameCount": 2,
                "retainedFrameCount": 1,
                "width": 1600,
                "height": 900,
                "totalByteCount": 4,
                "items": [{"id":"frame-000000","index":0,"path":"frames/frame-000000.jpg","offsetMilliseconds":30000,"byteCount":4,"sha256":"{{Convert.ToHexString(SHA256.HashData(jpeg))}}"}]
              }
            }
            """);
        return new ChunkPaths(manifestPath, framePath);
    }

    private sealed record ChunkPaths(string ManifestPath, string FramePath);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinDayFlow.ManifestScanner.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

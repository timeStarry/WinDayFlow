using System.Text.Json;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class CaptureDiagnosticLogTests
{
    [Fact]
    public void WritesOnlyFixedAllowlistedNumericFields()
    {
        WithTemporaryLog((log, _) =>
        {
            log.Write(
                CaptureDiagnosticEvent.PrivacySampled,
                new(CaptureDiagnosticField.Generation, 7),
                new((CaptureDiagnosticField)int.MaxValue, 99));

            var text = File.ReadAllText(log.FilePath);
            using var record = JsonDocument.Parse(text);
            var root = record.RootElement;
            Assert.Equal("privacy_sampled", root.GetProperty("event").GetString());
            Assert.Equal(7, root.GetProperty("generation").GetInt64());
            Assert.Equal(JsonValueKind.String, root.GetProperty("timestampUtc").ValueKind);
            Assert.Equal(3, root.EnumerateObject().Count());
            Assert.DoesNotContain("windowTitle", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void RotatesAtTheConfiguredBoundAndRetainsOnlyRequestedBackups()
    {
        WithTemporaryLog((_, filePath) =>
        {
            var log = CaptureDiagnosticLog.CreateForTests(
                filePath,
                maximumBytes: 256,
                backupCount: 3);
            for (var index = 0; index < 30; index++)
            {
                log.Write(
                    CaptureDiagnosticEvent.BackendStatusChanged,
                    new CaptureDiagnosticFieldValue(
                        CaptureDiagnosticField.Sequence,
                        index));
            }

            Assert.True(File.Exists(filePath));
            Assert.True(File.Exists($"{filePath}.1"));
            Assert.True(File.Exists($"{filePath}.2"));
            Assert.True(File.Exists($"{filePath}.3"));
            Assert.False(File.Exists($"{filePath}.4"));
            foreach (var path in Directory.EnumerateFiles(
                         Path.GetDirectoryName(filePath)!))
            {
                Assert.InRange(new FileInfo(path).Length, 1, 256);
            }
        });
    }

    [Fact]
    public void ConcurrentWritesProduceCompleteJsonLines()
    {
        WithTemporaryLog((log, _) =>
        {
            Parallel.For(
                0,
                200,
                index => log.Write(
                    CaptureDiagnosticEvent.PrivacyPublished,
                    new(CaptureDiagnosticField.Generation, index),
                    new(CaptureDiagnosticField.Accepted, index % 2)));

            var lines = File.ReadAllLines(log.FilePath);
            Assert.Equal(200, lines.Length);
            foreach (var line in lines)
            {
                using var record = JsonDocument.Parse(line);
                Assert.Equal(
                    "privacy_published",
                    record.RootElement.GetProperty("event").GetString());
            }
        });
    }

    [Fact]
    public void ReleasesTheFileAfterEachWrite()
    {
        WithTemporaryLog((log, filePath) =>
        {
            log.Write(
                CaptureDiagnosticEvent.PrivacyInvalidated,
                new CaptureDiagnosticFieldValue(
                    CaptureDiagnosticField.Generation,
                    1));

            var movedPath = $"{filePath}.moved";
            File.Move(filePath, movedPath);
            Assert.True(File.Exists(movedPath));
        });
    }

    private static void WithTemporaryLog(
        Action<CaptureDiagnosticLog, string> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WinDayFlow-diagnostics-{Guid.NewGuid():N}");
        var filePath = Path.Combine(root, "capture.jsonl");
        try
        {
            var log = CaptureDiagnosticLog.CreateForTests(
                filePath,
                maximumBytes: 1024 * 1024,
                backupCount: 3);
            action(log, filePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

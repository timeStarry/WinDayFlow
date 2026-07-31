using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Capture;
using WinDayFlow.Infrastructure.Persistence;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class SqliteCaptureContextStoreTests
{
    [Fact]
    public async Task ReplaceAsyncPersistsObservedAndApplicationRuleMatches()
    {
        using var root = new TemporaryRoot();
        var factory = new SqliteConnectionFactory(root.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        var chunk = CreateChunk();
        await new SqliteCaptureAnalysisStore(factory, root.RootPath)
            .IngestCommittedAsync(chunk);

        var applicationRule = CaptureExclusionRule.Create(
            Guid.Parse("a7bb7db6-eed6-4f68-adce-597e2b524258"),
            "Password manager",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe");
        var windowRule = new CaptureExclusionRule(
            Guid.Parse("99c15b93-1019-40e8-9432-c16c82d9e776"),
            "Credential window",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            "API key",
            revision: 4);
        var application = new CaptureContextApplication(
            "process:keepassxc.exe",
            "KeePassXC.exe",
            ApplicationIdentityKind.ExecutableName,
            "KeePassXC.exe",
            processId: 42,
            cpuUsageBasisPoints: 125,
            workingSetBytes: 64 * 1024 * 1024,
            privateMemoryBytes: 48 * 1024 * 1024);
        var samples = new[]
        {
            new CaptureContextSample(
                chunk.Id,
                ordinal: 0,
                chunk.Range.Start.AddSeconds(5),
                application,
                [
                    new CaptureContextRuleMatch(windowRule.Id, windowRule.Revision),
                    new CaptureContextRuleMatch(applicationRule.Id, applicationRule.Revision),
                ],
                evaluatedRuleSetRevision: 7,
                applicationContextAvailable: true,
                windowContextAvailable: true),
        };

        var store = new SqliteCaptureContextStore(factory);
        await store.ReplaceAsync(
            chunk,
            samples,
            new CaptureExclusionRuleSet([applicationRule, windowRule]));

        var persisted = Assert.Single(await store.ListAsync(chunk.Id));
        Assert.Equal(application, persisted.Application);
        Assert.Equal(7, persisted.EvaluatedRuleSetRevision);
        Assert.True(persisted.ApplicationContextAvailable);
        Assert.True(persisted.WindowContextAvailable);
        Assert.Equal(2, persisted.RuleMatches.Count);
        Assert.Contains(
            persisted.RuleMatches,
            match => match.RuleId == applicationRule.Id
                && match.RuleRevision == applicationRule.Revision);
        Assert.Contains(
            persisted.RuleMatches,
            match => match.RuleId == windowRule.Id
                && match.RuleRevision == windowRule.Revision);
    }

    private static CaptureChunk CreateChunk()
    {
        var start = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
        return new CaptureChunk(
            "context-chunk",
            new EvidenceRelativePath("chunks/context-chunk/manifest.json"),
            new TimeRange(start, start.AddMinutes(15)),
            capturedFrameCount: 1,
            frameCount: 1,
            frameWidth: 1920,
            frameHeight: 1080,
            frameByteCount: 1024,
            persistenceGeneration: 1,
            targetEpoch: 1,
            committedAtUtc: start.AddMinutes(15),
            ingestedAtUtc: start.AddMinutes(15));
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "windayflow-context-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath => Path.Combine(RootPath, "windayflow.db");

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}

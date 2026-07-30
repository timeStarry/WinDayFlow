using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Statistics;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Ai;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Statistics;
using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Statistics;

public sealed class SqliteStatisticsServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAsyncUsesUnionsNormalizedTimelineLedgerAndStorageCategories()
    {
        using var root = new TemporaryRoot();
        var factory = new SqliteConnectionFactory(root.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        await SeedCaptureAsync(factory, root.RootPath);
        await SeedTimelineAsync(factory);
        await SeedInvocationsAsync(factory);
        root.CreateStorageFixtures();

        var service = new SqliteStatisticsService(
            factory,
            root.RootPath,
            new FixedTimeProvider(Now));

        var snapshot = await service.GetAsync(StatisticsRange.Today);

        Assert.Equal(TimeSpan.FromMinutes(25), snapshot.RecordedDuration);
        Assert.Equal(1, snapshot.ActiveDayCount);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.FocusedDuration);
        Assert.Collection(
            snapshot.Categories,
            focused =>
            {
                Assert.Equal(ActivityCategory.FocusedWork, focused.Key);
                Assert.Equal(TimeSpan.FromMinutes(20), focused.Duration);
            },
            research =>
            {
                Assert.Equal(ActivityCategory.Research, research.Key);
                Assert.Equal(TimeSpan.FromMinutes(10), research.Duration);
            });
        Assert.Collection(
            snapshot.Productivity,
            focused =>
            {
                Assert.Equal(ProductivityKind.Focused, focused.Key);
                Assert.Equal(TimeSpan.FromMinutes(20), focused.Duration);
            },
            neutral =>
            {
                Assert.Equal(ProductivityKind.Neutral, neutral.Key);
                Assert.Equal(TimeSpan.FromMinutes(10), neutral.Duration);
            });
        Assert.Equal(16, snapshot.CaptureFilters.SampledCount);
        Assert.Equal(3, snapshot.CaptureFilters.BlackFrameCount);
        Assert.Equal(4, snapshot.CaptureFilters.DuplicateFrameCount);
        Assert.Equal(9, snapshot.CaptureFilters.RetainedFrameCount);
        Assert.Equal(9d / 16d, snapshot.CaptureFilters.RetentionRate, precision: 10);
        Assert.Equal(2, snapshot.ProviderInvocations.InvocationCount);
        Assert.Equal(1, snapshot.ProviderInvocations.SuccessfulCount);
        Assert.Equal(0.5, snapshot.ProviderInvocations.SuccessRate);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.ProviderInvocations.AverageLatency);
        Assert.Null(snapshot.ProviderInvocations.InputTokens);
        Assert.Null(snapshot.ProviderInvocations.OutputTokens);
        Assert.Equal(3, snapshot.Storage.RawCaptureBytes);
        Assert.Equal(5, snapshot.Storage.ScreeningBytes);
        Assert.Equal(7, snapshot.Storage.ApplicationCacheBytes);
        Assert.Equal(11, snapshot.Storage.LogBytes);
        Assert.Equal(13, snapshot.Storage.InAppExportBytes);
        Assert.True(snapshot.Storage.DatabaseBytes > 0);
    }

    [Fact]
    public async Task GetAsyncHonorsPreCancelledStorageScan()
    {
        using var root = new TemporaryRoot();
        var factory = new SqliteConnectionFactory(root.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var service = new SqliteStatisticsService(
            factory,
            root.RootPath,
            new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetAsync(StatisticsRange.All, cancellation.Token));
    }

    private static async Task SeedCaptureAsync(
        SqliteConnectionFactory factory,
        string rootPath)
    {
        var store = new SqliteCaptureAnalysisStore(factory, rootPath);
        await store.IngestCommittedAsync(CreateChunk(
            "statistics-a",
            Now.Date.AddHours(8),
            Now.Date.AddHours(8).AddMinutes(15),
            captured: 10,
            retained: 5,
            black: 2,
            duplicate: 3,
            generation: 1));
        await store.IngestCommittedAsync(CreateChunk(
            "statistics-b",
            Now.Date.AddHours(8).AddMinutes(10),
            Now.Date.AddHours(8).AddMinutes(25),
            captured: 6,
            retained: 4,
            black: 1,
            duplicate: 1,
            generation: 2));
    }

    private static CaptureChunk CreateChunk(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        uint captured,
        uint retained,
        uint black,
        uint duplicate,
        ulong generation) => new(
            id,
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, end),
            captured,
            retained,
            frameWidth: 1920,
            frameHeight: 1080,
            frameByteCount: retained * 1024,
            persistenceGeneration: generation,
            targetEpoch: 1,
            committedAtUtc: end,
            ingestedAtUtc: end,
            blackFrameCount: black,
            duplicateFrameCount: duplicate);

    private static async Task SeedTimelineAsync(SqliteConnectionFactory factory)
    {
        var repository = new SqliteTimelineRepository(factory);
        var firstStart = Now.Date.AddHours(8);
        await repository.AddAsync(TimelineEntry.CreateManual(
            Guid.Parse("39c23af4-23a3-48a1-b407-77e6eed17398"),
            new TimeRange(firstStart, firstStart.AddMinutes(20)),
            "Focused work",
            "Fixture",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            [],
            Now));
        var secondStart = firstStart.AddMinutes(10);
        await repository.AddAsync(TimelineEntry.CreateManual(
            Guid.Parse("8d58e300-8134-424f-bc4a-e2fcd3215d50"),
            new TimeRange(secondStart, secondStart.AddMinutes(20)),
            "Research",
            "Fixture",
            ActivityCategory.Research,
            ProductivityKind.Neutral,
            [],
            Now));
    }

    private static async Task SeedInvocationsAsync(SqliteConnectionFactory factory)
    {
        var profileId = Guid.Parse("a66e7087-f980-4ba6-8221-3b5961195204");
        await new SqliteAiProviderProfileStore(factory).CreateAsync(
            new AiProviderProfile(
                profileId,
                "Local fixture",
                AiProviderKind.OpenAiCompatible,
                new Uri("http://127.0.0.1:11434/v1/"),
                "vision",
                TimeSpan.FromSeconds(30)),
            AiProviderCredentialUpdate.Clear,
            Now.AddHours(-1));

        var store = new SqliteProviderInvocationStore(factory);
        await AddInvocationAsync(
            store,
            Guid.Parse("86bfe2ad-d833-4ef7-b135-29628ca7eb6b"),
            profileId,
            Now.AddMinutes(-10),
            TimeSpan.FromSeconds(2),
            ProviderInvocationOutcome.Succeeded,
            new ProviderInvocationUsage(100, 20));
        await AddInvocationAsync(
            store,
            Guid.Parse("ce4d4e72-88c5-488f-9615-f6f027f4aa42"),
            profileId,
            Now.AddMinutes(-5),
            TimeSpan.FromSeconds(4),
            ProviderInvocationOutcome.FailedRetryable,
            usage: null);
    }

    private static async Task AddInvocationAsync(
        SqliteProviderInvocationStore store,
        Guid id,
        Guid profileId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ProviderInvocationOutcome outcome,
        ProviderInvocationUsage? usage)
    {
        await store.StartAsync(new ProviderInvocationStart(
            id,
            AnalysisStage.TimelineAnalysis,
            profileId,
            ProviderProfileRevision: 1,
            RouteRevision: 1,
            "http://127.0.0.1:11434",
            new string('A', 64),
            ItemCount: 1,
            ByteCount: 1024,
            startedAt,
            Guid.NewGuid()));
        await store.CompleteAsync(id, outcome, usage, startedAt.Add(duration));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "windayflow-statistics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath => Path.Combine(RootPath, "windayflow.db");

        public void CreateStorageFixtures()
        {
            WriteFixture("chunks", 3);
            WriteFixture("screenings", 5);
            WriteFixture("cache", 7);
            WriteFixture("logs", 11);
            WriteFixture("exports", 13);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private void WriteFixture(string directory, int byteCount)
        {
            var path = Path.Combine(RootPath, directory);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "fixture.bin"), new byte[byteCount]);
        }
    }
}
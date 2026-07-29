using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Ai;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Capture;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Analysis;

public sealed class AnalysisPipelineEndToEndTests
{
    private const string ChunkId = "chunk-e2e-0001";
    private const string EvidenceFrameId = "frame-000000";
    private const string GeneratedTitle = "Implement the analysis pipeline";
    private const string EditedTitle = "Review the completed analysis pipeline";
    private const string ForegroundDisplayCaptureScope =
        "authorized-foreground-display";
    private const string ContinuousDisplayCaptureScope =
        "authorized-display-continuous";

    private static readonly DateTimeOffset ChunkStart =
        new(2026, 7, 23, 9, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan PipelineWaitTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ForegroundDisplayCaptureScope)]
    [InlineData(ContinuousDisplayCaptureScope)]
    public async Task CloudEnableBackfillsExistingChunkAndCommitsTimelineOnce(
        string captureScope)
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateCommittedChunk(
            ChunkId,
            ChunkStart,
            ChunkStart.AddMinutes(1),
            captureScope);

        var timeProvider = new FixedTimeProvider(Now);
        var connectionFactory = new SqliteConnectionFactory(workspace.DatabasePath);
        await new SqliteDatabaseInitializer(connectionFactory, timeProvider)
            .InitializeAsync();
        using var settings = new AppSettingsService(
            new SqliteAppSettingsRepository(connectionFactory),
            timeProvider);
        await settings.InitializeAsync();

        var transport = new FakeOpenAiTransport();
        var profileStore = new SqliteAiProviderProfileStore(connectionFactory);
        var providerFactory = new OpenAiCompatibleProviderFactory(
            profileStore,
            transport.CreateHandler,
            timeProvider);
        using var configuration = new AiProviderConfigurationService(
            profileStore,
            providerFactory,
            settings,
            timeProvider);
        await configuration.InitializeAsync();

        var store = new SqliteCaptureAnalysisStore(
            connectionFactory,
            workspace.EvidenceRoot);
        var nativeEvidence = CreateEvidenceServices(workspace.EvidenceRoot);
        using var ingestion = new CaptureAnalysisIngestionService(
            CreateScanner(workspace.EvidenceRoot, timeProvider),
            store,
            store,
            nativeEvidence.FingerprintProvider,
            profileStore,
            settings,
            timeProvider: timeProvider);
        var processor = CreateProcessor(
            store,
            profileStore,
            providerFactory,
            nativeEvidence.EvidenceExtractor,
            settings,
            connectionFactory,
            timeProvider,
            "analysis-e2e-cloud-enable");
        var supervisor = new AnalysisPipelineSupervisor(
            store,
            ingestion,
            processor,
            timeProvider: timeProvider);
        await using var runner = new AnalysisPipelineBackgroundRunner(
            supervisor,
            new UnavailableCaptureBackend(),
            settings,
            configuration,
            new AnalysisPipelineBackgroundRunnerOptions(TimeSpan.FromDays(1)),
            timeProvider);

        await runner.StartAsync();
        await WaitForPipelineCountsAsync(
            connectionFactory,
            new PipelineCounts(1, 0, 0, 0));

        Assert.False(settings.Current.CloudAnalysisEnabled);
        Assert.Equal(0, nativeEvidence.FingerprintCallCount);
        Assert.Empty(transport.Requests);

        _ = await configuration.SaveAsync(
            "Local integration provider",
            "http://127.0.0.1:11434/v1",
            "vision-e2e",
            requestTimeoutSeconds: 30,
            replacementApiKey: null);
        var validated = await configuration.TestConnectionAsync();
        await configuration.SetCloudAnalysisEnabledAsync(enabled: true);

        Assert.True(validated.IsValidated);
        Assert.True(settings.Current.CloudAnalysisEnabled);
        await WaitForPipelineCountsAsync(
            connectionFactory,
            new PipelineCounts(1, 1, 1, 1));
        await runner.StopAsync().WaitAsync(PipelineWaitTimeout);

        Assert.True(nativeEvidence.FingerprintCallCount >= 1);
        Assert.Equal(1, nativeEvidence.ExtractionCallCount);
        Assert.Equal(1, nativeEvidence.FrameReadCallCount);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("synthetic-frame", transport.Requests[0].FrameId);
        Assert.Equal(EvidenceFrameId, transport.Requests[1].FrameId);
        Assert.Equal(
            new PipelineCounts(1, 1, 1, 1),
            await ReadPipelineCountsAsync(connectionFactory));

        var timeline = new TimelineQueryService(
            new SqliteTimelineRepository(connectionFactory));
        var generated = Assert.Single(await timeline.GetForDayAsync(
            DateOnly.FromDateTime(ChunkStart.DateTime)));
        Assert.Equal(TimelineEntryOrigin.Analyzed, generated.Origin);
        Assert.Equal(ChunkId, generated.Evidence?.CaptureChunkId);

        var fingerprintCallCountAfterAnalysis = nativeEvidence.FingerprintCallCount;
        var repeatedRun = await supervisor.RunOnceAsync();

        Assert.Equal(
            new CaptureAnalysisIngestionResult(1, 0, 0, AnalysisReady: true),
            repeatedRun.Ingestion);
        Assert.Equal(0, repeatedRun.ProcessedJobCount);
        Assert.Equal(
            fingerprintCallCountAfterAnalysis + 1,
            nativeEvidence.FingerprintCallCount);
        Assert.Equal(1, nativeEvidence.ExtractionCallCount);
        Assert.Equal(1, nativeEvidence.FrameReadCallCount);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(
            new PipelineCounts(1, 1, 1, 1),
            await ReadPipelineCountsAsync(connectionFactory));
        Assert.Equal(
            generated.Id,
            Assert.Single(await timeline.GetForDayAsync(
                DateOnly.FromDateTime(ChunkStart.DateTime))).Id);
    }

    [Theory]
    [InlineData(ForegroundDisplayCaptureScope)]
    [InlineData(ContinuousDisplayCaptureScope)]
    public async Task ProviderRevisionChangeDoesNotReanalyzeCompletedChunkOrReplaceEditedTimeline(
        string captureScope)
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateCommittedChunk(
            ChunkId,
            ChunkStart,
            ChunkStart.AddMinutes(1),
            captureScope);

        var timeProvider = new FixedTimeProvider(Now);
        var connectionFactory = new SqliteConnectionFactory(workspace.DatabasePath);
        await new SqliteDatabaseInitializer(connectionFactory, timeProvider)
            .InitializeAsync();
        var transport = new FakeOpenAiTransport();
        Guid completedJobId;
        Guid timelineEntryId;

        {
            using var settings = new AppSettingsService(
                new SqliteAppSettingsRepository(connectionFactory),
                timeProvider);
            await settings.InitializeAsync();

            var profileStore = new SqliteAiProviderProfileStore(connectionFactory);
            var providerFactory = new OpenAiCompatibleProviderFactory(
                profileStore,
                transport.CreateHandler,
                timeProvider);
            using var configuration = new AiProviderConfigurationService(
                profileStore,
                providerFactory,
                settings,
                timeProvider);
            await configuration.InitializeAsync();
            _ = await configuration.SaveAsync(
                "Local integration provider",
                "http://127.0.0.1:11434/v1",
                "vision-e2e",
                requestTimeoutSeconds: 30,
                replacementApiKey: null);
            var validated = await configuration.TestConnectionAsync();
            await configuration.SetCloudAnalysisEnabledAsync(enabled: true);

            Assert.True(validated.IsValidated);
            Assert.True(settings.Current.CloudAnalysisEnabled);

            var store = new SqliteCaptureAnalysisStore(
                connectionFactory,
                workspace.EvidenceRoot);
            var nativeEvidence = CreateEvidenceServices(workspace.EvidenceRoot);
            var fingerprintProvider = nativeEvidence.FingerprintProvider;
            var evidenceExtractor = nativeEvidence.EvidenceExtractor;
            using var ingestion = new CaptureAnalysisIngestionService(
                CreateScanner(workspace.EvidenceRoot, timeProvider),
                store,
                store,
                fingerprintProvider,
                profileStore,
                settings,
                timeProvider: timeProvider);

            var ingestionResult = await ingestion.ReconcileAsync();

            Assert.Equal(
                new CaptureAnalysisIngestionResult(1, 1, 1, AnalysisReady: true),
                ingestionResult);
            Assert.Equal(1, nativeEvidence.FingerprintCallCount);
            Assert.NotNull(nativeEvidence.SourceFingerprint);

            var processor = CreateProcessor(
                store,
                profileStore,
                providerFactory,
                evidenceExtractor,
                settings,
                connectionFactory,
                timeProvider,
                "analysis-e2e-first");
            var processResult = await processor.ProcessNextAsync();

            Assert.Equal(AnalysisJobProcessStatus.Completed, processResult.Status);
            completedJobId = Assert.IsType<Guid>(processResult.JobId);
            Assert.Equal(1, nativeEvidence.ExtractionCallCount);
            Assert.Equal(1, nativeEvidence.FrameReadCallCount);
            Assert.Equal(2, transport.Requests.Count);

            var completedJob = await ((IAnalysisJobStore)store).GetAsync(completedJobId);
            Assert.Equal(AnalysisJobState.Completed, completedJob?.State);

            var timelineRepository = new SqliteTimelineRepository(connectionFactory);
            var timeline = new TimelineQueryService(timelineRepository);
            var generated = Assert.Single(await timeline.GetForDayAsync(
                DateOnly.FromDateTime(ChunkStart.DateTime)));
            timelineEntryId = generated.Id;

            Assert.Equal(GeneratedTitle, generated.Title);
            Assert.Equal(TimelineEntryOrigin.Analyzed, generated.Origin);
            Assert.Equal(ChunkId, generated.Evidence?.CaptureChunkId);
            Assert.Equal(
                $"chunks/{ChunkId}/manifest.json",
                generated.Evidence?.ArtifactPath);
            Assert.Empty(generated.Apps);
            Assert.Equal(["coding", "integration"], generated.Tags);

            var command = new TimelineCommandService(timelineRepository, timeProvider);
            var edited = await command.UpdateAsync(
                generated.Id,
                new TimelineEntryDraft(
                    generated.Range,
                    EditedTitle,
                    generated.Summary,
                    generated.Category,
                    generated.Productivity,
                    generated.Tags));

            Assert.Equal(EditedTitle, edited.Title);
            Assert.True(edited.HasUserEdits);
            Assert.Equal(1, edited.Revision);
        }

        {
            using var settings = new AppSettingsService(
                new SqliteAppSettingsRepository(connectionFactory),
                timeProvider);
            await settings.InitializeAsync();
            var profileStore = new SqliteAiProviderProfileStore(connectionFactory);
            var providerFactory = new OpenAiCompatibleProviderFactory(
                profileStore,
                transport.CreateHandler,
                timeProvider);
            using var configuration = new AiProviderConfigurationService(
                profileStore,
                providerFactory,
                settings,
                timeProvider);
            await configuration.InitializeAsync();

            Assert.True(settings.Current.CloudAnalysisEnabled);
            Assert.True(configuration.Current?.IsValidated);
            Assert.Equal(1, configuration.Current?.Revision);

            var saved = await configuration.SaveAsync(
                "Local integration provider",
                "http://127.0.0.1:11434/v1",
                "vision-e2e",
                requestTimeoutSeconds: 31,
                replacementApiKey: null);

            Assert.Equal(2, saved.Revision);
            Assert.False(saved.IsValidated);
            Assert.False(settings.Current.CloudAnalysisEnabled);

            var revalidated = await configuration.TestConnectionAsync();
            Assert.Equal(2, revalidated.Revision);
            Assert.True(revalidated.IsValidated);
            Assert.Equal(3, transport.Requests.Count);

            await configuration.SetCloudAnalysisEnabledAsync(enabled: true);
            Assert.True(settings.Current.CloudAnalysisEnabled);

            var store = new SqliteCaptureAnalysisStore(
                connectionFactory,
                workspace.EvidenceRoot);
            var nativeEvidence = CreateEvidenceServices(workspace.EvidenceRoot);
            var fingerprintProvider = nativeEvidence.FingerprintProvider;
            var evidenceExtractor = nativeEvidence.EvidenceExtractor;
            using var ingestion = new CaptureAnalysisIngestionService(
                CreateScanner(workspace.EvidenceRoot, timeProvider),
                store,
                store,
                fingerprintProvider,
                profileStore,
                settings,
                timeProvider: timeProvider);

            var ingestionResult = await ingestion.ReconcileAsync();
            var processor = CreateProcessor(
                store,
                profileStore,
                providerFactory,
                evidenceExtractor,
                settings,
                connectionFactory,
                timeProvider,
                "analysis-e2e-restart");
            var processResult = await processor.ProcessNextAsync();

            Assert.Equal(
                new CaptureAnalysisIngestionResult(1, 0, 0, AnalysisReady: true),
                ingestionResult);
            Assert.Equal(AnalysisJobProcessStatus.NoWork, processResult.Status);
            Assert.Equal(1, nativeEvidence.FingerprintCallCount);
            Assert.Equal(0, nativeEvidence.ExtractionCallCount);
            Assert.Equal(0, nativeEvidence.FrameReadCallCount);
            Assert.Equal(3, transport.Requests.Count);

            var completedJob = await ((IAnalysisJobStore)store).GetAsync(completedJobId);
            Assert.Equal(AnalysisJobState.Completed, completedJob?.State);

            var timeline = new TimelineQueryService(
                new SqliteTimelineRepository(connectionFactory));
            var persisted = Assert.Single(await timeline.GetForDayAsync(
                DateOnly.FromDateTime(ChunkStart.DateTime)));
            Assert.Equal(timelineEntryId, persisted.Id);
            Assert.Equal(EditedTitle, persisted.Title);
            Assert.True(persisted.HasUserEdits);
            Assert.Equal(1, persisted.Revision);
        }

        workspace.ReplaceCommittedFrameBytes();
        {
            using var settings = new AppSettingsService(
                new SqliteAppSettingsRepository(connectionFactory),
                timeProvider);
            await settings.InitializeAsync();
            var profileStore = new SqliteAiProviderProfileStore(connectionFactory);
            var store = new SqliteCaptureAnalysisStore(
                connectionFactory,
                workspace.EvidenceRoot);
            var nativeEvidence = CreateEvidenceServices(workspace.EvidenceRoot);
            using var ingestion = new CaptureAnalysisIngestionService(
                CreateScanner(workspace.EvidenceRoot, timeProvider),
                store,
                store,
                nativeEvidence.FingerprintProvider,
                profileStore,
                settings,
                timeProvider: timeProvider);

            await Assert.ThrowsAsync<CaptureChunkConflictException>(
                () => ingestion.ReconcileAsync());

            Assert.Equal(0, nativeEvidence.FingerprintCallCount);
            Assert.Equal(0, nativeEvidence.ExtractionCallCount);
            Assert.Equal(0, nativeEvidence.FrameReadCallCount);
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(
                AnalysisJobState.Completed,
                (await ((IAnalysisJobStore)store).GetAsync(completedJobId))?.State);

            var persisted = Assert.Single(await new TimelineQueryService(
                    new SqliteTimelineRepository(connectionFactory))
                .GetForDayAsync(DateOnly.FromDateTime(ChunkStart.DateTime)));
            Assert.Equal(timelineEntryId, persisted.Id);
            Assert.Equal(EditedTitle, persisted.Title);
            Assert.True(persisted.HasUserEdits);
            Assert.Equal(1, persisted.Revision);
        }

        Assert.Equal(
            new PipelineCounts(1, 1, 1, 1),
            await ReadPipelineCountsAsync(connectionFactory));
        var analysisRequest = Assert.Single(
            transport.Requests,
            request => request.FrameId == EvidenceFrameId);
        Assert.Equal(
            "http://127.0.0.1:11434/v1/chat/completions",
            analysisRequest.RequestUri.AbsoluteUri);
        Assert.Contains(
            "data:image/jpeg;base64,/9j/2Q==",
            analysisRequest.Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("capture.mp4", analysisRequest.Body, StringComparison.Ordinal);
    }

    private static CaptureManifestScanner CreateScanner(
        string evidenceRoot,
        TimeProvider timeProvider) =>
        new(
            evidenceRoot,
            timeProvider,
            TimeZoneInfo.CreateCustomTimeZone(
                "WinDayFlow-E2E-UTC+08",
                TimeSpan.FromHours(8),
                "WinDayFlow E2E UTC+08",
                "WinDayFlow E2E UTC+08"));

    private static AnalysisJobProcessor CreateProcessor(
        SqliteCaptureAnalysisStore store,
        SqliteAiProviderProfileStore profileStore,
        OpenAiCompatibleProviderFactory providerFactory,
        IAnalysisEvidenceExtractor evidenceExtractor,
        AppSettingsService settings,
        SqliteConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        string leaseOwner) =>
        new(
            store,
            store,
            profileStore,
            providerFactory,
            evidenceExtractor,
            new SqliteAnalysisResultCommitter(connectionFactory),
            settings,
            new AnalysisJobProcessorOptions(
                leaseOwner,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(10),
                TimeSpan.Zero,
                "zh-CN"),
            timeProvider);

    private static async Task<PipelineCounts> ReadPipelineCountsAsync(
        SqliteConnectionFactory connectionFactory)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM capture_chunks),
                (SELECT COUNT(*) FROM analysis_jobs),
                (SELECT COUNT(*) FROM analysis_jobs WHERE state = $completed_state),
                (SELECT COUNT(*) FROM timeline_entries);
            """;
        command.Parameters.AddWithValue("$completed_state", (int)AnalysisJobState.Completed);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PipelineCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task WaitForPipelineCountsAsync(
        SqliteConnectionFactory connectionFactory,
        PipelineCounts expected)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var actual = await ReadPipelineCountsAsync(connectionFactory);
            if (actual == expected)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= PipelineWaitTimeout)
            {
                Assert.Equal(expected, actual);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static TestEvidenceServices CreateEvidenceServices(string evidenceRoot)
    {
        var archive = new CanonicalCaptureFrameArchive(evidenceRoot);
        var rawFingerprint = new CanonicalCaptureChunkFingerprintProvider(archive);
        var fingerprint = new CountingFingerprintProvider(rawFingerprint);
        var extractor = new CountingEvidenceExtractor(
            new CanonicalFrameAnalysisEvidenceExtractor(archive, rawFingerprint));
        return new TestEvidenceServices(fingerprint, extractor);
    }

    private sealed record TestEvidenceServices(
        CountingFingerprintProvider FingerprintProvider,
        CountingEvidenceExtractor EvidenceExtractor)
    {
        public int FingerprintCallCount => FingerprintProvider.CallCount;
        public int ExtractionCallCount => EvidenceExtractor.CallCount;
        public int FrameReadCallCount => EvidenceExtractor.CallCount;
        public string? SourceFingerprint => FingerprintProvider.LastValue;
    }

    private sealed class CountingFingerprintProvider(
        ICaptureChunkFingerprintProvider inner) : ICaptureChunkFingerprintProvider
    {
        public int CallCount { get; private set; }
        public string? LastValue { get; private set; }

        public async Task<CaptureChunkFingerprint> ComputeAsync(
            CaptureChunk chunk,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = await inner.ComputeAsync(chunk, cancellationToken);
            LastValue = result.Value;
            return result;
        }
    }

    private sealed class CountingEvidenceExtractor(
        IAnalysisEvidenceExtractor inner) : IAnalysisEvidenceExtractor
    {
        public int CallCount { get; private set; }

        public async Task<AnalysisEvidenceBatch> ExtractAsync(
            CaptureChunk chunk,
            CaptureChunkFingerprint expectedSourceFingerprint,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return await inner.ExtractAsync(
                chunk,
                expectedSourceFingerprint,
                cancellationToken);
        }
    }

    private sealed class FakeOpenAiTransport
    {
        public List<RecordedRequest> Requests { get; } = [];

        public HttpMessageHandler CreateHandler() => new Handler(this);

        private async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? throw new InvalidDataException("The provider request had no content.")
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var frameId = ReadFrameId(body);
            var requestUri = request.RequestUri
                ?? throw new InvalidDataException("The provider request had no URI.");
            Requests.Add(new RecordedRequest(requestUri, frameId, body));

            var isConnectionTest = string.Equals(
                frameId,
                "synthetic-frame",
                StringComparison.Ordinal);
            var activity = new Dictionary<string, object?>
            {
                ["start_offset_ms"] = 0,
                ["end_offset_ms"] = isConnectionTest ? 1_000 : 60_000,
                ["title"] = isConnectionTest ? "Connection check" : GeneratedTitle,
                ["summary"] = isConnectionTest
                    ? "Validate the configured provider."
                    : "Exercise scan, analysis, persistence, and timeline projection.",
                ["category"] = "focused_work",
                ["productivity"] = "focused",
                ["application_ids"] = Array.Empty<string>(),
                ["tags"] = isConnectionTest
                    ? new[] { "validation" }
                    : new[] { "coding", "integration" },
                ["confidence"] = 0.95,
                ["evidence_frame_ids"] = new[] { frameId },
            };
            var structured = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schema_version"] = AiAnalysisContract.CurrentSchemaVersion,
                ["activities"] = new[] { activity },
            });
            var envelope = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = $"fake-{Requests.Count}",
                ["model"] = "vision-e2e",
                ["choices"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["finish_reason"] = "stop",
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["content"] = structured,
                        },
                    },
                },
                ["usage"] = new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 10,
                    ["completion_tokens"] = 5,
                    ["total_tokens"] = 15,
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }

        private static string ReadFrameId(string body)
        {
            const string prefix = "Evidence frame id: ";
            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("messages")[1]
                .GetProperty("content");
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var type)
                    || type.GetString() != "text"
                    || !part.TryGetProperty("text", out var textElement)
                    || textElement.GetString() is not { } text
                    || !text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = text.IndexOf(';', prefix.Length);
                return separator < 0
                    ? text[prefix.Length..]
                    : text[prefix.Length..separator];
            }

            throw new InvalidDataException(
                "The provider request contained no evidence frame identifier.");
        }

        private sealed class Handler(FakeOpenAiTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                owner.RespondAsync(request, cancellationToken);
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.AnalysisPipeline.E2E.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "data", "windayflow.db");

        public string EvidenceRoot => Path.Combine(_root, "evidence");

        public void CreateCommittedChunk(
            string chunkId,
            DateTimeOffset start,
            DateTimeOffset end,
            string captureScope = ForegroundDisplayCaptureScope)
        {
            var chunkDirectory = Path.Combine(EvidenceRoot, "chunks", chunkId);
            Directory.CreateDirectory(chunkDirectory);
            var framesDirectory = Path.Combine(chunkDirectory, "frames");
            Directory.CreateDirectory(framesDirectory);
            var framePath = Path.Combine(framesDirectory, "frame-000000.jpg");
            var manifestPath = Path.Combine(chunkDirectory, "manifest.json");
            byte[] frame = [0xff, 0xd8, 0xff, 0xd9];
            File.WriteAllBytes(framePath, frame);
            File.WriteAllText(
                manifestPath,
                $$"""
                {
                  "schemaVersion": 2,
                  "captureScope": "{{captureScope}}",
                  "chunkId": "{{chunkId}}",
                  "startTimeUnixMs": {{start.ToUnixTimeMilliseconds()}},
                  "endTimeUnixMs": {{end.ToUnixTimeMilliseconds()}},
                  "authorization": {
                    "persistenceGeneration": 7,
                    "targetEpoch": 11
                  },
                  "frames": {
                    "format": "jpeg",
                    "quality": 82,
                    "capturedFrameCount": 6,
                    "retainedFrameCount": 1,
                    "width": 1600,
                    "height": 900,
                    "totalByteCount": 4,
                    "items": [{
                      "id": "frame-000000",
                      "index": 0,
                      "path": "frames/frame-000000.jpg",
                      "offsetMilliseconds": 30000,
                      "byteCount": 4,
                      "sha256": "{{Convert.ToHexString(SHA256.HashData(frame))}}"
                    }]
                  }
                }
                """);

            var committedAt = end.AddSeconds(1).UtcDateTime;
            File.SetLastWriteTimeUtc(manifestPath, committedAt);
            Directory.SetLastWriteTimeUtc(chunkDirectory, committedAt);
        }

        public void ReplaceCommittedFrameBytes()
        {
            var framePath = Path.Combine(
                EvidenceRoot,
                "chunks",
                ChunkId,
                "frames",
                "frame-000000.jpg");
            byte[] replacement = [0xff, 0xd8, 0x00, 0xff, 0xd9];
            File.WriteAllBytes(framePath, replacement);
            var manifestPath = Path.Combine(
                EvidenceRoot,
                "chunks",
                ChunkId,
                "manifest.json");
            var manifest = File.ReadAllText(manifestPath);
            var oldHash = Convert.ToHexString(SHA256.HashData(
                new byte[] { 0xff, 0xd8, 0xff, 0xd9 }));
            File.WriteAllText(
                manifestPath,
                manifest.Replace(
                    oldHash,
                    Convert.ToHexString(SHA256.HashData(replacement)),
                    StringComparison.Ordinal)
                .Replace("\"totalByteCount\": 4", "\"totalByteCount\": 5", StringComparison.Ordinal)
                .Replace("\"byteCount\": 4", "\"byteCount\": 5", StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record RecordedRequest(
        Uri RequestUri,
        string FrameId,
        string Body);

    private sealed record PipelineCounts(
        int Chunks,
        int Jobs,
        int CompletedJobs,
        int TimelineEntries);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Ai;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Ai;

public sealed class EvidenceSendPolicyTests
{
    [Fact]
    public async Task CurrentObservedWindowMatchBlocksSend()
    {
        var rule = CreateWindowRule(revision: 3);
        var sample = CreateSample(
            new CaptureContextRuleMatch(rule.Id, rule.Revision));
        using var settings = await CreateSettingsAsync(rule);
        var overrides = new StubOverrideStore();
        var policy = new EvidenceSendPolicy(
            settings,
            new StubContextStore([sample]),
            overrides);

        var decision = await EvaluateAsync(policy, sample.CaptureChunkId);

        Assert.Equal(EvidenceSendDecisionKind.BlockedByRule, decision.Kind);
        var match = Assert.Single(decision.RuleMatches);
        Assert.Equal((rule.Id, rule.Revision), (match.RuleId, match.RuleRevision));
        Assert.Equal(1, overrides.ConsumeAttempts);
    }

    [Fact]
    public async Task CurrentWindowEvaluationWithoutMatchAllowsSend()
    {
        var rule = CreateWindowRule(revision: 4);
        var chunk = CreateChunk("policy-chunk");
        var sample = new CaptureContextSample(
            chunk.Id,
            ordinal: 0,
            chunk.Range.Start.AddSeconds(5),
            new CaptureContextApplication(
                "process:browser.exe",
                "browser.exe",
                ApplicationIdentityKind.ExecutableName,
                "browser.exe",
                processId: 42,
                cpuUsageBasisPoints: 100,
                workingSetBytes: 1024,
                privateMemoryBytes: 512),
            ruleMatches: [],
            evaluatedRuleSetRevision: 1,
            applicationContextAvailable: true,
            windowContextAvailable: true);
        using var settings = await CreateSettingsAsync(rule);
        var overrides = new StubOverrideStore();
        var policy = new EvidenceSendPolicy(
            settings,
            new StubContextStore([sample]),
            overrides);

        var decision = await EvaluateAsync(policy, sample.CaptureChunkId);

        Assert.Equal(EvidenceSendDecisionKind.Allowed, decision.Kind);
        Assert.Empty(decision.RuleMatches);
        Assert.Equal(0, overrides.ConsumeAttempts);
    }

    [Fact]
    public async Task StaleObservedWindowMatchFailsClosed()
    {
        var rule = CreateWindowRule(revision: 4);
        var sample = CreateSample(
            new CaptureContextRuleMatch(rule.Id, rule.Revision - 1));
        using var settings = await CreateSettingsAsync(rule);
        var overrides = new StubOverrideStore();
        var policy = new EvidenceSendPolicy(
            settings,
            new StubContextStore([sample]),
            overrides);

        var decision = await EvaluateAsync(policy, sample.CaptureChunkId);

        Assert.Equal(EvidenceSendDecisionKind.BlockedMissingContext, decision.Kind);
        Assert.Empty(decision.RuleMatches);
        Assert.Equal(1, overrides.ConsumeAttempts);
    }

    private static async Task<AppSettingsService> CreateSettingsAsync(
        CaptureExclusionRule rule)
    {
        var snapshot = new AppSettings(
            AppThemePreference.System,
            RecordingConsent: null,
            new EvidenceSettings(
                EvidenceSettings.DefaultRetentionDays,
                RulesRevision: 1,
                new CaptureExclusionRuleSet([rule])),
            CaptureIntervalSeconds: 10,
            CaptureIntent.Stopped);
        var service = new AppSettingsService(new StubSettingsRepository(snapshot));
        await service.InitializeAsync();
        return service;
    }

    private static CaptureExclusionRule CreateWindowRule(long revision) => new(
        Guid.Parse("185cead3-38b0-4e79-ab34-ce4a00532cc5"),
        "Credential window",
        enabled: true,
        CaptureExclusionRuleScope.Window,
        ApplicationIdentityKind.ExecutableName,
        "browser.exe",
        WindowTitleMatchKind.Contains,
        "API key",
        revision);

    private static CaptureContextSample CreateSample(
        CaptureContextRuleMatch match)
    {
        var chunk = CreateChunk("policy-chunk");
        return new CaptureContextSample(
            chunk.Id,
            ordinal: 0,
            chunk.Range.Start.AddSeconds(5),
            new CaptureContextApplication(
                "process:browser.exe",
                "browser.exe",
                ApplicationIdentityKind.ExecutableName,
                "browser.exe",
                processId: 42,
                cpuUsageBasisPoints: 100,
                workingSetBytes: 1024,
                privateMemoryBytes: 512),
            [match]);
    }

    private static async Task<EvidenceSendDecision> EvaluateAsync(
        EvidenceSendPolicy policy,
        string chunkId)
    {
        var chunk = CreateChunk(chunkId);
        var profileId = Guid.Parse("f34e7740-30d1-4ccd-8cca-2aad5352f22a");
        var profile = new AiProviderProfileSnapshot(
            new AiProviderProfile(
                profileId,
                "Local test",
                AiProviderKind.OpenAiCompatible,
                new Uri("http://127.0.0.1:11434/v1/"),
                "vision",
                TimeSpan.FromSeconds(30)),
            revision: 2,
            hasApiKey: false,
            validatedRevision: null,
            validatedAtUtc: null);
        var route = new AnalysisStageBinding(
            AnalysisStage.TimelineAnalysis,
            enabled: true,
            profileId,
            routeRevision: 5);
        return await policy.EvaluateAsync(
            chunk,
            AnalysisStage.TimelineAnalysis,
            profile,
            route,
            new CaptureChunkFingerprint(new string('A', 64)),
            Guid.Parse("77e49155-f36a-4c35-b944-45d5b35b5d78"));
    }

    private static CaptureChunk CreateChunk(string id)
    {
        var start = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
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

    private sealed class StubContextStore(
        IReadOnlyList<CaptureContextSample> samples) : ICaptureContextStore
    {
        public Task ReplaceAsync(
            CaptureChunk chunk,
            IReadOnlyList<CaptureContextSample> replacement,
            CaptureExclusionRuleSet rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CaptureContextSample>> ListAsync(
            string captureChunkId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(samples);
    }

    private sealed class StubOverrideStore : IEvidenceSendOverrideStore
    {
        public int ConsumeAttempts { get; private set; }

        public Task<EvidenceSendOverride> CreateAsync(
            EvidenceSendOverride value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task<bool> TryConsumeAsync(
            string captureChunkId,
            AnalysisStage stage,
            Guid providerProfileId,
            long providerProfileRevision,
            long routeRevision,
            string evidenceFingerprint,
            Guid logicalOperationId,
            DateTimeOffset consumedAtUtc,
            CancellationToken cancellationToken = default)
        {
            ConsumeAttempts++;
            return Task.FromResult(false);
        }
    }

    private sealed class StubSettingsRepository(AppSettings snapshot)
        : IAppSettingsRepository
    {
        private AppSettings _snapshot = snapshot;

        public Task<AppSettings> GetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_snapshot, expected);
            _snapshot = proposed;
            return Task.CompletedTask;
        }
    }
}

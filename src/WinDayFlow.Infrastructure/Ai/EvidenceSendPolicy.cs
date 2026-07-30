using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class EvidenceSendPolicy : IEvidenceSendPolicy
{
    private readonly AppSettingsService _settings;
    private readonly ICaptureContextStore _contextStore;
    private readonly IEvidenceSendOverrideStore _overrideStore;
    private readonly TimeProvider _timeProvider;

    public EvidenceSendPolicy(
        AppSettingsService settings,
        ICaptureContextStore contextStore,
        IEvidenceSendOverrideStore overrideStore,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
        _overrideStore = overrideStore ?? throw new ArgumentNullException(nameof(overrideStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EvidenceSendDecision> EvaluateAsync(
        CaptureChunk chunk,
        AnalysisStage stage,
        AiProviderProfileSnapshot profile,
        AnalysisStageBinding route,
        CaptureChunkFingerprint evidenceFingerprint,
        Guid logicalOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(evidenceFingerprint);
        if (route.Stage != stage
            || route.ProviderProfileId != profile.Profile.Id
            || logicalOperationId == Guid.Empty)
        {
            throw new ArgumentException("The evidence-send policy route is inconsistent.");
        }

        var evidenceSettings = _settings.Current.Evidence;
        var rules = evidenceSettings.SendRules.Rules
            .Where(static rule => rule.Enabled)
            .ToArray();
        if (rules.Length == 0)
        {
            return new EvidenceSendDecision(EvidenceSendDecisionKind.Allowed, []);
        }

        var samples = await _contextStore
            .ListAsync(chunk.Id, cancellationToken)
            .ConfigureAwait(false);
        var matches = new Dictionary<Guid, CaptureContextRuleMatch>();
        var missingContext = samples.Count == 0;
        var enabledRulesById = rules.ToDictionary(static rule => rule.Id);
        foreach (var sample in samples)
        {
            foreach (var observedMatch in sample.RuleMatches)
            {
                if (enabledRulesById.TryGetValue(observedMatch.RuleId, out var rule)
                    && observedMatch.RuleRevision == rule.Revision)
                {
                    matches[rule.Id] = observedMatch;
                }
            }
        }

        foreach (var rule in rules)
        {
            if (rule.Scope == CaptureExclusionRuleScope.Window)
            {
                if (samples.Any(sample => !HasCurrentEvaluation(
                        sample,
                        evidenceSettings.RulesRevision,
                        requireWindowContext: true)))
                {
                    missingContext = true;
                }
                continue;
            }

            foreach (var sample in samples)
            {
                var application = sample.Application;
                if (application is not null
                    && application.IdentityKind == rule.ApplicationIdentityKind)
                {
                    if (IdentityEquals(
                            rule.ApplicationIdentityKind,
                            rule.IdentityValue,
                            application.IdentityValue))
                    {
                        matches[rule.Id] = new CaptureContextRuleMatch(
                            rule.Id,
                            rule.Revision);
                    }
                    continue;
                }

                if (!HasCurrentEvaluation(
                        sample,
                        evidenceSettings.RulesRevision,
                        requireWindowContext: false))
                {
                    missingContext = true;
                }
            }
        }

        var blockedKind = matches.Count != 0
            ? EvidenceSendDecisionKind.BlockedByRule
            : missingContext
                ? EvidenceSendDecisionKind.BlockedMissingContext
                : EvidenceSendDecisionKind.Allowed;
        if (blockedKind == EvidenceSendDecisionKind.Allowed)
        {
            return new EvidenceSendDecision(blockedKind, []);
        }

        var overrideConsumed = await _overrideStore.TryConsumeAsync(
                chunk.Id,
                stage,
                profile.Profile.Id,
                profile.Revision,
                route.RouteRevision,
                evidenceFingerprint.Value,
                logicalOperationId,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
        return new EvidenceSendDecision(
            overrideConsumed
                ? EvidenceSendDecisionKind.AllowedByOverride
                : blockedKind,
            matches.Values.OrderBy(static value => value.RuleId).ToArray());
    }

    private static bool HasCurrentEvaluation(
        CaptureContextSample sample,
        long rulesRevision,
        bool requireWindowContext) =>
        sample.EvaluatedRuleSetRevision == rulesRevision
        && (requireWindowContext
            ? sample.WindowContextAvailable
            : sample.ApplicationContextAvailable);

    private static bool IdentityEquals(
        ApplicationIdentityKind kind,
        string left,
        string right) => kind switch
        {
            ApplicationIdentityKind.ExecutableName
                or ApplicationIdentityKind.PackageFamilyName =>
                string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            ApplicationIdentityKind.PublisherCertificateSha256 =>
                string.Equals(left, right, StringComparison.Ordinal),
            _ => false,
        };
}

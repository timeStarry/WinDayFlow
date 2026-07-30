using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public sealed class CaptureRuleObservationBuffer : ICaptureRuleObservationSource
{
    private const int MaximumObservationCount = 8_192;
    private readonly object _sync = new();
    private readonly List<Observation> _observations = [];

    public void Observe(
        DateTimeOffset observedAt,
        AppSettings settings,
        NativeCapturePrivacySignals signals)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signals);
        var rules = settings.Evidence.SendRules;
        var evaluation = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            signals.CaptureIdentity);
        var matches = new List<CaptureContextRuleMatch>(capacity: 2);
        AddMatch(evaluation.Application.MatchedRuleId);
        AddMatch(evaluation.Window.MatchedRuleId);
        Add(
            observedAt,
            new CaptureContextRuleEvaluation(
                settings.Evidence.RulesRevision,
                evaluation.Application.Decision != NativeCapturePolicyDecision.Unknown,
                evaluation.Window.Decision != NativeCapturePolicyDecision.Unknown,
                matches));
        return;

        void AddMatch(Guid? id)
        {
            if (!id.HasValue
                || matches.Any(match => match.RuleId == id.Value))
            {
                return;
            }

            var rule = rules.Rules.Single(rule => rule.Id == id.Value);
            matches.Add(new CaptureContextRuleMatch(rule.Id, rule.Revision));
        }
    }

    public void Invalidate(DateTimeOffset observedAt, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Add(
            observedAt,
            new CaptureContextRuleEvaluation(
                settings.Evidence.RulesRevision,
                applicationContextAvailable: false,
                windowContextAvailable: false));
    }

    public CaptureContextRuleEvaluation? FindAt(DateTimeOffset sampledAt)
    {
        lock (_sync)
        {
            for (var index = _observations.Count - 1; index >= 0; index--)
            {
                if (_observations[index].ObservedAt <= sampledAt)
                {
                    return _observations[index].Evaluation;
                }
            }
        }

        return null;
    }

    private void Add(
        DateTimeOffset observedAt,
        CaptureContextRuleEvaluation evaluation)
    {
        var normalizedTime = observedAt.ToUniversalTime();
        lock (_sync)
        {
            if (_observations.Count != 0
                && normalizedTime < _observations[^1].ObservedAt)
            {
                normalizedTime = _observations[^1].ObservedAt;
            }

            _observations.Add(new Observation(normalizedTime, evaluation));
            if (_observations.Count > MaximumObservationCount)
            {
                _observations.RemoveRange(
                    0,
                    _observations.Count - MaximumObservationCount);
            }
        }
    }

    private sealed record Observation(
        DateTimeOffset ObservedAt,
        CaptureContextRuleEvaluation Evaluation);
}
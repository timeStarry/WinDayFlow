using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public enum NativeCaptureObservationState
{
    Unknown = 0,
    Absent = 1,
    Present = 2,
}

public sealed class NativeCaptureObservation
    : IEquatable<NativeCaptureObservation>
{
    private NativeCaptureObservation(
        NativeCaptureObservationState state,
        string? value)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The native capture observation state is not supported.");
        }

        if ((state == NativeCaptureObservationState.Present) != (value is not null))
        {
            throw new ArgumentException(
                "Only a present native capture observation can contain a value.",
                nameof(value));
        }

        State = state;
        Value = value;
    }

    public static NativeCaptureObservation Unknown { get; } = new(
        NativeCaptureObservationState.Unknown,
        value: null);

    public static NativeCaptureObservation Absent { get; } = new(
        NativeCaptureObservationState.Absent,
        value: null);

    public NativeCaptureObservationState State { get; }

    public string? Value { get; }

    public static NativeCaptureObservation Present(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new NativeCaptureObservation(
            NativeCaptureObservationState.Present,
            value);
    }

    public bool Equals(NativeCaptureObservation? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && State == other.State
                && string.Equals(Value, other.Value, StringComparison.Ordinal));
    }

    public override bool Equals(object? obj)
    {
        return obj is NativeCaptureObservation other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(State, Value);
    }

    public override string ToString()
    {
        return $"{nameof(NativeCaptureObservation)} {{ State = {State}, Value = [REDACTED] }}";
    }
}

public sealed class NativeCaptureIdentitySnapshot
    : IEquatable<NativeCaptureIdentitySnapshot>
{
    public NativeCaptureIdentitySnapshot(
        string? executableName,
        string? packageFamilyName,
        string? publisherCertificateSha256,
        string? windowTitle)
        : this(
            NormalizeApplicationObservation(
                ApplicationIdentityKind.ExecutableName,
                FromLegacyValue(executableName)),
            NormalizeApplicationObservation(
                ApplicationIdentityKind.PackageFamilyName,
                FromLegacyValue(packageFamilyName)),
            NormalizeApplicationObservation(
                ApplicationIdentityKind.PublisherCertificateSha256,
                FromLegacyValue(publisherCertificateSha256)),
            FromLegacyValue(windowTitle))
    {
    }

    private NativeCaptureIdentitySnapshot(
        NativeCaptureObservation executableName,
        NativeCaptureObservation packageFamilyName,
        NativeCaptureObservation publisherCertificateSha256,
        NativeCaptureObservation windowTitle)
    {
        ExecutableNameObservation = executableName;
        PackageFamilyNameObservation = packageFamilyName;
        PublisherCertificateSha256Observation = publisherCertificateSha256;
        WindowTitleObservation = windowTitle;
    }

    public static NativeCaptureIdentitySnapshot Unknown { get; } = FromObservations(
        NativeCaptureObservation.Unknown,
        NativeCaptureObservation.Unknown,
        NativeCaptureObservation.Unknown,
        NativeCaptureObservation.Unknown);

    public static NativeCaptureIdentitySnapshot Absent { get; } = FromObservations(
        NativeCaptureObservation.Absent,
        NativeCaptureObservation.Absent,
        NativeCaptureObservation.Absent,
        NativeCaptureObservation.Absent);

    public NativeCaptureObservation ExecutableNameObservation { get; }

    public NativeCaptureObservation PackageFamilyNameObservation { get; }

    public NativeCaptureObservation PublisherCertificateSha256Observation { get; }

    public NativeCaptureObservation WindowTitleObservation { get; }

    public string? ExecutableName => ExecutableNameObservation.Value;

    public string? PackageFamilyName => PackageFamilyNameObservation.Value;

    public string? PublisherCertificateSha256 =>
        PublisherCertificateSha256Observation.Value;

    public string? WindowTitle => WindowTitleObservation.Value;

    public static NativeCaptureIdentitySnapshot FromObservations(
        NativeCaptureObservation executableName,
        NativeCaptureObservation packageFamilyName,
        NativeCaptureObservation publisherCertificateSha256,
        NativeCaptureObservation windowTitle)
    {
        ArgumentNullException.ThrowIfNull(executableName);
        ArgumentNullException.ThrowIfNull(packageFamilyName);
        ArgumentNullException.ThrowIfNull(publisherCertificateSha256);
        ArgumentNullException.ThrowIfNull(windowTitle);

        return new NativeCaptureIdentitySnapshot(
            NormalizeApplicationObservation(
                ApplicationIdentityKind.ExecutableName,
                executableName),
            NormalizeApplicationObservation(
                ApplicationIdentityKind.PackageFamilyName,
                packageFamilyName),
            NormalizeApplicationObservation(
                ApplicationIdentityKind.PublisherCertificateSha256,
                publisherCertificateSha256),
            windowTitle);
    }

    public bool Equals(NativeCaptureIdentitySnapshot? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && ObservationsEqual(
                    ExecutableNameObservation,
                    other.ExecutableNameObservation)
                && ObservationsEqual(
                    PackageFamilyNameObservation,
                    other.PackageFamilyNameObservation)
                && ObservationsEqual(
                    PublisherCertificateSha256Observation,
                    other.PublisherCertificateSha256Observation)
                && ObservationsEqual(
                    WindowTitleObservation,
                    other.WindowTitleObservation));
    }

    public override bool Equals(object? obj)
    {
        return obj is NativeCaptureIdentitySnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        AddObservationHashCode(ref hash, ExecutableNameObservation);
        AddObservationHashCode(ref hash, PackageFamilyNameObservation);
        AddObservationHashCode(ref hash, PublisherCertificateSha256Observation);
        AddObservationHashCode(ref hash, WindowTitleObservation);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return $"{nameof(NativeCaptureIdentitySnapshot)} {{ "
            + $"ExecutableNameState = {ExecutableNameObservation.State}, "
            + $"PackageFamilyNameState = {PackageFamilyNameObservation.State}, "
            + $"PublisherCertificateSha256State = {PublisherCertificateSha256Observation.State}, "
            + $"WindowTitleState = {WindowTitleObservation.State}, "
            + "Values = [REDACTED] }";
    }

    internal NativeCaptureObservation GetApplicationIdentityObservation(
        ApplicationIdentityKind identityKind)
    {
        return identityKind switch
        {
            ApplicationIdentityKind.ExecutableName => ExecutableNameObservation,
            ApplicationIdentityKind.PackageFamilyName => PackageFamilyNameObservation,
            ApplicationIdentityKind.PublisherCertificateSha256 =>
                PublisherCertificateSha256Observation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(identityKind),
                identityKind,
                "The application identity kind is not supported."),
        };
    }

    private static NativeCaptureObservation FromLegacyValue(string? value)
    {
        return value is null
            ? NativeCaptureObservation.Unknown
            : NativeCaptureObservation.Present(value);
    }

    private static NativeCaptureObservation NormalizeApplicationObservation(
        ApplicationIdentityKind identityKind,
        NativeCaptureObservation observation)
    {
        if (observation.State != NativeCaptureObservationState.Present)
        {
            return observation;
        }

        return CaptureExclusionRule.TryNormalizeApplicationIdentity(
            identityKind,
            observation.Value,
            out var normalizedIdentity)
                ? NativeCaptureObservation.Present(normalizedIdentity)
                : NativeCaptureObservation.Unknown;
    }

    private static bool ObservationsEqual(
        NativeCaptureObservation left,
        NativeCaptureObservation right)
    {
        return left.State == right.State
            && string.Equals(
                left.Value,
                right.Value,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void AddObservationHashCode(
        ref HashCode hash,
        NativeCaptureObservation observation)
    {
        hash.Add(observation.State);
        hash.Add(observation.Value, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record NativeCaptureExclusionScopeEvaluation
{
    internal NativeCaptureExclusionScopeEvaluation(
        NativeCapturePolicyDecision decision,
        Guid? matchedRuleId,
        bool hasEnabledRules)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "The exclusion rule decision is not supported.");
        }

        if (matchedRuleId == Guid.Empty)
        {
            throw new ArgumentException(
                "A matched exclusion rule identifier cannot be empty.",
                nameof(matchedRuleId));
        }

        if (decision == NativeCapturePolicyDecision.Allow
            && matchedRuleId is not null)
        {
            throw new ArgumentException(
                "An allowed exclusion evaluation cannot identify a rule.",
                nameof(decision));
        }

        if (decision == NativeCapturePolicyDecision.Block
            && matchedRuleId is null)
        {
            throw new ArgumentException(
                "A blocked exclusion evaluation requires a matched rule.",
                nameof(matchedRuleId));
        }

        if (decision == NativeCapturePolicyDecision.Unknown
            && matchedRuleId is not null)
        {
            throw new ArgumentException(
                "An unknown exclusion evaluation cannot identify a matched rule.",
                nameof(matchedRuleId));
        }

        if (!hasEnabledRules
            && decision != NativeCapturePolicyDecision.Allow)
        {
            throw new ArgumentException(
                "An exclusion evaluation without enabled rules must allow capture.",
                nameof(hasEnabledRules));
        }

        Decision = decision;
        MatchedRuleId = matchedRuleId;
        HasEnabledRules = hasEnabledRules;
    }

    public NativeCapturePolicyDecision Decision { get; }

    public Guid? MatchedRuleId { get; }

    public bool HasEnabledRules { get; }

    public override string ToString()
    {
        return $"{nameof(NativeCaptureExclusionScopeEvaluation)} {{ "
            + $"Decision = {Decision}, MatchedRuleId = {MatchedRuleId}, "
            + $"HasEnabledRules = {HasEnabledRules} }}";
    }
}

public sealed record NativeCaptureExclusionEvaluation(
    NativeCaptureExclusionScopeEvaluation Application,
    NativeCaptureExclusionScopeEvaluation Window)
{
    public NativeCaptureExclusionScopeEvaluation Application { get; } = Application
        ?? throw new ArgumentNullException(nameof(Application));

    public NativeCaptureExclusionScopeEvaluation Window { get; } = Window
        ?? throw new ArgumentNullException(nameof(Window));

    public override string ToString()
    {
        return $"{nameof(NativeCaptureExclusionEvaluation)} {{ "
            + $"Application = {Application}, Window = {Window} }}";
    }
}

public static class NativeCaptureExclusionRuleMatcher
{
    public static NativeCaptureExclusionEvaluation Evaluate(
        CaptureExclusionRuleSet ruleSet,
        NativeCaptureIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(identity);

        return new NativeCaptureExclusionEvaluation(
            EvaluateScope(
                ruleSet,
                identity,
                CaptureExclusionRuleScope.Application),
            EvaluateScope(
                ruleSet,
                identity,
                CaptureExclusionRuleScope.Window));
    }

    private static NativeCaptureExclusionScopeEvaluation EvaluateScope(
        CaptureExclusionRuleSet ruleSet,
        NativeCaptureIdentitySnapshot identity,
        CaptureExclusionRuleScope scope)
    {
        var hasEnabledRules = false;
        foreach (var rule in ruleSet.Rules)
        {
            if (!rule.Enabled || rule.Scope != scope)
            {
                continue;
            }

            hasEnabledRules = true;

            var observedIdentity = identity.GetApplicationIdentityObservation(
                rule.ApplicationIdentityKind);
            if (observedIdentity.State == NativeCaptureObservationState.Unknown)
            {
                return Unknown();
            }

            if (observedIdentity.State == NativeCaptureObservationState.Absent)
            {
                continue;
            }

            if (!string.Equals(
                    observedIdentity.Value,
                    rule.IdentityValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.Scope == CaptureExclusionRuleScope.Application)
            {
                return Blocked(rule);
            }

            if (identity.WindowTitleObservation.State
                == NativeCaptureObservationState.Unknown)
            {
                return Unknown();
            }

            if (identity.WindowTitleObservation.State
                == NativeCaptureObservationState.Absent)
            {
                continue;
            }

            if (MatchesWindowTitle(
                    identity.WindowTitleObservation.Value!,
                    rule.WindowTitleMatchKind!.Value,
                    rule.Pattern!))
            {
                return Blocked(rule);
            }
        }

        return new NativeCaptureExclusionScopeEvaluation(
            NativeCapturePolicyDecision.Allow,
            matchedRuleId: null,
            hasEnabledRules);

        NativeCaptureExclusionScopeEvaluation Blocked(
            CaptureExclusionRule matchedRule)
        {
            return new NativeCaptureExclusionScopeEvaluation(
                NativeCapturePolicyDecision.Block,
                matchedRule.Id,
                hasEnabledRules: true);
        }

        NativeCaptureExclusionScopeEvaluation Unknown()
        {
            return new NativeCaptureExclusionScopeEvaluation(
                NativeCapturePolicyDecision.Unknown,
                matchedRuleId: null,
                hasEnabledRules: true);
        }
    }

    private static bool MatchesWindowTitle(
        string windowTitle,
        WindowTitleMatchKind matchKind,
        string pattern)
    {
        return matchKind switch
        {
            WindowTitleMatchKind.Exact => string.Equals(
                windowTitle,
                pattern,
                StringComparison.OrdinalIgnoreCase),
            WindowTitleMatchKind.StartsWith => windowTitle.StartsWith(
                pattern,
                StringComparison.OrdinalIgnoreCase),
            WindowTitleMatchKind.Contains => windowTitle.Contains(
                pattern,
                StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(
                nameof(matchKind),
                matchKind,
                "The window title match kind is not supported."),
        };
    }
}

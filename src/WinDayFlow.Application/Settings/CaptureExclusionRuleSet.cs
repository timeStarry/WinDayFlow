using System.Collections.ObjectModel;

namespace WinDayFlow.Application.Settings;

public sealed class CaptureExclusionRuleSet : IEquatable<CaptureExclusionRuleSet>
{
    public const int MaximumRuleCount = 100;

    private readonly ReadOnlyCollection<CaptureExclusionRule> _rules;

    public CaptureExclusionRuleSet(IReadOnlyList<CaptureExclusionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > MaximumRuleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rules),
                rules.Count,
                $"At most {MaximumRuleCount} capture exclusion rules are supported.");
        }

        var copy = rules.ToArray();
        if (copy.Any(static rule => rule is null))
        {
            throw new ArgumentException(
                "Capture exclusion rules cannot contain null items.",
                nameof(rules));
        }

        var identifiers = new HashSet<Guid>();
        var boundaries = new HashSet<CaptureExclusionRule>(RuleBoundaryComparer.Instance);
        foreach (var rule in copy)
        {
            if (!identifiers.Add(rule.Id))
            {
                throw new ArgumentException(
                    "Capture exclusion rule identifiers must be unique.",
                    nameof(rules));
            }

            if (!boundaries.Add(rule))
            {
                throw new ArgumentException(
                    "Capture exclusion rule matching boundaries must be unique.",
                    nameof(rules));
            }
        }

        _rules = Array.AsReadOnly(copy);
    }

    public static CaptureExclusionRuleSet Empty { get; } = new([]);

    public IReadOnlyList<CaptureExclusionRule> Rules => _rules;

    public int Count => _rules.Count;

    public CaptureExclusionRule this[int index] => _rules[index];

    public bool Equals(CaptureExclusionRuleSet? other)
    {
        return ReferenceEquals(this, other)
            || (other is not null
                && _rules.SequenceEqual(other._rules));
    }

    public override bool Equals(object? obj)
    {
        return obj is CaptureExclusionRuleSet other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var rule in _rules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }

    public bool HasSameEffectivePolicy(CaptureExclusionRuleSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return HasSameEffectivePolicy(other, CaptureExclusionRuleScope.Application)
            && HasSameEffectivePolicy(other, CaptureExclusionRuleScope.Window);
    }

    private bool HasSameEffectivePolicy(
        CaptureExclusionRuleSet other,
        CaptureExclusionRuleScope scope)
    {
        using var left = _rules
            .Where(rule => rule.Enabled && rule.Scope == scope)
            .GetEnumerator();
        using var right = other._rules
            .Where(rule => rule.Enabled && rule.Scope == scope)
            .GetEnumerator();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (hasLeft != hasRight)
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            if (!EffectivePolicyRuleComparer.Instance.Equals(
                    left.Current,
                    right.Current))
            {
                return false;
            }
        }
    }

    internal int IndexOf(Guid id)
    {
        for (var index = 0; index < _rules.Count; index++)
        {
            if (_rules[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    internal CaptureExclusionRuleSet Add(CaptureExclusionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new CaptureExclusionRuleSet([.. _rules, rule]);
    }

    internal CaptureExclusionRuleSet Replace(
        int index,
        CaptureExclusionRule replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var copy = _rules.ToArray();
        copy[index] = replacement;
        return new CaptureExclusionRuleSet(copy);
    }

    internal CaptureExclusionRuleSet Move(
        int oldIndex,
        int newIndex,
        CaptureExclusionRule movedRule)
    {
        ArgumentNullException.ThrowIfNull(movedRule);
        var copy = _rules.ToList();
        copy.RemoveAt(oldIndex);
        copy.Insert(newIndex, movedRule);
        return new CaptureExclusionRuleSet(copy);
    }

    internal CaptureExclusionRuleSet RemoveAt(int index)
    {
        var copy = _rules.ToList();
        copy.RemoveAt(index);
        return new CaptureExclusionRuleSet(copy);
    }

    private sealed class RuleBoundaryComparer : IEqualityComparer<CaptureExclusionRule>
    {
        private RuleBoundaryComparer()
        {
        }

        public static RuleBoundaryComparer Instance { get; } = new();

        public bool Equals(CaptureExclusionRule? left, CaptureExclusionRule? right)
        {
            return ReferenceEquals(left, right)
                || (left is not null
                    && right is not null
                    && left.Scope == right.Scope
                    && left.ApplicationIdentityKind == right.ApplicationIdentityKind
                    && string.Equals(
                        left.IdentityValue,
                        right.IdentityValue,
                        StringComparison.OrdinalIgnoreCase)
                    && left.WindowTitleMatchKind == right.WindowTitleMatchKind
                    && string.Equals(
                        left.Pattern,
                        right.Pattern,
                        StringComparison.OrdinalIgnoreCase));
        }

        public int GetHashCode(CaptureExclusionRule rule)
        {
            var hash = new HashCode();
            hash.Add(rule.Scope);
            hash.Add(rule.ApplicationIdentityKind);
            hash.Add(rule.IdentityValue, StringComparer.OrdinalIgnoreCase);
            hash.Add(rule.WindowTitleMatchKind);
            hash.Add(rule.Pattern, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private sealed class EffectivePolicyRuleComparer : IEqualityComparer<CaptureExclusionRule>
    {
        private EffectivePolicyRuleComparer()
        {
        }

        public static EffectivePolicyRuleComparer Instance { get; } = new();

        public bool Equals(CaptureExclusionRule? left, CaptureExclusionRule? right)
        {
            return ReferenceEquals(left, right)
                || (left is not null
                    && right is not null
                    && left.Id == right.Id
                    && RuleBoundaryComparer.Instance.Equals(left, right));
        }

        public int GetHashCode(CaptureExclusionRule rule)
        {
            return HashCode.Combine(
                rule.Id,
                RuleBoundaryComparer.Instance.GetHashCode(rule));
        }
    }
}

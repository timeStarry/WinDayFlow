namespace WinDayFlow.Application.Settings;

public sealed class CaptureExclusionRuleNotFoundException : KeyNotFoundException
{
    public CaptureExclusionRuleNotFoundException(Guid ruleId)
        : base("The capture exclusion rule no longer exists.")
    {
        RuleId = ruleId;
    }

    public Guid RuleId { get; }
}

public sealed class CaptureExclusionRuleRevisionConflictException : InvalidOperationException
{
    public CaptureExclusionRuleRevisionConflictException(
        Guid ruleId,
        long expectedRevision,
        long actualRevision)
        : base("The capture exclusion rule changed after it was loaded.")
    {
        RuleId = ruleId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public Guid RuleId { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}

public sealed class AppSettingsConcurrencyException : InvalidOperationException
{
    public AppSettingsConcurrencyException()
        : base("The application settings changed after they were loaded.")
    {
    }
}

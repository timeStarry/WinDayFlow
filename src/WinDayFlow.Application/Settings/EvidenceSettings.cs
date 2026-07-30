namespace WinDayFlow.Application.Settings;

public sealed record EvidenceSettings(
    int RetentionDays,
    long RulesRevision,
    CaptureExclusionRuleSet SendRules)
{
    public static readonly Guid WinDayFlowSendRuleId =
        Guid.Parse("df2c2131-bfe5-4a17-bf4c-4f3378a4b093");

    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 365;
    public const int DefaultRetentionDays = 30;

    public static EvidenceSettings Default { get; } = new(
        DefaultRetentionDays,
        RulesRevision: 1,
        SendRules: new CaptureExclusionRuleSet(
        [
            CaptureExclusionRule.Create(
                WinDayFlowSendRuleId,
                "WinDayFlow",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "WinDayFlow.App.exe"),
        ]));

    public int RetentionDays { get; } = ValidateRetentionDays(RetentionDays);

    public long RulesRevision { get; } = RulesRevision > 0
        ? RulesRevision
        : throw new ArgumentOutOfRangeException(
            nameof(RulesRevision),
            RulesRevision,
            "The evidence send-rule revision must be positive.");

    public CaptureExclusionRuleSet SendRules { get; } = SendRules
        ?? throw new ArgumentNullException(nameof(SendRules));

    public EvidenceSettings ChangeRetentionDays(int retentionDays)
    {
        _ = ValidateRetentionDays(retentionDays);
        return retentionDays == RetentionDays
            ? this
            : new EvidenceSettings(retentionDays, RulesRevision, SendRules);
    }

    public EvidenceSettings ChangeSendRules(CaptureExclusionRuleSet sendRules)
    {
        ArgumentNullException.ThrowIfNull(sendRules);
        if (SendRules.Equals(sendRules))
        {
            return this;
        }

        return new EvidenceSettings(
            RetentionDays,
            SendRules.HasSameEffectivePolicy(sendRules)
                ? RulesRevision
                : NextRulesRevision(),
            sendRules);
    }

    private long NextRulesRevision()
    {
        if (RulesRevision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The evidence send-rule revision has been exhausted.");
        }

        return RulesRevision + 1;
    }

    private static int ValidateRetentionDays(int retentionDays)
    {
        if (retentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                retentionDays,
                $"Evidence retention must be between {MinimumRetentionDays} and {MaximumRetentionDays} days.");
        }

        return retentionDays;
    }
}

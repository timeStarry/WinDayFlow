namespace WinDayFlow.Application.Settings;

public sealed record CapturePrivacySettings(
    int EvidenceRetentionDays,
    bool ExcludeSensitiveApplications,
    bool PauseInRemoteSessions,
    bool PauseDuringScreenSharing,
    long Revision,
    CaptureExclusionRuleSet ExclusionRules)
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 365;
    public const int DefaultRetentionDays = 30;

    public static CapturePrivacySettings Default { get; } = new(
        DefaultRetentionDays,
        ExcludeSensitiveApplications: true,
        PauseInRemoteSessions: true,
        PauseDuringScreenSharing: true,
        Revision: 1,
        ExclusionRules: CaptureExclusionRuleSet.Empty);

    public CapturePrivacySettings(
        int EvidenceRetentionDays,
        bool ExcludeSensitiveApplications,
        bool PauseInRemoteSessions,
        bool PauseDuringScreenSharing,
        long Revision)
        : this(
            EvidenceRetentionDays,
            ExcludeSensitiveApplications,
            PauseInRemoteSessions,
            PauseDuringScreenSharing,
            Revision,
            CaptureExclusionRuleSet.Empty)
    {
    }

    public int EvidenceRetentionDays { get; } =
        ValidateRetentionDays(EvidenceRetentionDays);

    public bool ExcludeSensitiveApplications { get; } = ExcludeSensitiveApplications;

    public bool PauseInRemoteSessions { get; } = PauseInRemoteSessions;

    public bool PauseDuringScreenSharing { get; } = PauseDuringScreenSharing;

    public long Revision { get; } = ValidateRevision(Revision);

    public CaptureExclusionRuleSet ExclusionRules { get; } = ExclusionRules
        ?? throw new ArgumentNullException(nameof(ExclusionRules));

    public CapturePrivacySettings Change(
        int evidenceRetentionDays,
        bool excludeSensitiveApplications,
        bool pauseInRemoteSessions,
        bool pauseDuringScreenSharing)
    {
        _ = ValidateRetentionDays(evidenceRetentionDays);
        if (evidenceRetentionDays == EvidenceRetentionDays
            && excludeSensitiveApplications == ExcludeSensitiveApplications
            && pauseInRemoteSessions == PauseInRemoteSessions
            && pauseDuringScreenSharing == PauseDuringScreenSharing)
        {
            return this;
        }

        if (Revision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The capture privacy settings revision has been exhausted.");
        }

        return new CapturePrivacySettings(
            evidenceRetentionDays,
            excludeSensitiveApplications,
            pauseInRemoteSessions,
            pauseDuringScreenSharing,
            Revision + 1,
            ExclusionRules);
    }

    public CapturePrivacySettings ChangeRules(CaptureExclusionRuleSet exclusionRules)
    {
        ArgumentNullException.ThrowIfNull(exclusionRules);
        if (ExclusionRules.Equals(exclusionRules))
        {
            return this;
        }

        var revision = ExclusionRules.HasSameEffectivePolicy(exclusionRules)
            ? Revision
            : NextRevision();
        return new CapturePrivacySettings(
            EvidenceRetentionDays,
            ExcludeSensitiveApplications,
            PauseInRemoteSessions,
            PauseDuringScreenSharing,
            revision,
            exclusionRules);
    }

    public bool HasSameEffectivePolicy(CapturePrivacySettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return EvidenceRetentionDays == other.EvidenceRetentionDays
            && ExcludeSensitiveApplications == other.ExcludeSensitiveApplications
            && PauseInRemoteSessions == other.PauseInRemoteSessions
            && PauseDuringScreenSharing == other.PauseDuringScreenSharing
            && ExclusionRules.HasSameEffectivePolicy(other.ExclusionRules);
    }

    private long NextRevision()
    {
        if (Revision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The capture privacy settings revision has been exhausted.");
        }

        return Revision + 1;
    }

    private static int ValidateRetentionDays(int evidenceRetentionDays)
    {
        if (evidenceRetentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceRetentionDays),
                evidenceRetentionDays,
                $"Evidence retention must be between {MinimumRetentionDays} and {MaximumRetentionDays} days.");
        }

        return evidenceRetentionDays;
    }

    private static long ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The capture privacy settings revision must be positive.");
        }

        return revision;
    }
}

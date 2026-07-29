namespace WinDayFlow.Application.Settings;

public sealed record CapturePrivacySettings(
    int EvidenceRetentionDays,
    bool ExcludeSensitiveApplications,
    bool PauseInRemoteSessions,
    bool PauseDuringScreenSharing,
    long Revision,
    CaptureExclusionRuleSet ExclusionRules,
    CaptureApplicationPrivacyMode ApplicationPrivacyMode =
        CaptureApplicationPrivacyMode.ProtectByForegroundApplication)
{
    public static readonly Guid WinDayFlowExclusionRuleId =
        Guid.Parse("df2c2131-bfe5-4a17-bf4c-4f3378a4b093");

    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 365;
    public const int DefaultRetentionDays = 30;

    public static CapturePrivacySettings Default { get; } = new(
        DefaultRetentionDays,
        ExcludeSensitiveApplications: true,
        PauseInRemoteSessions: true,
        PauseDuringScreenSharing: true,
        Revision: 1,
        ExclusionRules: new CaptureExclusionRuleSet(
        [
            CaptureExclusionRule.Create(
                WinDayFlowExclusionRuleId,
                "WinDayFlow",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "WinDayFlow.App.exe"),
        ]),
        ApplicationPrivacyMode:
            CaptureApplicationPrivacyMode.ProtectByForegroundApplication);

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

    public CapturePrivacySettings(
        int EvidenceRetentionDays,
        bool ExcludeSensitiveApplications,
        bool PauseInRemoteSessions,
        bool PauseDuringScreenSharing,
        long Revision,
        CaptureApplicationPrivacyMode ApplicationPrivacyMode)
        : this(
            EvidenceRetentionDays,
            ExcludeSensitiveApplications,
            PauseInRemoteSessions,
            PauseDuringScreenSharing,
            Revision,
            CaptureExclusionRuleSet.Empty,
            ApplicationPrivacyMode)
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

    public CaptureApplicationPrivacyMode ApplicationPrivacyMode { get; } =
        ValidateApplicationPrivacyMode(ApplicationPrivacyMode);

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
            ExclusionRules,
            ApplicationPrivacyMode);
    }

    public CapturePrivacySettings ChangeApplicationPrivacyMode(
        CaptureApplicationPrivacyMode applicationPrivacyMode)
    {
        _ = ValidateApplicationPrivacyMode(applicationPrivacyMode);
        if (applicationPrivacyMode == ApplicationPrivacyMode)
        {
            return this;
        }

        return new CapturePrivacySettings(
            EvidenceRetentionDays,
            ExcludeSensitiveApplications,
            PauseInRemoteSessions,
            PauseDuringScreenSharing,
            NextRevision(),
            ExclusionRules,
            applicationPrivacyMode);
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
            exclusionRules,
            ApplicationPrivacyMode);
    }

    public bool HasSameEffectivePolicy(CapturePrivacySettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return EvidenceRetentionDays == other.EvidenceRetentionDays
            && ExcludeSensitiveApplications == other.ExcludeSensitiveApplications
            && PauseInRemoteSessions == other.PauseInRemoteSessions
            && PauseDuringScreenSharing == other.PauseDuringScreenSharing
            && ApplicationPrivacyMode == other.ApplicationPrivacyMode
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

    private static CaptureApplicationPrivacyMode ValidateApplicationPrivacyMode(
        CaptureApplicationPrivacyMode applicationPrivacyMode)
    {
        if (!Enum.IsDefined(applicationPrivacyMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(applicationPrivacyMode),
                applicationPrivacyMode,
                "The capture application privacy mode is not supported.");
        }

        return applicationPrivacyMode;
    }
}

namespace WinDayFlow.Application.Settings;

public sealed record AppSettings(
    AppThemePreference Theme,
    bool CaptureEnabled,
    bool CloudAnalysisEnabled,
    RecordingConsent? RecordingConsent,
    CapturePrivacySettings CapturePrivacy)
{
    public AppSettings(
        AppThemePreference Theme,
        bool CaptureEnabled,
        bool CloudAnalysisEnabled,
        RecordingConsent? RecordingConsent)
        : this(
            Theme,
            CaptureEnabled,
            CloudAnalysisEnabled,
            RecordingConsent,
            CapturePrivacySettings.Default)
    {
    }

    public static AppSettings Default { get; } = new(
        AppThemePreference.System,
        CaptureEnabled: false,
        CloudAnalysisEnabled: false,
        RecordingConsent: null,
        CapturePrivacySettings.Default);

    public AppThemePreference Theme { get; } = ValidateTheme(Theme);

    public bool CaptureEnabled { get; } = ValidateCaptureEnabled(
        CaptureEnabled,
        RecordingConsent);

    public bool CloudAnalysisEnabled { get; } = CloudAnalysisEnabled;

    public RecordingConsent? RecordingConsent { get; } = RecordingConsent;

    public CapturePrivacySettings CapturePrivacy { get; } = CapturePrivacy
        ?? throw new ArgumentNullException(nameof(CapturePrivacy));

    private static AppThemePreference ValidateTheme(AppThemePreference theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(
                nameof(theme),
                theme,
                "The application theme preference is not supported.");
        }

        return theme;
    }

    private static bool ValidateCaptureEnabled(
        bool captureEnabled,
        RecordingConsent? recordingConsent)
    {
        if (captureEnabled && recordingConsent is null)
        {
            throw new ArgumentException(
                "Capture cannot be enabled without recorded consent.",
                nameof(recordingConsent));
        }

        return captureEnabled;
    }
}

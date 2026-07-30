namespace WinDayFlow.Application.Settings;

public sealed record AppSettings(
    AppThemePreference Theme,
    RecordingConsent? RecordingConsent,
    EvidenceSettings Evidence,
    int CaptureIntervalSeconds = 10,
    CaptureIntent CaptureIntent = CaptureIntent.Stopped)
{
    public static AppSettings Default { get; } = new(
        AppThemePreference.System,
        RecordingConsent: null,
        EvidenceSettings.Default,
        CaptureIntervalSeconds: 10,
        CaptureIntent.Stopped);

    public AppThemePreference Theme { get; } = ValidateTheme(Theme);

    public RecordingConsent? RecordingConsent { get; } = RecordingConsent;

    public EvidenceSettings Evidence { get; } = Evidence
        ?? throw new ArgumentNullException(nameof(Evidence));

    public int CaptureIntervalSeconds { get; } =
        ValidateCaptureIntervalSeconds(CaptureIntervalSeconds);

    public CaptureIntent CaptureIntent { get; } = ValidateCaptureIntent(
        CaptureIntent,
        RecordingConsent);

    public bool CaptureEnabled => CaptureIntent == CaptureIntent.Recording;

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

    private static CaptureIntent ValidateCaptureIntent(
        CaptureIntent captureIntent,
        RecordingConsent? recordingConsent)
    {
        if (captureIntent is not (CaptureIntent.Stopped
            or CaptureIntent.Paused
            or CaptureIntent.Recording))
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureIntent),
                captureIntent,
                "The capture intent is not supported.");
        }

        if (captureIntent != CaptureIntent.Stopped && recordingConsent is null)
        {
            throw new ArgumentException(
                "Capture cannot be armed without recorded consent.",
                nameof(recordingConsent));
        }

        return captureIntent;
    }

    private static int ValidateCaptureIntervalSeconds(int value)
    {
        if (value is not (5 or 10 or 15 or 30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "The capture interval must be 5, 10, 15, 30, or 60 seconds.");
        }

        return value;
    }
}

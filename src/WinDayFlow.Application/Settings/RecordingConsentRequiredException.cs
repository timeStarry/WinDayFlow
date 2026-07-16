namespace WinDayFlow.Application.Settings;

public sealed class RecordingConsentRequiredException : InvalidOperationException
{
    public const string ErrorMessage =
        "Current recording consent is required before capture can be enabled.";

    public RecordingConsentRequiredException()
        : base(ErrorMessage)
    {
    }
}

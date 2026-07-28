using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public interface INativeCaptureApplicationPrivacyModeSource
{
    CaptureApplicationPrivacyMode ApplicationPrivacyMode { get; }

    CaptureState CurrentCaptureState { get; }

    event EventHandler? ApplicationPrivacyModeChanged;
}

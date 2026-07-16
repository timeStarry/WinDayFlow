using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Presentation.Settings;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string SaveErrorText = "无法保存设置，请稍后重试。";
    private const string CaptureErrorText = "无法更改录制状态，请稍后重试。";

    private readonly AppSettingsService _settingsService;
    private readonly ICaptureService _captureService;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeCapture))]
    [NotifyPropertyChangedFor(nameof(CanGrantConsent))]
    [NotifyPropertyChangedFor(nameof(CanRevokeConsent))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public SettingsViewModel(
        AppSettingsService settingsService,
        ICaptureService captureService)
    {
        _settingsService = settingsService
            ?? throw new ArgumentNullException(nameof(settingsService));
        _captureService = captureService
            ?? throw new ArgumentNullException(nameof(captureService));
        _synchronizationContext = SynchronizationContext.Current;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _captureService.StatusChanged += OnCaptureStatusChanged;
    }

    public AppThemePreference Theme => _settingsService.Current.Theme;

    public bool CaptureEnabled => _settingsService.Current.CaptureEnabled;

    public bool CloudAnalysisEnabled => _settingsService.Current.CloudAnalysisEnabled;

    public bool HasValidRecordingConsent => _settingsService.HasValidRecordingConsent;

    public bool IsCaptureBackendAvailable =>
        _captureService.CurrentStatus.State != CaptureState.Unavailable;

    public bool CanChangeCapture =>
        !IsBusy
        && (CaptureEnabled
            || (IsCaptureBackendAvailable && HasValidRecordingConsent));

    public bool CanGrantConsent => !IsBusy && !HasValidRecordingConsent;

    public bool CanRevokeConsent => !IsBusy && HasValidRecordingConsent;

    public bool HasError => ErrorMessage.Length > 0;

    public string ConsentStatusText => HasValidRecordingConsent
        ? "已同意当前录制说明"
        : "尚未同意屏幕活动录制";

    public string ConsentDetailText
    {
        get
        {
            var consent = _settingsService.Current.RecordingConsent;
            return HasValidRecordingConsent && consent is not null
                ? $"版本 {consent.PolicyVersion} · {consent.AcceptedAtUtc.ToLocalTime():g}"
                : "录制保持关闭；你仍可使用手工时间线。";
        }
    }

    public string CaptureAvailabilityText => _captureService.CurrentStatus.State switch
    {
        CaptureState.Unavailable => "原生录制组件尚未接入。",
        CaptureState.BlockedByConsent => "需要先查看并同意录制说明。",
        CaptureState.Recording => "正在将屏幕活动记录到本地。",
        CaptureState.Paused => "录制已暂停。",
        CaptureState.Faulted => "录制组件发生错误。",
        _ => "录制组件已就绪。",
    };

    public async Task<bool> SetThemeAsync(
        AppThemePreference theme,
        CancellationToken cancellationToken = default)
    {
        return await RunMutationAsync(
            token => _settingsService.SetThemeAsync(theme, token),
            SaveErrorText,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<bool> GrantRecordingConsentAsync(
        CancellationToken cancellationToken = default)
    {
        return await RunMutationAsync(
            _settingsService.GrantRecordingConsentAsync,
            SaveErrorText,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<bool> RevokeRecordingConsentAsync(
        CancellationToken cancellationToken = default)
    {
        return await RunMutationAsync(
            async token =>
            {
                if (ShouldStopCapture(_captureService.CurrentStatus.State))
                {
                    await _captureService.StopAsync(token).ConfigureAwait(false);
                }

                await _settingsService
                    .RevokeRecordingConsentAsync(token)
                    .ConfigureAwait(false);
            },
            CaptureErrorText,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<bool> SetCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return await RunMutationAsync(
            async token =>
            {
                if (enabled)
                {
                    await _settingsService
                        .SetCaptureEnabledAsync(enabled: true, token)
                        .ConfigureAwait(false);
                    try
                    {
                        await _captureService.StartAsync(token).ConfigureAwait(false);
                    }
                    catch
                    {
                        await _settingsService
                            .SetCaptureEnabledAsync(enabled: false, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw;
                    }
                }
                else
                {
                    if (ShouldStopCapture(_captureService.CurrentStatus.State))
                    {
                        await _captureService.StopAsync(token).ConfigureAwait(false);
                    }

                    await _settingsService
                        .SetCaptureEnabledAsync(enabled: false, token)
                        .ConfigureAwait(false);
                }
            },
            CaptureErrorText,
            cancellationToken).ConfigureAwait(true);
    }

    public void ClearError()
    {
        ErrorMessage = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _captureService.StatusChanged -= OnCaptureStatusChanged;
    }

    private async Task<bool> RunMutationAsync(
        Func<CancellationToken, Task> mutation,
        string errorText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (_disposed)
        {
            return false;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        bool entered;
        try
        {
            entered = await _mutationGate
                .WaitAsync(0, operation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }

        if (!entered)
        {
            ErrorMessage = "另一项设置操作正在进行，请稍候。";
            return false;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await mutation(operation.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
        catch (Exception)
        {
            ErrorMessage = errorText;
            return false;
        }
        finally
        {
            IsBusy = false;
            _mutationGate.Release();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs args)
    {
        Dispatch(NotifySettingsChanged);
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusChangedEventArgs args)
    {
        Dispatch(NotifyCaptureStatusChanged);
    }

    private void Dispatch(Action update)
    {
        if (_disposed)
        {
            return;
        }

        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var dispatch = ((SettingsViewModel Owner, Action Update))state!;
                    if (!dispatch.Owner._disposed)
                    {
                        dispatch.Update();
                    }
                },
                (this, update));
            return;
        }

        update();
    }

    private void NotifySettingsChanged()
    {
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(CaptureEnabled));
        OnPropertyChanged(nameof(CloudAnalysisEnabled));
        OnPropertyChanged(nameof(HasValidRecordingConsent));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CanGrantConsent));
        OnPropertyChanged(nameof(CanRevokeConsent));
        OnPropertyChanged(nameof(ConsentStatusText));
        OnPropertyChanged(nameof(ConsentDetailText));
    }

    private void NotifyCaptureStatusChanged()
    {
        OnPropertyChanged(nameof(IsCaptureBackendAvailable));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CaptureAvailabilityText));
    }

    private static bool ShouldStopCapture(CaptureState state)
    {
        return state is CaptureState.Starting
            or CaptureState.Recording
            or CaptureState.Pausing
            or CaptureState.Paused
            or CaptureState.Resuming
            or CaptureState.Faulted
            or CaptureState.BlockedByConsent;
    }
}

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
    [NotifyPropertyChangedFor(nameof(CanChangePrivacy))]
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

    public bool HasOutdatedRecordingConsent =>
        !HasValidRecordingConsent
        && _settingsService.Current.RecordingConsent is not null;

    public int EvidenceRetentionDays =>
        _settingsService.Current.CapturePrivacy.EvidenceRetentionDays;

    public bool ExcludeSensitiveApplications =>
        _settingsService.Current.CapturePrivacy.ExcludeSensitiveApplications;

    public bool PauseInRemoteSessions =>
        _settingsService.Current.CapturePrivacy.PauseInRemoteSessions;

    public bool PauseDuringScreenSharing =>
        _settingsService.Current.CapturePrivacy.PauseDuringScreenSharing;

    public long CapturePrivacyRevision =>
        _settingsService.Current.CapturePrivacy.Revision;

    public bool IsCaptureBackendAvailable =>
        _captureService.CurrentStatus.State != CaptureState.Unavailable;

    public bool CanChangeCapture =>
        !IsBusy
        && (CaptureEnabled
            || (IsCaptureBackendAvailable && HasValidRecordingConsent));

    public bool CanGrantConsent => !IsBusy && !HasValidRecordingConsent;

    public bool CanRevokeConsent => !IsBusy && HasValidRecordingConsent;

    public bool CanChangePrivacy => !IsBusy;

    public bool HasError => ErrorMessage.Length > 0;

    public string ConsentStatusText => HasValidRecordingConsent
        ? "已同意当前录制说明"
        : HasOutdatedRecordingConsent
            ? "录制说明或隐私选择已更新"
            : "尚未同意屏幕活动录制";

    public string ConsentDetailText
    {
        get
        {
            var consent = _settingsService.Current.RecordingConsent;
            if (HasValidRecordingConsent && consent is not null)
            {
                return $"版本 {consent.PolicyVersion} · {consent.AcceptedAtUtc.ToLocalTime():g} · 隐私修订 {consent.PrivacyRevision}";
            }

            return HasOutdatedRecordingConsent
                ? "旧授权已失效；录制保持关闭，请重新确认当前隐私选择。"
                : "录制保持关闭；你仍可使用手工时间线。";
        }
    }

    public string RetentionSummaryText => $"屏幕证据保留 {EvidenceRetentionDays} 天";

    public string PrivacySummaryText => string.Join(
        " · ",
        ExcludeSensitiveApplications ? "排除敏感应用" : "不自动排除敏感应用",
        PauseInRemoteSessions ? "远程会话暂停" : "远程会话继续",
        PauseDuringScreenSharing ? "Windows 演示模式暂停" : "Windows 演示模式继续");

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
                var shouldStop = ShouldStopCapture(_captureService.CurrentStatus.State);
                await _settingsService
                    .RevokeRecordingConsentAsync(token)
                    .ConfigureAwait(false);
                if (shouldStop)
                {
                    await _captureService.StopAsync(token).ConfigureAwait(false);
                }
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
                    var shouldStop = ShouldStopCapture(_captureService.CurrentStatus.State);
                    await _settingsService
                        .SetCaptureEnabledAsync(enabled: false, token)
                        .ConfigureAwait(false);
                    if (shouldStop)
                    {
                        await _captureService.StopAsync(token).ConfigureAwait(false);
                    }
                }
            },
            CaptureErrorText,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<bool> SetCapturePrivacyAsync(
        int evidenceRetentionDays,
        bool excludeSensitiveApplications,
        bool pauseInRemoteSessions,
        bool pauseDuringScreenSharing,
        CancellationToken cancellationToken = default)
    {
        return await RunMutationAsync(
            async token =>
            {
                var current = _settingsService.Current.CapturePrivacy;
                if (current.EvidenceRetentionDays == evidenceRetentionDays
                    && current.ExcludeSensitiveApplications == excludeSensitiveApplications
                    && current.PauseInRemoteSessions == pauseInRemoteSessions
                    && current.PauseDuringScreenSharing == pauseDuringScreenSharing)
                {
                    return;
                }

                var shouldStop = ShouldStopCapture(_captureService.CurrentStatus.State);
                await _settingsService.SetCapturePrivacyAsync(
                        evidenceRetentionDays,
                        excludeSensitiveApplications,
                        pauseInRemoteSessions,
                        pauseDuringScreenSharing,
                        token)
                    .ConfigureAwait(false);
                if (shouldStop)
                {
                    await _captureService.StopAsync(token).ConfigureAwait(false);
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
        OnPropertyChanged(nameof(HasOutdatedRecordingConsent));
        OnPropertyChanged(nameof(EvidenceRetentionDays));
        OnPropertyChanged(nameof(ExcludeSensitiveApplications));
        OnPropertyChanged(nameof(PauseInRemoteSessions));
        OnPropertyChanged(nameof(PauseDuringScreenSharing));
        OnPropertyChanged(nameof(CapturePrivacyRevision));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CanGrantConsent));
        OnPropertyChanged(nameof(CanRevokeConsent));
        OnPropertyChanged(nameof(CanChangePrivacy));
        OnPropertyChanged(nameof(ConsentStatusText));
        OnPropertyChanged(nameof(ConsentDetailText));
        OnPropertyChanged(nameof(RetentionSummaryText));
        OnPropertyChanged(nameof(PrivacySummaryText));
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

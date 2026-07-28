using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Presentation.Settings;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string SaveErrorText = "无法保存设置，请稍后重试。";
    private const string CaptureErrorText = "无法更改录制状态，请稍后重试。";
    private const string CaptureModeStopConfirmationErrorText =
        "应用录制范围已保存，旧授权也已失效，但未能确认录制已停止。请退出 WinDayFlow 后重新打开，再重新同意并启用录制。";
    private const string ExclusionRuleErrorText = "无法更改排除规则，请稍后重试。";

    private readonly AppSettingsService _settingsService;
    private readonly ICaptureService _captureService;
    private readonly ObservableCollection<ExclusionRuleItemViewModel> _exclusionRules = [];
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly bool _isExclusionEngineAvailable;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeCapture))]
    [NotifyPropertyChangedFor(nameof(CanGrantConsent))]
    [NotifyPropertyChangedFor(nameof(CanRevokeConsent))]
    [NotifyPropertyChangedFor(nameof(CanChangePrivacy))]
    [NotifyPropertyChangedFor(nameof(CanChangeApplicationPrivacyMode))]
    [NotifyPropertyChangedFor(nameof(CanChangeApplicationProtection))]
    [NotifyPropertyChangedFor(nameof(CanAddExclusionRule))]
    [NotifyPropertyChangedFor(nameof(CanChangeExclusionRules))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleMutationNotice))]
    private string _ruleMutationNoticeText = string.Empty;

    public SettingsViewModel(
        AppSettingsService settingsService,
        ICaptureService captureService,
        bool isExclusionEngineAvailable = false)
    {
        _settingsService = settingsService
            ?? throw new ArgumentNullException(nameof(settingsService));
        _captureService = captureService
            ?? throw new ArgumentNullException(nameof(captureService));
        _isExclusionEngineAvailable = isExclusionEngineAvailable;
        ExclusionRules = new ReadOnlyObservableCollection<ExclusionRuleItemViewModel>(
            _exclusionRules);
        _synchronizationContext = SynchronizationContext.Current;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _captureService.StatusChanged += OnCaptureStatusChanged;
        SynchronizeExclusionRules();
    }

    public ReadOnlyObservableCollection<ExclusionRuleItemViewModel> ExclusionRules { get; }

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

    public CaptureApplicationPrivacyMode ApplicationPrivacyMode =>
        _settingsService.Current.CapturePrivacy.ApplicationPrivacyMode;

    public bool IsForegroundApplicationProtectionEnabled =>
        ApplicationPrivacyMode
            == CaptureApplicationPrivacyMode.ProtectByForegroundApplication;

    public bool IsAllowAllApplicationsMode =>
        ApplicationPrivacyMode
            == CaptureApplicationPrivacyMode.AllowAllApplications;

    public long CapturePrivacyRevision =>
        _settingsService.Current.CapturePrivacy.Revision;

    public bool HasExclusionRules => _exclusionRules.Count > 0;

    public int ExclusionRuleCount => _exclusionRules.Count;

    public int EnabledExclusionRuleCount => _exclusionRules.Count(static rule => rule.IsEnabled);

    public string ExclusionRuleSummaryText => ExclusionRuleCount == 0
        ? "没有自定义规则"
        : $"{ExclusionRuleCount} 条规则 · {EnabledExclusionRuleCount} 条已启用";

    public bool IsCaptureBackendAvailable =>
        _captureService.CurrentStatus.State != CaptureState.Unavailable;

    public bool CanChangeCapture =>
        !IsBusy
        && (CaptureEnabled
            || (IsCaptureBackendAvailable && HasValidRecordingConsent));

    public bool CanGrantConsent => !IsBusy && !HasValidRecordingConsent;

    public bool CanRevokeConsent => !IsBusy && HasValidRecordingConsent;

    public bool CanChangePrivacy => !IsBusy;

    public bool CanChangeApplicationPrivacyMode => !IsBusy;

    public bool CanChangeApplicationProtection =>
        !IsBusy && IsForegroundApplicationProtectionEnabled;

    public bool CanAddExclusionRule =>
        CanChangeApplicationProtection
        && ExclusionRuleCount < CaptureExclusionRuleSet.MaximumRuleCount;

    public bool CanChangeExclusionRules => CanChangeApplicationProtection;

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasRuleMutationNotice => RuleMutationNoticeText.Length > 0;

    public bool IsExclusionEngineAvailable => _isExclusionEngineAvailable;

    public string ExclusionEngineStatusText => IsExclusionEngineAvailable
        ? IsAllowAllApplicationsMode
            ? "连续录制模式已启用；规则保留在本机，切回前台应用保护后恢复生效。"
            : "排除规则监视器已就绪；规则本身不会开启录制。"
        : "规则已保存到本机，尚未接入录制监视器；当前不会用于录制。";

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

    public string ApplicationPrivacyModeDetailText => IsAllowAllApplicationsMode
        ? "每次开始录制时固定当时授权的一个显示器。录制期间切换应用或把焦点移到其他显示器都不会改换目标，因此不会反复重建授权或丢弃未完成录制块；其他显示器不会被录制。要更换录制显示器，请等待录制完全停止，将 WinDayFlow 窗口移到目标显示器，并在那里重新开始录制。该固定显示器上的普通桌面内容都可能进入本地证据。"
        : "切换前台应用时重新验证敏感应用和自定义排除规则；无法确认的窗口会暂停录制。如需减少切换应用造成的暂停、恢复和时间线空档，可选择“固定一个显示器并允许全部应用”；切换范围会停止录制并要求重新同意。";

    public string ContinuousCaptureDisclosureText => IsAllowAllApplicationsMode
        ? "连续录制每次只覆盖开始时授权的一个显示器，不会录制所有显示器。该固定显示器上可见的普通桌面内容都可能进入本地录制证据，包括 WinDayFlow 设置页、AI 提供方配置，以及原本匹配敏感应用或自定义排除规则的内容；这些应用级保护在此模式下暂不生效。录制期间切换应用或把焦点移到其他显示器都不会改换录制目标，这能避免授权反复暂停、恢复和丢弃未完成录制块，使时间线更连续。要更换录制显示器，请等待录制完全停止，将 WinDayFlow 窗口移到目标显示器，并在那里重新开始录制。显示器断开或显示拓扑变化、锁屏或安全桌面、睡眠或唤醒、Windows 会话切换、存储不足或不可读、撤销同意，以及手动暂停或停止仍会中断或停止录制。远程会话和 Windows 演示模式继续遵循下方设置。切换应用录制范围会先停录、使旧授权失效，并要求重新同意。"
        : "选择连续录制后会在这里显示完整的采集范围和暂停边界。";

    public string SensitiveApplicationProtectionDetailText =>
        IsAllowAllApplicationsMode
            ? "当前模式下暂不生效。设置会保留，切回“按前台应用保护”后恢复使用。"
            : "识别到身份验证、密码、财务、健康或私密浏览上下文时将暂停录制。";

    public string PrivacySummaryText => string.Join(
        " · ",
        IsAllowAllApplicationsMode
            ? "固定一个显示器并允许全部应用"
            : "按前台应用保护",
        IsAllowAllApplicationsMode
            ? "敏感应用和自定义排除暂不生效"
            : ExcludeSensitiveApplications
                ? "排除敏感应用"
                : "不自动排除敏感应用",
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
                    ExceptionDispatchInfo? stopFailure = null;
                    if (shouldStop)
                    {
                        try
                        {
                            await _captureService.StopAsync(token).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            stopFailure = ExceptionDispatchInfo.Capture(exception);
                        }
                    }

                    try
                    {
                        // Once an explicit stop begins, persist the fail-closed intent even
                        // when stopping or the originating UI operation is cancelled.
                        await _settingsService
                            .SetCaptureEnabledAsync(
                                enabled: false,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception persistenceFailure)
                    {
                        if (stopFailure is not null)
                        {
                            throw new AggregateException(
                                "Stopping capture and persisting the disabled state both failed.",
                                stopFailure.SourceException,
                                persistenceFailure);
                        }

                        throw;
                    }

                    stopFailure?.Throw();
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

    public async Task<bool> SetCaptureApplicationPrivacyModeAsync(
        CaptureApplicationPrivacyMode applicationPrivacyMode,
        CancellationToken cancellationToken = default)
    {
        var settingsChanged = false;
        var changed = await RunMutationAsync(
            async token =>
            {
                if (_settingsService.Current.CapturePrivacy.ApplicationPrivacyMode
                    == applicationPrivacyMode)
                {
                    return;
                }

                var shouldStop = ShouldStopCapture(_captureService.CurrentStatus.State);
                await _settingsService
                    .SetCaptureApplicationPrivacyModeAsync(
                        applicationPrivacyMode,
                        token)
                    .ConfigureAwait(false);
                settingsChanged = true;
                if (shouldStop)
                {
                    await _captureService.StopAsync(token).ConfigureAwait(false);
                }
            },
            CaptureErrorText,
            cancellationToken).ConfigureAwait(true);

        if (!changed && settingsChanged && !_disposed)
        {
            ErrorMessage = CaptureModeStopConfirmationErrorText;
        }

        return changed;
    }

    public async Task<bool> AddExclusionRuleAsync(
        string name,
        bool enabled,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        return await RunExclusionRuleMutationAsync(
                async token =>
                {
                    var rule = CaptureExclusionRule.Create(
                        Guid.NewGuid(),
                        name,
                        enabled,
                        scope,
                        applicationIdentityKind,
                        identityValue,
                        windowTitleMatchKind,
                        pattern);
                    _ = await _settingsService
                        .AddCaptureExclusionRuleAsync(rule, token)
                        .ConfigureAwait(false);
                },
                "排除规则已添加。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<bool> UpdateExclusionRuleAsync(
        ExclusionRuleItemViewModel item,
        string name,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return await RunExclusionRuleMutationAsync(
                async token =>
                {
                    _ = await _settingsService
                        .UpdateCaptureExclusionRuleAsync(
                            item.Id,
                            item.Revision,
                            name,
                            scope,
                            applicationIdentityKind,
                            identityValue,
                            windowTitleMatchKind,
                            pattern,
                            token)
                        .ConfigureAwait(false);
                },
                "排除规则已保存。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<bool> SetExclusionRuleEnabledAsync(
        ExclusionRuleItemViewModel item,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return await RunExclusionRuleMutationAsync(
                async token =>
                {
                    _ = await _settingsService
                        .SetCaptureExclusionRuleEnabledAsync(
                            item.Id,
                            item.Revision,
                            enabled,
                            token)
                        .ConfigureAwait(false);
                },
                enabled ? "排除规则已启用。" : "排除规则已停用。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<bool> MoveExclusionRuleAsync(
        ExclusionRuleItemViewModel item,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var newIndex = item.Index + offset;
        if (newIndex < 0 || newIndex >= ExclusionRuleCount)
        {
            return false;
        }

        return await RunExclusionRuleMutationAsync(
                async token =>
                {
                    _ = await _settingsService
                        .MoveCaptureExclusionRuleAsync(
                            item.Id,
                            item.Revision,
                            newIndex,
                            token)
                        .ConfigureAwait(false);
                },
                "排除规则顺序已更新。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<bool> DeleteExclusionRuleAsync(
        ExclusionRuleItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return await RunExclusionRuleMutationAsync(
                token => _settingsService.DeleteCaptureExclusionRuleAsync(
                    item.Id,
                    item.Revision,
                    token),
                "排除规则已删除。",
                cancellationToken)
            .ConfigureAwait(true);
    }

    public void ClearError()
    {
        ErrorMessage = string.Empty;
    }

    public void ClearRuleMutationNotice()
    {
        RuleMutationNoticeText = string.Empty;
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

    private async Task<bool> RunExclusionRuleMutationAsync(
        Func<CancellationToken, Task> mutation,
        string successText,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        RuleMutationNoticeText = string.Empty;
        var changed = await RunMutationAsync(
                async token =>
                {
                    var captureWasEnabled = _settingsService.Current.CaptureEnabled;
                    var shouldStop = captureWasEnabled
                        && ShouldStopCapture(_captureService.CurrentStatus.State);
                    await mutation(token).ConfigureAwait(false);
                    if (shouldStop && !_settingsService.Current.CaptureEnabled)
                    {
                        await _captureService.StopAsync(token).ConfigureAwait(false);
                    }
                },
                ExclusionRuleErrorText,
                cancellationToken)
            .ConfigureAwait(true);

        if (!changed || _disposed)
        {
            return false;
        }

        SynchronizeExclusionRules();
        if (_disposed)
        {
            return false;
        }

        RuleMutationNoticeText = successText;
        return true;
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
        SynchronizeExclusionRules();
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(CaptureEnabled));
        OnPropertyChanged(nameof(CloudAnalysisEnabled));
        OnPropertyChanged(nameof(HasValidRecordingConsent));
        OnPropertyChanged(nameof(HasOutdatedRecordingConsent));
        OnPropertyChanged(nameof(EvidenceRetentionDays));
        OnPropertyChanged(nameof(ExcludeSensitiveApplications));
        OnPropertyChanged(nameof(PauseInRemoteSessions));
        OnPropertyChanged(nameof(PauseDuringScreenSharing));
        OnPropertyChanged(nameof(ApplicationPrivacyMode));
        OnPropertyChanged(nameof(IsForegroundApplicationProtectionEnabled));
        OnPropertyChanged(nameof(IsAllowAllApplicationsMode));
        OnPropertyChanged(nameof(CapturePrivacyRevision));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CanGrantConsent));
        OnPropertyChanged(nameof(CanRevokeConsent));
        OnPropertyChanged(nameof(CanChangePrivacy));
        OnPropertyChanged(nameof(CanChangeApplicationPrivacyMode));
        OnPropertyChanged(nameof(CanChangeApplicationProtection));
        OnPropertyChanged(nameof(ConsentStatusText));
        OnPropertyChanged(nameof(ConsentDetailText));
        OnPropertyChanged(nameof(RetentionSummaryText));
        OnPropertyChanged(nameof(ApplicationPrivacyModeDetailText));
        OnPropertyChanged(nameof(ContinuousCaptureDisclosureText));
        OnPropertyChanged(nameof(SensitiveApplicationProtectionDetailText));
        OnPropertyChanged(nameof(PrivacySummaryText));
        OnPropertyChanged(nameof(HasExclusionRules));
        OnPropertyChanged(nameof(ExclusionRuleCount));
        OnPropertyChanged(nameof(EnabledExclusionRuleCount));
        OnPropertyChanged(nameof(ExclusionRuleSummaryText));
        OnPropertyChanged(nameof(ExclusionEngineStatusText));
        OnPropertyChanged(nameof(CanAddExclusionRule));
        OnPropertyChanged(nameof(CanChangeExclusionRules));
    }

    private void NotifyCaptureStatusChanged()
    {
        OnPropertyChanged(nameof(IsCaptureBackendAvailable));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CaptureAvailabilityText));
        OnPropertyChanged(nameof(IsExclusionEngineAvailable));
        OnPropertyChanged(nameof(ExclusionEngineStatusText));
    }

    private void SynchronizeExclusionRules()
    {
        var rules = _settingsService.Current.CapturePrivacy.ExclusionRules.Rules;
        var identifiers = rules.Select(static rule => rule.Id).ToHashSet();
        for (var index = _exclusionRules.Count - 1; index >= 0; index--)
        {
            if (!identifiers.Contains(_exclusionRules[index].Id))
            {
                _exclusionRules.RemoveAt(index);
            }
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var existingIndex = -1;
            for (var candidate = index; candidate < _exclusionRules.Count; candidate++)
            {
                if (_exclusionRules[candidate].Id == rule.Id)
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _exclusionRules.Insert(
                    index,
                    new ExclusionRuleItemViewModel(rule, index, rules.Count));
            }
            else
            {
                if (existingIndex != index)
                {
                    _exclusionRules.Move(existingIndex, index);
                }

                _exclusionRules[index].Update(rule, index, rules.Count);
            }
        }

        for (var index = 0; index < _exclusionRules.Count; index++)
        {
            _exclusionRules[index].Update(rules[index], index, rules.Count);
        }
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

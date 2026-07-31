using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Presentation.Settings;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string SaveErrorText = "无法保存设置，请稍后重试。";
    private const string CaptureErrorText = "无法更改录制状态，请稍后重试。";
    private const string SendRuleErrorText = "无法更改不发送规则，请稍后重试。";

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
    [NotifyPropertyChangedFor(nameof(CanChangeCaptureInterval))]
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
        bool isExclusionEngineAvailable = true)
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

    public bool CaptureEnabled =>
        _settingsService.Current.CaptureIntent == CaptureIntent.Recording;

    public int CaptureIntervalSeconds =>
        _settingsService.Current.CaptureIntervalSeconds;

    public bool HasValidRecordingConsent => _settingsService.HasValidRecordingConsent;

    public bool HasOutdatedRecordingConsent =>
        !HasValidRecordingConsent
        && _settingsService.Current.RecordingConsent is not null;

    public int EvidenceRetentionDays => _settingsService.Current.Evidence.RetentionDays;

    public bool HasExclusionRules => _exclusionRules.Count > 0;

    public int ExclusionRuleCount => _exclusionRules.Count;

    public int EnabledExclusionRuleCount =>
        _exclusionRules.Count(static rule => rule.IsEnabled);

    public string ExclusionRuleSummaryText => ExclusionRuleCount == 0
        ? "没有不发送规则"
        : $"{ExclusionRuleCount} 条规则 · {EnabledExclusionRuleCount} 条已启用";

    public bool IsCaptureBackendAvailable =>
        _captureService.CurrentStatus.State != CaptureState.Unavailable;

    public bool CanChangeCapture =>
        !IsBusy
        && (CaptureEnabled
            || (IsCaptureBackendAvailable && HasValidRecordingConsent));

    public bool CanGrantConsent => !IsBusy && !HasValidRecordingConsent;

    public bool CanRevokeConsent => !IsBusy && _settingsService.Current.RecordingConsent is not null;

    public bool CanChangePrivacy => !IsBusy;

    public bool CanChangeCaptureInterval => !IsBusy;

    public bool CanAddExclusionRule =>
        !IsBusy && ExclusionRuleCount < CaptureExclusionRuleSet.MaximumRuleCount;

    public bool CanChangeExclusionRules => !IsBusy;

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasRuleMutationNotice => RuleMutationNoticeText.Length > 0;

    public bool IsExclusionEngineAvailable => _isExclusionEngineAvailable;

    public string ExclusionEngineStatusText => _isExclusionEngineAvailable
        ? "不发送规则仅在实际供应商请求前检查，不会暂停或停止本地录制。"
        : "不发送规则检查当前不可用；本地录制不会因此暂停或停止。";

    public string ConsentStatusText => HasValidRecordingConsent
        ? "已同意当前录制说明"
        : HasOutdatedRecordingConsent
            ? "录制说明已更新"
            : "尚未同意屏幕活动录制";

    public string ConsentDetailText
    {
        get
        {
            var consent = _settingsService.Current.RecordingConsent;
            if (HasValidRecordingConsent && consent is not null)
            {
                return $"版本 {consent.PolicyVersion} · {consent.AcceptedAtUtc.ToLocalTime():g}";
            }

            return HasOutdatedRecordingConsent
                ? "旧授权已失效；请重新确认当前录制说明。"
                : "录制默认关闭；你仍可使用手工时间线。";
        }
    }

    public string RetentionSummaryText => EvidenceRetentionDays
        == EvidenceSettings.UnlimitedRetentionDays
            ? "屏幕证据不自动清理"
            : $"屏幕证据保留 {EvidenceRetentionDays} 天";

    public string PrivacySummaryText => EnabledExclusionRuleCount == 0
        ? "本地录制持续独立运行；隐私检查和阶段供应商由用户分别配置"
        : $"本地录制持续独立运行；另有 {EnabledExclusionRuleCount} 条不发送规则在联网前检查";

    public string CaptureAvailabilityText => _captureService.CurrentStatus.State switch
    {
        CaptureState.Unavailable => "原生录制组件尚未接入。",
        CaptureState.BlockedByConsent => "需要先查看并同意录制说明。",
        CaptureState.Recording => "正在将屏幕活动记录到本地。",
        CaptureState.Paused => "录制已由用户暂停。",
        CaptureState.Faulted or CaptureState.NeedsAttention =>
            _captureService.CurrentStatus.Detail ?? "录制需要处理，请检查显示器、权限或存储。",
        _ => "录制组件已就绪。",
    };

    public Task<bool> SetThemeAsync(
        AppThemePreference theme,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            token => _settingsService.SetThemeAsync(theme, token),
            SaveErrorText,
            cancellationToken);

    public Task<bool> GrantRecordingConsentAsync(
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            _settingsService.GrantRecordingConsentAsync,
            SaveErrorText,
            cancellationToken);

    public Task<bool> RevokeRecordingConsentAsync(
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            async token =>
            {
                await _settingsService.RevokeRecordingConsentAsync(token)
                    .ConfigureAwait(false);
                if (ShouldStopCapture(_captureService.CurrentStatus.State))
                {
                    await _captureService.StopAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            },
            CaptureErrorText,
            cancellationToken);

    public Task<bool> SetCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            token => enabled
                ? _captureService.StartAsync(token)
                : _captureService.StopAsync(token),
            CaptureErrorText,
            cancellationToken);

    public Task<bool> SetEvidenceRetentionDaysAsync(
        int evidenceRetentionDays,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            token => _settingsService.SetEvidenceRetentionDaysAsync(
                evidenceRetentionDays,
                token),
            SaveErrorText,
            cancellationToken);

    public Task<bool> SetCaptureIntervalSecondsAsync(
        int captureIntervalSeconds,
        CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            async token =>
            {
                if (_settingsService.Current.CaptureIntervalSeconds == captureIntervalSeconds)
                {
                    return;
                }

                var restart = _settingsService.Current.CaptureIntent == CaptureIntent.Recording
                    && ShouldStopCapture(_captureService.CurrentStatus.State);
                if (restart)
                {
                    await _captureService.StopAsync(token).ConfigureAwait(false);
                }
                await _settingsService.SetCaptureIntervalSecondsAsync(
                        captureIntervalSeconds,
                        token)
                    .ConfigureAwait(false);
                if (restart)
                {
                    await _captureService.StartAsync(token).ConfigureAwait(false);
                }
            },
            CaptureErrorText,
            cancellationToken);

    public Task<bool> AddExclusionRuleAsync(
        string name,
        bool enabled,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern,
        CancellationToken cancellationToken = default) =>
        RunSendRuleMutationAsync(
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
                _ = await _settingsService.AddCaptureExclusionRuleAsync(rule, token)
                    .ConfigureAwait(false);
            },
            "不发送规则已添加。",
            cancellationToken);

    public Task<bool> UpdateExclusionRuleAsync(
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
        return RunSendRuleMutationAsync(
            async token =>
            {
                _ = await _settingsService.UpdateCaptureExclusionRuleAsync(
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
            "不发送规则已保存。",
            cancellationToken);
    }

    public Task<bool> SetExclusionRuleEnabledAsync(
        ExclusionRuleItemViewModel item,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunSendRuleMutationAsync(
            async token =>
            {
                _ = await _settingsService.SetCaptureExclusionRuleEnabledAsync(
                        item.Id,
                        item.Revision,
                        enabled,
                        token)
                    .ConfigureAwait(false);
            },
            enabled ? "不发送规则已启用。" : "不发送规则已停用。",
            cancellationToken);
    }

    public Task<bool> MoveExclusionRuleAsync(
        ExclusionRuleItemViewModel item,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var newIndex = item.Index + offset;
        if (newIndex < 0 || newIndex >= ExclusionRuleCount)
        {
            return Task.FromResult(false);
        }

        return RunSendRuleMutationAsync(
            async token =>
            {
                _ = await _settingsService.MoveCaptureExclusionRuleAsync(
                        item.Id,
                        item.Revision,
                        newIndex,
                        token)
                    .ConfigureAwait(false);
            },
            "不发送规则顺序已更新。",
            cancellationToken);
    }

    public Task<bool> DeleteExclusionRuleAsync(
        ExclusionRuleItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunSendRuleMutationAsync(
            token => _settingsService.DeleteCaptureExclusionRuleAsync(
                item.Id,
                item.Revision,
                token),
            "不发送规则已删除。",
            cancellationToken);
    }

    public void ClearError() => ErrorMessage = string.Empty;

    public void ClearRuleMutationNotice() => RuleMutationNoticeText = string.Empty;

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
        _lifetimeCancellation.Dispose();
        _mutationGate.Dispose();
    }

    private async Task<bool> RunSendRuleMutationAsync(
        Func<CancellationToken, Task> mutation,
        string successText,
        CancellationToken cancellationToken)
    {
        RuleMutationNoticeText = string.Empty;
        var changed = await RunMutationAsync(
                mutation,
                SendRuleErrorText,
                cancellationToken)
            .ConfigureAwait(true);
        if (!changed || _disposed)
        {
            return false;
        }

        SynchronizeExclusionRules();
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
            entered = await _mutationGate.WaitAsync(0, operation.Token)
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
        catch
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
        _ = sender;
        _ = args;
        Dispatch(NotifySettingsChanged);
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusChangedEventArgs args)
    {
        _ = sender;
        _ = args;
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
                    var value = ((SettingsViewModel Owner, Action Update))state!;
                    if (!value.Owner._disposed)
                    {
                        value.Update();
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
        OnPropertyChanged(nameof(CaptureIntervalSeconds));
        OnPropertyChanged(nameof(HasValidRecordingConsent));
        OnPropertyChanged(nameof(HasOutdatedRecordingConsent));
        OnPropertyChanged(nameof(EvidenceRetentionDays));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CanGrantConsent));
        OnPropertyChanged(nameof(CanRevokeConsent));
        OnPropertyChanged(nameof(ConsentStatusText));
        OnPropertyChanged(nameof(ConsentDetailText));
        OnPropertyChanged(nameof(RetentionSummaryText));
        OnPropertyChanged(nameof(PrivacySummaryText));
        NotifyRulesChanged();
    }

    private void NotifyCaptureStatusChanged()
    {
        OnPropertyChanged(nameof(IsCaptureBackendAvailable));
        OnPropertyChanged(nameof(CanChangeCapture));
        OnPropertyChanged(nameof(CaptureAvailabilityText));
    }

    private void NotifyRulesChanged()
    {
        OnPropertyChanged(nameof(HasExclusionRules));
        OnPropertyChanged(nameof(ExclusionRuleCount));
        OnPropertyChanged(nameof(EnabledExclusionRuleCount));
        OnPropertyChanged(nameof(ExclusionRuleSummaryText));
        OnPropertyChanged(nameof(ExclusionEngineStatusText));
        OnPropertyChanged(nameof(CanAddExclusionRule));
        OnPropertyChanged(nameof(CanChangeExclusionRules));
    }

    private void SynchronizeExclusionRules()
    {
        var rules = _settingsService.Current.Evidence.SendRules.Rules;
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
            for (var candidateIndex = 0;
                 candidateIndex < _exclusionRules.Count;
                 candidateIndex++)
            {
                if (_exclusionRules[candidateIndex].Id == rule.Id)
                {
                    existingIndex = candidateIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _exclusionRules.Insert(
                    index,
                    new ExclusionRuleItemViewModel(rule, index, rules.Count));
                continue;
            }

            if (existingIndex != index)
            {
                _exclusionRules.Move(existingIndex, index);
            }
            _exclusionRules[index].Update(rule, index, rules.Count);
        }

        for (var index = 0; index < _exclusionRules.Count; index++)
        {
            _exclusionRules[index].Update(rules[index], index, rules.Count);
        }
        NotifyRulesChanged();
    }

    private static bool ShouldStopCapture(CaptureState state) => state is
        CaptureState.Starting
        or CaptureState.Recording
        or CaptureState.Pausing
        or CaptureState.Paused
        or CaptureState.Resuming
        or CaptureState.Faulted
        or CaptureState.BlockedByConsent
        or CaptureState.NeedsAttention;
}

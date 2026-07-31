using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;

namespace WinDayFlow.Presentation.Settings;

public sealed partial class AiRoutingSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AiProviderRoutingService _service;
    private readonly IAnalysisPipelineScheduler? _scheduler;
    private readonly CancellationTokenSource _lifetime = new();
    private int _operationActive;
    private int _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _privacyEnabled;

    [ObservableProperty]
    private bool _timelineEnabled;

    [ObservableProperty]
    private AiProviderProfileItemViewModel? _privacyProvider;

    [ObservableProperty]
    private AiProviderProfileItemViewModel? _timelineProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivacyOnMatchIndex))]
    private PrivacyMatchAction _privacyOnMatch = PrivacyMatchAction.RedactAndContinue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivacyOnErrorIndex))]
    private PrivacyFailureAction _privacyOnError = PrivacyFailureAction.Hold;

    private long _privacyRouteRevision = 1;
    private long _timelineRouteRevision = 1;

    public AiRoutingSettingsViewModel(
        AiProviderRoutingService service,
        IAnalysisPipelineScheduler? scheduler = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _scheduler = scheduler;
    }

    public ObservableCollection<AiProviderProfileItemViewModel> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count != 0;

    public bool HasError => ErrorMessage.Length != 0;

    public bool HasNotice => NoticeMessage.Length != 0;

    public bool CanMutate => !IsBusy;

    public int PrivacyOnMatchIndex
    {
        get => (int)PrivacyOnMatch;
        set => PrivacyOnMatch = (PrivacyMatchAction)value;
    }

    public int PrivacyOnErrorIndex
    {
        get => (int)PrivacyOnError;
        set => PrivacyOnError = (PrivacyFailureAction)value;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async token =>
        {
            var profiles = await _service.ListProfilesAsync(token);
            var items = new List<AiProviderProfileItemViewModel>(profiles.Count);
            foreach (var profile in profiles)
            {
                var privacyValidation = await _service.GetStageValidationAsync(
                    profile.Profile.Id,
                    profile.Revision,
                    AnalysisStage.PrivacyInspection,
                    token);
                var timelineValidation = await _service.GetStageValidationAsync(
                    profile.Profile.Id,
                    profile.Revision,
                    AnalysisStage.TimelineAnalysis,
                    token);
                items.Add(new AiProviderProfileItemViewModel(
                    profile,
                    privacyValidation is not null,
                    timelineValidation is not null));
            }

            var bindings = await _service.ListBindingsAsync(token);
            Apply(items, bindings);
        }, successNotice: null, cancellationToken);
    }

    public Task<bool> SaveProfileAsync(
        Guid? profileId,
        long? expectedRevision,
        string displayName,
        string baseEndpoint,
        string model,
        int requestTimeoutSeconds,
        int maximumConcurrency,
        string? replacementApiKey,
        bool clearApiKey,
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            if (profileId.HasValue)
            {
                if (!expectedRevision.HasValue)
                {
                    throw new ArgumentException("An edited provider requires its revision.");
                }
                _ = await _service.UpdateProfileAsync(
                    profileId.Value,
                    expectedRevision.Value,
                    displayName,
                    baseEndpoint,
                    model,
                    requestTimeoutSeconds,
                    replacementApiKey,
                    clearApiKey,
                    maximumConcurrency,
                    token);
            }
            else
            {
                if (clearApiKey)
                {
                    throw new ArgumentException("A new provider has no saved key to clear.");
                }
                _ = await _service.CreateProfileAsync(
                    displayName,
                    baseEndpoint,
                    model,
                    requestTimeoutSeconds,
                    replacementApiKey,
                    maximumConcurrency,
                    token);
            }
            await ReloadAsync(token);
            _scheduler?.RequestRun();
        }, "供应商配置已保存；阶段绑定没有自动更改。", cancellationToken);

    public Task<bool> DeleteProfileAsync(
        AiProviderProfileItemViewModel profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunAsync(async token =>
        {
            await _service.DeleteProfileAsync(profile.Id, profile.Revision, token);
            await ReloadAsync(token);
            _scheduler?.RequestRun();
        }, "供应商已删除。", cancellationToken);
    }

    public Task<bool> ValidateStageAsync(
        AiProviderProfileItemViewModel profile,
        AnalysisStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunAsync(async token =>
        {
            _ = await _service.ValidateStageAsync(profile.Id, stage, token);
            await ReloadAsync(token);
            _scheduler?.RequestRun();
        }, stage == AnalysisStage.PrivacyInspection
            ? "隐私检查结构化输出验证通过。"
            : "时间线分析结构化输出验证通过。", cancellationToken);
    }

    public Task<bool> SavePrivacyBindingAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var saved = await _service.SaveBindingAsync(
                AnalysisStage.PrivacyInspection,
                PrivacyEnabled,
                PrivacyProvider?.Id,
                _privacyRouteRevision,
                new PrivacyStageOptions(PrivacyOnMatch, PrivacyOnError),
                token);
            _privacyRouteRevision = saved.RouteRevision;
            await ReloadAsync(token);
            _scheduler?.RequestRun();
        }, PrivacyEnabled
            ? "隐私检查路由已启用。"
            : "隐私检查已关闭；原始证据保持本地记录。", cancellationToken);

    public Task<bool> SaveTimelineBindingAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var saved = await _service.SaveBindingAsync(
                AnalysisStage.TimelineAnalysis,
                TimelineEnabled,
                TimelineProvider?.Id,
                _timelineRouteRevision,
                privacyOptions: null,
                token);
            _timelineRouteRevision = saved.RouteRevision;
            await ReloadAsync(token);
            _scheduler?.RequestRun();
        }, TimelineEnabled
            ? "时间线分析路由已启用。"
            : "时间线分析已关闭；本地录制不受影响。", cancellationToken);

    public void ClearMessages()
    {
        ErrorMessage = string.Empty;
        NoticeMessage = string.Empty;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var profiles = await _service.ListProfilesAsync(cancellationToken);
        var items = new List<AiProviderProfileItemViewModel>(profiles.Count);
        foreach (var profile in profiles)
        {
            var privacyValidation = await _service.GetStageValidationAsync(
                profile.Profile.Id,
                profile.Revision,
                AnalysisStage.PrivacyInspection,
                cancellationToken);
            var timelineValidation = await _service.GetStageValidationAsync(
                profile.Profile.Id,
                profile.Revision,
                AnalysisStage.TimelineAnalysis,
                cancellationToken);
            items.Add(new AiProviderProfileItemViewModel(
                profile,
                privacyValidation is not null,
                timelineValidation is not null));
        }
        Apply(items, await _service.ListBindingsAsync(cancellationToken));
    }

    private void Apply(
        IReadOnlyList<AiProviderProfileItemViewModel> profiles,
        IReadOnlyList<AnalysisStageBinding> bindings)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }
        OnPropertyChanged(nameof(HasProfiles));

        var privacy = bindings.Single(binding => binding.Stage == AnalysisStage.PrivacyInspection);
        var timeline = bindings.Single(binding => binding.Stage == AnalysisStage.TimelineAnalysis);
        _privacyRouteRevision = privacy.RouteRevision;
        _timelineRouteRevision = timeline.RouteRevision;
        PrivacyEnabled = privacy.Enabled;
        TimelineEnabled = timeline.Enabled;
        PrivacyOnMatch = privacy.PrivacyOptions?.OnMatch
            ?? PrivacyMatchAction.RedactAndContinue;
        PrivacyOnError = privacy.PrivacyOptions?.OnError
            ?? PrivacyFailureAction.Hold;
        PrivacyProvider = FindProfile(privacy.ProviderProfileId);
        TimelineProvider = FindProfile(timeline.ProviderProfileId);
    }

    private AiProviderProfileItemViewModel? FindProfile(Guid? id) => id.HasValue
        ? Profiles.FirstOrDefault(profile => profile.Id == id.Value)
        : null;

    private async Task<bool> RunAsync(
        Func<CancellationToken, Task> operation,
        string? successNotice,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            ErrorMessage = "另一项供应商设置操作正在进行，请稍候。";
            NoticeMessage = string.Empty;
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            IsBusy = true;
            ClearMessages();
            await operation(linked.Token);
            NoticeMessage = successNotice ?? string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return false;
        }
        catch (AiProviderProfileInUseException exception)
        {
            var stages = exception.Stages.Count == 0
                ? "处理阶段"
                : string.Join("、", exception.Stages.Select(StageName));
            ErrorMessage = $"该供应商仍被{stages}引用，请先修改阶段绑定。";
            NoticeMessage = string.Empty;
            return false;
        }
        catch (AiProviderConfigurationConflictException)
        {
            ErrorMessage = "供应商配置已在其他位置更改，请刷新后重试。";
            NoticeMessage = string.Empty;
            return false;
        }
        catch (AnalysisStageBindingConflictException)
        {
            ErrorMessage = "阶段路由已在其他位置更改，请刷新后重试。";
            NoticeMessage = string.Empty;
            return false;
        }
        catch (AiProviderException exception)
        {
            ErrorMessage = DescribeProviderError(exception.ErrorCode);
            NoticeMessage = string.Empty;
            return false;
        }
        catch (Exception)
        {
            ErrorMessage = "无法完成供应商设置操作，请检查输入后重试。";
            NoticeMessage = string.Empty;
            return false;
        }
        finally
        {
            IsBusy = false;
            Volatile.Write(ref _operationActive, 0);
        }
    }

    private static string StageName(AnalysisStage stage) => stage switch
    {
        AnalysisStage.PrivacyInspection => "隐私检查",
        AnalysisStage.TimelineAnalysis => "时间线分析",
        _ => "处理阶段",
    };

    private static string DescribeProviderError(AiProviderErrorCode code) => code switch
    {
        AiProviderErrorCode.AuthenticationFailed => "API 密钥未通过供应商验证。",
        AiProviderErrorCode.AccessDenied => "当前 API 密钥无权使用所选模型。",
        AiProviderErrorCode.ModelNotFound => "供应商未找到所选模型或接口。",
        AiProviderErrorCode.UnsupportedCapability => "供应商不支持该阶段需要的结构化输出。",
        AiProviderErrorCode.RateLimited => "供应商请求过于频繁，请稍后重试。",
        AiProviderErrorCode.NetworkUnavailable => "无法连接供应商，请检查地址和网络。",
        AiProviderErrorCode.Timeout => "供应商响应超时。",
        AiProviderErrorCode.InvalidResponse => "供应商返回了不兼容的结构化结果。",
        AiProviderErrorCode.InvalidConfiguration => "供应商配置无效，请检查地址、模型和 API 密钥。",
        _ => "供应商请求失败，请检查配置后重试。",
    };
}

public sealed class AiProviderProfileItemViewModel
{
    public AiProviderProfileItemViewModel(
        AiProviderProfileSnapshot snapshot,
        bool privacyValidated,
        bool timelineValidated)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        PrivacyValidated = privacyValidated;
        TimelineValidated = timelineValidated;
    }

    public AiProviderProfileSnapshot Snapshot { get; }
    public Guid Id => Snapshot.Profile.Id;
    public long Revision => Snapshot.Revision;
    public string DisplayName => Snapshot.Profile.DisplayName;
    public string BaseEndpoint => Snapshot.Profile.BaseEndpoint.AbsoluteUri;
    public string Model => Snapshot.Profile.Model;
    public int RequestTimeoutSeconds => checked((int)Snapshot.Profile.RequestTimeout.TotalSeconds);
    public int MaximumConcurrency => Snapshot.Profile.MaximumConcurrency;
    public bool HasApiKey => Snapshot.HasApiKey;
    public bool PrivacyValidated { get; }
    public bool TimelineValidated { get; }
    public string EndpointSummary => Snapshot.Profile.IsLoopback
        ? $"本机 · {Model}"
        : $"{Snapshot.Profile.BaseEndpoint.Host} · {Model}";
    public string ValidationSummary => PrivacyValidated && TimelineValidated
        ? "隐私检查与时间线分析均已验证"
        : PrivacyValidated
            ? "已验证隐私检查"
            : TimelineValidated
                ? "已验证时间线分析"
                : "尚未验证处理阶段";
    public string ConcurrencySummary => $"后台最大并发 {MaximumConcurrency}";
}

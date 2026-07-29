using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;

namespace WinDayFlow.Presentation.Timeline;

public sealed partial class TimelineViewModel : ObservableObject, IDisposable
{
    private const string LoadErrorText = "无法加载时间线，请稍后重试。";
    private const string IntervalLoadErrorText = "暂时无法读取录制处理状态。";
    private const string SaveErrorText = "无法保存活动，请检查内容后重试。";
    private const string DeleteErrorText = "无法删除活动，请刷新时间线后重试。";
    private const string RetryAnalysisErrorText = "无法重新安排分析，请稍后重试。";
    private const string RetryAnalysisEvidenceUnavailableText =
        "本地录制证据已不可用，无法重试分析。";
    private const string RetryAnalysisAttemptLimitText =
        "此录制内容已达到重试次数上限。";
    private const string RetryAnalysisPipelineErrorText =
        "无法立即重试后台分析，请稍后再试。";
    private static readonly TimeSpan AnalysisRefreshDebounceDelay =
        TimeSpan.FromMilliseconds(200);

    private readonly TimelineQueryService _queryService;
    private readonly TimelineCommandService? _commandService;
    private readonly IUnprocessedIntervalRepository? _unprocessedIntervalRepository;
    private readonly IAnalysisPipelineStatusSource? _analysisPipelineStatusSource;
    private readonly AnalysisJobRetryService? _analysisJobRetryService;
    private readonly IAnalysisPipelineScheduler? _analysisPipelineScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _analysisRefreshSync = new();
    private readonly object _analysisStatusSync = new();
    private IReadOnlyList<TimelineEntry> _dayEntries = [];
    private CancellationTokenSource? _loadCancellation;
    private AnalysisRefreshRequest? _analysisRefreshRequest;
    private long _loadVersion;
    private long _analysisRefreshVersion;
    private long _observedAnalysisDataRevision;
    private long _observedAnalysisStatusSequence;
    private int _explicitLoadCount;
    private int _silentRefreshPending;
    private DateOnly _selectedDate;
    private string _searchText = string.Empty;
    private ActivityCategory? _selectedCategory;
    private ProductivityKind? _selectedProductivity;
    private AnalysisPipelineStatus? _analysisPipelineStatus;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutateSelectedEntry))]
    private TimelineEntryItemViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutateSelectedEntry))]
    [NotifyCanExecuteChangedFor(nameof(RetryAnalysisCommand))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMutationError))]
    private string _mutationErrorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isInitialized;

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService: null,
            unprocessedIntervalRepository: null,
            analysisPipelineStatusSource: null,
            analysisJobRetryService: null,
            analysisPipelineScheduler: null,
            timeProvider,
            initialize: true)
    {
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService: null,
            unprocessedIntervalRepository
                ?? throw new ArgumentNullException(
                    nameof(unprocessedIntervalRepository)),
            analysisPipelineStatusSource: null,
            analysisJobRetryService: null,
            analysisPipelineScheduler: null,
            timeProvider,
            initialize: true)
    {
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService
                ?? throw new ArgumentNullException(nameof(commandService)),
            unprocessedIntervalRepository: null,
            analysisPipelineStatusSource: null,
            analysisJobRetryService: null,
            analysisPipelineScheduler: null,
            timeProvider,
            initialize: true)
    {
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService
                ?? throw new ArgumentNullException(nameof(commandService)),
            unprocessedIntervalRepository
                ?? throw new ArgumentNullException(
                    nameof(unprocessedIntervalRepository)),
            analysisPipelineStatusSource: null,
            analysisJobRetryService: null,
            analysisPipelineScheduler: null,
            timeProvider,
            initialize: true)
    {
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        IAnalysisPipelineStatusSource analysisPipelineStatusSource,
        AnalysisJobRetryService analysisJobRetryService,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService
                ?? throw new ArgumentNullException(nameof(commandService)),
            unprocessedIntervalRepository
                ?? throw new ArgumentNullException(
                    nameof(unprocessedIntervalRepository)),
            analysisPipelineStatusSource
                ?? throw new ArgumentNullException(
                    nameof(analysisPipelineStatusSource)),
            analysisJobRetryService
                ?? throw new ArgumentNullException(nameof(analysisJobRetryService)),
            analysisPipelineScheduler: null,
            timeProvider,
            initialize: true)
    {
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        IAnalysisPipelineStatusSource analysisPipelineStatusSource,
        AnalysisJobRetryService analysisJobRetryService,
        IAnalysisPipelineScheduler analysisPipelineScheduler,
        TimeProvider? timeProvider = null)
        : this(
            queryService,
            commandService
                ?? throw new ArgumentNullException(nameof(commandService)),
            unprocessedIntervalRepository
                ?? throw new ArgumentNullException(
                    nameof(unprocessedIntervalRepository)),
            analysisPipelineStatusSource
                ?? throw new ArgumentNullException(
                    nameof(analysisPipelineStatusSource)),
            analysisJobRetryService
                ?? throw new ArgumentNullException(nameof(analysisJobRetryService)),
            analysisPipelineScheduler
                ?? throw new ArgumentNullException(nameof(analysisPipelineScheduler)),
            timeProvider,
            initialize: true)
    {
    }

    private TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService? commandService,
        IUnprocessedIntervalRepository? unprocessedIntervalRepository,
        IAnalysisPipelineStatusSource? analysisPipelineStatusSource,
        AnalysisJobRetryService? analysisJobRetryService,
        IAnalysisPipelineScheduler? analysisPipelineScheduler,
        TimeProvider? timeProvider,
        bool initialize)
    {
        _ = initialize;
        _queryService = queryService
            ?? throw new ArgumentNullException(nameof(queryService));
        _commandService = commandService;
        _unprocessedIntervalRepository = unprocessedIntervalRepository;
        _analysisPipelineStatusSource = analysisPipelineStatusSource;
        _analysisJobRetryService = analysisJobRetryService;
        _analysisPipelineScheduler = analysisPipelineScheduler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _synchronizationContext = SynchronizationContext.Current;
        _selectedDate = GetToday();

        if (_analysisPipelineStatusSource is not null)
        {
            _analysisPipelineStatusSource.StatusChanged +=
                OnAnalysisPipelineStatusChanged;
            MergeInitialAnalysisPipelineStatus(
                _analysisPipelineStatusSource.Current);
        }
    }

    public ObservableCollection<TimelineEntryItemViewModel> Entries { get; } = [];

    public ObservableCollection<UnprocessedIntervalItemViewModel> UnprocessedIntervals { get; } = [];

    public bool HasUnprocessedIntervals => UnprocessedIntervals.Count > 0;

    public bool HasUnprocessedIntervalLoadError =>
        UnprocessedIntervalLoadErrorMessage.Length > 0;

    public string UnprocessedIntervalLoadErrorMessage { get; private set; } = string.Empty;

    public DateOnly SelectedDate
    {
        get => _selectedDate;
        private set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                OnPropertyChanged(nameof(SelectedDateText));
                OnPropertyChanged(nameof(IsToday));
            }
        }
    }

    public string SelectedDateText => SelectedDate.ToString("D", System.Globalization.CultureInfo.CurrentCulture);

    public bool IsToday => SelectedDate == GetToday();

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (SetProperty(ref _searchText, normalizedValue))
            {
                OnFilterChanged();
            }
        }
    }

    public ActivityCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnFilterChanged();
            }
        }
    }

    public ProductivityKind? SelectedProductivity
    {
        get => _selectedProductivity;
        set
        {
            if (SetProperty(ref _selectedProductivity, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || SelectedCategory.HasValue
        || SelectedProductivity.HasValue;

    public bool IsEmpty => IsInitialized && !IsLoading && !HasError && Entries.Count == 0;

    public bool HasMutationError => MutationErrorMessage.Length > 0;

    public bool HasAnalysisPipelineStatus => _analysisPipelineStatus is not null;

    public bool IsAnalysisPipelineRunning =>
        _analysisPipelineStatus?.State == AnalysisPipelineActivityState.Running;

    public bool HasAnalysisPipelineFault =>
        _analysisPipelineStatus?.State == AnalysisPipelineActivityState.Faulted;

    public bool HasAnalysisPipelineWarning =>
        _analysisPipelineStatus is
        {
            State: AnalysisPipelineActivityState.Idle,
            LastRunSummary: { } summary,
        }
        && (summary.RetryableFailureCount > 0
            || summary.TerminalFailureCount > 0
            || summary.LeaseLostCount > 0);

    public bool HasSuccessfulAnalysisPipelineRun =>
        _analysisPipelineStatus is
        {
            State: AnalysisPipelineActivityState.Idle,
            LastRunSummary.CompletedJobCount: > 0,
        }
        && !HasAnalysisPipelineWarning;

    public string AnalysisPipelineStatusTitle =>
        _analysisPipelineStatus switch
        {
            null => string.Empty,
            { State: AnalysisPipelineActivityState.Running } =>
                "正在更新时间线",
            { State: AnalysisPipelineActivityState.Faulted } =>
                "后台分析暂时不可用",
            { LastRunSummary: null } =>
                "正在检查分析状态",
            { LastRunSummary: { } } when HasAnalysisPipelineWarning =>
                "最近一次后台分析有未完成内容",
            {
                LastRunSummary:
                {
                    Ingestion.AnalysisReady: false,
                    Ingestion.ScannedChunkCount: > 0,
                },
            } => "录制内容等待分析",
            { LastRunSummary.Ingestion.AnalysisReady: false } =>
                "分析尚未启用",
            { LastRunSummary.CompletedJobCount: > 0 } =>
                "最近一次后台分析已完成",
            _ => "分析服务已就绪",
        };

    public string AnalysisPipelineStatusText =>
        BuildAnalysisPipelineStatusText(_analysisPipelineStatus);

    public string AnalysisPipelineCompactStatusText =>
        _analysisPipelineStatus switch
        {
            null => string.Empty,
            { State: AnalysisPipelineActivityState.Running } => "分析中",
            { State: AnalysisPipelineActivityState.Faulted } => "分析异常",
            { LastRunSummary: null } => "检查中",
            { LastRunSummary: { } } when HasAnalysisPipelineWarning =>
                "部分未完成",
            { LastRunSummary.Ingestion.AnalysisReady: false } => "等待分析",
            _ => "分析就绪",
        };

    public string AnalysisPipelineAutomationName => HasAnalysisPipelineStatus
        ? $"后台分析状态：{AnalysisPipelineCompactStatusText}。选择查看详情。"
        : string.Empty;

    public bool CanRetryAnalysisPipeline =>
        !_isDisposed
        && HasAnalysisPipelineFault
        && _analysisPipelineScheduler is not null;

    public bool CanMutateSelectedEntry => SelectedEntry is not null && !IsSaving;

    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return LoadDateAsync(SelectedDate, cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return LoadDateAsync(SelectedDate, cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PreviousDateAsync(CancellationToken cancellationToken = default)
    {
        return LoadDateAsync(SelectedDate.AddDays(-1), cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NextDateAsync(CancellationToken cancellationToken = default)
    {
        return LoadDateAsync(SelectedDate.AddDays(1), cancellationToken);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task TodayAsync(CancellationToken cancellationToken = default)
    {
        return LoadDateAsync(GetToday(), cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(HasActiveFilters))]
    private void ClearFilters()
    {
        _searchText = string.Empty;
        _selectedCategory = null;
        _selectedProductivity = null;

        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedProductivity));
        OnPropertyChanged(nameof(HasActiveFilters));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        ApplyFilters();
    }

    [RelayCommand(CanExecute = nameof(CanRetryAnalysis))]
    private async Task RetryAnalysisAsync(
        UnprocessedIntervalItemViewModel? interval,
        CancellationToken cancellationToken)
    {
        if (!CanRetryAnalysis(interval)
            || interval?.LatestJobId is not { } latestJobId
            || _analysisJobRetryService is null)
        {
            return;
        }

        var operation = await BeginMutationAsync(cancellationToken)
            .ConfigureAwait(true);
        if (operation is null)
        {
            return;
        }

        try
        {
            var result = await _analysisJobRetryService
                .RetryAsync(latestJobId, operation.Token)
                .ConfigureAwait(true);
            switch (result.Outcome)
            {
                case AnalysisJobRetryOutcome.Scheduled:
                case AnalysisJobRetryOutcome.AlreadyScheduled:
                case AnalysisJobRetryOutcome.NotFound:
                case AnalysisJobRetryOutcome.StateNotRetryable:
                case AnalysisJobRetryOutcome.StaleJob:
                case AnalysisJobRetryOutcome.AnalysisAlreadyCompleted:
                    break;
                case AnalysisJobRetryOutcome.EvidenceUnavailable:
                    MutationErrorMessage = RetryAnalysisEvidenceUnavailableText;
                    break;
                case AnalysisJobRetryOutcome.AttemptLimitReached:
                    MutationErrorMessage = RetryAnalysisAttemptLimitText;
                    break;
                default:
                    MutationErrorMessage = RetryAnalysisErrorText;
                    break;
            }

            await RequestSilentRefreshAsync(
                    TimeSpan.Zero,
                    operation.Token)
                .ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception)
        {
            MutationErrorMessage = RetryAnalysisErrorText;
        }
        finally
        {
            EndMutation(operation);
        }
    }

    private bool CanRetryAnalysis(UnprocessedIntervalItemViewModel? interval)
    {
        return !_isDisposed
            && !IsSaving
            && _analysisJobRetryService is not null
            && interval?.CanRetry == true;
    }

    [RelayCommand(CanExecute = nameof(CanRetryAnalysisPipeline))]
    private void RetryAnalysisPipeline()
    {
        if (!CanRetryAnalysisPipeline || _analysisPipelineScheduler is null)
        {
            return;
        }

        try
        {
            MutationErrorMessage = string.Empty;
            _analysisPipelineScheduler.RequestRun();
        }
        catch (Exception)
        {
            MutationErrorMessage = RetryAnalysisPipelineErrorText;
        }
    }

    public async Task<bool> CreateManualEntryAsync(
        TimelineEntryDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var commandService = GetCommandService();
        var operation = await BeginMutationAsync(cancellationToken).ConfigureAwait(true);
        if (operation is null)
        {
            return false;
        }

        try
        {
            var entry = await commandService
                .CreateManualAsync(draft, operation.Token)
                .ConfigureAwait(true);
            await LoadDateAsync(SelectedDate, operation.Token).ConfigureAwait(true);
            SelectedEntry = Entries.FirstOrDefault(item => item.Id == entry.Id);
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
            MutationErrorMessage = SaveErrorText;
            return false;
        }
        finally
        {
            EndMutation(operation);
        }
    }

    public async Task<bool> UpdateSelectedEntryAsync(
        TimelineEntryDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var selectedId = SelectedEntry?.Id;
        if (!selectedId.HasValue)
        {
            return false;
        }

        var commandService = GetCommandService();
        var operation = await BeginMutationAsync(cancellationToken).ConfigureAwait(true);
        if (operation is null)
        {
            return false;
        }

        try
        {
            await commandService
                .UpdateAsync(selectedId.Value, draft, operation.Token)
                .ConfigureAwait(true);
            await LoadDateAsync(SelectedDate, operation.Token).ConfigureAwait(true);
            SelectedEntry = Entries.FirstOrDefault(item => item.Id == selectedId.Value);
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
            MutationErrorMessage = SaveErrorText;
            return false;
        }
        finally
        {
            EndMutation(operation);
        }
    }

    public async Task<bool> DeleteSelectedEntryAsync(
        CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedEntry?.Id;
        if (!selectedId.HasValue)
        {
            return false;
        }

        var commandService = GetCommandService();
        var operation = await BeginMutationAsync(cancellationToken).ConfigureAwait(true);
        if (operation is null)
        {
            return false;
        }

        try
        {
            if (!await commandService
                    .DeleteAsync(selectedId.Value, operation.Token)
                    .ConfigureAwait(true))
            {
                MutationErrorMessage = DeleteErrorText;
                return false;
            }

            SelectedEntry = null;
            await LoadDateAsync(SelectedDate, operation.Token).ConfigureAwait(true);
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
            MutationErrorMessage = DeleteErrorText;
            return false;
        }
        finally
        {
            EndMutation(operation);
        }
    }

    public void ClearMutationError()
    {
        MutationErrorMessage = string.Empty;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_analysisPipelineStatusSource is not null)
        {
            _analysisPipelineStatusSource.StatusChanged -=
                OnAnalysisPipelineStatusChanged;
        }

        _lifetimeCancellation.Cancel();
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();

        AnalysisRefreshRequest? analysisRefreshRequest;
        lock (_analysisRefreshSync)
        {
            analysisRefreshRequest = _analysisRefreshRequest;
            _analysisRefreshRequest = null;
        }

        analysisRefreshRequest?.Cancel();
        RetryAnalysisCommand.NotifyCanExecuteChanged();
        RetryAnalysisPipelineCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _explicitLoadCount);
        var requestVersion = Interlocked.Increment(ref _loadVersion);
        using var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref _loadCancellation, currentCancellation);
        previousCancellation?.Cancel();

        if (date != SelectedDate)
        {
            SelectedDate = date;
            _dayEntries = [];
            ReplaceEntries([]);
            ReplaceUnprocessedIntervals([]);
            SetUnprocessedIntervalLoadError(string.Empty);
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            var entriesTask = CaptureLoadResultAsync(
                _queryService.GetForDayAsync(date, currentCancellation.Token));
            var intervalsTask = LoadUnprocessedIntervalsAsync(
                date,
                currentCancellation.Token);

            await Task.WhenAll(entriesTask, intervalsTask).ConfigureAwait(true);
            currentCancellation.Token.ThrowIfCancellationRequested();
            if (requestVersion != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            var entriesResult = await entriesTask.ConfigureAwait(true);
            if (entriesResult.Error is null)
            {
                _dayEntries = entriesResult.Value;
                ApplyFilters();
            }
            else
            {
                _dayEntries = [];
                ReplaceEntries([]);
                HasError = true;
                ErrorMessage = LoadErrorText;
            }

            var intervalsResult = await intervalsTask.ConfigureAwait(true);
            if (intervalsResult.Error is null)
            {
                ReplaceUnprocessedIntervals(intervalsResult.Value);
                SetUnprocessedIntervalLoadError(string.Empty);
            }
            else
            {
                ReplaceUnprocessedIntervals([]);
                SetUnprocessedIntervalLoadError(IntervalLoadErrorText);
            }

            IsInitialized = true;
        }
        catch (OperationCanceledException) when (currentCancellation.IsCancellationRequested)
        {
            // A newer navigation request or caller cancellation owns the visible state.
        }
        catch (Exception)
        {
            if (requestVersion == Volatile.Read(ref _loadVersion))
            {
                _dayEntries = [];
                ReplaceEntries([]);
                HasError = true;
                ErrorMessage = LoadErrorText;
                IsInitialized = true;
            }
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _loadVersion))
            {
                Interlocked.CompareExchange(ref _loadCancellation, null, currentCancellation);
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }

            if (Interlocked.Decrement(ref _explicitLoadCount) == 0
                && Interlocked.Exchange(ref _silentRefreshPending, 0) != 0)
            {
                ScheduleSilentRefresh(TimeSpan.Zero);
            }
        }
    }

    private void OnAnalysisPipelineStatusChanged(
        object? sender,
        AnalysisPipelineStatusChangedEventArgs eventArgs)
    {
        _ = sender;
        if (_isDisposed)
        {
            return;
        }

        PublishAnalysisPipelineStatus(eventArgs.Current);
        if (!TryAdvanceAnalysisDataRevision(eventArgs.Current.DataRevision))
        {
            return;
        }

        ScheduleSilentRefresh(AnalysisRefreshDebounceDelay);
    }

    private void PublishAnalysisPipelineStatus(AnalysisPipelineStatus status)
    {
        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var update = ((TimelineViewModel ViewModel,
                        AnalysisPipelineStatus Status))state!;
                    update.ViewModel.ApplyAnalysisPipelineStatus(update.Status);
                },
                (this, status));
            return;
        }

        ApplyAnalysisPipelineStatus(status);
    }

    private void MergeInitialAnalysisPipelineStatus(AnalysisPipelineStatus status)
    {
        lock (_analysisStatusSync)
        {
            if (_analysisPipelineStatus is null
                || status.Sequence >= _observedAnalysisStatusSequence)
            {
                _observedAnalysisStatusSequence = status.Sequence;
                _analysisPipelineStatus = status;
            }
        }

        _ = TryAdvanceAnalysisDataRevision(status.DataRevision);
    }

    private void ApplyAnalysisPipelineStatus(AnalysisPipelineStatus status)
    {
        lock (_analysisStatusSync)
        {
            if (_isDisposed
                || status.Sequence <= _observedAnalysisStatusSequence)
            {
                return;
            }

            _observedAnalysisStatusSequence = status.Sequence;
            _analysisPipelineStatus = status;
        }

        OnPropertyChanged(nameof(HasAnalysisPipelineStatus));
        OnPropertyChanged(nameof(IsAnalysisPipelineRunning));
        OnPropertyChanged(nameof(HasAnalysisPipelineFault));
        OnPropertyChanged(nameof(HasAnalysisPipelineWarning));
        OnPropertyChanged(nameof(HasSuccessfulAnalysisPipelineRun));
        OnPropertyChanged(nameof(AnalysisPipelineStatusTitle));
        OnPropertyChanged(nameof(AnalysisPipelineStatusText));
        OnPropertyChanged(nameof(AnalysisPipelineCompactStatusText));
        OnPropertyChanged(nameof(AnalysisPipelineAutomationName));
        OnPropertyChanged(nameof(CanRetryAnalysisPipeline));
        RetryAnalysisPipelineCommand.NotifyCanExecuteChanged();
    }

    private string BuildAnalysisPipelineStatusText(
        AnalysisPipelineStatus? status)
    {
        if (status is null)
        {
            return string.Empty;
        }

        if (status.State == AnalysisPipelineActivityState.Running)
        {
            return "正在扫描本地录制分片、提取证据并更新时间线。";
        }

        if (status.State == AnalysisPipelineActivityState.Faulted)
        {
            return status.FaultCode switch
            {
                AnalysisPipelineFaultCode.SchedulerFailed =>
                    "后台分析调度暂时中断；现有录制和时间线数据未丢失，可以立即重试。",
                _ =>
                    "后台分析未完成；现有录制和时间线数据未丢失，可以立即重试。",
            };
        }

        var summary = status.LastRunSummary;
        if (summary is null)
        {
            return "正在检查分析设置和待处理的本地录制分片。";
        }

        var checkedAt = TimeZoneInfo
            .ConvertTime(status.ChangedAtUtc, _timeProvider.LocalTimeZone)
            .ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (!summary.Ingestion.AnalysisReady)
        {
            return summary.Ingestion.ScannedChunkCount > 0
                ? $"已发现 {summary.Ingestion.ScannedChunkCount} 个本地录制分片；在你启用云分析并验证提供方前不会发送。最近检查 {checkedAt}。"
                : $"云分析未启用或提供方尚未验证；录制内容保留在本机且不会发送。最近检查 {checkedAt}。";
        }

        var resultParts = new List<string>();
        AddCount(resultParts, summary.CompletedJobCount, "个录制块已完成");
        AddCount(resultParts, summary.RetryableFailureCount, "个等待重试");
        AddCount(resultParts, summary.TerminalFailureCount, "个分析未完成");
        AddCount(resultParts, summary.LeaseLostCount, "个由后续运行接管");
        AddCount(resultParts, summary.Ingestion.CreatedJobCount, "个新任务已创建");
        AddCount(resultParts, summary.Ingestion.UnstableChunkCount, "个分片仍在写入");

        return resultParts.Count == 0
            ? $"没有新的录制内容需要处理。最近检查 {checkedAt}。"
            : $"最近一次运行：{string.Join("，", resultParts)}。检查时间 {checkedAt}。";
    }

    private static void AddCount(
        List<string> parts,
        int count,
        string description)
    {
        if (count > 0)
        {
            parts.Add($"{count} {description}");
        }
    }

    private bool TryAdvanceAnalysisDataRevision(long dataRevision)
    {
        var observed = Volatile.Read(ref _observedAnalysisDataRevision);
        while (dataRevision > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref _observedAnalysisDataRevision,
                dataRevision,
                observed);
            if (previous == observed)
            {
                return true;
            }

            observed = previous;
        }

        return false;
    }

    private void ScheduleSilentRefresh(TimeSpan delay)
    {
        _ = RequestSilentRefreshAsync(delay, CancellationToken.None);
    }

    private Task RequestSilentRefreshAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        var nextCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            cancellationToken);
        AnalysisRefreshRequest nextRequest;
        AnalysisRefreshRequest? previousRequest;
        lock (_analysisRefreshSync)
        {
            if (_isDisposed)
            {
                nextCancellation.Dispose();
                return Task.CompletedTask;
            }

            var nextVersion = checked(_analysisRefreshVersion + 1);
            nextRequest = new AnalysisRefreshRequest(
                nextVersion,
                nextCancellation);
            _analysisRefreshVersion = nextVersion;
            previousRequest = _analysisRefreshRequest;
            _analysisRefreshRequest = nextRequest;
        }

        var completion = RunScheduledSilentRefreshAsync(nextRequest, delay);
        previousRequest?.Cancel();
        return completion;
    }

    private async Task RunScheduledSilentRefreshAsync(
        AnalysisRefreshRequest request,
        TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, request.Token)
                    .ConfigureAwait(false);
            }

            await RefreshSilentlyOrDeferAsync(
                    request.Version,
                    request.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (request.IsCancellationRequested)
        {
        }
        catch
        {
            // A background refresh is best effort and must preserve visible data.
        }
        finally
        {
            lock (_analysisRefreshSync)
            {
                if (ReferenceEquals(
                        _analysisRefreshRequest,
                        request))
                {
                    _analysisRefreshRequest = null;
                }
            }

            request.Dispose();
        }
    }

    private async Task RefreshSilentlyOrDeferAsync(
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isDisposed || !IsCurrentAnalysisRefresh(refreshVersion))
        {
            return;
        }

        if (Volatile.Read(ref _explicitLoadCount) != 0)
        {
            if (IsCurrentAnalysisRefresh(refreshVersion))
            {
                Interlocked.Exchange(ref _silentRefreshPending, 1);
            }

            return;
        }

        var date = SelectedDate;
        var loadVersion = Volatile.Read(ref _loadVersion);
        var entriesTask = CaptureLoadResultAsync(
            _queryService.GetForDayAsync(date, cancellationToken));
        var intervalsTask = LoadUnprocessedIntervalsAsync(
            date,
            cancellationToken);

        await Task.WhenAll(entriesTask, intervalsTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var entriesResult = await entriesTask.ConfigureAwait(false);
        var intervalsResult = await intervalsTask.ConfigureAwait(false);
        var deferred = false;

        await RunOnCapturedContextAsync(
                () =>
                {
                    if (_isDisposed
                        || !IsCurrentAnalysisRefresh(refreshVersion))
                    {
                        return;
                    }

                    if (Volatile.Read(ref _explicitLoadCount) != 0
                        || date != SelectedDate
                        || loadVersion != Volatile.Read(ref _loadVersion))
                    {
                        deferred = true;
                        Interlocked.Exchange(ref _silentRefreshPending, 1);
                        return;
                    }

                    if (entriesResult.Error is null)
                    {
                        _dayEntries = entriesResult.Value;
                        ApplyFilters();
                        HasError = false;
                        ErrorMessage = string.Empty;
                        IsInitialized = true;
                    }

                    if (intervalsResult.Error is null)
                    {
                        ReplaceUnprocessedIntervals(intervalsResult.Value);
                        SetUnprocessedIntervalLoadError(string.Empty);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (deferred
            && IsCurrentAnalysisRefresh(refreshVersion)
            && Volatile.Read(ref _explicitLoadCount) == 0)
        {
            Interlocked.Exchange(ref _silentRefreshPending, 0);
            ScheduleSilentRefresh(TimeSpan.Zero);
        }
    }

    private bool IsCurrentAnalysisRefresh(long refreshVersion)
    {
        return refreshVersion == Volatile.Read(ref _analysisRefreshVersion);
    }

    private Task RunOnCapturedContextAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_synchronizationContext is null
            || ReferenceEquals(
                SynchronizationContext.Current,
                _synchronizationContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizationContext.Post(
            static state =>
            {
                var request = ((Action Action,
                    CancellationToken CancellationToken,
                    TaskCompletionSource Completion))state!;
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.CancellationToken);
                    return;
                }

                try
                {
                    request.Action();
                    request.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            },
            (action, cancellationToken, completion));
        return completion.Task;
    }

    private void OnFilterChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var search = SearchText.Trim();
        var visibleEntries = _dayEntries
            .Where(entry => !SelectedCategory.HasValue || entry.Category == SelectedCategory.Value)
            .Where(entry => !SelectedProductivity.HasValue || entry.Productivity == SelectedProductivity.Value)
            .Where(entry => MatchesSearch(entry, search))
            .OrderByDescending(static entry => entry.Range.Start)
            .ThenByDescending(static entry => entry.Range.End)
            .ThenByDescending(static entry => entry.Id)
            .Select(entry => new TimelineEntryItemViewModel(entry))
            .ToArray();

        ReplaceEntries(visibleEntries);
    }

    private void ReplaceEntries(IReadOnlyList<TimelineEntryItemViewModel> entries)
    {
        var selectedId = SelectedEntry?.Id;

        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        SelectedEntry = selectedId.HasValue
            ? Entries.FirstOrDefault(entry => entry.Id == selectedId.Value)
            : null;

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ReplaceUnprocessedIntervals(
        IReadOnlyList<UnprocessedIntervalItemViewModel> intervals)
    {
        UnprocessedIntervals.Clear();
        foreach (var interval in intervals)
        {
            UnprocessedIntervals.Add(interval);
        }

        OnPropertyChanged(nameof(HasUnprocessedIntervals));
    }

    private void SetUnprocessedIntervalLoadError(string message)
    {
        if (string.Equals(
                UnprocessedIntervalLoadErrorMessage,
                message,
                StringComparison.Ordinal))
        {
            return;
        }

        UnprocessedIntervalLoadErrorMessage = message;
        OnPropertyChanged(nameof(UnprocessedIntervalLoadErrorMessage));
        OnPropertyChanged(nameof(HasUnprocessedIntervalLoadError));
    }

    private async Task<LoadResult<IReadOnlyList<UnprocessedIntervalItemViewModel>>>
        LoadUnprocessedIntervalsAsync(
            DateOnly date,
            CancellationToken cancellationToken)
    {
        if (_unprocessedIntervalRepository is null)
        {
            return LoadResult<IReadOnlyList<UnprocessedIntervalItemViewModel>>.Success([]);
        }

        try
        {
            var utcRange = CreateUtcDayRange(date);
            var intervals = await _unprocessedIntervalRepository
                .GetForUtcRangeAsync(utcRange, cancellationToken)
                .ConfigureAwait(true);
            IReadOnlyList<UnprocessedIntervalItemViewModel> items = intervals
                .OrderByDescending(static interval => interval.Range.Start)
                .ThenByDescending(static interval => interval.Range.End)
                .ThenByDescending(static interval => interval.CaptureChunkId, StringComparer.Ordinal)
                .Select(static interval => new UnprocessedIntervalItemViewModel(interval))
                .ToArray();
            return LoadResult<IReadOnlyList<UnprocessedIntervalItemViewModel>>.Success(items);
        }
        catch (Exception exception)
        {
            return LoadResult<IReadOnlyList<UnprocessedIntervalItemViewModel>>.Failure(
                exception);
        }
    }

    private TimeRange CreateUtcDayRange(DateOnly date)
    {
        var start = ConvertLocalBoundaryToUtc(date.ToDateTime(TimeOnly.MinValue));
        var end = ConvertLocalBoundaryToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return new TimeRange(start, end);
    }

    private DateTimeOffset ConvertLocalBoundaryToUtc(DateTime localBoundary)
    {
        var local = DateTime.SpecifyKind(localBoundary, DateTimeKind.Unspecified);
        var timeZone = _timeProvider.LocalTimeZone;

        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static async Task<LoadResult<T>> CaptureLoadResultAsync<T>(Task<T> task)
    {
        try
        {
            return LoadResult<T>.Success(await task.ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            return LoadResult<T>.Failure(exception);
        }
    }

    private static bool MatchesSearch(TimelineEntry entry, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase))
            || entry.Apps.Any(app =>
                app.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || app.ApplicationId.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private DateOnly GetToday()
    {
        return DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
    }

    private TimelineCommandService GetCommandService()
    {
        return _commandService
            ?? throw new InvalidOperationException("Timeline editing is not configured.");
    }

    private async Task<CancellationTokenSource?> BeginMutationAsync(
        CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return null;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            if (!await _mutationGate
                    .WaitAsync(0, operation.Token)
                    .ConfigureAwait(true))
            {
                MutationErrorMessage = "另一项时间线操作正在进行，请稍候。";
                operation.Dispose();
                return null;
            }
        }
        catch
        {
            operation.Dispose();
            throw;
        }

        IsSaving = true;
        MutationErrorMessage = string.Empty;
        return operation;
    }

    private void EndMutation(CancellationTokenSource operation)
    {
        IsSaving = false;
        _mutationGate.Release();
        operation.Dispose();
    }

    private sealed record LoadResult<T>(T Value, Exception? Error)
    {
        public static LoadResult<T> Success(T value) => new(value, null);

        public static LoadResult<T> Failure(Exception error) => new(default!, error);
    }

    private sealed class AnalysisRefreshRequest : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;

        public AnalysisRefreshRequest(
            long version,
            CancellationTokenSource cancellation)
        {
            Version = version;
            _cancellation = cancellation
                ?? throw new ArgumentNullException(nameof(cancellation));
        }

        public long Version { get; }

        public CancellationToken Token => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request task already completed and released its token source.
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}

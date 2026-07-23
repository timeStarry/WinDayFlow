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

    private readonly TimelineQueryService _queryService;
    private readonly TimelineCommandService? _commandService;
    private readonly IUnprocessedIntervalRepository? _unprocessedIntervalRepository;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<TimelineEntry> _dayEntries = [];
    private CancellationTokenSource? _loadCancellation;
    private long _loadVersion;
    private DateOnly _selectedDate;
    private string _searchText = string.Empty;
    private ActivityCategory? _selectedCategory;
    private ProductivityKind? _selectedProductivity;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutateSelectedEntry))]
    private TimelineEntryItemViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutateSelectedEntry))]
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
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _selectedDate = GetToday();
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        TimeProvider? timeProvider = null)
        : this(queryService, timeProvider)
    {
        _unprocessedIntervalRepository = unprocessedIntervalRepository
            ?? throw new ArgumentNullException(nameof(unprocessedIntervalRepository));
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        TimeProvider? timeProvider = null)
        : this(queryService, timeProvider)
    {
        _commandService = commandService
            ?? throw new ArgumentNullException(nameof(commandService));
    }

    public TimelineViewModel(
        TimelineQueryService queryService,
        TimelineCommandService commandService,
        IUnprocessedIntervalRepository unprocessedIntervalRepository,
        TimeProvider? timeProvider = null)
        : this(queryService, commandService, timeProvider)
    {
        _unprocessedIntervalRepository = unprocessedIntervalRepository
            ?? throw new ArgumentNullException(nameof(unprocessedIntervalRepository));
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
        _lifetimeCancellation.Cancel();
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task LoadDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
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
        }
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
                .OrderBy(static interval => interval.Range.Start)
                .ThenBy(static interval => interval.Range.End)
                .ThenBy(static interval => interval.CaptureChunkId, StringComparer.Ordinal)
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
}

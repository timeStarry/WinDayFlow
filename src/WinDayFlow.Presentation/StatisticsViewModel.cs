using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Application.Statistics;
using WinDayFlow.Domain;

namespace WinDayFlow.Presentation.Statistics;

public sealed partial class StatisticsViewModel : ObservableObject
{
    private readonly IStatisticsService _service;
    private int _loadActive;

    [ObservableProperty]
    private StatisticsRange _selectedRange = StatisticsRange.Today;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _recordedDurationText = "0 分钟";

    [ObservableProperty]
    private string _activeDayCountText = "0";

    [ObservableProperty]
    private string _focusedDurationText = "0 分钟";

    [ObservableProperty]
    private string _retentionRateText = "0%";

    [ObservableProperty]
    private string _sampledCountText = "0";

    [ObservableProperty]
    private string _blackFrameCountText = "0";

    [ObservableProperty]
    private string _duplicateFrameCountText = "0";

    [ObservableProperty]
    private string _retainedFrameCountText = "0";

    [ObservableProperty]
    private double _retentionRatePercent;

    [ObservableProperty]
    private string _invocationCountText = "0";

    [ObservableProperty]
    private string _invocationSuccessRateText = "0%";

    [ObservableProperty]
    private string _averageLatencyText = "不可用";

    [ObservableProperty]
    private string _tokenUsageText = "不可用";

    [ObservableProperty]
    private string _storageTotalText = "0 B";

    [ObservableProperty]
    private string _firstStartedText = "不可用";

    public StatisticsViewModel(IStatisticsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public ObservableCollection<StatisticsBarItem> CategoryItems { get; } = [];

    public ObservableCollection<StatisticsBarItem> ProductivityItems { get; } = [];

    public ObservableCollection<StatisticsBarItem> StorageItems { get; } = [];

    public bool HasError => ErrorMessage.Length != 0;

    public bool HasCategoryItems => CategoryItems.Count != 0;

    public bool HasProductivityItems => ProductivityItems.Count != 0;

    public bool IsCategoryEmpty => !HasCategoryItems;

    public bool IsProductivityEmpty => !HasProductivityItems;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _loadActive, 1) != 0)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var snapshot = await _service.GetAsync(SelectedRange, cancellationToken)
                .ConfigureAwait(true);
            Apply(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ErrorMessage = "统计信息暂时无法读取，请稍后重试。";
        }
        finally
        {
            IsLoading = false;
            Volatile.Write(ref _loadActive, 0);
        }
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    private void Apply(StatisticsSnapshot value)
    {
        RecordedDurationText = FormatDuration(value.RecordedDuration);
        ActiveDayCountText = value.ActiveDayCount.ToString("N0", CultureInfo.CurrentCulture);
        FocusedDurationText = FormatDuration(value.FocusedDuration);
        RetentionRateText = value.CaptureFilters.RetentionRate.ToString("P0", CultureInfo.CurrentCulture);
        RetentionRatePercent = value.CaptureFilters.RetentionRate * 100;
        SampledCountText = value.CaptureFilters.SampledCount.ToString("N0", CultureInfo.CurrentCulture);
        BlackFrameCountText = value.CaptureFilters.BlackFrameCount.ToString("N0", CultureInfo.CurrentCulture);
        DuplicateFrameCountText = value.CaptureFilters.DuplicateFrameCount.ToString("N0", CultureInfo.CurrentCulture);
        RetainedFrameCountText = value.CaptureFilters.RetainedFrameCount.ToString("N0", CultureInfo.CurrentCulture);
        InvocationCountText = value.ProviderInvocations.InvocationCount.ToString("N0", CultureInfo.CurrentCulture);
        InvocationSuccessRateText = value.ProviderInvocations.SuccessRate.ToString("P0", CultureInfo.CurrentCulture);
        AverageLatencyText = value.ProviderInvocations.AverageLatency is { } latency
            ? latency.TotalSeconds >= 1
                ? $"{latency.TotalSeconds:N1} 秒"
                : $"{latency.TotalMilliseconds:N0} 毫秒"
            : "不可用";
        TokenUsageText = value.ProviderInvocations.InputTokens is { } input
            && value.ProviderInvocations.OutputTokens is { } output
            ? $"输入 {input:N0} / 输出 {output:N0}"
            : "不可用";
        StorageTotalText = FormatBytes(value.Storage.TotalBytes);
        FirstStartedText = value.FirstStartedAtUtc.ToLocalTime().ToString(
            "yyyy 年 M 月 d 日",
            CultureInfo.CurrentCulture);

        Replace(CategoryItems, CreateDurationBars(
            value.Categories.Select(item => (CategoryName(item.Key), item.Duration))));
        Replace(ProductivityItems, CreateDurationBars(
            value.Productivity.Select(item => (ProductivityName(item.Key), item.Duration))));
        OnPropertyChanged(nameof(HasCategoryItems));
        OnPropertyChanged(nameof(HasProductivityItems));
        OnPropertyChanged(nameof(IsCategoryEmpty));
        OnPropertyChanged(nameof(IsProductivityEmpty));
        var storage = new[]
        {
            ("数据库", value.Storage.DatabaseBytes),
            ("原始截图", value.Storage.RawCaptureBytes),
            ("打码派生", value.Storage.ScreeningBytes),
            ("应用缓存", value.Storage.ApplicationCacheBytes),
            ("诊断日志", value.Storage.LogBytes),
            ("应用内导出", value.Storage.InAppExportBytes),
        };
        var maximumStorage = Math.Max(1L, storage.Max(static item => item.Item2));
        Replace(StorageItems, storage.Select(item => new StatisticsBarItem(
            item.Item1,
            FormatBytes(item.Item2),
            100d * item.Item2 / maximumStorage)));
    }

    private static IEnumerable<StatisticsBarItem> CreateDurationBars(
        IEnumerable<(string Label, TimeSpan Duration)> values)
    {
        var items = values.ToArray();
        var maximum = Math.Max(1L, items.Select(static value => value.Duration.Ticks)
            .DefaultIfEmpty(1L).Max());
        return items.Select(item => new StatisticsBarItem(
            item.Label,
            FormatDuration(item.Duration),
            100d * item.Duration.Ticks / maximum));
    }

    private static void Replace(
        ObservableCollection<StatisticsBarItem> target,
        IEnumerable<StatisticsBarItem> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟"
        : $"{Math.Max(0, (int)Math.Round(value.TotalMinutes))} 分钟";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var scaled = (double)Math.Max(0, value);
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }
        return unit == 0 ? $"{scaled:N0} {units[unit]}" : $"{scaled:N1} {units[unit]}";
    }

    private static string CategoryName(ActivityCategory value) => value switch
    {
        ActivityCategory.FocusedWork => "专注工作",
        ActivityCategory.Communication => "沟通",
        ActivityCategory.Meeting => "会议",
        ActivityCategory.Planning => "规划",
        ActivityCategory.Research => "调研",
        ActivityCategory.Administration => "行政事务",
        ActivityCategory.Learning => "学习",
        ActivityCategory.Break => "休息",
        ActivityCategory.Personal => "个人事务",
        _ => "未分类",
    };

    private static string ProductivityName(ProductivityKind value) => value switch
    {
        ProductivityKind.Focused => "专注",
        ProductivityKind.Neutral => "中性",
        ProductivityKind.Distracting => "分心",
        ProductivityKind.Break => "休息",
        _ => "未知",
    };
}

public sealed record StatisticsBarItem(string Label, string Value, double Percent);

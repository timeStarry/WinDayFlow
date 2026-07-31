using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDayFlow.Application.Statistics;
using WinDayFlow.Presentation.Statistics;

namespace WinDayFlow.App.Views;

public sealed partial class StatisticsPage : Page, IDisposable
{
    private const double CompactLayoutMaximumWidth = 760;
    private CancellationTokenSource? _lifetime;

    public StatisticsPage()
    {
        ViewModel = App.GetService<StatisticsViewModel>();
        InitializeComponent();
        RangeSelector.SelectedItem = TodayRange;
        SetPresetDates(StatisticsRange.Today);
        UpdateRangeSummary(StatisticsRange.Today);
    }

    public StatisticsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lifetime = new CancellationTokenSource();
        UpdateLayout(ActualWidth);
        await LoadAsync();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateLayout(e.NewSize.Width);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async void OnRangeChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs e)
    {
        _ = e;
        var range = sender.SelectedItem switch
        {
            var item when ReferenceEquals(item, TodayRange) => StatisticsRange.Today,
            var item when ReferenceEquals(item, SevenDaysRange) => StatisticsRange.SevenDays,
            var item when ReferenceEquals(item, ThirtyDaysRange) => StatisticsRange.ThirtyDays,
            var item when ReferenceEquals(item, AllRange) => StatisticsRange.All,
            var item when ReferenceEquals(item, CustomRange) => StatisticsRange.Custom,
            _ => (StatisticsRange?)null,
        };
        if (range is null)
        {
            return;
        }

        if (range == StatisticsRange.Custom)
        {
            RangeSummaryButton.Flyout.ShowAt(RangeSummaryButton);
            return;
        }

        ViewModel.SelectedRange = range.Value;
        ViewModel.ErrorMessage = string.Empty;
        SetPresetDates(range.Value);
        UpdateRangeSummary(range.Value);
        await LoadAsync();
    }

    private async void OnApplyDateRange(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (StartDatePicker.Date is not { } selectedStart
            || EndDatePicker.Date is not { } selectedEnd)
        {
            ViewModel.ErrorMessage = "请选择开始日期和结束日期。";
            return;
        }

        var start = StartOfLocalDay(selectedStart);
        var end = StartOfLocalDay(selectedEnd).AddDays(1);
        if (!ViewModel.TrySelectCustomRange(start, end))
        {
            return;
        }

        RangeSelector.SelectedItem = CustomRange;
        UpdateRangeSummary(StatisticsRange.Custom);
        RangeSummaryButton.Flyout.Hide();
        await LoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private void SetPresetDates(StatisticsRange range)
    {
        var today = StartOfLocalDay(DateTimeOffset.Now);
        EndDatePicker.Date = today;
        StartDatePicker.Date = range switch
        {
            StatisticsRange.Today => today,
            StatisticsRange.SevenDays => today.AddDays(-6),
            StatisticsRange.ThirtyDays => today.AddDays(-29),
            StatisticsRange.All => null,
            _ => StartDatePicker.Date,
        };
    }

    private void UpdateRangeSummary(StatisticsRange range)
    {
        RangeSummaryText.Text = range switch
        {
            StatisticsRange.Today => "今天",
            StatisticsRange.SevenDays => "过去 7 天",
            StatisticsRange.ThirtyDays => "过去 30 天",
            StatisticsRange.All => "全部时间",
            StatisticsRange.Custom
                when StartDatePicker.Date is { } start
                    && EndDatePicker.Date is { } end =>
                $"{start:yyyy/M/d} - {end:yyyy/M/d}",
            _ => "选择日期范围",
        };
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset value)
    {
        var localDate = DateOnly.FromDateTime(value.LocalDateTime);
        var localStart = DateTime.SpecifyKind(
            localDate.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        return new DateTimeOffset(
            localStart,
            TimeZoneInfo.Local.GetUtcOffset(localStart));
    }

    private async Task LoadAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null)
        {
            return;
        }
        try
        {
            await ViewModel.LoadAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void UpdateLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var compact = width <= CompactLayoutMaximumWidth;
        ConfigureFourItemGrid(SummaryGrid, compact);
        ConfigureFourItemGrid(FilterStatisticsGrid, compact);
        ConfigureTwoItemGrid(DistributionGrid, compact);
        ConfigureTwoItemGrid(OperationsGrid, compact);
    }

    private static void ConfigureFourItemGrid(Grid grid, bool compact)
    {
        EnsureRows(grid, 2);
        for (var index = 0; index < grid.ColumnDefinitions.Count; index++)
        {
            grid.ColumnDefinitions[index].Width = !compact || index < 2
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }

        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, compact ? index % 2 : index);
                Grid.SetRow(child, compact ? index / 2 : 0);
            }
        }
    }

    private static void ConfigureTwoItemGrid(Grid grid, bool compact)
    {
        EnsureRows(grid, 2);
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, compact ? 0 : index);
                Grid.SetRow(child, compact ? index : 0);
            }
        }
    }

    private static void EnsureRows(Grid grid, int count)
    {
        while (grid.RowDefinitions.Count < count)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }
}

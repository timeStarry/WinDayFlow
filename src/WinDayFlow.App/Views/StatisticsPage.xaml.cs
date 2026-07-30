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
        TodayRange.IsChecked = true;
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

    private async void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string value }
            && Enum.TryParse<StatisticsRange>(value, out var range))
        {
            ViewModel.SelectedRange = range;
            await LoadAsync();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

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

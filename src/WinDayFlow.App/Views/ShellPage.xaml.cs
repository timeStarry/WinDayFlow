using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDayFlow.Presentation.Shell;

namespace WinDayFlow.App.Views;

public sealed partial class ShellPage : Page
{
    private const double WideHeaderMinimumWidth = 900;

    private static readonly Dictionary<string, (Type PageType, string Title)> Routes =
        new Dictionary<string, (Type, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["timeline"] = (typeof(TimelinePage), "时间线"),
            ["daily"] = (typeof(DailyPage), "日报"),
            ["journal"] = (typeof(JournalPage), "日志"),
            ["weekly"] = (typeof(WeeklyPage), "周报"),
            ["statistics"] = (typeof(StatisticsPage), "统计"),
            ["chat"] = (typeof(ChatPage), "问答"),
            ["settings"] = (typeof(SettingsPage), "设置"),
        };

    public ShellPage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

    public void OpenHome()
    {
        Navigation.SelectedItem = TimelineNavigationItem;
        Navigate("timeline");
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        UpdateHeaderLayout(Navigation.ActualWidth);
        Navigation.SelectedItem = TimelineNavigationItem;
        Navigate("timeline");
    }

    private void OnNavigationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeaderLayout(e.NewSize.Width);
    }

    private void UpdateHeaderLayout(double navigationWidth)
    {
        var statusTextVisibility = navigationWidth >= WideHeaderMinimumWidth
            ? Visibility.Visible
            : Visibility.Collapsed;
        CaptureStatusText.Visibility = statusTextVisibility;
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string route)
        {
            Navigate(route);
        }
    }

    private void Navigate(string route)
    {
        if (!Routes.TryGetValue(route, out var destination))
        {
            return;
        }

        PageTitle.Text = destination.Title;

        if (ContentFrame.CurrentSourcePageType != destination.PageType)
        {
            ContentFrame.Navigate(destination.PageType);
        }
    }

    private void OnOpenCaptureSettings(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = SettingsNavigationItem;
    }
}

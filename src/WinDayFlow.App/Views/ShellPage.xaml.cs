using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDayFlow.Presentation.Shell;

namespace WinDayFlow.App.Views;

public sealed partial class ShellPage : Page
{
    private const double AnalysisPanePreferredWidth = 360;
    private const double WideHeaderMinimumWidth = 900;

    private static readonly Dictionary<string, (Type PageType, string Title)> Routes =
        new Dictionary<string, (Type, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["timeline"] = (typeof(TimelinePage), "时间线"),
            ["daily"] = (typeof(DailyPage), "日报"),
            ["journal"] = (typeof(JournalPage), "日志"),
            ["weekly"] = (typeof(WeeklyPage), "周报"),
            ["chat"] = (typeof(ChatPage), "问答"),
            ["settings"] = (typeof(SettingsPage), "设置"),
        };

    public ShellPage()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();
    }

    public ShellViewModel ViewModel { get; }

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
        AnalysisSplitView.OpenPaneLength = Math.Min(
            AnalysisPanePreferredWidth,
            navigationWidth);

        var statusTextVisibility = navigationWidth >= WideHeaderMinimumWidth
            ? Visibility.Visible
            : Visibility.Collapsed;
        CaptureStatusText.Visibility = statusTextVisibility;
        AnalysisStatusText.Visibility = statusTextVisibility;
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
        AnalysisSplitView.IsPaneOpen = false;

        if (ContentFrame.CurrentSourcePageType != destination.PageType)
        {
            ContentFrame.Navigate(destination.PageType);
        }
    }

    private void OnOpenAnalysisPane(object sender, RoutedEventArgs e)
    {
        AnalysisSplitView.IsPaneOpen = true;
    }

    private void OnCloseAnalysisPane(object sender, RoutedEventArgs e)
    {
        AnalysisSplitView.IsPaneOpen = false;
        AnalysisStatusButton.Focus(FocusState.Programmatic);
    }
}

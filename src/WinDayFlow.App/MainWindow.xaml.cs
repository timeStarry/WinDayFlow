using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace WinDayFlow.App;

public sealed partial class MainWindow : Window
{
    private readonly bool _usesMica;
    private bool _initialSizeSet;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinDayFlow";
        WindowRoot.RequestedTheme = App.Current.SelectedTheme;

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            _usesMica = true;
        }
        else
        {
            WindowRoot.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[
                "ApplicationPageBackgroundThemeBrush"];
        }

        ConfigureTitleBar();
        WindowRoot.Loaded += OnWindowRootLoaded;
        WindowRoot.ActualThemeChanged += OnActualThemeChanged;
        Closed += OnClosed;
    }

    public void SetInitialSize()
    {
        if (_initialSizeSet)
        {
            return;
        }

        if (!WindowRoot.IsLoaded || WindowRoot.XamlRoot is null)
        {
            WindowRoot.Loaded += OnSetInitialSizeLoaded;
            return;
        }

        _initialSizeSet = true;
        var scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var edgeInset = (int)Math.Round(48 * scale);
        var width = Math.Min(
            (int)Math.Round(1180 * scale),
            Math.Max(1, workArea.Width - edgeInset));
        var height = Math.Min(
            (int)Math.Round(760 * scale),
            Math.Max(1, workArea.Height - edgeInset));

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2)));
    }

    public void OpenHome()
    {
        ShellContent.OpenHome();
    }

    private void OnSetInitialSizeLoaded(object sender, RoutedEventArgs e)
    {
        WindowRoot.Loaded -= OnSetInitialSizeLoaded;
        SetInitialSize();
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private void OnWindowRootLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarColors();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_usesMica)
        {
            WindowRoot.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[
                "ApplicationPageBackgroundThemeBrush"];
        }

        UpdateTitleBarColors();
    }

    private void UpdateTitleBarColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var isDark = WindowRoot.ActualTheme == ElementTheme.Dark;
        var hoverBackground = isDark
            ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x12, 0x00, 0x00, 0x00);
        var pressedBackground = isDark
            ? Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        var foreground = isDark
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressedBackground;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        WindowRoot.Loaded -= OnSetInitialSizeLoaded;
        WindowRoot.Loaded -= OnWindowRootLoaded;
        WindowRoot.ActualThemeChanged -= OnActualThemeChanged;
        Closed -= OnClosed;
    }
}

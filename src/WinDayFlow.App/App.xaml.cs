using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using WinDayFlow.Infrastructure.Timeline;
using WinDayFlow.Presentation.Capture;
using WinDayFlow.Presentation.Settings;
using WinDayFlow.Presentation.Shell;
using WinDayFlow.Presentation.Timeline;

namespace WinDayFlow.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                var connectionFactory = new SqliteConnectionFactory(
                    Path.Combine(DataDirectoryPath, "windayflow.db"));

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(connectionFactory);
                services.AddSingleton<SqliteDatabaseInitializer>();
                services.AddSingleton<IAppSettingsRepository, SqliteAppSettingsRepository>();
                services.AddSingleton<IAppSettingsCommitBarrier>(
                    NoOpAppSettingsCommitBarrier.Instance);
                services.AddSingleton<ICaptureRuntimeAuthorization>(
                    DenyCaptureRuntimeAuthorization.Instance);
                services.AddSingleton<AppSettingsService>();
                services.AddSingleton<SqliteTimelineRepository>();
                services.AddSingleton<ITimelineStore>(static provider =>
                    provider.GetRequiredService<SqliteTimelineRepository>());
                services.AddSingleton<ITimelineRepository>(static provider =>
                    provider.GetRequiredService<SqliteTimelineRepository>());
                services.AddSingleton<TimelineQueryService>();
                services.AddSingleton<TimelineCommandService>();
                services.AddSingleton<ICaptureBackend, UnavailableCaptureBackend>();
                services.AddSingleton<ICaptureService, ConsentGatedCaptureService>();

                services.AddTransient<TimelineViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddSingleton<CaptureStatusViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        UnhandledException += OnUnhandledException;
        if (Program.CurrentInstance is not null)
        {
            Program.CurrentInstance.Activated += OnAppInstanceActivated;
        }
    }

    public static new App Current => (App)Microsoft.UI.Xaml.Application.Current;

    public ElementTheme SelectedTheme { get; private set; } = ElementTheme.Default;

    public static string DataDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinDayFlow",
        "Data");

    public static T GetService<T>() where T : notnull =>
        Current._host.Services.GetRequiredService<T>();

    public void ApplyTheme(AppThemePreference theme)
    {
        SelectedTheme = theme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = SelectedTheme;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await _host.StartAsync();
            await _host.Services
                .GetRequiredService<SqliteDatabaseInitializer>()
                .InitializeAsync();
            var settings = _host.Services.GetRequiredService<AppSettingsService>();
            await settings.InitializeAsync();
            ApplyTheme(settings.Current.Theme);
            _window = _host.Services.GetRequiredService<MainWindow>();
            _window.Closed += OnWindowClosed;
            _window.Activate();
            _window.SetInitialSize();
        }
        catch (Exception exception)
        {
            Program.WriteStartupFailure(exception);
            Program.ShowStartupFailure();
            throw;
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        var window = _window;
        _window = null;
        if (window is not null)
        {
            window.Closed -= OnWindowClosed;
        }

        try
        {
            if (Program.CurrentInstance is not null)
            {
                Program.CurrentInstance.Activated -= OnAppInstanceActivated;
                Program.CurrentInstance.UnregisterKey();
            }

            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            Program.WriteShutdownFailure(exception);
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            catch (Exception exception)
            {
                Program.WriteShutdownFailure(exception);
            }
            finally
            {
                Exit();
            }
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _ = args;
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(window.Activate);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Program.WriteStartupFailure(e.Exception);
        Program.ShowStartupFailure();
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}

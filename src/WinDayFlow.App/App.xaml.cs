using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinDayFlow.App.Services;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Infrastructure.Ai;
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
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownCleanupBudget = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
#if WDF_DEV_LIVE_CAPTURE
    private readonly DevLiveCaptureHostedService? _devLiveCaptureLifetime;
#else
    private readonly IAsyncDisposable? _devLiveCaptureLifetime;
#endif
    private MainWindow? _window;
    private AppWindow? _appWindow;
    private CaptureAwareWindowCloseCoordinator? _windowCloseCoordinator;

    public App()
    {
        InitializeComponent();

#if WDF_DEV_LIVE_CAPTURE
        DevLiveCaptureHostedService? devLiveCaptureLifetime = null;
#else
        IAsyncDisposable? devLiveCaptureLifetime = null;
#endif
        try
        {
            var hostBuilder = Host.CreateDefaultBuilder();
            hostBuilder.ConfigureServices(services =>
                {
                    var connectionFactory = new SqliteConnectionFactory(
                        Path.Combine(DataDirectoryPath, "windayflow.db"));
                    var isExclusionEngineAvailable = false;

                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(connectionFactory);
                    services.AddSingleton<SqliteDatabaseInitializer>();
                    services.AddSingleton<IAppSettingsRepository,
                        SqliteAppSettingsRepository>();
#if WDF_DEV_LIVE_CAPTURE
                    if (Program.IsDevLiveCaptureRequested)
                    {
                        devLiveCaptureLifetime = services.AddDevLiveCapture(
                            DataDirectoryPath);
                        isExclusionEngineAvailable = true;
                    }
                    else
#endif
                    {
                        AddUnavailableCaptureServices(services);
                    }
                    services.AddSingleton<AppSettingsService>();
                    services.AddSingleton<WindowsDpapiCredentialProtector>();
                    services.AddSingleton<SqliteAiProviderProfileStore>();
                    services.AddSingleton<IAiProviderProfileStore>(static provider =>
                        provider.GetRequiredService<SqliteAiProviderProfileStore>());
                    services.AddSingleton<OpenAiCompatibleProviderFactory>();
                    services.AddSingleton<IAiAnalysisProviderFactory>(static provider =>
                        provider.GetRequiredService<OpenAiCompatibleProviderFactory>());
                    services.AddSingleton<AiProviderConfigurationService>();
                    services.AddSingleton<SqliteTimelineRepository>();
                    services.AddSingleton<ITimelineStore>(static provider =>
                        provider.GetRequiredService<SqliteTimelineRepository>());
                    services.AddSingleton<ITimelineRepository>(static provider =>
                        provider.GetRequiredService<SqliteTimelineRepository>());
                    services.AddSingleton<TimelineQueryService>();
                    services.AddSingleton<TimelineCommandService>();
                    services.AddSingleton<ICaptureService,
                        ConsentGatedCaptureService>();
                    services.AddAnalysisPipeline(
                        DataDirectoryPath,
                        static dataRoot =>
                            new NativeAnalysisEvidenceExtractor(dataRoot));

                    services.AddTransient<TimelineViewModel>();
                    services.AddTransient(provider => new SettingsViewModel(
                        provider.GetRequiredService<AppSettingsService>(),
                        provider.GetRequiredService<ICaptureService>(),
                        isExclusionEngineAvailable));
                    services.AddTransient<AiProviderSettingsViewModel>();
                    services.AddSingleton<CaptureStatusViewModel>();
                    services.AddSingleton<ShellViewModel>();
                    services.AddSingleton<MainWindow>();
                });
            _host = hostBuilder.Build();
        }
        catch (Exception buildFailure)
        {
            try
            {
                devLiveCaptureLifetime?.DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(buildFailure, cleanupFailure);
            }

            throw;
        }

        _devLiveCaptureLifetime = devLiveCaptureLifetime;

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
            await _host.Services
                .GetRequiredService<SqliteDatabaseInitializer>()
                .InitializeAsync();
            var settings = _host.Services.GetRequiredService<AppSettingsService>();
            await settings.InitializeAsync();
            await _host.Services
                .GetRequiredService<AiProviderConfigurationService>()
                .InitializeAsync();
            await _host.StartAsync();
            ApplyTheme(settings.Current.Theme);
            _window = _host.Services.GetRequiredService<MainWindow>();
            _appWindow = _window.AppWindow;
            _windowCloseCoordinator = new CaptureAwareWindowCloseCoordinator(
                _host.Services.GetRequiredService<ICaptureService>().StopAsync,
                CompleteShutdownBeforeCloseAsync,
                _window.Close,
                Program.WriteShutdownFailure);
            _appWindow.Closing += OnAppWindowClosing;
            _window.Closed += OnWindowClosed;
            _window.Activate();
            _window.SetInitialSize();
        }
        catch (Exception exception)
        {
            await DisposeDevLiveCaptureAfterFailureAsync();
            Program.WriteStartupFailure(exception);
            Program.ShowStartupFailure();
            throw;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _ = sender;
        if (_windowCloseCoordinator?.ShouldCancelClose() == true)
        {
            args.Cancel = true;
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = sender;
        _ = args;
        var window = _window;
        var appWindow = _appWindow;
        if (appWindow is not null)
        {
            appWindow.Closing -= OnAppWindowClosing;
        }

        if (window is not null)
        {
            window.Closed -= OnWindowClosed;
        }

        var currentInstance = Program.CurrentInstance;
        if (currentInstance is not null)
        {
            currentInstance.Activated -= OnAppInstanceActivated;
        }

        _window = null;
        _appWindow = null;
        _windowCloseCoordinator = null;
        try
        {
            currentInstance?.UnregisterKey();
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

    private async Task CompleteShutdownBeforeCloseAsync()
    {
        try
        {
            await _host.StopAsync(HostStopTimeout);
        }
        catch (Exception exception)
        {
            Program.WriteShutdownFailure(exception);
        }
        finally
        {
            using var cleanupBudget = new CancellationTokenSource(
                ShutdownCleanupBudget);
            await RunShutdownCleanupAsync(
                "Development capture cleanup",
                DisposeDevLiveCaptureAsync,
                cleanupBudget.Token);
            await RunShutdownCleanupAsync(
                "Application host disposal",
                DisposeHostAsync,
                cleanupBudget.Token);
        }
    }

    private static void AddUnavailableCaptureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppSettingsCommitBarrier>(
            NoOpAppSettingsCommitBarrier.Instance);
        services.AddSingleton<ICaptureRuntimeAuthorization>(
            DenyCaptureRuntimeAuthorization.Instance);
        services.AddSingleton<UnavailableCaptureBackend>();
        services.AddSingleton<ICaptureBackend>(static provider =>
            provider.GetRequiredService<UnavailableCaptureBackend>());
        services.AddSingleton<ICaptureChunkCommitNotifier>(static provider =>
            provider.GetRequiredService<UnavailableCaptureBackend>());
    }

    private ValueTask DisposeDevLiveCaptureAsync()
    {
        return _devLiveCaptureLifetime?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private ValueTask DisposeHostAsync()
    {
        if (_host is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task RunShutdownCleanupAsync(
        string operationName,
        Func<ValueTask> cleanupAsync,
        CancellationToken budgetToken)
    {
        if (budgetToken.IsCancellationRequested)
        {
            return;
        }

        var cleanupTask = Task.Run(
            async () => await cleanupAsync().ConfigureAwait(false),
            CancellationToken.None);
        try
        {
            await cleanupTask.WaitAsync(budgetToken);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested)
        {
            ObserveFailure(cleanupTask);
            Program.WriteShutdownFailure(new TimeoutException(
                $"{operationName} did not complete within the "
                + $"{ShutdownCleanupBudget.TotalSeconds:g}-second shutdown cleanup budget."));
        }
        catch (Exception exception)
        {
            Program.WriteShutdownFailure(exception);
        }
    }

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task DisposeDevLiveCaptureAfterFailureAsync()
    {
        try
        {
            await DisposeDevLiveCaptureAsync();
        }
        catch (Exception cleanupFailure)
        {
            Program.WriteShutdownFailure(cleanupFailure);
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

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_window, window))
            {
                window.Activate();
            }
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Program.WriteStartupFailure(e.Exception);
        Program.ShowStartupFailure();
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}

using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace WinDayFlow.App;

internal static class Program
{
#if WDF_DEV_LIVE_CAPTURE
    private const string DevLiveCaptureArgument = "--enable-dev-live-capture";
#endif
    private const uint MessageBoxIconError = 0x00000010;
    private static int _startupFailureShown;

    internal static AppInstance? CurrentInstance { get; private set; }

    internal static bool IsDevLiveCaptureRequested { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            IsDevLiveCaptureRequested = IsExactDevLiveCaptureInvocation(args);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            var registeredInstance = AppInstance.FindOrRegisterForKey("WinDayFlow.Main");
            if (!registeredInstance.IsCurrent)
            {
                registeredInstance
                    .RedirectActivationToAsync(activationArguments)
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            CurrentInstance = registeredInstance;
            Microsoft.UI.Xaml.Application.Start(initializationParameters =>
            {
                try
                {
                    _ = initializationParameters;
                    var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                    var context = new DispatcherQueueSynchronizationContext(dispatcherQueue);
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                }
                catch (Exception exception)
                {
                    WriteStartupFailure(exception);
                    throw;
                }
            });
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            ShowStartupFailure();
            Environment.ExitCode = 1;
        }
    }

    private static bool IsExactDevLiveCaptureInvocation(string[] args)
    {
#if WDF_DEV_LIVE_CAPTURE
        return args.Length == 1
            && string.Equals(args[0], DevLiveCaptureArgument, StringComparison.Ordinal);
#else
        _ = args;
        return false;
#endif
    }

    internal static void WriteStartupFailure(Exception exception)
    {
        WriteFailureReport(
            exception,
            "startup-error.log",
            "WinDayFlow failed before the application window was created.");
    }

    internal static void WriteShutdownFailure(Exception exception)
    {
        WriteFailureReport(
            exception,
            "shutdown-error.log",
            "WinDayFlow failed while closing the application.");
    }

    private static void WriteFailureReport(
        Exception exception,
        string fileName,
        string summary)
    {
        try
        {
            var report = $"""
                {summary}
                Time: {DateTimeOffset.Now:O}
                OS: {Environment.OSVersion}
                Runtime: {Environment.Version}

                {exception}
                """;
            File.WriteAllText(Path.Combine(GetDiagnosticsDirectory(), fileName), report);
        }
        catch (Exception loggingException) when (
            loggingException is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine(loggingException);
        }
    }

    internal static void ShowStartupFailure()
    {
        if (Interlocked.Exchange(ref _startupFailureShown, 1) != 0)
        {
            return;
        }

        var diagnosticsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDayFlow",
            "Diagnostics",
            "startup-error.log");
        try
        {
            _ = MessageBox(
                IntPtr.Zero,
                $"WinDayFlow 无法启动。\n\n诊断日志：\n{diagnosticsPath}",
                "WinDayFlow 启动失败",
                MessageBoxIconError);
        }
        catch (Exception dialogException) when (
            dialogException is DllNotFoundException or EntryPointNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine(dialogException);
        }
    }

    private static string GetDiagnosticsDirectory()
    {
        var diagnosticsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDayFlow",
            "Diagnostics");
        Directory.CreateDirectory(diagnosticsDirectory);
        return diagnosticsDirectory;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}

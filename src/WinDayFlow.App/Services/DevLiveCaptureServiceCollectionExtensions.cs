#if WDF_DEV_LIVE_CAPTURE
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;

namespace WinDayFlow.App.Services;

internal static class DevLiveCaptureServiceCollectionExtensions
{
    // Preserve 1 GiB for the OS and SQLite before admitting another capture write.
    internal const ulong MinimumStorageHeadroomBytes = 1UL * 1024 * 1024 * 1024;

    public static DevLiveCaptureHostedService AddDevLiveCapture(
        this IServiceCollection services,
        string dataDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectoryPath);
        if (!Path.IsPathFullyQualified(dataDirectoryPath))
        {
            throw new ArgumentException(
                "The development live-capture data directory must be fully qualified.",
                nameof(dataDirectoryPath));
        }

        var dataRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectoryPath));
        var diagnosticLog = CaptureDiagnosticLog.CreateForDataDirectory(dataRoot);
        var ruleObservations = new CaptureRuleObservationBuffer();
        var owner = new NativeCaptureRuntimeOwner(
            new NativeCaptureConfiguration(dataRoot),
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
            AppSettings.Default,
            NativeCapturePrivacySignals.FailClosed,
            diagnosticLog,
            ruleObservations);

        try
        {
            var monitor = DevLiveCapturePrivacy.CreateMonitor(
                owner,
                dataRoot,
                MinimumStorageHeadroomBytes,
                diagnosticLog);
            var lifetime = new DevLiveCaptureHostedService(monitor, owner);

            // Implementation-instance registrations are intentionally not owned by DI.
            // The hosted lifetime is the only component that disposes monitor then owner.
            services.AddSingleton(ruleObservations);
            services.AddSingleton<ICaptureRuleObservationSource>(ruleObservations);
            services.AddSingleton(owner);
            services.AddSingleton<ICaptureBackend>(owner);
            services.AddSingleton<ICaptureChunkCommitNotifier>(owner);
            services.AddSingleton<IAppSettingsCommitBarrier>(owner);
            services.AddSingleton<ICaptureRuntimeAuthorization>(owner);
            services.AddSingleton<INativeCapturePrivacySignalSink>(owner);
            services.AddSingleton<IHostedService>(lifetime);
            return lifetime;
        }
        catch
        {
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
#endif

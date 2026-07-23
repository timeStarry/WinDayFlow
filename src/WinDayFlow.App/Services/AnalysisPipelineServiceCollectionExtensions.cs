using Microsoft.Extensions.DependencyInjection;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Capture.Interop;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Capture;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.App.Services;

internal static class AnalysisPipelineServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisPipeline(
        this IServiceCollection services,
        string dataRootPath,
        Func<string, IAnalysisEvidenceExtractor> evidenceExtractorFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        ArgumentNullException.ThrowIfNull(evidenceExtractorFactory);
        if (!Path.IsPathFullyQualified(dataRootPath))
        {
            throw new ArgumentException(
                "The analysis data root must be a fully qualified path.",
                nameof(dataRootPath));
        }

        var dataRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataRootPath));

        services.AddSingleton<ICaptureManifestScanner>(provider =>
            new CaptureManifestScanner(
                dataRoot,
                provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<SqliteCaptureAnalysisStore>(provider =>
            new SqliteCaptureAnalysisStore(
                provider.GetRequiredService<SqliteConnectionFactory>(),
                dataRoot));
        services.AddSingleton<ICaptureChunkStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureAnalysisStore>());
        services.AddSingleton<IAnalysisJobStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureAnalysisStore>());

        services.AddSingleton<ICaptureChunkFingerprintProvider>(_ =>
            new NativeCaptureChunkFingerprintProvider(dataRoot));
        services.AddSingleton<IAnalysisEvidenceExtractor>(_ =>
            evidenceExtractorFactory(dataRoot)
                ?? throw new InvalidOperationException(
                    "The analysis evidence extractor factory returned null."));
        services.AddSingleton<IAnalysisResultCommitter, SqliteAnalysisResultCommitter>();
        services.AddSingleton<IUnprocessedIntervalRepository,
            SqliteUnprocessedIntervalRepository>();

        services.AddSingleton(CaptureAnalysisIngestionOptions.Default);
        services.AddSingleton<CaptureAnalysisIngestionService>();
        services.AddSingleton(AnalysisJobProcessorOptions.CreateDefault(
            $"windayflow-app-{Guid.NewGuid():N}"));
        services.AddSingleton<AnalysisJobProcessor>();
        services.AddSingleton(AnalysisPipelineSupervisorOptions.Default);
        services.AddSingleton<AnalysisPipelineSupervisor>();
        services.AddSingleton(AnalysisPipelineBackgroundRunnerOptions.Default);
        services.AddSingleton<AnalysisPipelineBackgroundRunner>();
        services.AddHostedService<AnalysisPipelineHostedService>();

        return services;
    }
}

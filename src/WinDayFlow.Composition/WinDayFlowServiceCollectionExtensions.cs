using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Application.Statistics;
using WinDayFlow.Infrastructure.Ai;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Capture;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using WinDayFlow.Infrastructure.Statistics;
using WinDayFlow.Infrastructure.Timeline;

namespace WinDayFlow.Composition;

public static class WinDayFlowServiceCollectionExtensions
{
    public static IServiceCollection AddWinDayFlowProductionServices(
        this IServiceCollection services,
        string dataRootPath)
    {
        return AddWinDayFlowProductionServicesCore(
            services,
            dataRootPath,
            evidenceExtractorFactory: null);
    }

    public static IServiceCollection AddWinDayFlowProductionServices(
        this IServiceCollection services,
        string dataRootPath,
        Func<string, IAnalysisEvidenceExtractor> evidenceExtractorFactory)
    {
        ArgumentNullException.ThrowIfNull(evidenceExtractorFactory);
        return AddWinDayFlowProductionServicesCore(
            services,
            dataRootPath,
            evidenceExtractorFactory);
    }

    private static IServiceCollection AddWinDayFlowProductionServicesCore(
        IServiceCollection services,
        string dataRootPath,
        Func<string, IAnalysisEvidenceExtractor>? evidenceExtractorFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        if (!Path.IsPathFullyQualified(dataRootPath))
        {
            throw new ArgumentException(
                "The WinDayFlow data root must be a fully qualified path.",
                nameof(dataRootPath));
        }

        var dataRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataRootPath));
        var connectionFactory = new SqliteConnectionFactory(
            Path.Combine(dataRoot, "windayflow.db"));

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<SqliteDatabaseInitializer>();
        services.AddSingleton<IAppSettingsRepository,
            SqliteAppSettingsRepository>();
        services.AddSingleton<AppSettingsService>();

        services.AddSingleton<WindowsDpapiCredentialProtector>();
        services.AddSingleton<SqliteAiProviderProfileStore>();
        services.AddSingleton<IAiProviderProfileStore>(static provider =>
            provider.GetRequiredService<SqliteAiProviderProfileStore>());
        services.AddSingleton<SqliteAnalysisStageBindingStore>();
        services.AddSingleton<IAnalysisStageBindingStore>(static provider =>
            provider.GetRequiredService<SqliteAnalysisStageBindingStore>());
        services.AddSingleton<SqliteProviderInvocationStore>();
        services.AddSingleton<IProviderInvocationStore>(static provider =>
            provider.GetRequiredService<SqliteProviderInvocationStore>());
        services.AddSingleton<SqlitePrivacyScreeningStore>();
        services.AddSingleton<IPrivacyScreeningStore>(static provider =>
            provider.GetRequiredService<SqlitePrivacyScreeningStore>());
        services.AddSingleton<SqliteEvidenceSendOverrideStore>();
        services.AddSingleton<IEvidenceSendOverrideStore>(static provider =>
            provider.GetRequiredService<SqliteEvidenceSendOverrideStore>());
        services.AddSingleton(static provider =>
            new OpenAiCompatibleProviderFactory(
                provider.GetRequiredService<SqliteAiProviderProfileStore>()));
        services.AddSingleton<IAiAnalysisProviderFactory>(static provider =>
            provider.GetRequiredService<OpenAiCompatibleProviderFactory>());
        services.AddSingleton<AnalysisProviderSendGate>();
        services.AddSingleton<AiProviderRoutingService>();

        services.AddSingleton<SqliteTimelineRepository>();
        services.AddSingleton<ITimelineStore>(static provider =>
            provider.GetRequiredService<SqliteTimelineRepository>());
        services.AddSingleton<ITimelineRepository>(static provider =>
            provider.GetRequiredService<SqliteTimelineRepository>());
        services.AddSingleton<TimelineQueryService>();
        services.AddSingleton<TimelineCommandService>();
        services.AddSingleton<IStatisticsService>(provider =>
            new SqliteStatisticsService(
                provider.GetRequiredService<SqliteConnectionFactory>(),
                dataRoot,
                provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<CaptureManifestScanner>(provider =>
            new CaptureManifestScanner(
                dataRoot,
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICaptureManifestScanner>(static provider =>
            provider.GetRequiredService<CaptureManifestScanner>());
        services.AddSingleton<ICaptureManifestContextSource>(static provider =>
            provider.GetRequiredService<CaptureManifestScanner>());
        services.AddSingleton<SqliteCaptureContextStore>();
        services.AddSingleton<ICaptureContextStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureContextStore>());
        services.AddSingleton<EvidenceSendPolicy>();
        services.AddSingleton<IEvidenceSendPolicy>(static provider =>
            provider.GetRequiredService<EvidenceSendPolicy>());
        services.AddSingleton(_ => new CanonicalCaptureFrameArchive(dataRoot));
        services.AddSingleton<ICaptureFrameArchive>(static provider =>
            provider.GetRequiredService<CanonicalCaptureFrameArchive>());
        services.AddSingleton<SqliteCaptureAnalysisStore>(provider =>
            new SqliteCaptureAnalysisStore(
                provider.GetRequiredService<SqliteConnectionFactory>(),
                dataRoot));
        services.AddSingleton<ICaptureChunkStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureAnalysisStore>());
        services.AddSingleton<IAnalysisJobStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureAnalysisStore>());
        services.AddSingleton<IAnalysisWindowStore>(static provider =>
            provider.GetRequiredService<SqliteCaptureAnalysisStore>());
        services.AddSingleton<ICaptureChunkFingerprintProvider>(static provider =>
            new CanonicalCaptureChunkFingerprintProvider(
                provider.GetRequiredService<CanonicalCaptureFrameArchive>()));
        if (evidenceExtractorFactory is null)
        {
            services.AddSingleton<CanonicalFrameAnalysisEvidenceExtractor>(static provider =>
                new CanonicalFrameAnalysisEvidenceExtractor(
                    provider.GetRequiredService<ICaptureFrameArchive>(),
                    provider.GetRequiredService<ICaptureChunkFingerprintProvider>()));
            services.AddSingleton<IAnalysisEvidenceExtractor>(provider =>
                new PrivacyAwareAnalysisEvidenceExtractor(
                    dataRoot,
                    provider.GetRequiredService<CanonicalFrameAnalysisEvidenceExtractor>(),
                    provider.GetRequiredService<ICaptureChunkFingerprintProvider>(),
                    provider.GetRequiredService<IPrivacyScreeningStore>(),
                    provider.GetRequiredService<ICaptureContextStore>()));
        }
        else
        {
            services.AddSingleton<IAnalysisEvidenceExtractor>(_ =>
                evidenceExtractorFactory(dataRoot)
                    ?? throw new InvalidOperationException(
                        "The analysis evidence extractor factory returned null."));
        }
        services.AddSingleton<IAnalysisResultCommitter,
            SqliteAnalysisResultCommitter>();
        services.AddSingleton<IUnprocessedIntervalRepository,
            SqliteUnprocessedIntervalRepository>();
        services.AddSingleton<PrivacyScreeningService>();
        services.AddSingleton<IPrivacyScreeningService>(static provider =>
            provider.GetRequiredService<PrivacyScreeningService>());

        services.AddSingleton(CaptureAnalysisIngestionOptions.Default);
        services.AddSingleton<CaptureAnalysisIngestionService>();
        services.AddSingleton(AnalysisJobProcessorOptions.CreateDefault(
            $"windayflow-app-{Guid.NewGuid():N}"));
        services.AddSingleton<AnalysisJobProcessor>();
        services.AddSingleton(AnalysisPipelineSupervisorOptions.Default);
        services.AddSingleton<AnalysisPipelineSupervisor>();
        services.AddSingleton(AnalysisPipelineBackgroundRunnerOptions.Default);
        services.AddSingleton<AnalysisPipelineStatusSource>();
        services.AddSingleton<IAnalysisPipelineStatusSource>(static provider =>
            provider.GetRequiredService<AnalysisPipelineStatusSource>());
        services.AddSingleton<AnalysisPipelineBackgroundRunner>();
        services.AddSingleton<IAnalysisPipelineScheduler>(static provider =>
            provider.GetRequiredService<AnalysisPipelineBackgroundRunner>());
        services.AddSingleton<AnalysisJobRetryService>();
        services.AddHostedService<AnalysisPipelineHostedService>();

        return services;
    }
}

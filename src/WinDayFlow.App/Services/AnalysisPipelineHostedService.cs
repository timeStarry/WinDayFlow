using Microsoft.Extensions.Hosting;
using WinDayFlow.Application.Analysis;

namespace WinDayFlow.App.Services;

internal sealed class AnalysisPipelineHostedService : IHostedService
{
    private readonly AnalysisPipelineBackgroundRunner _runner;

    public AnalysisPipelineHostedService(AnalysisPipelineBackgroundRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _runner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _runner.StopAsync(cancellationToken);
}

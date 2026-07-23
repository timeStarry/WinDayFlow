using WinDayFlow.Domain;

namespace WinDayFlow.Application.Capture;

public interface ICaptureManifestScanner
{
    Task<IReadOnlyList<CaptureChunk>> ScanCommittedAsync(
        CancellationToken cancellationToken = default);
}

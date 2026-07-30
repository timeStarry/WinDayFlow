using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinDayFlow.App.Services;

internal sealed record EvidenceFrameMedia(
    int SourceOrdinal,
    string CaptureChunkId,
    uint Index,
    DateTimeOffset CapturedAt,
    ulong OffsetMilliseconds,
    string AbsolutePath,
    CaptureFrameDescriptor Descriptor,
    CaptureProcessTelemetry? ProcessTelemetry);

internal sealed record EvidenceApplicationSummary(
    string ApplicationId,
    string DisplayName,
    int SampleCount,
    double AverageCpuPercent,
    double PeakCpuPercent,
    long PeakWorkingSetBytes,
    long PeakPrivateMemoryBytes)
{
    public string CpuSummary =>
        $"CPU 平均 {AverageCpuPercent:F1}% · 峰值 {PeakCpuPercent:F1}%";

    public string MemorySummary =>
        $"工作集峰值 {FormatBytes(PeakWorkingSetBytes)} · 私有内存峰值 {FormatBytes(PeakPrivateMemoryBytes)}";

    private static string FormatBytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        const double gibibyte = 1024d * mebibyte;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:F1} GB"
            : $"{bytes / mebibyte:F0} MB";
    }
}

internal sealed class EvidenceMediaService
{
    private readonly string _dataRoot;
    private readonly string _dataRootPrefix;
    private readonly ICaptureManifestScanner _scanner;
    private readonly ICaptureFrameArchive _archive;
    private readonly ICaptureContextStore _contextStore;

    public EvidenceMediaService(
        string dataRoot,
        ICaptureManifestScanner scanner,
        ICaptureFrameArchive archive,
        ICaptureContextStore contextStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _contextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _dataRootPrefix = _dataRoot + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<EvidenceFrameMedia>> GetFramesAsync(
        IReadOnlyList<EvidenceReference> evidenceReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        if (evidenceReferences.Count == 0)
        {
            return [];
        }

        var chunks = (await _scanner.ScanCommittedAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
        var result = new List<EvidenceFrameMedia>();
        for (var sourceOrdinal = 0; sourceOrdinal < evidenceReferences.Count; sourceOrdinal++)
        {
            var evidence = evidenceReferences[sourceOrdinal];
            if (!chunks.TryGetValue(evidence.CaptureChunkId, out var chunk)
                || !string.Equals(
                    evidence.ArtifactPath,
                    chunk.ManifestPath.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The timeline evidence does not match a committed chunk.");
            }

            var frames = await _archive.ListFramesAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            foreach (var frame in frames)
            {
                if (evidence.ContributionRange is { } contribution
                    && !contribution.Contains(frame.CapturedAt))
                {
                    continue;
                }
                result.Add(ToMedia(frame, sourceOrdinal, chunk.ProcessTelemetry));
            }
        }

        return result
            .OrderBy(static frame => frame.CapturedAt)
            .ThenBy(static frame => frame.CaptureChunkId, StringComparer.Ordinal)
            .ThenBy(static frame => frame.Index)
            .ToArray();
    }

    public async Task<IReadOnlyList<EvidenceFrameMedia>> GetFramesAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, start);

        var chunks = await _scanner.ScanCommittedAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<EvidenceFrameMedia>();
        foreach (var chunk in chunks)
        {
            if (chunk.Range.End <= start || chunk.Range.Start >= end)
            {
                continue;
            }
            var frames = await _archive.ListFramesAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(frames
                .Where(frame => frame.CapturedAt >= start && frame.CapturedAt < end)
                .Select(frame => ToMedia(
                    frame,
                    sourceOrdinal: 0,
                    chunk.ProcessTelemetry)));
        }

        return result
            .OrderBy(static frame => frame.CapturedAt)
            .ThenBy(static frame => frame.CaptureChunkId, StringComparer.Ordinal)
            .ThenBy(static frame => frame.Index)
            .GroupBy(
                static frame => (frame.CaptureChunkId, frame.Index),
                static frame => frame)
            .Select(static group => group.First())
            .ToArray();
    }

    public Task<byte[]> ReadFrameBytesAsync(
        EvidenceFrameMedia frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureUnderDataRoot(frame.AbsolutePath);
        return _archive.ReadFrameBytesAsync(frame.Descriptor, cancellationToken);
    }

    public async Task<IReadOnlyList<EvidenceApplicationSummary>> GetApplicationSummariesAsync(
        IReadOnlyList<EvidenceReference> evidenceReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        var samples = new List<CaptureContextSample>();
        foreach (var evidence in evidenceReferences)
        {
            var chunkSamples = await _contextStore.ListAsync(
                    evidence.CaptureChunkId,
                    cancellationToken)
                .ConfigureAwait(false);
            samples.AddRange(chunkSamples.Where(sample =>
                sample.Application is not null
                && (evidence.ContributionRange is null
                    || evidence.ContributionRange.Contains(sample.SampledAt))));
        }

        return samples
            .GroupBy(
                static sample => sample.Application!.ApplicationId,
                StringComparer.Ordinal)
            .Select(group =>
            {
                var applications = group.Select(static sample => sample.Application!).ToArray();
                return new EvidenceApplicationSummary(
                    group.Key,
                    applications
                        .GroupBy(static app => app.DisplayName, StringComparer.Ordinal)
                        .OrderByDescending(static names => names.Count())
                        .ThenBy(static names => names.Key, StringComparer.Ordinal)
                        .First().Key,
                    applications.Length,
                    applications.Average(static app => app.CpuUsageBasisPoints) / 100d,
                    applications.Max(static app => app.CpuUsageBasisPoints) / 100d,
                    applications.Max(static app => app.WorkingSetBytes),
                    applications.Max(static app => app.PrivateMemoryBytes));
            })
            .OrderByDescending(static summary => summary.SampleCount)
            .ThenBy(static summary => summary.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task ExportTimelapseAsync(
        IReadOnlyList<EvidenceFrameMedia> frames,
        StorageFile output,
        uint framesPerSecond,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(output);
        if (frames.Count == 0 || framesPerSecond is not (10 or 15 or 30 or 60))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        var composition = new MediaComposition();
        var frameDuration = TimeSpan.FromSeconds(1d / framesPerSecond);
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await ReadFrameBytesAsync(frame, cancellationToken);
            var file = await StorageFile.GetFileFromPathAsync(frame.AbsolutePath);
            composition.Clips.Add(
                await MediaClip.CreateFromImageFileAsync(file, frameDuration));
        }

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.FrameRate.Numerator = framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        var result = await composition.RenderToFileAsync(
            output,
            MediaTrimmingPreference.Precise,
            profile);
        if (result != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException(
                $"Timelapse rendering failed with reason {result}.");
        }
    }

    private EvidenceFrameMedia ToMedia(
        CaptureFrameDescriptor frame,
        int sourceOrdinal,
        CaptureProcessTelemetry? processTelemetry)
    {
        var path = Path.GetFullPath(Path.Combine(
            _dataRoot,
            frame.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderDataRoot(path);
        return new EvidenceFrameMedia(
            sourceOrdinal,
            frame.CaptureChunkId,
            frame.Index,
            frame.CapturedAt,
            frame.OffsetMilliseconds,
            path,
            frame,
            processTelemetry);
    }

    private void EnsureUnderDataRoot(string fullPath)
    {
        if (!fullPath.StartsWith(_dataRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The capture path escapes the data root.");
        }
    }
}

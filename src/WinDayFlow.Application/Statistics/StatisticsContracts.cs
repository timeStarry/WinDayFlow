using WinDayFlow.Domain;

namespace WinDayFlow.Application.Statistics;

public enum StatisticsRange
{
    Today = 0,
    SevenDays = 1,
    ThirtyDays = 2,
    All = 3,
    Custom = 4,
}

public sealed record StatisticsDurationBucket<T>(T Key, TimeSpan Duration)
    where T : struct, Enum;

public sealed record CaptureFilterStatistics(
    long SampledCount,
    long BlackFrameCount,
    long DuplicateFrameCount,
    long RetainedFrameCount)
{
    public double RetentionRate => SampledCount == 0
        ? 0
        : (double)RetainedFrameCount / SampledCount;
}

public sealed record ProviderInvocationStatistics(
    long InvocationCount,
    long SuccessfulCount,
    TimeSpan? AverageLatency,
    long? InputTokens,
    long? OutputTokens)
{
    public double SuccessRate => InvocationCount == 0
        ? 0
        : (double)SuccessfulCount / InvocationCount;
}

public sealed record StorageStatistics(
    long DatabaseBytes,
    long RawCaptureBytes,
    long ScreeningBytes,
    long ApplicationCacheBytes,
    long LogBytes,
    long InAppExportBytes)
{
    public long TotalBytes => DatabaseBytes
        + RawCaptureBytes
        + ScreeningBytes
        + ApplicationCacheBytes
        + LogBytes
        + InAppExportBytes;
}

public sealed record StatisticsSnapshot(
    StatisticsRange Range,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    DateTimeOffset FirstStartedAtUtc,
    TimeSpan RecordedDuration,
    int ActiveDayCount,
    TimeSpan FocusedDuration,
    IReadOnlyList<StatisticsDurationBucket<ActivityCategory>> Categories,
    IReadOnlyList<StatisticsDurationBucket<ProductivityKind>> Productivity,
    CaptureFilterStatistics CaptureFilters,
    ProviderInvocationStatistics ProviderInvocations,
    StorageStatistics Storage);

public interface IStatisticsService
{
    Task<StatisticsSnapshot> GetAsync(
        StatisticsRange range,
        CancellationToken cancellationToken = default);

    Task<StatisticsSnapshot> GetAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken = default);
}

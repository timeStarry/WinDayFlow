using System.Collections.ObjectModel;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public sealed record AnalysisWindowMember
{
    public AnalysisWindowMember(
        CaptureChunk chunk,
        CaptureChunkFingerprint sourceFingerprint,
        TimeRange contributionRange)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(contributionRange);
        if (contributionRange.Start < chunk.Range.Start
            || contributionRange.End > chunk.Range.End)
        {
            throw new ArgumentException(
                "An analysis window contribution must be contained by its capture chunk.",
                nameof(contributionRange));
        }

        Chunk = chunk;
        SourceFingerprint = sourceFingerprint;
        ContributionRange = contributionRange;
    }

    public CaptureChunk Chunk { get; }

    public CaptureChunkFingerprint SourceFingerprint { get; }

    public TimeRange ContributionRange { get; }
}

public sealed record AnalysisWindowExistingEntry(
    Guid Id,
    TimeRange Range,
    string Title,
    string Summary,
    TimelineEntryOrigin Origin,
    long Revision,
    bool HasUserEdits)
{
    public bool IsLocked => Origin == TimelineEntryOrigin.Manual || HasUserEdits;

    public bool IsRewriteProtectedBy(TimeRange window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return IsLocked || Range.End > window.End;
    }
}

public sealed class AnalysisWindowSnapshot
{
    public AnalysisWindowSnapshot(
        TimeRange range,
        IReadOnlyList<AnalysisWindowMember> members,
        IReadOnlyList<AnalysisWindowExistingEntry> existingEntries)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(existingEntries);
        if (members.Count == 0 || members.Any(static member => member is null))
        {
            throw new ArgumentException(
                "An analysis window requires at least one capture member.",
                nameof(members));
        }

        var memberCopy = members
            .OrderBy(static member => member.ContributionRange.Start)
            .ThenBy(static member => member.Chunk.Id, StringComparer.Ordinal)
            .ToArray();
        if (memberCopy.Any(member =>
                member.ContributionRange.Start < range.Start
                || member.ContributionRange.End > range.End)
            || memberCopy.Select(static member => member.Chunk.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != memberCopy.Length)
        {
            throw new ArgumentException(
                "Analysis window members must be unique and contained by the window.",
                nameof(members));
        }

        var existingCopy = existingEntries.ToArray();
        if (existingCopy.Any(static entry => entry is null)
            || existingCopy.Select(static entry => entry.Id).Distinct().Count()
                != existingCopy.Length
            || existingCopy.Any(entry =>
                entry.Range.Start >= range.End || entry.Range.End <= range.Start))
        {
            throw new ArgumentException(
                "Existing entries must uniquely overlap the analysis window.",
                nameof(existingEntries));
        }

        Range = range;
        Members = Array.AsReadOnly(memberCopy);
        ExistingEntries = Array.AsReadOnly(existingCopy);
    }

    public TimeRange Range { get; }

    public ReadOnlyCollection<AnalysisWindowMember> Members { get; }

    public ReadOnlyCollection<AnalysisWindowExistingEntry> ExistingEntries { get; }
}

public interface IAnalysisWindowStore
{
    Task<AnalysisJobEnqueueResult> EnqueueWindowAsync(
        AnalysisJob pendingJob,
        IReadOnlyList<AnalysisWindowMember> members,
        CancellationToken cancellationToken = default);

    Task<AnalysisWindowSnapshot?> GetWindowAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}

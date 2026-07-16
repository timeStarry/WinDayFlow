namespace WinDayFlow.Application.Settings;

/// <summary>
/// Coordinates safety-sensitive work around an application settings commit.
/// </summary>
public interface IAppSettingsCommitBarrier
{
    /// <summary>
    /// Runs under the settings write gate before any required repository save.
    /// Restrictive changes must establish their runtime block before this method returns.
    /// </summary>
    Task PrepareAsync(
        AppSettings previous,
        AppSettings proposed,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs after repository persistence and the in-memory settings snapshot are applied.
    /// </summary>
    Task CommittedAsync(
        AppSettings previous,
        AppSettings current,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a failed prepare, save, or commit callback. Implementations must retain any
    /// restrictive block established by <see cref="PrepareAsync"/> and must never reauthorize
    /// capture as rollback behavior. <paramref name="settingsApplied"/> is true when persistence
    /// (when required) and the in-memory snapshot update already completed.
    /// </summary>
    Task AbortedAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpAppSettingsCommitBarrier : IAppSettingsCommitBarrier
{
    private NoOpAppSettingsCommitBarrier()
    {
    }

    public static NoOpAppSettingsCommitBarrier Instance { get; } = new();

    public Task PrepareAsync(
        AppSettings previous,
        AppSettings proposed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CommittedAsync(
        AppSettings previous,
        AppSettings current,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task AbortedAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

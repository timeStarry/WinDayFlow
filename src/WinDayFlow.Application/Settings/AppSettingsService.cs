using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace WinDayFlow.Application.Settings;

public sealed class AppSettingsService : IDisposable
{
    public const int CurrentRecordingConsentVersion = 2;

    private readonly IAppSettingsRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly IAppSettingsCommitBarrier _commitBarrier;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public AppSettingsService(
        IAppSettingsRepository repository,
        TimeProvider? timeProvider = null,
        IAppSettingsCommitBarrier? commitBarrier = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commitBarrier = commitBarrier ?? NoOpAppSettingsCommitBarrier.Instance;
    }

    public AppSettings Current { get; private set; } = AppSettings.Default;

    public bool HasValidRecordingConsent =>
        IsValidRecordingConsent(Current);

    public event EventHandler<AppSettingsChangedEventArgs>? SettingsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        AppSettings? previous = null;
        AppSettings? current = null;
        var settingsApplied = false;
        ExceptionDispatchInfo? operationFailure = null;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _repository
                .GetAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The settings repository returned no settings snapshot.");

            previous = Current;
            current = EnsureCaptureConsentIsCurrent(loaded);
            try
            {
                await ApplySnapshotAsync(
                        previous,
                        current,
                        loaded,
                        saveRequired: current != loaded,
                        () => settingsApplied = true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        NotifyAppliedChange(previous!, current!, settingsApplied, operationFailure);
        operationFailure?.Throw();
    }

    public Task SetThemeAsync(
        AppThemePreference theme,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            current => new AppSettings(
                theme,
                current.CaptureEnabled,
                current.CloudAnalysisEnabled,
                current.RecordingConsent,
                current.CapturePrivacy),
            cancellationToken);
    }

    public Task GrantRecordingConsentAsync(
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            current => new AppSettings(
                current.Theme,
                current.CaptureEnabled,
                current.CloudAnalysisEnabled,
                new RecordingConsent(
                    CurrentRecordingConsentVersion,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    current.CapturePrivacy.Revision),
                current.CapturePrivacy),
            cancellationToken);
    }

    public Task RevokeRecordingConsentAsync(
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            current => new AppSettings(
                current.Theme,
                CaptureEnabled: false,
                current.CloudAnalysisEnabled,
                RecordingConsent: null,
                current.CapturePrivacy),
            cancellationToken);
    }

    public Task SetCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            current =>
            {
                if (enabled && !IsValidRecordingConsent(current))
                {
                    throw new RecordingConsentRequiredException();
                }

                return new AppSettings(
                    current.Theme,
                    enabled,
                    current.CloudAnalysisEnabled,
                    current.RecordingConsent,
                    current.CapturePrivacy);
            },
            cancellationToken);
    }

    public Task SetCapturePrivacyAsync(
        int evidenceRetentionDays,
        bool excludeSensitiveApplications,
        bool pauseInRemoteSessions,
        bool pauseDuringScreenSharing,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            current =>
            {
                var privacy = current.CapturePrivacy.Change(
                    evidenceRetentionDays,
                    excludeSensitiveApplications,
                    pauseInRemoteSessions,
                    pauseDuringScreenSharing);
                if (privacy == current.CapturePrivacy)
                {
                    return current;
                }

                return new AppSettings(
                    current.Theme,
                    CaptureEnabled: false,
                    current.CloudAnalysisEnabled,
                    current.RecordingConsent,
                    privacy);
            },
            cancellationToken);
    }

    public async Task<CaptureExclusionRule> AddCaptureExclusionRuleAsync(
        CaptureExclusionRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Revision != 1)
        {
            throw new ArgumentException(
                "A new capture exclusion rule must start at revision one.",
                nameof(rule));
        }

        CaptureExclusionRule? added = null;
        await UpdateAsync(
                current =>
                {
                    added = rule;
                    return ChangeExclusionRules(
                        current,
                        current.CapturePrivacy.ExclusionRules.Add(rule));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return added!;
    }

    public async Task<CaptureExclusionRule> UpdateCaptureExclusionRuleAsync(
        Guid id,
        long expectedRevision,
        string name,
        CaptureExclusionRuleScope scope,
        ApplicationIdentityKind applicationIdentityKind,
        string identityValue,
        WindowTitleMatchKind? windowTitleMatchKind,
        string? pattern,
        CancellationToken cancellationToken = default)
    {
        CaptureExclusionRule? updated = null;
        await UpdateAsync(
                current =>
                {
                    var (index, rule) = FindRule(
                        current.CapturePrivacy.ExclusionRules,
                        id,
                        expectedRevision);
                    updated = rule.Change(
                        name,
                        scope,
                        applicationIdentityKind,
                        identityValue,
                        windowTitleMatchKind,
                        pattern);
                    if (updated == rule)
                    {
                        return current;
                    }

                    return ChangeExclusionRules(
                        current,
                        current.CapturePrivacy.ExclusionRules.Replace(index, updated));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated!;
    }

    public async Task<CaptureExclusionRule> SetCaptureExclusionRuleEnabledAsync(
        Guid id,
        long expectedRevision,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        CaptureExclusionRule? updated = null;
        await UpdateAsync(
                current =>
                {
                    var (index, rule) = FindRule(
                        current.CapturePrivacy.ExclusionRules,
                        id,
                        expectedRevision);
                    updated = rule.ChangeEnabled(enabled);
                    if (updated == rule)
                    {
                        return current;
                    }

                    return ChangeExclusionRules(
                        current,
                        current.CapturePrivacy.ExclusionRules.Replace(index, updated));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return updated!;
    }

    public async Task<CaptureExclusionRule> MoveCaptureExclusionRuleAsync(
        Guid id,
        long expectedRevision,
        int newIndex,
        CancellationToken cancellationToken = default)
    {
        CaptureExclusionRule? moved = null;
        await UpdateAsync(
                current =>
                {
                    var rules = current.CapturePrivacy.ExclusionRules;
                    if (newIndex < 0 || newIndex >= rules.Count)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(newIndex),
                            newIndex,
                            "The capture exclusion rule position is outside the rule set.");
                    }

                    var (oldIndex, rule) = FindRule(rules, id, expectedRevision);
                    if (oldIndex == newIndex)
                    {
                        moved = rule;
                        return current;
                    }

                    moved = rule.AdvanceRevision();
                    return ChangeExclusionRules(
                        current,
                        rules.Move(oldIndex, newIndex, moved));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return moved!;
    }

    public async Task DeleteCaptureExclusionRuleAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await UpdateAsync(
                current =>
                {
                    var rules = current.CapturePrivacy.ExclusionRules;
                    var (index, _) = FindRule(rules, id, expectedRevision);
                    return ChangeExclusionRules(current, rules.RemoveAt(index));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writeGate.Dispose();
        _disposed = true;
    }

    private async Task UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        AppSettings? previous = null;
        AppSettings? current = null;
        var settingsApplied = false;
        ExceptionDispatchInfo? operationFailure = null;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previous = Current;
            current = update(previous)
                ?? throw new InvalidOperationException(
                    "The settings update produced no settings snapshot.");

            if (current == previous)
            {
                return;
            }

            try
            {
                await ApplySnapshotAsync(
                        previous,
                        current,
                        previous,
                        saveRequired: true,
                        () => settingsApplied = true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        NotifyAppliedChange(previous, current, settingsApplied, operationFailure);
        operationFailure?.Throw();
    }

    private async Task ApplySnapshotAsync(
        AppSettings previous,
        AppSettings current,
        AppSettings repositoryExpected,
        bool saveRequired,
        Action markSettingsApplied,
        CancellationToken cancellationToken)
    {
        var settingsApplied = false;
        try
        {
            await _commitBarrier
                .PrepareAsync(previous, current, cancellationToken)
                .ConfigureAwait(false);

            if (saveRequired)
            {
                await _repository
                    .SaveAsync(repositoryExpected, current, cancellationToken)
                    .ConfigureAwait(false);
            }

            Current = current;
            settingsApplied = true;
            markSettingsApplied();

            await _commitBarrier
                .CommittedAsync(previous, current, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await NotifyAbortedWithoutMaskingAsync(
                    previous,
                    current,
                    settingsApplied,
                    failure)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task NotifyAbortedWithoutMaskingAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure)
    {
        try
        {
            await _commitBarrier
                .AbortedAsync(
                    previous,
                    proposed,
                    settingsApplied,
                    failure,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception abortFailure)
        {
            Debug.WriteLine(
                $"The app settings commit barrier failed while handling an abort: {abortFailure}");
        }
    }

    private void NotifyAppliedChange(
        AppSettings previous,
        AppSettings current,
        bool settingsApplied,
        ExceptionDispatchInfo? operationFailure)
    {
        if (!settingsApplied || previous == current)
        {
            return;
        }

        try
        {
            OnSettingsChanged(previous, current);
        }
        catch (Exception notificationFailure) when (operationFailure is not null)
        {
            Debug.WriteLine(
                $"An app settings subscriber failed after the settings operation had already failed: {notificationFailure}");
        }
    }

    private static AppSettings EnsureCaptureConsentIsCurrent(AppSettings settings)
    {
        if (!settings.CaptureEnabled
            || IsValidRecordingConsent(settings))
        {
            return settings;
        }

        return new AppSettings(
            settings.Theme,
            CaptureEnabled: false,
            settings.CloudAnalysisEnabled,
            settings.RecordingConsent,
            settings.CapturePrivacy);
    }

    private static bool IsValidRecordingConsent(AppSettings settings)
    {
        return settings.RecordingConsent is { } consent
            && consent.PolicyVersion == CurrentRecordingConsentVersion
            && consent.PrivacyRevision == settings.CapturePrivacy.Revision;
    }

    private static AppSettings ChangeExclusionRules(
        AppSettings current,
        CaptureExclusionRuleSet rules)
    {
        var privacy = current.CapturePrivacy.ChangeRules(rules);
        if (privacy == current.CapturePrivacy)
        {
            return current;
        }

        return new AppSettings(
            current.Theme,
            CaptureEnabled: privacy.Revision == current.CapturePrivacy.Revision
                && current.CaptureEnabled,
            current.CloudAnalysisEnabled,
            current.RecordingConsent,
            privacy);
    }

    private static (int Index, CaptureExclusionRule Rule) FindRule(
        CaptureExclusionRuleSet rules,
        Guid id,
        long expectedRevision)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A capture exclusion rule identifier cannot be empty.",
                nameof(id));
        }

        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "The expected capture exclusion rule revision must be positive.");
        }

        var index = rules.IndexOf(id);
        if (index < 0)
        {
            throw new CaptureExclusionRuleNotFoundException(id);
        }

        var rule = rules[index];
        if (rule.Revision != expectedRevision)
        {
            throw new CaptureExclusionRuleRevisionConflictException(
                id,
                expectedRevision,
                rule.Revision);
        }

        return (index, rule);
    }

    private void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        SettingsChanged?.Invoke(
            this,
            new AppSettingsChangedEventArgs(previous, current));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

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

    public bool HasValidRecordingConsent => IsValidRecordingConsent(Current);

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
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new AppSettings(
                theme,
                current.RecordingConsent,
                current.Evidence,
                current.CaptureIntervalSeconds,
                current.CaptureIntent),
            cancellationToken);

    public Task GrantRecordingConsentAsync(
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new AppSettings(
                current.Theme,
                new RecordingConsent(
                    CurrentRecordingConsentVersion,
                    _timeProvider.GetUtcNow().ToUniversalTime()),
                current.Evidence,
                current.CaptureIntervalSeconds,
                current.CaptureIntent),
            cancellationToken);

    public Task RevokeRecordingConsentAsync(
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new AppSettings(
                current.Theme,
                RecordingConsent: null,
                current.Evidence,
                current.CaptureIntervalSeconds,
                CaptureIntent.Stopped),
            cancellationToken);

    public Task SetCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SetCaptureIntentAsync(
            enabled ? CaptureIntent.Recording : CaptureIntent.Stopped,
            cancellationToken);

    public Task SetCaptureIntentAsync(
        CaptureIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent is not (CaptureIntent.Recording
            or CaptureIntent.Paused
            or CaptureIntent.Stopped))
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        return UpdateAsync(
            current =>
            {
                if (intent != CaptureIntent.Stopped
                    && !IsValidRecordingConsent(current))
                {
                    throw new RecordingConsentRequiredException();
                }

                return new AppSettings(
                    current.Theme,
                    current.RecordingConsent,
                    current.Evidence,
                    current.CaptureIntervalSeconds,
                    intent);
            },
            cancellationToken);
    }

    public Task SetCaptureIntervalSecondsAsync(
        int captureIntervalSeconds,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current => new AppSettings(
                current.Theme,
                current.RecordingConsent,
                current.Evidence,
                captureIntervalSeconds,
                current.CaptureIntent),
            cancellationToken);

    public Task SetEvidenceRetentionDaysAsync(
        int evidenceRetentionDays,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current =>
            {
                var evidence = current.Evidence.ChangeRetentionDays(evidenceRetentionDays);
                return evidence == current.Evidence
                    ? current
                    : new AppSettings(
                        current.Theme,
                        current.RecordingConsent,
                        evidence,
                        current.CaptureIntervalSeconds,
                        current.CaptureIntent);
            },
            cancellationToken);

    public async Task<CaptureExclusionRule> AddCaptureExclusionRuleAsync(
        CaptureExclusionRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Revision != 1)
        {
            throw new ArgumentException(
                "A new evidence send rule must start at revision one.",
                nameof(rule));
        }

        await UpdateAsync(
                current => ChangeSendRules(
                    current,
                    current.Evidence.SendRules.Add(rule)),
                cancellationToken)
            .ConfigureAwait(false);
        return rule;
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
                        current.Evidence.SendRules,
                        id,
                        expectedRevision);
                    updated = rule.Change(
                        name,
                        scope,
                        applicationIdentityKind,
                        identityValue,
                        windowTitleMatchKind,
                        pattern);
                    return updated == rule
                        ? current
                        : ChangeSendRules(
                            current,
                            current.Evidence.SendRules.Replace(index, updated));
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
                        current.Evidence.SendRules,
                        id,
                        expectedRevision);
                    updated = rule.ChangeEnabled(enabled);
                    return updated == rule
                        ? current
                        : ChangeSendRules(
                            current,
                            current.Evidence.SendRules.Replace(index, updated));
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
                    var rules = current.Evidence.SendRules;
                    if (newIndex < 0 || newIndex >= rules.Count)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(newIndex),
                            newIndex,
                            "The evidence send-rule position is outside the rule set.");
                    }

                    var (oldIndex, rule) = FindRule(rules, id, expectedRevision);
                    if (oldIndex == newIndex)
                    {
                        moved = rule;
                        return current;
                    }

                    moved = rule.AdvanceRevision();
                    return ChangeSendRules(current, rules.Move(oldIndex, newIndex, moved));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return moved!;
    }

    public Task DeleteCaptureExclusionRuleAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            current =>
            {
                var rules = current.Evidence.SendRules;
                var (index, _) = FindRule(rules, id, expectedRevision);
                return ChangeSendRules(current, rules.RemoveAt(index));
            },
            cancellationToken);

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

        NotifyAppliedChange(previous!, current!, settingsApplied, operationFailure);
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
            SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs(previous, current));
        }
        catch (Exception notificationFailure) when (operationFailure is not null)
        {
            Debug.WriteLine(
                $"An app settings subscriber failed after the settings operation had already failed: {notificationFailure}");
        }
    }

    private static AppSettings EnsureCaptureConsentIsCurrent(AppSettings settings)
    {
        if (settings.CaptureIntent == CaptureIntent.Stopped
            || IsValidRecordingConsent(settings))
        {
            return settings;
        }

        return new AppSettings(
            settings.Theme,
            settings.RecordingConsent,
            settings.Evidence,
            settings.CaptureIntervalSeconds,
            CaptureIntent.Stopped);
    }

    private static bool IsValidRecordingConsent(AppSettings settings) =>
        settings.RecordingConsent is { } consent
        && consent.PolicyVersion == CurrentRecordingConsentVersion;

    private static AppSettings ChangeSendRules(
        AppSettings current,
        CaptureExclusionRuleSet rules)
    {
        var evidence = current.Evidence.ChangeSendRules(rules);
        return evidence == current.Evidence
            ? current
            : new AppSettings(
                current.Theme,
                current.RecordingConsent,
                evidence,
                current.CaptureIntervalSeconds,
                current.CaptureIntent);
    }

    private static (int Index, CaptureExclusionRule Rule) FindRule(
        CaptureExclusionRuleSet rules,
        Guid id,
        long expectedRevision)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "An evidence send-rule identifier cannot be empty.",
                nameof(id));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

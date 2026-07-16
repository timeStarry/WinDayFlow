namespace WinDayFlow.Application.Settings;

public sealed class AppSettingsService : IDisposable
{
    public const int CurrentRecordingConsentVersion = 2;

    private readonly IAppSettingsRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public AppSettingsService(
        IAppSettingsRepository repository,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _repository
                .GetAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The settings repository returned no settings snapshot.");

            current = EnsureCaptureConsentIsCurrent(loaded);
            if (current != loaded)
            {
                await _repository
                    .SaveAsync(current, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (Current == current)
            {
                return;
            }

            previous = Current;
            Current = current;
        }
        finally
        {
            _writeGate.Release();
        }

        OnSettingsChanged(previous!, current!);
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

            await _repository
                .SaveAsync(current, cancellationToken)
                .ConfigureAwait(false);

            Current = current;
        }
        finally
        {
            _writeGate.Release();
        }

        OnSettingsChanged(previous, current);
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

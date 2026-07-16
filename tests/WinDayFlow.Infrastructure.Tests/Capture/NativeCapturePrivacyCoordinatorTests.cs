using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCapturePrivacyCoordinatorTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InitialContextMustBeFailClosed()
    {
        var allowed = new NativeCapturePrivacyContext(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            RuntimePolicyRevision: 1);

        Assert.Throws<ArgumentException>(
            () => new NativeCapturePrivacyCoordinator(
                new TestPrivacyTarget(),
                allowed));
    }

    [Fact]
    public async Task PersistentPrivacyRevisionNeverSeedsRuntimePolicyRevision()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings(privacyRevision: 7);

        await CommitAsync(coordinator, AppSettings.Default, settings);
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());

        Assert.Equal(
            new ulong[] { 2, 3 },
            target.Contexts.Select(static context => context.RuntimePolicyRevision));
        var applied = target.Contexts[^1];
        Assert.Equal(7, settings.CapturePrivacy.Revision);
        Assert.True(coordinator.IsCaptureAuthorized);
    }

    [Fact]
    public async Task IdenticalSnapshotsDoNotConsumeRuntimePolicyRevisions()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var signals = CreateAllowedSignals();

        await coordinator.UpdateSignalsAsync(signals);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        await CommitAsync(coordinator, settings, settings);
        await coordinator.UpdateSignalsAsync(signals);

        Assert.Equal(
            new ulong[] { 2, 3 },
            target.Contexts.Select(static context => context.RuntimePolicyRevision));
    }

    [Fact]
    public async Task RestrictivePrepareDropsRuntimeLatchBeforeNativeBarrierCompletes()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        Assert.True(coordinator.IsCaptureAuthorized);
        target.Reset();
        target.BlockNextUpdate();
        var revoked = CreateRevokedSettings(settings);

        var prepare = coordinator.PrepareAsync(settings, revoked);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(NativeCapturePolicyDecision.Allow, coordinator.LastAppliedContext.ConsentGranted);

        target.ReleaseUpdate();
        await prepare;

        var blocked = Assert.Single(target.Contexts);
        Assert.Equal(NativeCapturePolicyDecision.Block, blocked.ConsentGranted);
        Assert.Equal<ulong>(4, blocked.RuntimePolicyRevision);
        await coordinator.AbortedAsync(
            settings,
            revoked,
            settingsApplied: false,
            new InvalidOperationException("test cleanup"));
    }

    [Fact]
    public async Task RestrictivePrepareCannotBeCanceledAfterRuntimeLatchDrops()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();
        target.BlockNextUpdate();
        var revoked = CreateRevokedSettings(settings);
        using var cancellation = new CancellationTokenSource();

        var prepare = coordinator.PrepareAsync(
            settings,
            revoked,
            cancellation.Token);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.False(prepare.IsCompleted);

        target.ReleaseUpdate();
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.ConsentGranted);
        await coordinator.AbortedAsync(
            settings,
            revoked,
            settingsApplied: false,
            new InvalidOperationException("test cleanup"));
    }

    [Fact]
    public async Task AbortedRestrictiveSaveNeverRestoresRuntimeAuthorization()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var revoked = CreateRevokedSettings(settings);

        await coordinator.PrepareAsync(settings, revoked);
        await coordinator.AbortedAsync(
            settings,
            revoked,
            settingsApplied: false,
            new InvalidOperationException("save failed"));

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(NativeCapturePolicyDecision.Block, coordinator.LastAppliedContext.ConsentGranted);
    }

    [Fact]
    public async Task DynamicSignalsUseMonotonicRuntimeRevisionsWithoutInvalidatingConsent()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings(privacyRevision: 11);
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        Assert.True(coordinator.IsCaptureAuthorized);

        await coordinator.UpdateSignalsAsync(CopySignals(
            allowed,
            remoteSession: NativeCaptureConditionState.Active));
        Assert.False(coordinator.IsCaptureAuthorized);
        await coordinator.UpdateSignalsAsync(allowed);

        Assert.True(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            new ulong[] { 2, 3, 4, 5 },
            target.Contexts.Select(static context => context.RuntimePolicyRevision));
        Assert.Equal(11, settings.RecordingConsent?.PrivacyRevision);
        Assert.Equal(11, settings.CapturePrivacy.Revision);
    }

    [Fact]
    public async Task SignalBlockObservedDuringPreparedAuthorizingCommitPreventsStaleAllow()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var disabled = CreateDisabledConsentedSettings();
        var enabled = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, disabled);

        await coordinator.PrepareAsync(disabled, enabled);
        var signalUpdate = coordinator.UpdateSignalsAsync(CopySignals(
            allowed,
            remoteSession: NativeCaptureConditionState.Active));
        await coordinator.CommittedAsync(disabled, enabled);
        await signalUpdate;

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.RemoteSessionAllowed);
    }

    [Fact]
    public async Task CommittedAuthorizingChangeReconcilesSignalsPublishedDuringNativeUpdate()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var disabled = CreateDisabledConsentedSettings();
        var enabled = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, disabled);
        target.Reset();
        target.BlockNextUpdate();
        await coordinator.PrepareAsync(disabled, enabled);

        var commit = coordinator.CommittedAsync(disabled, enabled);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var blockedSignals = CopySignals(
            allowed,
            remoteSession: NativeCaptureConditionState.Active);
        var signalUpdate = coordinator.UpdateSignalsAsync(blockedSignals);

        target.ReleaseUpdate();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new[]
            {
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Block,
            },
            target.Contexts.Select(static context => context.RemoteSessionAllowed));
        Assert.False(coordinator.IsCaptureAuthorized);
        await signalUpdate.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RestrictiveSignalBypassesBlockedSettingsSaveAndIgnoresLaterCancellation()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var repository = new BlockingSettingsRepository(AppSettings.Default);
        using var settings = new AppSettingsService(
            repository,
            commitBarrier: coordinator);
        var allowed = CreateAllowedSignals();
        await settings.InitializeAsync();
        await coordinator.UpdateSignalsAsync(allowed);
        await settings.GrantRecordingConsentAsync();
        await settings.SetCaptureEnabledAsync(enabled: true);
        Assert.True(coordinator.IsCaptureAuthorized);
        target.Reset();
        target.BlockNextUpdate();
        repository.BlockNextSave();

        var save = settings.SetThemeAsync(AppThemePreference.Dark);
        await repository.SaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var signalUpdate = coordinator.UpdateSignalsAsync(
            CopySignals(
                allowed,
                presentationMode: NativeCaptureConditionState.Active),
            cancellation.Token);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(save.IsCompleted);
        Assert.False(signalUpdate.IsCompleted);
        Assert.False(coordinator.IsCaptureAuthorized);

        target.ReleaseUpdate();
        await signalUpdate.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.PresentationAllowed);
        repository.ReleaseSave();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentSignalChangesReconcileToTheLatestSnapshotInOrder()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();
        target.BlockNextUpdate();

        var blockedSignals = CopySignals(
            allowed,
            presentationMode: NativeCaptureConditionState.Active);
        var block = coordinator.UpdateSignalsAsync(blockedSignals);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var allow = coordinator.UpdateSignalsAsync(allowed);

        target.ReleaseUpdate();
        await Task.WhenAll(block, allow);

        Assert.Equal(
            new[]
            {
                NativeCapturePolicyDecision.Block,
                NativeCapturePolicyDecision.Allow,
            },
            target.Contexts.Select(static context => context.PresentationAllowed));
        Assert.True(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            coordinator.LastAppliedContext.PresentationAllowed);
    }

    [Fact]
    public async Task NativeUpdateFailurePermanentlyFaultsTheHandleGeneration()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var nativeFailure = new InvalidOperationException("native update failed");
        target.UpdateException = nativeFailure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateSignalsAsync(CopySignals(
                allowed,
                presentationMode: NativeCaptureConditionState.Active)));

        Assert.Same(nativeFailure, thrown);
        Assert.False(coordinator.IsCaptureAuthorized);
        var faulted = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateSignalsAsync(allowed));
        Assert.Same(nativeFailure, faulted.InnerException);
    }

    [Fact]
    public async Task RuntimeRevisionOverflowRequiresANewNativeHandle()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(ulong.MaxValue);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateSignalsAsync(new NativeCapturePrivacySignals(
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Unknown,
                NativeCaptureConditionState.Unknown,
                NativeCaptureConditionState.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown)));

        Assert.Contains("exhausted", thrown.Message, StringComparison.Ordinal);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Empty(target.Contexts);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForAnOutstandingNativeUpdate()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.BlockNextUpdate();
        var signalUpdate = coordinator.UpdateSignalsAsync(CopySignals(
            allowed,
            remoteSession: NativeCaptureConditionState.Active));
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        target.ReleaseUpdate();
        await signalUpdate.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeDuringAuthorizingNativeUpdateCannotRepublishAuthorization()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var disabled = CreateDisabledConsentedSettings();
        var enabled = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, disabled);
        target.Reset();
        target.BlockNextUpdate();
        await coordinator.PrepareAsync(disabled, enabled);
        var commit = coordinator.CommittedAsync(disabled, enabled);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Dispose();
        target.ReleaseUpdate();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            coordinator.LastAppliedContext.ConsentGranted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.UpdateSignalsAsync(CreateAllowedSignals()));
    }

    private static AppSettings CreateEnabledSettings(long privacyRevision = 1)
    {
        var privacy = new CapturePrivacySettings(
            EvidenceRetentionDays: 30,
            ExcludeSensitiveApplications: true,
            PauseInRemoteSessions: true,
            PauseDuringScreenSharing: true,
            privacyRevision);
        return new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion,
                ConsentTime,
                privacy.Revision),
            privacy);
    }

    private static AppSettings CreateDisabledConsentedSettings()
    {
        var enabled = CreateEnabledSettings();
        return new AppSettings(
            enabled.Theme,
            CaptureEnabled: false,
            enabled.CloudAnalysisEnabled,
            enabled.RecordingConsent,
            enabled.CapturePrivacy);
    }

    private static async Task CommitAsync(
        NativeCapturePrivacyCoordinator coordinator,
        AppSettings previous,
        AppSettings current)
    {
        await coordinator.PrepareAsync(previous, current);
        await coordinator.CommittedAsync(previous, current);
    }

    private static NativeCapturePrivacySignals CreateAllowedSignals()
    {
        return new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow);
    }

    private static AppSettings CreateRevokedSettings(AppSettings settings)
    {
        return new AppSettings(
            settings.Theme,
            CaptureEnabled: false,
            settings.CloudAnalysisEnabled,
            RecordingConsent: null,
            settings.CapturePrivacy);
    }

    private static NativeCapturePrivacySignals CopySignals(
        NativeCapturePrivacySignals source,
        NativeCaptureConditionState? remoteSession = null,
        NativeCaptureConditionState? presentationMode = null)
    {
        return new NativeCapturePrivacySignals(
            source.SessionUnlocked,
            source.SecureDesktopClear,
            remoteSession ?? source.RemoteSession,
            presentationMode ?? source.PresentationMode,
            source.ApplicationAllowed,
            source.WindowAllowed,
            source.StorageAvailable);
    }

    private sealed class TestPrivacyTarget : INativeCapturePrivacyTarget
    {
        private TaskCompletionSource _updateStarted = CreateCompletionSource();
        private TaskCompletionSource _releaseUpdate = CreateCompletionSource();
        private bool _blockNextUpdate;

        public List<NativeCapturePrivacyContext> Contexts { get; } = [];

        public Exception? UpdateException { get; set; }

        public Task UpdateStarted => _updateStarted.Task;

        public async Task UpdatePrivacyContextAsync(
            NativeCapturePrivacyContext context,
            CancellationToken cancellationToken = default)
        {
            if (_blockNextUpdate)
            {
                _blockNextUpdate = false;
                _updateStarted.TrySetResult();
                await _releaseUpdate.Task.WaitAsync(cancellationToken);
            }

            if (UpdateException is { } exception)
            {
                throw exception;
            }

            Contexts.Add(context);
        }

        public void BlockNextUpdate()
        {
            _updateStarted = CreateCompletionSource();
            _releaseUpdate = CreateCompletionSource();
            _blockNextUpdate = true;
        }

        public void ReleaseUpdate()
        {
            _releaseUpdate.TrySetResult();
        }

        public void Reset()
        {
            Contexts.Clear();
            UpdateException = null;
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class BlockingSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _saveStarted = CreateCompletionSource();
        private readonly TaskCompletionSource _releaseSave = CreateCompletionSource();
        private AppSettings _settings;
        private int _blockNextSave;

        public BlockingSettingsRepository(AppSettings settings)
        {
            _settings = settings;
        }

        public Task SaveStarted => _saveStarted.Task;

        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public async Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blockNextSave, 0) != 0)
            {
                _saveStarted.TrySetResult();
                await _releaseSave.Task.WaitAsync(cancellationToken);
            }

            _settings = settings;
        }

        public void BlockNextSave()
        {
            Interlocked.Exchange(ref _blockNextSave, 1);
        }

        public void ReleaseSave()
        {
            _releaseSave.TrySetResult();
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

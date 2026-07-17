using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCapturePrivacyCoordinatorTests
{
    private static readonly DateTimeOffset ConsentTime =
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryAdmissionSnapshotFieldIsRevalidatedAtConsumption(
        int field)
    {
        var target = new TestPrivacyTarget();
        using var coordinator = new NativeCapturePrivacyCoordinator(
            target,
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1));
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, CreateEnabledSettings());

        var issued = await coordinator.TryIssueAdmissionAsync(
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);
        Assert.NotNull(issued);
        var stale = field switch
        {
            0 => issued.Value with
            {
                InvalidationGeneration = issued.Value.InvalidationGeneration + 1,
            },
            1 => issued.Value with
            {
                RuntimePolicyRevision = issued.Value.RuntimePolicyRevision + 1,
            },
            2 => issued.Value with
            {
                PersistenceGeneration = issued.Value.PersistenceGeneration + 1,
            },
            3 => issued.Value with
            {
                TargetEpoch = issued.Value.TargetEpoch + 1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var executed = false;

        await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
            () => coordinator.ExecuteAdmissionAsync(
                stale,
                () =>
                {
                    executed = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.False(executed);
    }

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
    public async Task MissingCaptureTargetIsATemporaryFailClosedState()
    {
        foreach (var missingTarget in new[]
                 {
                     NativeCaptureTargetIdentity.Unknown,
                     NativeCaptureTargetIdentity.Absent,
                 })
        {
            var target = new TestPrivacyTarget();
            var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
            using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
            var settings = CreateEnabledSettings();
            var allowed = CreateAllowedSignals();
            var missing = new NativeCapturePrivacySignals(
                allowed.SessionUnlocked,
                allowed.SecureDesktopClear,
                allowed.RemoteSession,
                allowed.PresentationMode,
                allowed.ApplicationAllowed,
                allowed.WindowAllowed,
                allowed.StorageAvailable,
                allowed.CaptureIdentity,
                missingTarget);

            await coordinator.UpdateSignalsAsync(missing);
            await CommitAsync(coordinator, AppSettings.Default, settings);

            Assert.False(coordinator.IsCaptureAuthorized);
            Assert.Equal(
                NativeCapturePolicyDecision.Unknown,
                coordinator.LastAppliedContext.ApplicationAllowed);
            await coordinator.UpdateSignalsAsync(allowed);
            Assert.True(coordinator.IsCaptureAuthorized);
        }
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
    public async Task AllowedTargetChangeAdvancesRuntimeAndPersistenceGenerations()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var firstSignals = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(firstSignals);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var previousRevision = coordinator.LastAppliedContext.RuntimePolicyRevision;
        var previousGeneration = coordinator.LastPersistenceGeneration;
        var secondTarget = NativeCaptureTargetIdentity.Present(
            windowHandle: 0x5678,
            processId: 43,
            processCreationTime100ns: 101,
            targetEpoch: 2,
            displayMonitorHandle: 0x6002,
            displayDeviceKey: @"\\.\DISPLAY2");

        await coordinator.UpdateSignalsAsync(CopySignals(
            firstSignals,
            target: secondTarget));

        Assert.Equal(previousRevision + 1, coordinator.LastAppliedContext.RuntimePolicyRevision);
        Assert.True(coordinator.LastPersistenceGeneration > previousGeneration);
        Assert.Equal(secondTarget, coordinator.LastAppliedAuthorization.Target);
        Assert.Equal(secondTarget, target.Authorizations[^1].Target);
    }

    [Fact]
    public async Task AllowedDisplayChangeFlowsIntoTheAtomicRuntimeAuthorization()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var firstSignals = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(firstSignals);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var previousRevision = coordinator.LastAppliedContext.RuntimePolicyRevision;
        var previousGeneration = coordinator.LastPersistenceGeneration;
        var changedDisplayTarget = NativeCaptureTargetIdentity.Present(
            windowHandle: firstSignals.Target.WindowHandle,
            processId: firstSignals.Target.ProcessId,
            processCreationTime100ns: firstSignals.Target.ProcessCreationTime100ns,
            targetEpoch: firstSignals.Target.TargetEpoch + 1,
            displayMonitorHandle: firstSignals.Target.DisplayMonitorHandle + 1,
            displayDeviceKey: @"\\.\DISPLAY2");

        await coordinator.UpdateSignalsAsync(CopySignals(
            firstSignals,
            target: changedDisplayTarget));

        Assert.Equal(
            previousRevision + 1,
            coordinator.LastAppliedContext.RuntimePolicyRevision);
        Assert.True(coordinator.LastPersistenceGeneration > previousGeneration);
        Assert.Equal(changedDisplayTarget, coordinator.LastAppliedAuthorization.Target);
        Assert.Equal(changedDisplayTarget, target.Authorizations[^1].Target);
    }

    [Fact]
    public async Task AllowedTargetChangeDropsAuthorizationUntilNativeAcknowledgesIt()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var firstSignals = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(firstSignals);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.BlockNextUpdate();
        var secondTarget = NativeCaptureTargetIdentity.Present(
            windowHandle: 0x5678,
            processId: 43,
            processCreationTime100ns: 101,
            targetEpoch: 2,
            displayMonitorHandle: 0x6002,
            displayDeviceKey: @"\\.\DISPLAY2");

        var update = coordinator.UpdateSignalsAsync(CopySignals(
            firstSignals,
            target: secondTarget));
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        target.ReleaseUpdate();
        await update;
        Assert.True(coordinator.IsCaptureAuthorized);
    }

    [Fact]
    public async Task EveryPrivacyInvalidationAdvancesAndForcesANativeBarrier()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();

        var first = coordinator.InvalidatePrivacyObservation();

        Assert.Equal(1, first);
        Assert.Equal(first, coordinator.PrivacyObservationGeneration);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(1, target.InvalidateCount);
        Assert.Empty(target.Authorizations);
        await coordinator.ApplyPrivacyInvalidationAsync(first);

        var second = coordinator.InvalidatePrivacyObservation();

        Assert.Equal(first + 1, second);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(2, target.InvalidateCount);
        await coordinator.ApplyPrivacyInvalidationAsync(second);
        Assert.Equal(2, target.Authorizations.Count);
        Assert.All(
            target.Authorizations,
            static authorization => Assert.Equal(
                NativeCapturePolicyDecision.Block,
                authorization.PrivacyContext.ConsentGranted));
    }

    [Fact]
    public async Task NativeCallbackInvalidationFailureFaultsAndStaysClosed()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.InvalidateException = new InvalidOperationException(
            "sensitive native detail");

        var failure = Assert.Throws<InvalidOperationException>(
            () => coordinator.InvalidatePrivacyObservation());

        Assert.Equal("sensitive native detail", failure.Message);
        Assert.Equal(1, coordinator.PrivacyObservationGeneration);
        Assert.Equal(1, target.InvalidateCount);
        Assert.True(coordinator.IsFaulted);
        Assert.False(coordinator.IsCaptureAuthorized);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyPrivacyInvalidationAsync(1));
    }

    [Fact]
    public async Task GenerationBoundPublishRequiresOneCompletedBarrierAndIsConsumedOnce()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();

        var generation = coordinator.InvalidatePrivacyObservation();

        Assert.False(await coordinator.TryUpdateSignalsAsync(generation, allowed));
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Empty(target.Authorizations);

        await coordinator.UpdateSignalsAsync(allowed);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Empty(target.Authorizations);

        await coordinator.ApplyPrivacyInvalidationAsync(generation);
        var barrier = Assert.Single(target.Authorizations);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            barrier.PrivacyContext.ConsentGranted);

        await coordinator.ApplyPrivacyInvalidationAsync(generation);
        Assert.Single(target.Authorizations);

        Assert.True(await coordinator.TryUpdateSignalsAsync(generation, allowed));
        Assert.True(coordinator.IsCaptureAuthorized);
        Assert.Equal(2, target.Authorizations.Count);

        await coordinator.ApplyPrivacyInvalidationAsync(generation);
        Assert.False(await coordinator.TryUpdateSignalsAsync(generation, allowed));
        await coordinator.UpdateSignalsAsync(NativeCapturePrivacySignals.FailClosed);

        Assert.True(coordinator.IsCaptureAuthorized);
        Assert.Equal(2, target.Authorizations.Count);
    }

    [Fact]
    public async Task StaleAllowSupersededBeforeCommitDoesNotConsumeARevision()
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
        var staleGeneration = coordinator.PrivacyObservationGeneration;
        var changedTarget = NativeCaptureTargetIdentity.Present(
            windowHandle: 0x5678,
            processId: 43,
            processCreationTime100ns: 101,
            targetEpoch: 2,
            displayMonitorHandle: 0x6002,
            displayDeviceKey: @"\\.\DISPLAY2");

        var staleUpdate = coordinator.TryUpdateSignalsAsync(
            staleGeneration,
            CopySignals(allowed, target: changedTarget));
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var currentGeneration = coordinator.InvalidatePrivacyObservation();
        Assert.False(coordinator.IsCaptureAuthorized);

        target.ReleaseUpdate();
        Assert.False(await staleUpdate.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Empty(target.Authorizations);
        Assert.False(coordinator.IsCaptureAuthorized);

        await coordinator.ApplyPrivacyInvalidationAsync(currentGeneration);
        Assert.True(await coordinator.TryUpdateSignalsAsync(
            currentGeneration,
            allowed));
        Assert.Equal(
            new ulong[] { 4, 5 },
            target.Authorizations.Select(
                static authorization =>
                    authorization.PrivacyContext.RuntimePolicyRevision));
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            target.Authorizations[0].PrivacyContext.ConsentGranted);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            target.Authorizations[1].PrivacyContext.ConsentGranted);
        Assert.True(coordinator.IsCaptureAuthorized);
        Assert.False(await coordinator.TryUpdateSignalsAsync(
            staleGeneration,
            allowed));
        Assert.True(coordinator.IsCaptureAuthorized);
    }

    [Fact]
    public async Task AllowCommittedBeforeCallbackIsCompensatedWithNextRevision()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();
        target.BlockNextUpdate(commitBeforeBlocking: true);
        var staleGeneration = coordinator.PrivacyObservationGeneration;
        var changedTarget = NativeCaptureTargetIdentity.Present(
            windowHandle: 0x5678,
            processId: 43,
            processCreationTime100ns: 101,
            targetEpoch: 2,
            displayMonitorHandle: 0x6002,
            displayDeviceKey: @"\\.\DISPLAY2");

        var staleUpdate = coordinator.TryUpdateSignalsAsync(
            staleGeneration,
            CopySignals(allowed, target: changedTarget));
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var currentGeneration = coordinator.InvalidatePrivacyObservation();
        target.ReleaseUpdate();

        Assert.False(await staleUpdate.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.IsFaulted);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            new ulong[] { 4, 5 },
            target.Authorizations.Select(
                static authorization =>
                    authorization.PrivacyContext.RuntimePolicyRevision));
        Assert.Equal(changedTarget, target.Authorizations[0].Target);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            target.Authorizations[0].PrivacyContext.ConsentGranted);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            target.Authorizations[1].PrivacyContext.ConsentGranted);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Unknown,
            target.Authorizations[1].Target.State);

        await coordinator.ApplyPrivacyInvalidationAsync(currentGeneration);
        Assert.True(await coordinator.TryUpdateSignalsAsync(
            currentGeneration,
            allowed));
        Assert.True(coordinator.IsCaptureAuthorized);
    }

    [Fact]
    public async Task PrivacyObservationGenerationExhaustionNeverWrapsAndFaultsClosed()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var generationField = typeof(NativeCapturePrivacyCoordinator).GetField(
            "_privacyObservationGeneration",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(generationField);
        generationField.SetValue(coordinator, long.MaxValue - 1);

        Assert.Equal(long.MaxValue, coordinator.InvalidatePrivacyObservation());
        var exhausted = Assert.Throws<InvalidOperationException>(
            () => coordinator.InvalidatePrivacyObservation());

        Assert.Contains("exhausted", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal(long.MaxValue, coordinator.PrivacyObservationGeneration);
        Assert.Equal(2, target.InvalidateCount);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.True(coordinator.IsFaulted);
    }

    [Fact]
    public async Task AdmissionGenerationExhaustionStillInvalidatesNativeAndFaultsClosed()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(
            coordinator,
            AppSettings.Default,
            CreateEnabledSettings());
        var generationField = typeof(NativeCapturePrivacyCoordinator).GetField(
            "_invalidationGeneration",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(generationField);
        generationField.SetValue(coordinator, long.MaxValue);
        var invalidationsBefore = target.InvalidateCount;

        var exhausted = Assert.Throws<InvalidOperationException>(
            () => coordinator.InvalidatePrivacyObservation());

        Assert.Contains("exhausted", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal(invalidationsBefore + 1, target.InvalidateCount);
        Assert.Equal(long.MaxValue, coordinator.InvalidationGeneration);
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.True(coordinator.IsFaulted);
    }

    [Fact]
    public async Task QuiesceWaitsForAnOutstandingUpdateThenMakesTargetInaccessible()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.BlockNextUpdate();
        var update = coordinator.UpdateSignalsAsync(CopySignals(
            allowed,
            remoteSession: NativeCaptureConditionState.Active));
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var quiesce = coordinator.QuiesceAsync();
        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.False(quiesce.IsCompleted);
        target.ReleaseUpdate();
        await update;
        await quiesce;

        Assert.Equal(1, target.RevokeCount);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Unknown,
            coordinator.LastAppliedAuthorization.Target.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateSignalsAsync(allowed));
    }

    [Fact]
    public async Task ReentrantQuiesceSubscriberObservesTheSingleFlightTask()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        var allowed = CreateAllowedSignals();
        await coordinator.UpdateSignalsAsync(allowed);
        await CommitAsync(coordinator, AppSettings.Default, settings);
        var updatesBefore = target.Authorizations.Count;
        Task? reentrant = null;
        var subscriberCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.AuthorizationChanged += (_, eventArgs) =>
        {
            if (!eventArgs.IsCaptureAuthorized)
            {
                reentrant = coordinator.QuiesceAsync();
                reentrant.GetAwaiter().GetResult();
                subscriberCompleted.TrySetResult();
            }
        };

        var first = coordinator.QuiesceAsync();
        await first;
        await subscriberCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(first, reentrant);
        Assert.Equal(updatesBefore + 1, target.Authorizations.Count);
        Assert.Equal(1, target.RevokeCount);
    }

    [Fact]
    public async Task AuthorizingCommitCannotClearAConcurrentQuiesce()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        using var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var disabled = CreateDisabledConsentedSettings();
        var enabled = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, disabled);
        target.Reset();
        target.BlockNextUpdate();
        await coordinator.PrepareAsync(disabled, enabled);

        var commit = coordinator.CommittedAsync(disabled, enabled);
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var quiesce = coordinator.QuiesceAsync();

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.False(commit.IsCompleted);
        Assert.False(quiesce.IsCompleted);

        target.ReleaseUpdate();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));
        await quiesce.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(1, target.RevokeCount);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.ConsentGranted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.UpdateSignalsAsync(CreateAllowedSignals()));
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
    public async Task DisposeDuringAuthorizingNativeUpdateCompensatesTheNativeAllow()
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
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.ConsentGranted);
        Assert.Equal(
            new[]
            {
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Block,
            },
            target.Contexts.Select(static context => context.ConsentGranted));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.UpdateSignalsAsync(CreateAllowedSignals()));
    }

    [Fact]
    public async Task DisposeDuringStopReconciliationCompensatesTheNativeAllow()
    {
        var target = new TestPrivacyTarget();
        var initial = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
        var coordinator = new NativeCapturePrivacyCoordinator(target, initial);
        var settings = CreateEnabledSettings();
        await coordinator.UpdateSignalsAsync(CreateAllowedSignals());
        await CommitAsync(coordinator, AppSettings.Default, settings);
        target.Reset();
        target.BlockNextUpdate();

        var reconcile = coordinator.ReconcileAfterStopAsync();
        await target.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Dispose();
        target.ReleaseUpdate();
        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.IsCaptureAuthorized);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            coordinator.LastAppliedContext.ConsentGranted);
        Assert.Equal(
            new[]
            {
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Block,
            },
            target.Contexts.Select(static context => context.ConsentGranted));
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
            NativeCapturePolicyDecision.Allow,
            Target: NativeCaptureTargetIdentity.Present(
                windowHandle: 0x1234,
                processId: 42,
                processCreationTime100ns: 100,
                targetEpoch: 1,
                displayMonitorHandle: 0x6001,
                displayDeviceKey: @"\\.\DISPLAY1"));
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
        NativeCaptureConditionState? presentationMode = null,
        NativeCaptureTargetIdentity? target = null)
    {
        return new NativeCapturePrivacySignals(
            source.SessionUnlocked,
            source.SecureDesktopClear,
            remoteSession ?? source.RemoteSession,
            presentationMode ?? source.PresentationMode,
            source.ApplicationAllowed,
            source.WindowAllowed,
            source.StorageAvailable,
            source.CaptureIdentity,
            target ?? source.Target);
    }

    private sealed class TestPrivacyTarget : INativeCaptureAuthorizationTarget
    {
        private TaskCompletionSource _updateStarted = CreateCompletionSource();
        private TaskCompletionSource _releaseUpdate = CreateCompletionSource();
        private bool _blockNextUpdate;
        private bool _commitBeforeBlockingNextUpdate;
        private long _persistenceGeneration;
        private long _callbackInvalidationGeneration;

        public List<NativeCapturePrivacyContext> Contexts { get; } = [];

        public List<NativeCaptureRuntimeAuthorization> Authorizations { get; } = [];

        public Exception? UpdateException { get; set; }

        public Exception? RevokeException { get; set; }

        public Exception? InvalidateException { get; set; }

        public int RevokeCount { get; private set; }

        public int InvalidateCount { get; private set; }

        public Task UpdateStarted => _updateStarted.Task;

        public long InvalidateRuntimeAuthorization()
        {
            InvalidateCount++;
            if (InvalidateException is { } exception)
            {
                throw exception;
            }

            return Interlocked.Increment(
                ref _callbackInvalidationGeneration);
        }

        public async Task<NativeCaptureAuthorizationUpdateResult>
            UpdateRuntimeAuthorizationAsync(
            NativeCaptureRuntimeAuthorization authorization,
            long expectedCallbackInvalidationGeneration,
            CancellationToken cancellationToken = default)
        {
            if (expectedCallbackInvalidationGeneration
                != Volatile.Read(ref _callbackInvalidationGeneration))
            {
                return SupersededBeforeCommit();
            }

            if (UpdateException is { } exception)
            {
                throw exception;
            }

            if (_blockNextUpdate)
            {
                _blockNextUpdate = false;
                if (_commitBeforeBlockingNextUpdate)
                {
                    _commitBeforeBlockingNextUpdate = false;
                    var committed = Commit(authorization);
                    _updateStarted.TrySetResult();
                    await _releaseUpdate.Task.WaitAsync(cancellationToken);
                    return expectedCallbackInvalidationGeneration
                        == Volatile.Read(ref _callbackInvalidationGeneration)
                            ? committed
                            : new NativeCaptureAuthorizationUpdateResult(
                                committed.PersistenceGeneration,
                                NativeCaptureAuthorizationUpdateOutcome
                                    .AppliedThenSuperseded);
                }

                _updateStarted.TrySetResult();
                await _releaseUpdate.Task.WaitAsync(cancellationToken);
            }

            if (expectedCallbackInvalidationGeneration
                != Volatile.Read(ref _callbackInvalidationGeneration))
            {
                return SupersededBeforeCommit();
            }

            return Commit(authorization);
        }

        public Task<ulong> RevokeRuntimeAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RevokeException is { } exception)
            {
                throw exception;
            }

            RevokeCount++;
            return Task.FromResult(checked((ulong)Interlocked.Increment(
                ref _persistenceGeneration)));
        }

        public void BlockNextUpdate(bool commitBeforeBlocking = false)
        {
            _updateStarted = CreateCompletionSource();
            _releaseUpdate = CreateCompletionSource();
            _blockNextUpdate = true;
            _commitBeforeBlockingNextUpdate = commitBeforeBlocking;
        }

        public void ReleaseUpdate()
        {
            _releaseUpdate.TrySetResult();
        }

        public void Reset()
        {
            Contexts.Clear();
            Authorizations.Clear();
            UpdateException = null;
            RevokeException = null;
            InvalidateException = null;
        }

        private NativeCaptureAuthorizationUpdateResult Commit(
            NativeCaptureRuntimeAuthorization authorization)
        {
            Contexts.Add(authorization.PrivacyContext);
            Authorizations.Add(authorization);
            return new NativeCaptureAuthorizationUpdateResult(
                checked((ulong)Interlocked.Increment(
                    ref _persistenceGeneration)),
                NativeCaptureAuthorizationUpdateOutcome.Applied);
        }

        private NativeCaptureAuthorizationUpdateResult SupersededBeforeCommit()
        {
            return new NativeCaptureAuthorizationUpdateResult(
                checked((ulong)Volatile.Read(ref _persistenceGeneration)),
                NativeCaptureAuthorizationUpdateOutcome
                    .SupersededBeforeCommit);
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
            AppSettings expected,
            AppSettings proposed,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blockNextSave, 0) != 0)
            {
                _saveStarted.TrySetResult();
                await _releaseSave.Task.WaitAsync(cancellationToken);
            }

            if (_settings != expected)
            {
                throw new AppSettingsConcurrencyException();
            }

            _settings = proposed;
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

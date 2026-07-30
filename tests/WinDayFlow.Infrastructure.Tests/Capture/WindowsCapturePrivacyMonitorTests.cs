using System.Collections.Concurrent;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class WindowsCapturePrivacyMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ObservationRequiresMatchingTargetAndDisplayStatesAndRedactsValues()
    {
        var observation = CreateObservation(
            targetEpoch: 7,
            executableName: "private-app.exe",
            windowTitle: "Private project title",
            displayKey: @"\\.\DISPLAY7");

        var text = observation.ToString();

        Assert.Equal(
            NativeCaptureTargetIdentityState.Present,
            observation.Signals.Target.State);
        Assert.Equal(
            WindowsCaptureDisplayTargetState.Present,
            observation.DisplayTarget.State);
        Assert.Contains("Values = [REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-app.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Private project title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY7", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new WindowsCapturePrivacyObservation(
            NativeCapturePrivacySignals.FailClosed,
            observation.DisplayTarget));
        var otherTarget = CreateVerificationResult(
            targetEpoch: 8,
            displayKey: @"\\.\DISPLAY8");
        Assert.Throws<ArgumentException>(() => new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                observation.Signals.SessionUnlocked,
                observation.Signals.SecureDesktopClear,
                observation.Signals.RemoteSession,
                observation.Signals.PresentationMode,
                observation.Signals.ApplicationAllowed,
                observation.Signals.WindowAllowed,
                observation.Signals.StorageAvailable,
                observation.Signals.CaptureIdentity,
                otherTarget.Target),
            observation.DisplayTarget));
    }

    [Fact]
    public void SamplerUsesTwoEqualBaseReadsAndOneAtomicTargetObservation()
    {
        var baseSignals = CreateBaseSignals();
        var target = CreateVerificationResult(
            targetEpoch: 11,
            executableName: "atomic.exe",
            windowTitle: "Atomic title",
            displayKey: @"\\.\DISPLAY11");
        var baseReads = 0;
        var targetReads = 0;
        var invalidations = 0;
        var sampler = new WindowsCapturePrivacySampler(
            () =>
            {
                Interlocked.Increment(ref baseReads);
                return baseSignals;
            },
            () =>
            {
                Interlocked.Increment(ref targetReads);
                return target;
            },
            () => Interlocked.Increment(ref invalidations));

        sampler.InvalidateTargetObservation();
        var observation = sampler.Sample();

        Assert.Equal(2, baseReads);
        Assert.Equal(1, targetReads);
        Assert.Equal(1, invalidations);
        Assert.Same(target.Target, observation.Signals.Target);
        Assert.Same(target.CaptureIdentity, observation.Signals.CaptureIdentity);
        Assert.Same(target.DisplayTarget, observation.DisplayTarget);
        Assert.Equal(
            target.DisplayTarget.MonitorHandle,
            observation.Signals.Target.DisplayMonitorHandle);
        Assert.Equal(
            target.DisplayTarget.DeviceKey,
            observation.Signals.Target.DisplayDeviceKey,
            ignoreCase: true);
        Assert.Equal(baseSignals.SessionUnlocked, observation.Signals.SessionUnlocked);
        Assert.Equal(baseSignals.StorageAvailable, observation.Signals.StorageAvailable);
    }

    [Fact]
    public void SamplerFailsClosedWhenTheBaseProbeChangesAroundVerification()
    {
        var samples = new Queue<NativeCapturePrivacySignals>(
        [
            CreateBaseSignals(),
            CreateBaseSignals(storage: NativeCapturePolicyDecision.Block),
        ]);
        var sampler = new WindowsCapturePrivacySampler(
            samples.Dequeue,
            () => CreateVerificationResult(1),
            static () => { });

        var observation = sampler.Sample();

        Assert.Same(
            NativeCapturePrivacySignals.FailClosed,
            observation.Signals);
        Assert.Same(
            WindowsCaptureDisplayTarget.Unknown,
            observation.DisplayTarget);
    }

    [Fact]
    public async Task StorageRefreshBlocksWithoutAWindowEventWhenHeadroomDrops()
    {
        var storageDecision = NativeCapturePolicyDecision.Allow;
        var sink = new FakePrivacySignalSink();
        var sampler = new FakeStoragePrivacySampler(
            (_, _) => Task.FromResult(WithStorage(
                CreateObservation(301),
                storageDecision)),
            (_, _) => Task.FromResult(storageDecision));
        var source = new FakeEventSource();
        var refreshWait = new ControlledStorageRefreshWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            WindowsCapturePrivacyMonitor.StorageRefreshInterval,
            refreshWait.WaitAsync);

        await monitor.StartAsync();
        var allowed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        var refresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        storageDecision = NativeCapturePolicyDecision.Block;

        refresh.Release();

        var blocked = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(allowed.Generation + 1, blocked.Generation);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            blocked.Signals.StorageAvailable);
        Assert.Equal(blocked.Generation, sink.BarrierGenerations.Last());
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        Assert.True(
            monitor.ObservedInvalidationReasons.HasFlag(
                WindowsCapturePrivacyInvalidationReason.StorageHeadroomChanged));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task StorageRefreshExceptionFailsClosedAsUnknownAndCanRecover()
    {
        var fullSampleStorage = NativeCapturePolicyDecision.Allow;
        var storageRead = 0;
        var sink = new FakePrivacySignalSink();
        var sampler = new FakeStoragePrivacySampler(
            (_, _) => Task.FromResult(WithStorage(
                CreateObservation(302),
                fullSampleStorage)),
            (_, _) =>
            {
                if (Interlocked.Increment(ref storageRead) == 1)
                {
                    fullSampleStorage = NativeCapturePolicyDecision.Unknown;
                    return Task.FromException<NativeCapturePolicyDecision>(
                        new IOException("storage unavailable"));
                }

                fullSampleStorage = NativeCapturePolicyDecision.Allow;
                return Task.FromResult(NativeCapturePolicyDecision.Allow);
            });
        var source = new FakeEventSource();
        var refreshWait = new ControlledStorageRefreshWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            WindowsCapturePrivacyMonitor.StorageRefreshInterval,
            refreshWait.WaitAsync);

        await monitor.StartAsync();
        var allowed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        var failedRefresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        failedRefresh.Release();

        var unknown = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(allowed.Generation + 1, unknown.Generation);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            unknown.Signals.StorageAvailable);
        Assert.False(monitor.Completion.IsCompleted);

        var recoveryRefresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        recoveryRefresh.Release();

        var recovered = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(unknown.Generation + 1, recovered.Generation);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            recovered.Signals.StorageAvailable);
        Assert.False(monitor.Completion.IsCompleted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task StableStorageRefreshDoesNotAdvanceContinuousTargetOrGeneration()
    {
        var sink = new FakePrivacySignalSink();
        sink.SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        var sampler = new FakeStoragePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(303)),
            static (_, _) => Task.FromResult(
                NativeCapturePolicyDecision.Allow));
        var source = new FakeEventSource();
        var refreshWait = new ControlledStorageRefreshWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            WindowsCapturePrivacyMonitor.StorageRefreshInterval,
            refreshWait.WaitAsync);

        await monitor.StartAsync();
        var initial = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        var generation = sink.PrivacyObservationGeneration;
        var targetEpoch = monitor.LastObservation.Signals.Target.TargetEpoch;

        for (var index = 0; index < 3; index++)
        {
            var refresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
            refresh.Release();
        }

        _ = await refreshWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(initial.Generation, generation);
        Assert.Equal(generation, sink.PrivacyObservationGeneration);
        Assert.Equal(generation, monitor.LastPublishedGeneration);
        Assert.Equal(
            targetEpoch,
            monitor.LastObservation.Signals.Target.TargetEpoch);
        Assert.Equal(1, sampler.InvalidationCount);
        Assert.Equal(1, sampler.SampleCount);
        Assert.Equal(3, sampler.StorageSampleCount);
        Assert.False(
            monitor.ObservedInvalidationReasons.HasFlag(
                WindowsCapturePrivacyInvalidationReason.StorageHeadroomChanged));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SessionHoldRecoversFromPeriodicProbeWithoutAnAvailableEvent()
    {
        var sessionDecision = NativeCapturePolicyDecision.Block;
        var sink = new FakePrivacySignalSink();
        var sampler = new FakeStoragePrivacySampler(
            static (call, _) => Task.FromResult(CreateObservation(
                checked((ulong)call))),
            static (_, _) => Task.FromResult(
                NativeCapturePolicyDecision.Allow),
            (_, _) => Task.FromResult(sessionDecision));
        var source = new FakeEventSource();
        var refreshWait = new ControlledStorageRefreshWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            WindowsCapturePrivacyMonitor.StorageRefreshInterval,
            refreshWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.SessionUnavailable);
        var unavailable = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, unavailable.Signals);
        Assert.True(monitor.ActiveHolds.HasFlag(
            WindowsCapturePrivacyHold.SessionUnavailable));

        var blockedRefresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        blockedRefresh.Release();
        var recoveryRefresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(1, sampler.StorageSampleCount);
        Assert.Equal(1, sampler.SessionSampleCount);

        sessionDecision = NativeCapturePolicyDecision.Allow;
        recoveryRefresh.Release();

        var recovered = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, recovered.Generation);
        Assert.Equal<ulong>(2, recovered.Signals.Target.TargetEpoch);
        Assert.Equal(WindowsCapturePrivacyHold.None, monitor.ActiveHolds);
        Assert.Equal(2, sampler.SampleCount);
        Assert.Equal(2, sampler.StorageSampleCount);
        Assert.Equal(2, sampler.SessionSampleCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.SessionAvailable));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCancelsAndAwaitsAnActiveStorageRefresh()
    {
        var storageEntered = CreateCompletionSource();
        var storageCancelled = CreateCompletionSource();
        var sink = new FakePrivacySignalSink();
        var sampler = new FakeStoragePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(304)),
            async (_, cancellationToken) =>
            {
                storageEntered.TrySetResult();
                try
                {
                    await Task.Delay(
                        System.Threading.Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    return NativeCapturePolicyDecision.Allow;
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        storageCancelled.TrySetResult();
                    }
                }
            });
        var source = new FakeEventSource();
        var refreshWait = new ControlledStorageRefreshWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            WindowsCapturePrivacyMonitor.StorageRefreshInterval,
            refreshWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        var refresh = await refreshWait.ReadAsync().WaitAsync(Timeout);
        refresh.Release();
        await storageEntered.Task.WaitAsync(Timeout);

        await monitor.DisposeAsync().AsTask().WaitAsync(Timeout);

        await storageCancelled.Task.WaitAsync(Timeout);
        Assert.Equal(1, sampler.StorageSampleCount);
        Assert.Equal(1, source.DisposeCount);
        await monitor.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task CallbackReturnsWithInvalidationBeforeSamplingRuns()
    {
        var order = new ConcurrentQueue<string>();
        var sink = new FakePrivacySignalSink(order)
        {
            BlockBarrierCall = 2,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)),
            order);
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.Foreground);

        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(0, sampler.SampleCount);
        Assert.Equal(
            ["sink", "target", "sink", "target"],
            order.ToArray());

        sink.ReleaseBlockedBarrier();
        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task LocationChangeStormInvalidatesEveryEventAndRecoversFromTheLatestGeneration()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 2,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(101)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);
        for (var index = 0; index < 100; index++)
        {
            source.EmitChange(WindowsCaptureWinEventChange.ObjectLocationChanged);
        }

        Assert.Equal(101, sink.PrivacyObservationGeneration);
        Assert.Equal(0, sampler.SampleCount);
        sink.ReleaseBlockedBarrier();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(101, update.Generation);
        Assert.Equal(1, sampler.SampleCount);
        Assert.Equal(101, sampler.InvalidationCount);
        Assert.Equal(101, monitor.LastPublishedGeneration);
        Assert.Equal<ulong>(
            101,
            monitor.LastObservation.Signals.Target.TargetEpoch);
        Assert.True(
            monitor.ObservedInvalidationReasons.HasFlag(
                WindowsCapturePrivacyInvalidationReason.ObjectLocationChanged));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ObjectEventsForOtherWindowsDoNotInvalidateThePublishedTarget()
    {
        const ulong targetEpoch = 41;
        const ulong targetWindowHandle = targetEpoch + 100;
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(targetEpoch)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        source.EmitChange(WindowsCaptureWinEventChange.ObjectCreated, 900);
        source.EmitChange(WindowsCaptureWinEventChange.ObjectDestroyed, 901);
        source.EmitChange(WindowsCaptureWinEventChange.ObjectNameChanged, 902);
        source.EmitChange(WindowsCaptureWinEventChange.ObjectLocationChanged, 903);

        Assert.Equal(1L, sink.PrivacyObservationGeneration);
        Assert.Equal(1, sampler.InvalidationCount);
        Assert.Equal(1, sampler.SampleCount);
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectCreated));
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectDestroyed));
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectNameChanged));
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectLocationChanged));

        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectNameChanged,
            targetWindowHandle);

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectNameChanged));
        await monitor.DisposeAsync();
    }

    [Theory]
    [InlineData(CaptureState.Starting)]
    [InlineData(CaptureState.Recording)]
    [InlineData(CaptureState.Pausing)]
    [InlineData(CaptureState.Paused)]
    [InlineData(CaptureState.Resuming)]
    [InlineData(CaptureState.Stopping)]
    [InlineData(CaptureState.Faulted)]
    public async Task AllowAllApplicationsPinnedStateIgnoresForegroundSwitch(
        CaptureState captureState)
    {
        var initial = CreateObservation(
            1,
            displayKey: @"\\.\DISPLAY1",
            windowHandle: 101,
            displayMonitorHandle: 201);
        var changed = CreateObservation(
            2,
            displayKey: @"\\.\DISPLAY2",
            windowHandle: 102,
            displayMonitorHandle: 202);
        var sink = new FakePrivacySignalSink();
        sink.SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        sink.SetCaptureState(captureState);
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1 ? initial : changed));
        var source = new FakeEventSource();
        var resolverCalls = 0;
        bool Resolve(ulong _, out WindowsCaptureDisplayAnchor display)
        {
            Interlocked.Increment(ref resolverCalls);
            display = new WindowsCaptureDisplayAnchor(
                changed.DisplayTarget.MonitorHandle,
                changed.DisplayTarget.DeviceKey!);
            return true;
        }

        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            static (_, _) => Task.CompletedTask,
            Resolve);

        await monitor.StartAsync();
        var published = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            changed.Signals.Target.WindowHandle);

        Assert.Equal(0, resolverCalls);
        Assert.Equal(1, sink.PrivacyObservationGeneration);
        Assert.Equal(1, sampler.InvalidationCount);
        Assert.Equal(1, sampler.SampleCount);
        Assert.Equal(
            initial.Signals.Target.TargetEpoch,
            published.Signals.Target.TargetEpoch);
        Assert.Equal(
            initial.DisplayTarget.MonitorHandle,
            monitor.LastObservation.DisplayTarget.MonitorHandle);
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.Foreground));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task AllowAllApplicationsRecordingIgnoresLocationAndDestroyedEvents()
    {
        const ulong windowHandle = 101;
        var initial = CreateObservation(
            1,
            displayKey: @"\\.\DISPLAY1",
            windowHandle: windowHandle,
            displayMonitorHandle: 201);
        var sink = new FakePrivacySignalSink();
        sink.SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        sink.SetCaptureState(CaptureState.Recording);
        var sampler = new FakePrivacySampler(
            (_, _) => Task.FromResult(initial));
        var source = new FakeEventSource();
        var resolverCalls = 0;
        bool Resolve(ulong _, out WindowsCaptureDisplayAnchor display)
        {
            Interlocked.Increment(ref resolverCalls);
            display = new WindowsCaptureDisplayAnchor(
                202,
                @"\\.\DISPLAY2");
            return true;
        }

        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            static (_, _) => Task.CompletedTask,
            Resolve);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectLocationChanged,
            windowHandle);
        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectDestroyed,
            windowHandle);
        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectLocationChanged,
            windowHandle);

        Assert.Equal(0, resolverCalls);
        Assert.Equal(1, sink.PrivacyObservationGeneration);
        Assert.Equal(1, sampler.InvalidationCount);
        Assert.Equal(1, sampler.SampleCount);
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectLocationChanged));
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.ObjectDestroyed));
        await monitor.DisposeAsync();
    }

    [Theory]
    [InlineData(CaptureState.Stopped)]
    [InlineData(CaptureState.Unavailable)]
    [InlineData(CaptureState.BlockedByConsent)]
    public async Task AllowAllApplicationsNonCapturingStateSelectsANewDisplay(
        CaptureState captureState)
    {
        var initial = CreateObservation(
            1,
            displayKey: @"\\.\DISPLAY1",
            windowHandle: 101,
            displayMonitorHandle: 201);
        var changed = CreateObservation(
            2,
            displayKey: @"\\.\DISPLAY2",
            windowHandle: 102,
            displayMonitorHandle: 202);
        var sink = new FakePrivacySignalSink();
        sink.SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1 ? initial : changed));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        sink.SetCaptureState(captureState);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            changed.Signals.Target.WindowHandle);
        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        Assert.Equal(2, update.Generation);
        Assert.Equal(
            changed.DisplayTarget.MonitorHandle,
            update.Signals.Target.DisplayMonitorHandle);
        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.Foreground));
        await monitor.DisposeAsync();
    }

    [Theory]
    [InlineData((int)WindowsCaptureWinEventChange.DesktopSwitch)]
    [InlineData((int)WindowsCaptureWinEventChange.DisplayTopologyChanged)]
    public async Task AllowAllApplicationsRecordingBoundaryChangeAppliesBarrier(
        int changeValue)
    {
        var change = (WindowsCaptureWinEventChange)changeValue;
        var initial = CreateObservation(
            1,
            displayKey: @"\\.\DISPLAY1",
            windowHandle: 101,
            displayMonitorHandle: 201);
        var changed = CreateObservation(
            2,
            displayKey: @"\\.\DISPLAY2",
            windowHandle: 102,
            displayMonitorHandle: 202);
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 3,
        };
        sink.SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        sink.SetCaptureState(CaptureState.Recording);
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1 ? initial : changed));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(change);
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);

        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(2L, sink.BarrierGenerations.Last());
        Assert.Equal(1, sampler.SampleCount);

        sink.ReleaseBlockedBarrier();
        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        Assert.Equal(2, update.Generation);
        Assert.Equal<ulong>(202, update.Signals.Target.DisplayMonitorHandle);
        var expectedReason = change == WindowsCaptureWinEventChange.DesktopSwitch
            ? WindowsCapturePrivacyInvalidationReason.DesktopSwitch
            : WindowsCapturePrivacyInvalidationReason.DisplayTopologyChanged;
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(expectedReason));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundChangeTracksTheCandidateWindowBeforeRecovery()
    {
        const ulong oldTargetEpoch = 51;
        const ulong oldWindowHandle = oldTargetEpoch + 100;
        const ulong newTargetEpoch = 800;
        const ulong newWindowHandle = newTargetEpoch + 100;
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 3,
        };
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(oldTargetEpoch)
                : CreateObservation(newTargetEpoch)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            newWindowHandle);
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);

        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectLocationChanged,
            oldWindowHandle);
        Assert.Equal(2L, sink.PrivacyObservationGeneration);

        source.EmitChange(
            WindowsCaptureWinEventChange.ObjectLocationChanged,
            newWindowHandle);
        Assert.Equal(3L, sink.PrivacyObservationGeneration);
        sink.ReleaseBlockedBarrier();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, update.Generation);
        Assert.Equal(newWindowHandle, update.Signals.Target.WindowHandle);
        Assert.Equal(3, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundCandidateRejectsAStableStaleTargetBeforePublication()
    {
        var initial = CreateObservation(51, executableName: "WinDayFlow.App.exe");
        var stale = CreateObservation(51, executableName: "WinDayFlow.App.exe");
        var recovered = CreateObservation(800, executableName: "notepad.exe");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => initial,
                2 => stale,
                3 => recovered,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            recovered.Signals.Target.WindowHandle);

        var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(50), retry.Delay);
        Assert.Equal([1L], sink.UpdateAttemptGenerations);
        Assert.Equal(2, sampler.SampleCount);
        retry.Release();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Same(recovered.Signals, update.Signals);
        Assert.Equal(
            recovered.Signals.Target.WindowHandle,
            update.Signals.Target.WindowHandle);
        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);
        Assert.DoesNotContain(
            sink.Updates,
            update => ReferenceEquals(update.Signals, stale.Signals));
        Assert.False(monitor.Completion.IsFaulted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundCandidateMismatchEntersSlowRecoveryWithoutAnotherEvent()
    {
        var self = CreateObservation(51, executableName: "WinDayFlow.App.exe");
        var external = CreateObservation(800, executableName: "notepad.exe");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => self,
                <= 5 => self,
                6 => external,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            external.Signals.Target.WindowHandle);

        foreach (var expectedDelay in new[]
                 {
                     TimeSpan.FromMilliseconds(50),
                     TimeSpan.FromMilliseconds(150),
                     TimeSpan.FromMilliseconds(350),
                 })
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            Assert.Equal(expectedDelay, retry.Delay);
            retry.Release();
        }

        var failClosed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, failClosed.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, failClosed.Signals);

        var recoveryRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            recoveryRetry.Delay);
        recoveryRetry.Release();

        var recovered = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, recovered.Generation);
        Assert.Same(external.Signals, recovered.Signals);
        Assert.Equal(
            external.Signals.Target.WindowHandle,
            recovered.Signals.Target.WindowHandle);
        Assert.Equal([1L, 2L, 3L], sink.UpdateAttemptGenerations);
        Assert.Equal(6, sampler.SampleCount);
        Assert.Equal(4, retryWait.WaitCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.TransientTargetRecovery));
        Assert.False(monitor.Completion.IsFaulted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundTransientUnknownRecoversWithoutAnotherWinEvent()
    {
        var initial = CreateObservation(61);
        var recovered = CreateObservation(62);
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => initial,
                2 => WindowsCapturePrivacyObservation.FailClosed,
                3 => recovered,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            recovered.Signals.Target.WindowHandle);

        var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(50), retry.Delay);
        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal([1L], sink.UpdateAttemptGenerations);
        retry.Release();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Same(recovered.Signals, update.Signals);
        Assert.Equal(
            recovered.Signals.Target.WindowHandle,
            update.Signals.Target.WindowHandle);
        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);
        Assert.Equal(3, sampler.InvalidationCount);
        Assert.Equal(3, sampler.SampleCount);
        Assert.Equal(1, retryWait.WaitCount);
        Assert.False(monitor.Completion.IsFaulted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundDuringRetryWaitAbandonsTheStaleGeneration()
    {
        const ulong firstCandidateWindow = 9_001;
        var initial = CreateObservation(71);
        var latest = CreateObservation(73);
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => initial,
                2 => WindowsCapturePrivacyObservation.FailClosed,
                3 => latest,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            firstCandidateWindow);

        _ = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal([1L], sink.UpdateAttemptGenerations);

        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            latest.Signals.Target.WindowHandle);
        Assert.Equal(3, sink.PrivacyObservationGeneration);

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, update.Generation);
        Assert.Same(latest.Signals, update.Signals);
        Assert.Equal([1L, 3L], sink.UpdateAttemptGenerations);
        Assert.Equal(3, sampler.InvalidationCount);
        Assert.Equal(3, sampler.SampleCount);
        Assert.Equal(1, retryWait.WaitCount);
        Assert.False(monitor.Completion.IsFaulted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundWaitsForCancellationCallbacksBeforeResampling()
    {
        var initial = CreateObservation(76);
        var latest = CreateObservation(78);
        var delayStarted = CreateCompletionSource();
        var delayCompletion = CreateCompletionSource();
        var cancellationCallbackEntered = CreateCompletionSource();
        var releaseCancellationCallback = CreateCompletionSource();
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => initial,
                2 => WindowsCapturePrivacyObservation.FailClosed,
                3 => latest,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = delay;
            _ = cancellationToken.Register(() =>
            {
                delayCompletion.TrySetCanceled(cancellationToken);
                cancellationCallbackEntered.TrySetResult();
                releaseCancellationCallback.Task.GetAwaiter().GetResult();
            });
            delayStarted.TrySetResult();
            return delayCompletion.Task;
        }

        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            DelayAsync);
        try
        {
            await monitor.StartAsync();
            _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
            source.EmitChange(
                WindowsCaptureWinEventChange.Foreground,
                windowHandle: 9_051);
            await delayStarted.Task.WaitAsync(Timeout);

            source.EmitChange(
                WindowsCaptureWinEventChange.Foreground,
                latest.Signals.Target.WindowHandle);
            await cancellationCallbackEntered.Task.WaitAsync(Timeout);

            Assert.Equal(3, sink.PrivacyObservationGeneration);
            Assert.Equal([1L], sink.UpdateAttemptGenerations);
            Assert.Equal(2, sampler.SampleCount);

            releaseCancellationCallback.TrySetResult();
            var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
            Assert.Equal(3, update.Generation);
            Assert.Same(latest.Signals, update.Signals);
            Assert.Equal([1L, 3L], sink.UpdateAttemptGenerations);
            Assert.Equal(3, sampler.SampleCount);
        }
        finally
        {
            releaseCancellationCallback.TrySetResult();
            await monitor.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationCallbackFailureClosesTheMonitorFailClosed()
    {
        var delayStarted = CreateCompletionSource();
        var delayCompletion = CreateCompletionSource();
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(79)
                : WindowsCapturePrivacyObservation.FailClosed));
        var source = new FakeEventSource();
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = delay;
            _ = cancellationToken.Register(() =>
            {
                delayCompletion.TrySetCanceled(cancellationToken);
                throw new InvalidOperationException(
                    "private cancellation callback failure");
            });
            delayStarted.TrySetResult();
            return delayCompletion.Task;
        }

        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            DelayAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_061);
        await delayStarted.Task.WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_062);

        var failure = await Assert.ThrowsAsync<
            WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));
        Assert.Equal(WindowsCapturePrivacyMonitorFault.Worker, failure.Fault);
        Assert.Null(failure.InnerException);
        Assert.Equal([1L], sink.UpdateAttemptGenerations);
        Assert.Equal(
            sink.PrivacyObservationGeneration,
            sink.BarrierGenerations.Last());
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ForegroundUnknownRetryBudgetExhaustionRecoversWithoutAnotherWinEvent()
    {
        var recovered = CreateObservation(82);
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => CreateObservation(81),
                <= 5 => WindowsCapturePrivacyObservation.FailClosed,
                6 => recovered,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            recovered.Signals.Target.WindowHandle);

        var expectedDelays = new[]
        {
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(350),
        };
        foreach (var expectedDelay in expectedDelays)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            Assert.Equal(expectedDelay, retry.Delay);
            Assert.Equal(2, sink.PrivacyObservationGeneration);
            Assert.Equal([1L], sink.UpdateAttemptGenerations);
            retry.Release();
        }

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, update.Signals);
        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);
        Assert.Equal(
            1 + WindowsCapturePrivacyMonitor
                .MaxTransientTargetObservationAttempts,
            sampler.SampleCount);

        var recoveryRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            recoveryRetry.Delay);
        Assert.Equal(4, retryWait.WaitCount);
        recoveryRetry.Release();

        var recoveredUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, recoveredUpdate.Generation);
        Assert.Same(recovered.Signals, recoveredUpdate.Signals);
        Assert.Equal([1L, 2L, 3L], sink.UpdateAttemptGenerations);
        Assert.Equal(3, sink.PrivacyObservationGeneration);
        Assert.Equal(6, sampler.InvalidationCount);
        Assert.Equal(6, sampler.SampleCount);
        Assert.Equal(4, retryWait.WaitCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.TransientTargetRecovery));
        Assert.False(monitor.Completion.IsFaulted);

        await monitor.DisposeAsync();
        Assert.Equal(7, sampler.InvalidationCount);
        Assert.Equal(6, sampler.SampleCount);
        Assert.Equal(4, retryWait.WaitCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ForegroundAbsentRetryBudgetExhaustionRecoversWithoutAnotherWinEvent()
    {
        var absent = CreateAbsentTargetObservation();
        var recovered = CreateObservation(84);
        Assert.True(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(absent));
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => CreateObservation(83),
                <= 5 => absent,
                6 => recovered,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            recovered.Signals.Target.WindowHandle);

        var expectedDelays = new[]
        {
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(350),
        };
        foreach (var expectedDelay in expectedDelays)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            Assert.Equal(expectedDelay, retry.Delay);
            retry.Release();
        }

        var blockedUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, blockedUpdate.Generation);
        Assert.Same(absent.Signals, blockedUpdate.Signals);
        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);

        var recoveryRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            recoveryRetry.Delay);
        recoveryRetry.Release();

        var recoveredUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, recoveredUpdate.Generation);
        Assert.Same(recovered.Signals, recoveredUpdate.Signals);
        Assert.Equal([1L, 2L, 3L], sink.UpdateAttemptGenerations);
        Assert.Equal(6, sampler.InvalidationCount);
        Assert.Equal(6, sampler.SampleCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.TransientTargetRecovery));
        Assert.False(monitor.Completion.IsFaulted);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitBlockTargetUnknownDoesNotEnterSlowRecovery()
    {
        var blocked = CreateUnknownTargetObservation(
            applicationAllowed: NativeCapturePolicyDecision.Block);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(blocked));
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(86)
                : blocked));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_151);

        for (var index = 0;
             index < WindowsCapturePrivacyMonitor
                 .MaxTransientTargetObservationAttempts - 1;
             index++)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            retry.Release();
        }

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Same(blocked.Signals, update.Signals);
        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);
        Assert.Equal(5, sampler.SampleCount);
        Assert.Equal(3, retryWait.WaitCount);

        await monitor.DisposeAsync();
        Assert.Equal(3, retryWait.WaitCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeCancelsAPendingSlowTargetRecoveryRetry()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(88)
                : WindowsCapturePrivacyObservation.FailClosed));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_181);

        for (var index = 0;
             index < WindowsCapturePrivacyMonitor
                 .MaxTransientTargetObservationAttempts - 1;
             index++)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            retry.Release();
        }

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, update.Signals);
        var recoveryRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            recoveryRetry.Delay);

        await monitor.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal([1L, 2L], sink.UpdateAttemptGenerations);
        Assert.Equal(6, sampler.InvalidationCount);
        Assert.Equal(5, sampler.SampleCount);
        Assert.Equal(4, retryWait.WaitCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ForegroundDuringSlowRecoveryOwnsTheNextGeneration()
    {
        var recovered = CreateObservation(89);
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call switch
            {
                1 => CreateObservation(88),
                <= 5 => WindowsCapturePrivacyObservation.FailClosed,
                6 => recovered,
                _ => throw new InvalidOperationException(),
            }));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_182);

        for (var index = 0;
             index < WindowsCapturePrivacyMonitor
                 .MaxTransientTargetObservationAttempts - 1;
             index++)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            retry.Release();
        }

        var failClosed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, failClosed.Generation);
        var slowRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            slowRetry.Delay);

        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            recovered.Signals.Target.WindowHandle);
        var recoveredUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        Assert.Equal(3, recoveredUpdate.Generation);
        Assert.Same(recovered.Signals, recoveredUpdate.Signals);
        Assert.Equal(3, sink.PrivacyObservationGeneration);
        Assert.Equal([1L, 2L, 3L], sink.UpdateAttemptGenerations);
        Assert.Equal(6, sampler.SampleCount);
        Assert.Equal(4, retryWait.WaitCount);
        Assert.False(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.TransientTargetRecovery));

        await monitor.DisposeAsync();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

#if WDF_DEV_LIVE_CAPTURE
    [Fact]
    public async Task DevLiveForegroundSwitchFromSelfToExternalKeepsTargetsAllowed()
    {
        var self = CreateObservation(
            targetEpoch: 88,
            executable: NativeCaptureObservation.Present("WinDayFlow.App.exe"),
            packageFamily: NativeCaptureObservation.Absent,
            windowTitle: NativeCaptureObservation.Present("WinDayFlow"));
        var external = CreateObservation(
            targetEpoch: 89,
            executable: NativeCaptureObservation.Present("notepad.exe"),
            packageFamily: NativeCaptureObservation.Absent,
            windowTitle: NativeCaptureObservation.Present("Untitled - Notepad"));
        var sink = new FakePrivacySignalSink();
        var innerSampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1 ? self : external));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            new DevLiveQaWindowsCapturePrivacySampler(innerSampler),
            source);

        await monitor.StartAsync();
        var selfUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            selfUpdate.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            selfUpdate.Signals.WindowAllowed);

        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            external.Signals.Target.WindowHandle);
        var externalUpdate = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        Assert.Equal(2, externalUpdate.Generation);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            externalUpdate.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            externalUpdate.Signals.WindowAllowed);
        Assert.Equal(
            external.Signals.Target,
            externalUpdate.Signals.Target);

        await monitor.DisposeAsync();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DevLiveSelfWithUnresolvedOptionalIdentityDoesNotRetry()
    {
        var self = CreateObservation(
            targetEpoch: 89,
            executable: NativeCaptureObservation.Present("WinDayFlow.App.exe"),
            packageFamily: NativeCaptureObservation.Unknown,
            windowTitle: NativeCaptureObservation.Unknown);
        var sink = new FakePrivacySignalSink();
        var innerSampler = new FakePrivacySampler(
            (_, _) => Task.FromResult(self));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            new DevLiveQaWindowsCapturePrivacySampler(innerSampler),
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            update.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            update.Signals.WindowAllowed);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Present,
            update.Signals.Target.State);
        Assert.Equal(1, innerSampler.SampleCount);
        Assert.Equal(0, retryWait.WaitCount);

        await monitor.DisposeAsync();
        Assert.Equal(0, retryWait.WaitCount);
    }

    [Fact]
    public async Task DevLiveExplicitBlockWithUnknownTargetDoesNotEnterSlowRecovery()
    {
        var blocked = CreateUnknownTargetObservation(
            applicationAllowed: NativeCapturePolicyDecision.Block);
        var policyResult = DevLiveQaWindowsCapturePrivacySampler.ApplyPolicy(
            blocked);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(policyResult));
        var sink = new FakePrivacySignalSink();
        var innerSampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(
                    targetEpoch: 90,
                    executable: NativeCaptureObservation.Present("sample.exe"),
                    packageFamily: NativeCaptureObservation.Absent,
                    windowTitle: NativeCaptureObservation.Present("Sample title"))
                : blocked));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            new DevLiveQaWindowsCapturePrivacySampler(innerSampler),
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_191);

        for (var index = 0;
             index < WindowsCapturePrivacyMonitor
                 .MaxTransientTargetObservationAttempts - 1;
             index++)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            retry.Release();
        }

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            update.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Unknown,
            update.Signals.Target.State);
        Assert.Equal(3, retryWait.WaitCount);

        await monitor.DisposeAsync();
        Assert.Equal(3, retryWait.WaitCount);
    }

    [Fact]
    public async Task DevLiveUnresolvedExternalTargetEntersSlowRecovery()
    {
        var sink = new FakePrivacySignalSink();
        var innerSampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(
                    targetEpoch: 92,
                    executable: NativeCaptureObservation.Present("sample.exe"),
                    packageFamily: NativeCaptureObservation.Absent,
                    windowTitle: NativeCaptureObservation.Present("Sample title"))
                : CreateUnknownTargetObservation()));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            new DevLiveQaWindowsCapturePrivacySampler(innerSampler),
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_221);

        for (var index = 0;
             index < WindowsCapturePrivacyMonitor
                 .MaxTransientTargetObservationAttempts - 1;
             index++)
        {
            var retry = await retryWait.ReadAsync().WaitAsync(Timeout);
            retry.Release();
        }

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, update.Signals);
        var recoveryRetry = await retryWait.ReadAsync().WaitAsync(Timeout);
        Assert.Equal(
            WindowsCapturePrivacyMonitor.TransientTargetRecoveryRetryDelay,
            recoveryRetry.Delay);
        Assert.Equal(4, retryWait.WaitCount);

        await monitor.DisposeAsync();
        Assert.Equal(4, retryWait.WaitCount);
    }
#endif

    [Fact]
    public async Task DisposeCancelsAPendingTransientTargetRetry()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1
                ? CreateObservation(91)
                : WindowsCapturePrivacyObservation.FailClosed));
        var source = new FakeEventSource();
        var retryWait = new ControlledRetryWait();
        var monitor = new WindowsCapturePrivacyMonitor(
            sink,
            sampler,
            source,
            retryWait.WaitAsync);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(
            WindowsCaptureWinEventChange.Foreground,
            windowHandle: 9_201);
        _ = await retryWait.ReadAsync().WaitAsync(Timeout);

        await monitor.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal([1L], sink.UpdateAttemptGenerations);
        Assert.Equal(3, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        Assert.Equal(1, retryWait.WaitCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisplayTopologyChangeInvalidatesTargetContinuityAndPublishesOnlyTheNewGeneration()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (call, _) => Task.FromResult(CreateObservation(
                checked((ulong)call),
                displayKey: $@"\\.\DISPLAY{call}")));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        source.EmitChange(WindowsCaptureWinEventChange.DisplayTopologyChanged);

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Equal<ulong>(2, update.Signals.Target.TargetEpoch);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(2, sampler.SampleCount);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.DisplayTopologyChanged));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SessionUnavailableHoldBlocksSamplingUntilAnAvailableEventIsReverified()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (call, _) => Task.FromResult(CreateObservation(
                checked((ulong)call))));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.SessionUnavailable);

        var unavailable = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, unavailable.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, unavailable.Signals);
        Assert.Equal(1, sampler.SampleCount);
        Assert.True(monitor.ActiveHolds.HasFlag(
            WindowsCapturePrivacyHold.SessionUnavailable));

        source.EmitChange(WindowsCaptureWinEventChange.SessionChanged);
        var changedWhileUnavailable = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, changedWhileUnavailable.Generation);
        Assert.Same(
            NativeCapturePrivacySignals.FailClosed,
            changedWhileUnavailable.Signals);
        Assert.Equal(1, sampler.SampleCount);

        source.EmitChange(WindowsCaptureWinEventChange.SessionAvailable);
        var recovered = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(4, recovered.Generation);
        Assert.Equal<ulong>(2, recovered.Signals.Target.TargetEpoch);
        Assert.Equal(2, sampler.SampleCount);
        Assert.Equal(WindowsCapturePrivacyHold.None, monitor.ActiveHolds);
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.SessionUnavailable));
        Assert.True(monitor.ObservedInvalidationReasons.HasFlag(
            WindowsCapturePrivacyInvalidationReason.SessionAvailable));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task PowerSuspendHoldSurvivesOtherEventsAndResumeRequiresANewBarrier()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (call, _) => Task.FromResult(CreateObservation(
                checked((ulong)call))));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.PowerSuspending);

        var suspended = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, suspended.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, suspended.Signals);
        Assert.Equal(1, sampler.SampleCount);
        Assert.True(monitor.ActiveHolds.HasFlag(
            WindowsCapturePrivacyHold.PowerSuspended));

        source.EmitChange(WindowsCaptureWinEventChange.DisplayTopologyChanged);
        var topologyWhileSuspended = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, topologyWhileSuspended.Generation);
        Assert.Same(
            NativeCapturePrivacySignals.FailClosed,
            topologyWhileSuspended.Signals);
        Assert.Equal(1, sampler.SampleCount);

        source.EmitChange(WindowsCaptureWinEventChange.PowerResumed);
        var resumed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(4, resumed.Generation);
        Assert.Equal<ulong>(2, resumed.Signals.Target.TargetEpoch);
        Assert.Equal(2, sampler.SampleCount);
        Assert.Equal(WindowsCapturePrivacyHold.None, monitor.ActiveHolds);
        Assert.Contains(2L, sink.BarrierGenerations);
        Assert.Contains(4L, sink.BarrierGenerations);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SuspendDuringSamplingMakesTheOldAllowStaleBeforePublishingFailClosed()
    {
        var sampleStarted = CreateCompletionSource();
        var releaseSample = CreateCompletionSource();
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                sampleStarted.TrySetResult();
                await releaseSample.Task.WaitAsync(cancellationToken);
            }

            return CreateObservation(checked((ulong)call));
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sampleStarted.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.PowerSuspending);
        releaseSample.TrySetResult();

        var suspended = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, suspended.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, suspended.Signals);
        Assert.Equal([2L], sink.UpdateAttemptGenerations);
        Assert.Equal(1, sampler.SampleCount);

        source.EmitChange(WindowsCaptureWinEventChange.PowerResumed);
        var resumed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(3, resumed.Generation);
        Assert.Equal<ulong>(2, resumed.Signals.Target.TargetEpoch);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SessionAndPowerHoldsRecoverIndependently()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (call, _) => Task.FromResult(CreateObservation(
                checked((ulong)call))));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.SessionUnavailable);
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.PowerSuspending);
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        source.EmitChange(WindowsCaptureWinEventChange.SessionAvailable);
        var sessionOnlyRecovery = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Same(
            NativeCapturePrivacySignals.FailClosed,
            sessionOnlyRecovery.Signals);
        Assert.Equal(
            WindowsCapturePrivacyHold.PowerSuspended,
            monitor.ActiveHolds);
        Assert.Equal(1, sampler.SampleCount);

        source.EmitChange(WindowsCaptureWinEventChange.PowerResumed);
        var fullyRecovered = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal<ulong>(2, fullyRecovered.Signals.Target.TargetEpoch);
        Assert.Equal(WindowsCapturePrivacyHold.None, monitor.ActiveHolds);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task EventDuringSampleDropsTheOldAtomicObservation()
    {
        var firstSampleStarted = CreateCompletionSource();
        var releaseFirstSample = CreateCompletionSource();
        var first = CreateObservation(1, windowTitle: "old-title");
        var second = CreateObservation(2, windowTitle: "new-title");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                firstSampleStarted.TrySetResult();
                await releaseFirstSample.Task.WaitAsync(cancellationToken);
                return first;
            }

            return second;
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await firstSampleStarted.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.Foreground);
        releaseFirstSample.TrySetResult();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Equal("new-title", update.Signals.CaptureIdentity.WindowTitle);
        Assert.Single(sink.Updates);
        Assert.Equal(2, sampler.SampleCount);
        Assert.Same(second.DisplayTarget, monitor.LastObservation.DisplayTarget);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task LocationWinEventDuringTargetTitleSamplingNeverAttemptsStalePublication()
    {
        var staleSampleStarted = CreateCompletionSource();
        var releaseStaleSample = CreateCompletionSource();
        var stale = CreateObservation(21, windowTitle: "stale-title");
        var latest = CreateObservation(22, windowTitle: "latest-title");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                staleSampleStarted.TrySetResult();
                await releaseStaleSample.Task.WaitAsync(cancellationToken);
                return stale;
            }

            return latest;
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await staleSampleStarted.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.ObjectLocationChanged);

        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(2, sampler.InvalidationCount);
        releaseStaleSample.TrySetResult();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Equal("latest-title", update.Signals.CaptureIdentity.WindowTitle);
        Assert.Equal([2L], sink.UpdateAttemptGenerations);
        Assert.Single(sink.Updates);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task EventDuringPublicationDropsTheOldAtomicObservation()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockUpdateCall = 1,
        };
        var oldObservation = CreateObservation(1, windowTitle: "old-title");
        var newObservation = CreateObservation(2, windowTitle: "new-title");
        var sampler = new FakePrivacySampler((call, _) => Task.FromResult(
            call == 1 ? oldObservation : newObservation));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedUpdateEntered.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.Foreground);
        sink.ReleaseBlockedUpdate();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Equal("new-title", update.Signals.CaptureIdentity.WindowTitle);
        Assert.Single(sink.Updates);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SourceStartFailureIsSanitizedAndEstablishesTheBarrier()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource
        {
            StartFailure = new InvalidOperationException(
                @"private C:\secret\window-title"),
        };
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => monitor.StartAsync());

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.EventSourceStart,
            exception.Fault);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains(1, sink.BarrierGenerations);
        Assert.Equal(0, sampler.SampleCount);
        Assert.Equal(1, source.DisposeCount);
        await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SourceFaultCallbackInvalidatesSynchronouslyAndNeverSamplesAgain()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 2,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);
        source.EmitFault(WindowsCaptureWinEventSourceFault.MessageLoopFailed);

        Assert.Equal(2, sink.PrivacyObservationGeneration);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(0, sampler.SampleCount);
        sink.ReleaseBlockedBarrier();

        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));
        Assert.Equal(WindowsCapturePrivacyMonitorFault.EventSource, exception.Fault);
        Assert.Null(exception.InnerException);
        var terminalGeneration = sink.PrivacyObservationGeneration;
        var terminalBarrier = sink.BarrierGenerations.Last();
        source.EmitFault(WindowsCaptureWinEventSourceFault.HookUnregistrationFailed);
        Assert.Equal(terminalGeneration, sink.PrivacyObservationGeneration);
        Assert.Equal(terminalBarrier, sink.BarrierGenerations.Last());
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task BarrierFailureTerminatesSanitizedAndKeepsSamplingClosed()
    {
        var sink = new FakePrivacySignalSink
        {
            FailBarrierCall = 1,
            BarrierFailure = new InvalidOperationException(
                "private-title-and-path"),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => monitor.StartAsync());

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.PrivacyBarrier,
            exception.Fault);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private-title", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, sampler.SampleCount);
        await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task SourceFaultCallbackSwallowsInvalidationFailureAndStillInvalidatesTarget()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 2,
            FailInvalidationCall = 2,
            InvalidationFailure = new InvalidOperationException(
                @"private C:\secret\title"),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);
        source.EmitFault(WindowsCaptureWinEventSourceFault.CallbackFailed);

        Assert.Equal(1, sink.PrivacyObservationGeneration);
        Assert.Equal(2, sampler.InvalidationCount);
        sink.ReleaseBlockedBarrier();
        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));
        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.ObservationInvalidation,
            exception.Fault);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedCurrentGenerationRejectionTerminatesInsteadOfWaitingForWake()
    {
        var sink = new FakePrivacySignalSink
        {
            RejectUpdateCall = 1,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.GenerationDesynchronized,
            exception.Fault);
        Assert.Equal(1, sampler.SampleCount);
        Assert.True(sink.PrivacyObservationGeneration >= 2);
        Assert.Equal(
            sink.PrivacyObservationGeneration,
            sink.BarrierGenerations.Last());
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task InFlightInvalidationDoesNotLookLikeGenerationDesynchronization()
    {
        var firstSampleEntered = CreateCompletionSource();
        var releaseFirstSample = CreateCompletionSource();
        var secondObservation = CreateObservation(92);
        var sink = new FakePrivacySignalSink
        {
            BlockInvalidationCall = 2,
        };
        var sampler = new FakePrivacySampler(async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                firstSampleEntered.TrySetResult();
                await releaseFirstSample.Task.WaitAsync(cancellationToken);
                return CreateObservation(91);
            }

            return secondObservation;
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);
        Task? invalidation = null;
        try
        {
            await monitor.StartAsync();
            await firstSampleEntered.Task.WaitAsync(Timeout);

            invalidation = Task.Run(() => source.EmitChange(
                WindowsCaptureWinEventChange.DesktopSwitch));
            await sink.BlockedInvalidationAdvanced.Task.WaitAsync(Timeout);
            releaseFirstSample.TrySetResult();

            var prematureCompletion = await Task.WhenAny(
                monitor.Completion,
                Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(monitor.Completion, prematureCompletion);

            sink.ReleaseBlockedInvalidation();
            await invalidation.WaitAsync(Timeout);
            var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
            Assert.Equal(2, update.Generation);
            Assert.Same(secondObservation.Signals, update.Signals);
            Assert.False(monitor.Completion.IsFaulted);
        }
        finally
        {
            releaseFirstSample.TrySetResult();
            sink.ReleaseBlockedInvalidation();
            if (invalidation is not null)
            {
                await invalidation.WaitAsync(Timeout);
            }

            await monitor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAppliesTheLastBarrierAndRejectsLateCallbacksIdempotently()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        var generationBeforeDispose = sink.PrivacyObservationGeneration;

        await monitor.DisposeAsync();
        var disposedGeneration = sink.PrivacyObservationGeneration;
        source.EmitLateChange(WindowsCaptureWinEventChange.Foreground);
        await monitor.DisposeAsync();

        Assert.Equal(generationBeforeDispose + 1, disposedGeneration);
        Assert.Equal(disposedGeneration, sink.PrivacyObservationGeneration);
        Assert.Equal(disposedGeneration, sink.BarrierGenerations.Last());
        Assert.Equal(1, source.DisposeCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeRejectsAnUnprovenDisposedSink()
    {
        var sink = new FakePrivacySignalSink
        {
            FailInvalidationCall = 2,
            InvalidationFailure = new ObjectDisposedException("sink"),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        var failure = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => monitor.DisposeAsync().AsTask());

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.ObservationInvalidation,
            failure.Fault);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.True(monitor.Completion.IsFaulted);
    }

    [Fact]
    public async Task DisposeWaitsForAProvenSinkTermination()
    {
        var releaseTermination = CreateCompletionSource();
        var sink = new FakePrivacySignalSink
        {
            FailInvalidationCall = 1,
            InvalidationFailure = new ObjectDisposedException("sink"),
            TerminationStarted = true,
            TerminationTask = releaseTermination.Task,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        var disposal = monitor.DisposeAsync().AsTask();
        await sink.TerminationProofRequested.Task.WaitAsync(Timeout);
        await source.Disposed.Task.WaitAsync(Timeout);
        Assert.False(disposal.IsCompleted);

        releaseTermination.TrySetResult();
        await disposal.WaitAsync(Timeout);
        await monitor.DisposeAsync();

        Assert.Equal(1, sampler.InvalidationCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeRejectsAnUnprovenWorkerSinkDisposal()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockUpdateCall = 1,
            FailUpdateCall = 1,
            UpdateFailure = new ObjectDisposedException("sink"),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedUpdateEntered.Task.WaitAsync(Timeout);
        var disposal = monitor.DisposeAsync().AsTask();
        await source.Disposed.Task.WaitAsync(Timeout);
        sink.ReleaseBlockedUpdate();

        var failure = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => disposal);

        Assert.Equal(WindowsCapturePrivacyMonitorFault.Worker, failure.Fault);
        Assert.True(sink.TerminationProofRequested.Task.IsCompletedSuccessfully);
        Assert.False(sink.TerminationStarted);
    }

    [Fact]
    public async Task DisposeObservesAFailedWorkerSinkTerminationProof()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockUpdateCall = 1,
            FailUpdateCall = 1,
            UpdateFailure = new ObjectDisposedException("sink"),
            TerminationStarted = true,
            TerminationTask = Task.FromException(
                new InvalidOperationException("termination failed")),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await sink.BlockedUpdateEntered.Task.WaitAsync(Timeout);
        var disposal = monitor.DisposeAsync().AsTask();
        await source.Disposed.Task.WaitAsync(Timeout);
        sink.ReleaseBlockedUpdate();

        var failure = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => disposal);

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.SinkTerminationDisposal,
            failure.Fault);
        Assert.True(sink.TerminationProofRequested.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeReportsAFailedProvenSinkTermination()
    {
        var sink = new FakePrivacySignalSink
        {
            FailBarrierCall = 3,
            BarrierFailure = new ObjectDisposedException("sink"),
            TerminationStarted = true,
            TerminationTask = Task.FromException(
                new InvalidOperationException("termination failed")),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        var failure = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => monitor.DisposeAsync().AsTask());

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.SinkTerminationDisposal,
            failure.Fault);
        Assert.True(sink.TerminationProofRequested.Task.IsCompletedSuccessfully);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task DisposeStillReportsANonterminalInvalidationFailure()
    {
        var sink = new FakePrivacySignalSink
        {
            FailInvalidationCall = 2,
            InvalidationFailure = new InvalidOperationException(
                "invalidation failed"),
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);

        var failure = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            () => monitor.DisposeAsync().AsTask());

        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.ObservationInvalidation,
            failure.Fault);
        Assert.Equal(2, sampler.InvalidationCount);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task EqualSignalsFromAnOldGenerationCannotPublish()
    {
        var firstSampleStarted = CreateCompletionSource();
        var releaseFirstSample = CreateCompletionSource();
        var sameObservation = CreateObservation(9, windowTitle: "same-title");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                firstSampleStarted.TrySetResult();
                await releaseFirstSample.Task.WaitAsync(cancellationToken);
            }

            return sameObservation;
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        await firstSampleStarted.Task.WaitAsync(Timeout);
        source.EmitChange(WindowsCaptureWinEventChange.DesktopSwitch);
        releaseFirstSample.TrySetResult();

        var update = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, update.Generation);
        Assert.Single(sink.Updates);
        Assert.Equal(2, sampler.SampleCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task RecoverableSampleFailurePublishesFailClosedAndCanRecover()
    {
        var recovered = CreateObservation(2, windowTitle: "recovered");
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler((call, _) =>
        {
            return call == 1
                ? Task.FromException<WindowsCapturePrivacyObservation>(
                    new IOException("private-title"))
                : Task.FromResult(recovered);
        });
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        var failed = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(1, failed.Generation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, failed.Signals);
        Assert.False(monitor.Completion.IsCompleted);

        source.EmitChange(WindowsCaptureWinEventChange.ObjectCreated);
        var successful = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        Assert.Equal(2, successful.Generation);
        Assert.Equal("recovered", successful.Signals.CaptureIdentity.WindowTitle);
        Assert.False(monitor.Completion.IsCompleted);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task StartEstablishesTheNativeBarrierBeforeStartingTheSource()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 1,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        var start = monitor.StartAsync();
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);

        Assert.Equal(0, source.StartCount);
        sink.ReleaseBlockedBarrier();
        await start.WaitAsync(Timeout);
        Assert.Equal(1, source.StartCount);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeEstablishesTheNativeBarrierBeforeDisposingTheSource()
    {
        var sink = new FakePrivacySignalSink();
        long generationAtSourceDisposal = 0;
        long barrierAtSourceDisposal = 0;
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource
        {
            DisposeAction = () =>
            {
                generationAtSourceDisposal = sink.PrivacyObservationGeneration;
                barrierAtSourceDisposal = sink.BarrierGenerations.Last();
            },
        };
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        await monitor.DisposeAsync();

        Assert.True(generationAtSourceDisposal > 0);
        Assert.Equal(generationAtSourceDisposal, barrierAtSourceDisposal);
    }

    [Fact]
    public async Task DisposePreservesASourceFaultThatTheWorkerHasNotCompleted()
    {
        var sink = new FakePrivacySignalSink
        {
            BlockBarrierCall = 3,
        };
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitFault(WindowsCaptureWinEventSourceFault.MessageLoopFailed);
        await sink.BlockedBarrierEntered.Task.WaitAsync(Timeout);

        var disposal = monitor.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        sink.ReleaseBlockedBarrier();
        await disposal.WaitAsync(Timeout);

        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion);
        Assert.Equal(WindowsCapturePrivacyMonitorFault.EventSource, exception.Fault);
    }

    [Fact]
    public async Task SourceCleanupFaultIsReportedWithoutReplacingTheTerminalFault()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromResult(CreateObservation(1)));
        var source = new FakeEventSource
        {
            FaultOnDispose = WindowsCaptureWinEventSourceFault
                .HookUnregistrationFailed,
        };
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        _ = await sink.ReadUpdateAsync().WaitAsync(Timeout);
        source.EmitFault(WindowsCaptureWinEventSourceFault.MessageLoopFailed);
        var terminal = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));

        var disposal = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.DisposeAsync());
        var preserved = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion);
        Assert.Equal(WindowsCapturePrivacyMonitorFault.EventSource, terminal.Fault);
        Assert.Equal(
            WindowsCapturePrivacyMonitorFault.EventSourceDisposal,
            disposal.Fault);
        Assert.Equal(terminal.Fault, preserved.Fault);
    }

    [Fact]
    public async Task ProgrammingFailureTerminatesAfterTheFinalNativeBarrier()
    {
        var sink = new FakePrivacySignalSink();
        var sampler = new FakePrivacySampler(
            static (_, _) => Task.FromException<WindowsCapturePrivacyObservation>(
                new InvalidOperationException("private invariant detail")));
        var source = new FakeEventSource();
        var monitor = new WindowsCapturePrivacyMonitor(sink, sampler, source);

        await monitor.StartAsync();
        var exception = await Assert.ThrowsAsync<WindowsCapturePrivacyMonitorException>(
            async () => await monitor.Completion.WaitAsync(Timeout));

        Assert.Equal(WindowsCapturePrivacyMonitorFault.Worker, exception.Fault);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            sink.PrivacyObservationGeneration,
            sink.BarrierGenerations.Last());
        await monitor.DisposeAsync();
    }

    [Fact]
    public void MonitorPublicSurfaceDoesNotOwnPauseOrStopPolicy()
    {
        var commandNames = typeof(WindowsCapturePrivacyMonitor)
            .GetMethods()
            .Select(static method => method.Name)
            .ToArray();

        Assert.DoesNotContain("PauseAsync", commandNames);
        Assert.DoesNotContain("StopAsync", commandNames);
    }

    private static NativeCapturePrivacySignals CreateBaseSignals(
        NativeCapturePolicyDecision storage = NativeCapturePolicyDecision.Allow)
    {
        return new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            storage);
    }

    private static WindowsCaptureTargetVerificationResult CreateVerificationResult(
        ulong targetEpoch,
        string executableName = "sample.exe",
        string windowTitle = "Sample title",
        string displayKey = @"\\.\DISPLAY1",
        ulong? windowHandle = null,
        ulong? displayMonitorHandle = null)
    {
        var identity = new NativeCaptureIdentitySnapshot(
            executableName,
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle);
        return new WindowsCaptureTargetVerificationResult(
            NativeCaptureTargetIdentity.Present(
                windowHandle: windowHandle ?? targetEpoch + 100,
                processId: checked((uint)targetEpoch + 10),
                processCreationTime100ns: targetEpoch + 1_000,
                targetEpoch,
                displayMonitorHandle: displayMonitorHandle ?? targetEpoch + 200,
                displayDeviceKey: displayKey),
            WindowsCaptureDisplayTarget.Present(
                monitorHandle: displayMonitorHandle ?? targetEpoch + 200,
                displayKey),
            identity);
    }

    private static WindowsCapturePrivacyObservation CreateObservation(
        ulong targetEpoch,
        string executableName = "sample.exe",
        string windowTitle = "Sample title",
        string displayKey = @"\\.\DISPLAY1",
        ulong? windowHandle = null,
        ulong? displayMonitorHandle = null)
    {
        var target = CreateVerificationResult(
            targetEpoch,
            executableName,
            windowTitle,
            displayKey,
            windowHandle,
            displayMonitorHandle);
        var signals = CreateBaseSignals();
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                signals.SessionUnlocked,
                signals.SecureDesktopClear,
                signals.RemoteSession,
                signals.PresentationMode,
                signals.ApplicationAllowed,
                signals.WindowAllowed,
                signals.StorageAvailable,
                target.CaptureIdentity,
                target.Target),
            target.DisplayTarget);
    }

    private static WindowsCapturePrivacyObservation WithStorage(
        WindowsCapturePrivacyObservation observation,
        NativeCapturePolicyDecision storageAvailable)
    {
        var signals = observation.Signals;
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                signals.SessionUnlocked,
                signals.SecureDesktopClear,
                signals.RemoteSession,
                signals.PresentationMode,
                signals.ApplicationAllowed,
                signals.WindowAllowed,
                storageAvailable,
                signals.CaptureIdentity,
                signals.Target),
            observation.DisplayTarget);
    }

    private static WindowsCapturePrivacyObservation CreateUnknownTargetObservation(
        NativeCapturePolicyDecision applicationAllowed =
            NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision windowAllowed =
            NativeCapturePolicyDecision.Unknown)
    {
        var signals = CreateBaseSignals();
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                signals.SessionUnlocked,
                signals.SecureDesktopClear,
                signals.RemoteSession,
                signals.PresentationMode,
                applicationAllowed,
                windowAllowed,
                signals.StorageAvailable,
                NativeCaptureIdentitySnapshot.Unknown,
                NativeCaptureTargetIdentity.Unknown),
            WindowsCaptureDisplayTarget.Unknown);
    }

    private static WindowsCapturePrivacyObservation CreateAbsentTargetObservation()
    {
        var signals = CreateBaseSignals();
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                signals.SessionUnlocked,
                signals.SecureDesktopClear,
                signals.RemoteSession,
                signals.PresentationMode,
                signals.ApplicationAllowed,
                signals.WindowAllowed,
                signals.StorageAvailable,
                NativeCaptureIdentitySnapshot.Absent,
                NativeCaptureTargetIdentity.Absent),
            WindowsCaptureDisplayTarget.Absent);
    }

    private static WindowsCapturePrivacyObservation CreateObservation(
        ulong targetEpoch,
        NativeCaptureObservation executable,
        NativeCaptureObservation packageFamily,
        NativeCaptureObservation windowTitle)
    {
        var target = CreateVerificationResult(targetEpoch);
        var identity = NativeCaptureIdentitySnapshot.FromObservations(
            executable,
            packageFamily,
            NativeCaptureObservation.Unknown,
            windowTitle);
        var signals = CreateBaseSignals();
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                signals.SessionUnlocked,
                signals.SecureDesktopClear,
                signals.RemoteSession,
                signals.PresentationMode,
                signals.ApplicationAllowed,
                signals.WindowAllowed,
                signals.StorageAvailable,
                identity,
                target.Target),
            target.DisplayTarget);
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakePrivacySignalSink(
        ConcurrentQueue<string>? order = null)
        : INativeCapturePrivacySignalSink,
          INativeCapturePrivacySignalSinkTermination,
          INativeCaptureApplicationPrivacyModeSource
    {
        private readonly object _sync = new();
        private readonly Channel<(long Generation, NativeCapturePrivacySignals Signals)>
            _updates = Channel.CreateUnbounded<(
                long Generation,
                NativeCapturePrivacySignals Signals)>();
        private readonly TaskCompletionSource _releaseBarrier =
            CreateCompletionSource();
        private readonly TaskCompletionSource _releaseUpdate =
            CreateCompletionSource();
        private readonly TaskCompletionSource _releaseInvalidation =
            CreateCompletionSource();
        private readonly HashSet<long> _publishedGenerations = [];
        private long _generation;
        private int _barrierCallCount;
        private int _invalidationCallCount;
        private int _updateCallCount;

        internal int BlockBarrierCall { get; init; }

        internal int FailBarrierCall { get; init; }

        internal Exception? BarrierFailure { get; init; }

        internal int FailInvalidationCall { get; init; }

        internal int BlockInvalidationCall { get; init; }

        internal Exception? InvalidationFailure { get; init; }

        internal int RejectUpdateCall { get; init; }

        internal int BlockUpdateCall { get; init; }

        internal int FailUpdateCall { get; init; }

        internal Exception? UpdateFailure { get; init; }

        internal bool TerminationStarted { get; init; }

        internal Task TerminationTask { get; init; } = Task.CompletedTask;

        internal TaskCompletionSource BlockedBarrierEntered { get; } =
            CreateCompletionSource();

        internal TaskCompletionSource BlockedUpdateEntered { get; } =
            CreateCompletionSource();

        internal TaskCompletionSource BlockedInvalidationAdvanced { get; } =
            CreateCompletionSource();

        internal TaskCompletionSource TerminationProofRequested { get; } =
            CreateCompletionSource();

        internal List<long> BarrierGenerations { get; } = [];

        internal List<(long Generation, NativeCapturePrivacySignals Signals)>
            Updates
        { get; } = [];

        internal List<long> UpdateAttemptGenerations { get; } = [];

        public long PrivacyObservationGeneration =>
            Volatile.Read(ref _generation);

        public CaptureApplicationPrivacyMode ApplicationPrivacyMode
        {
            get;
            private set;
        } = CaptureApplicationPrivacyMode.ProtectByForegroundApplication;

        public CaptureState CurrentCaptureState
        {
            get;
            private set;
        } = CaptureState.Recording;

        public event EventHandler? ApplicationPrivacyModeChanged;

        internal void SetApplicationPrivacyMode(
            CaptureApplicationPrivacyMode mode)
        {
            ApplicationPrivacyMode = mode;
            ApplicationPrivacyModeChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void SetCaptureState(CaptureState state)
        {
            CurrentCaptureState = state;
        }

        bool INativeCapturePrivacySignalSinkTermination.IsTerminationStarted
        {
            get
            {
                TerminationProofRequested.TrySetResult();
                return TerminationStarted;
            }
        }

        Task INativeCapturePrivacySignalSinkTermination.Termination =>
            TerminationStarted
                ? TerminationTask
                : throw new InvalidOperationException(
                    "The fake sink termination has not started.");

        public long InvalidatePrivacyObservation()
        {
            order?.Enqueue("sink");
            var call = Interlocked.Increment(ref _invalidationCallCount);
            if (call == FailInvalidationCall)
            {
                throw InvalidationFailure ?? new InvalidOperationException();
            }

            var generation = Interlocked.Increment(ref _generation);
            if (call == BlockInvalidationCall)
            {
                BlockedInvalidationAdvanced.TrySetResult();
                _releaseInvalidation.Task.GetAwaiter().GetResult();
            }

            return generation;
        }

        public async Task ApplyPrivacyInvalidationAsync(
            long privacyObservationGeneration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _barrierCallCount);
            lock (_sync)
            {
                BarrierGenerations.Add(privacyObservationGeneration);
            }

            if (call == BlockBarrierCall)
            {
                BlockedBarrierEntered.TrySetResult();
                await _releaseBarrier.Task.WaitAsync(cancellationToken);
            }

            if (call == FailBarrierCall)
            {
                throw BarrierFailure ?? new InvalidOperationException();
            }
        }

        public async Task<bool> TryUpdateSignalsAsync(
            long privacyObservationGeneration,
            NativeCapturePrivacySignals signals,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _updateCallCount);
            lock (_sync)
            {
                UpdateAttemptGenerations.Add(privacyObservationGeneration);
            }

            if (call == BlockUpdateCall)
            {
                BlockedUpdateEntered.TrySetResult();
                await _releaseUpdate.Task.WaitAsync(cancellationToken);
            }

            if (call == FailUpdateCall)
            {
                throw UpdateFailure ?? new InvalidOperationException();
            }

            if (call == RejectUpdateCall)
            {
                return false;
            }

            if (privacyObservationGeneration != PrivacyObservationGeneration)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_publishedGenerations.Add(privacyObservationGeneration))
                {
                    return false;
                }

                Updates.Add((privacyObservationGeneration, signals));
            }

            _updates.Writer.TryWrite((privacyObservationGeneration, signals));
            return true;
        }

        public Task UpdateSignalsAsync(
            NativeCapturePrivacySignals signals,
            CancellationToken cancellationToken = default)
        {
            return TryUpdateSignalsAsync(
                PrivacyObservationGeneration,
                signals,
                cancellationToken);
        }

        internal Task<(long Generation, NativeCapturePrivacySignals Signals)>
            ReadUpdateAsync()
        {
            return _updates.Reader.ReadAsync().AsTask();
        }

        internal void ReleaseBlockedBarrier()
        {
            _releaseBarrier.TrySetResult();
        }

        internal void ReleaseBlockedUpdate()
        {
            _releaseUpdate.TrySetResult();
        }

        internal void ReleaseBlockedInvalidation()
        {
            _releaseInvalidation.TrySetResult();
        }
    }

    private sealed class ControlledRetryWait
    {
        private readonly Channel<Request> _requests =
            Channel.CreateUnbounded<Request>();
        private int _waitCount;

        internal int WaitCount => Volatile.Read(ref _waitCount);

        internal Task WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var release = CreateCompletionSource();
            Interlocked.Increment(ref _waitCount);
            _requests.Writer.TryWrite(new Request(delay, release));
            return release.Task.WaitAsync(cancellationToken);
        }

        internal Task<Request> ReadAsync()
        {
            return _requests.Reader.ReadAsync().AsTask();
        }

        internal sealed class Request(
            TimeSpan delay,
            TaskCompletionSource release)
        {
            internal TimeSpan Delay { get; } = delay;

            internal void Release()
            {
                release.TrySetResult();
            }
        }
    }

    private sealed class ControlledStorageRefreshWait
    {
        private readonly Channel<Request> _requests =
            Channel.CreateUnbounded<Request>();

        internal Task WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var release = CreateCompletionSource();
            _requests.Writer.TryWrite(new Request(delay, release));
            return release.Task.WaitAsync(cancellationToken);
        }

        internal Task<Request> ReadAsync()
        {
            return _requests.Reader.ReadAsync().AsTask();
        }

        internal sealed class Request(
            TimeSpan delay,
            TaskCompletionSource release)
        {
            internal TimeSpan Delay { get; } = delay;

            internal void Release()
            {
                release.TrySetResult();
            }
        }
    }

    private sealed class FakeStoragePrivacySampler
        : IWindowsCapturePrivacySampler,
          IWindowsCaptureStorageSampler,
          IWindowsCaptureSessionSampler
    {
        private readonly Func<
            int,
            CancellationToken,
            Task<WindowsCapturePrivacyObservation>> _sample;
        private readonly Func<
            int,
            CancellationToken,
            Task<NativeCapturePolicyDecision>> _sampleStorage;
        private readonly Func<
            int,
            CancellationToken,
            Task<NativeCapturePolicyDecision>> _sampleSession;
        private int _invalidationCount;
        private int _sampleCount;
        private int _storageSampleCount;
        private int _sessionSampleCount;

        internal FakeStoragePrivacySampler(
            Func<
                int,
                CancellationToken,
                Task<WindowsCapturePrivacyObservation>> sample,
            Func<
                int,
                CancellationToken,
                Task<NativeCapturePolicyDecision>> sampleStorage,
            Func<
                int,
                CancellationToken,
                Task<NativeCapturePolicyDecision>>? sampleSession = null)
        {
            _sample = sample ?? throw new ArgumentNullException(nameof(sample));
            _sampleStorage = sampleStorage
                ?? throw new ArgumentNullException(nameof(sampleStorage));
            _sampleSession = sampleSession
                ?? ((_, _) => Task.FromResult(
                    NativeCapturePolicyDecision.Unknown));
        }

        internal int InvalidationCount => Volatile.Read(ref _invalidationCount);

        internal int SampleCount => Volatile.Read(ref _sampleCount);

        internal int StorageSampleCount =>
            Volatile.Read(ref _storageSampleCount);

        internal int SessionSampleCount =>
            Volatile.Read(ref _sessionSampleCount);

        public void InvalidateTargetObservation()
        {
            Interlocked.Increment(ref _invalidationCount);
        }

        public ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _sampleCount);
            return new ValueTask<WindowsCapturePrivacyObservation>(
                _sample(call, cancellationToken));
        }

        public ValueTask<NativeCapturePolicyDecision> SampleStorageAsync(
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _storageSampleCount);
            return new ValueTask<NativeCapturePolicyDecision>(
                _sampleStorage(call, cancellationToken));
        }

        public ValueTask<NativeCapturePolicyDecision> SampleSessionAsync(
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _sessionSampleCount);
            return new ValueTask<NativeCapturePolicyDecision>(
                _sampleSession(call, cancellationToken));
        }
    }

    private sealed class FakePrivacySampler : IWindowsCapturePrivacySampler
    {
        private readonly Func<
            int,
            CancellationToken,
            Task<WindowsCapturePrivacyObservation>> _sample;
        private readonly ConcurrentQueue<string>? _order;
        private int _invalidationCount;
        private int _sampleCount;

        internal FakePrivacySampler(
            Func<
                int,
                CancellationToken,
                Task<WindowsCapturePrivacyObservation>> sample,
            ConcurrentQueue<string>? order = null)
        {
            _sample = sample;
            _order = order;
        }

        internal int InvalidationCount => Volatile.Read(ref _invalidationCount);

        internal int SampleCount => Volatile.Read(ref _sampleCount);

        public void InvalidateTargetObservation()
        {
            Interlocked.Increment(ref _invalidationCount);
            _order?.Enqueue("target");
        }

        public ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _sampleCount);
            return new ValueTask<WindowsCapturePrivacyObservation>(
                _sample(call, cancellationToken));
        }
    }

    private sealed class FakeEventSource : IWindowsCaptureEventSource
    {
        private Action<WindowsCaptureWinEventNotification>? _changeCallback;
        private Action<WindowsCaptureWinEventSourceFault>? _faultCallback;
        private Action<WindowsCaptureWinEventNotification>? _retainedChangeCallback;
        private int _disposeCount;
        private int _startCount;

        internal Exception? StartFailure { get; init; }

        internal Action? DisposeAction { get; init; }

        internal WindowsCaptureWinEventSourceFault? FaultOnDispose { get; init; }

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int StartCount => Volatile.Read(ref _startCount);

        internal TaskCompletionSource Disposed { get; } =
            CreateCompletionSource();

        public void Start(
            Action<WindowsCaptureWinEventNotification> changeCallback,
            Action<WindowsCaptureWinEventSourceFault> faultCallback)
        {
            _changeCallback = changeCallback;
            _retainedChangeCallback = changeCallback;
            _faultCallback = faultCallback;
            Interlocked.Increment(ref _startCount);
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeCount, 1) != 0)
            {
                return;
            }

            DisposeAction?.Invoke();
            if (FaultOnDispose is { } fault)
            {
                Volatile.Read(ref _faultCallback)?.Invoke(fault);
            }

            Volatile.Write(ref _changeCallback, null);
            Volatile.Write(ref _faultCallback, null);
            Disposed.TrySetResult();
        }

        internal void EmitChange(
            WindowsCaptureWinEventChange change,
            ulong windowHandle = 0)
        {
            Volatile.Read(ref _changeCallback)?.Invoke(
                new WindowsCaptureWinEventNotification(change, windowHandle));
        }

        internal void EmitFault(WindowsCaptureWinEventSourceFault fault)
        {
            Volatile.Read(ref _faultCallback)?.Invoke(fault);
        }

        internal void EmitLateChange(
            WindowsCaptureWinEventChange change,
            ulong windowHandle = 0)
        {
            _retainedChangeCallback?.Invoke(
                new WindowsCaptureWinEventNotification(change, windowHandle));
        }
    }
}

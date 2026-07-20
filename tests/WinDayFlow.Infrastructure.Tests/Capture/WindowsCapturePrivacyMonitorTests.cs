using System.Collections.Concurrent;
using System.Threading.Channels;
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
        string displayKey = @"\\.\DISPLAY1")
    {
        var identity = new NativeCaptureIdentitySnapshot(
            executableName,
            packageFamilyName: null,
            publisherCertificateSha256: null,
            windowTitle);
        return new WindowsCaptureTargetVerificationResult(
            NativeCaptureTargetIdentity.Present(
                windowHandle: targetEpoch + 100,
                processId: checked((uint)targetEpoch + 10),
                processCreationTime100ns: targetEpoch + 1_000,
                targetEpoch,
                displayMonitorHandle: targetEpoch + 200,
                displayDeviceKey: displayKey),
            WindowsCaptureDisplayTarget.Present(
                monitorHandle: targetEpoch + 200,
                displayKey),
            identity);
    }

    private static WindowsCapturePrivacyObservation CreateObservation(
        ulong targetEpoch,
        string executableName = "sample.exe",
        string windowTitle = "Sample title",
        string displayKey = @"\\.\DISPLAY1")
    {
        var target = CreateVerificationResult(
            targetEpoch,
            executableName,
            windowTitle,
            displayKey);
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

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakePrivacySignalSink(
        ConcurrentQueue<string>? order = null)
        : INativeCapturePrivacySignalSink
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
        private long _generation;
        private int _barrierCallCount;
        private int _invalidationCallCount;
        private int _updateCallCount;

        internal int BlockBarrierCall { get; init; }

        internal int FailBarrierCall { get; init; }

        internal Exception? BarrierFailure { get; init; }

        internal int FailInvalidationCall { get; init; }

        internal Exception? InvalidationFailure { get; init; }

        internal int RejectUpdateCall { get; init; }

        internal int BlockUpdateCall { get; init; }

        internal TaskCompletionSource BlockedBarrierEntered { get; } =
            CreateCompletionSource();

        internal TaskCompletionSource BlockedUpdateEntered { get; } =
            CreateCompletionSource();

        internal List<long> BarrierGenerations { get; } = [];

        internal List<(long Generation, NativeCapturePrivacySignals Signals)>
            Updates
        { get; } = [];

        internal List<long> UpdateAttemptGenerations { get; } = [];

        public long PrivacyObservationGeneration =>
            Volatile.Read(ref _generation);

        public long InvalidatePrivacyObservation()
        {
            order?.Enqueue("sink");
            var call = Interlocked.Increment(ref _invalidationCallCount);
            if (call == FailInvalidationCall)
            {
                throw InvalidationFailure ?? new InvalidOperationException();
            }

            return Interlocked.Increment(ref _generation);
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
        private Action<WindowsCaptureWinEventChange>? _changeCallback;
        private Action<WindowsCaptureWinEventSourceFault>? _faultCallback;
        private Action<WindowsCaptureWinEventChange>? _retainedChangeCallback;
        private int _disposeCount;
        private int _startCount;

        internal Exception? StartFailure { get; init; }

        internal Action? DisposeAction { get; init; }

        internal WindowsCaptureWinEventSourceFault? FaultOnDispose { get; init; }

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int StartCount => Volatile.Read(ref _startCount);

        public void Start(
            Action<WindowsCaptureWinEventChange> changeCallback,
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
        }

        internal void EmitChange(WindowsCaptureWinEventChange change)
        {
            Volatile.Read(ref _changeCallback)?.Invoke(change);
        }

        internal void EmitFault(WindowsCaptureWinEventSourceFault fault)
        {
            Volatile.Read(ref _faultCallback)?.Invoke(fault);
        }

        internal void EmitLateChange(WindowsCaptureWinEventChange change)
        {
            _retainedChangeCallback?.Invoke(change);
        }
    }
}

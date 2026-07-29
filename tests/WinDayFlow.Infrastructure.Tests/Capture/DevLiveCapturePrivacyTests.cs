#if WDF_DEV_LIVE_CAPTURE
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class DevLiveCapturePrivacyTests
{
    [Theory]
    [InlineData("CMD.EXE")]
    [InlineData("notepad.exe")]
    [InlineData("devenv.exe")]
    [InlineData("sample.exe")]
    public async Task ResolvedClassicForegroundTargetIsAdmittedForQa(
        string executableName)
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Present(executableName));
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.NotSame(WindowsCapturePrivacyObservation.FailClosed, result);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Present,
            result.Signals.Target.State);
        Assert.Same(source.Signals.CaptureIdentity, result.Signals.CaptureIdentity);
        Assert.Equal(source.Signals.SessionUnlocked, result.Signals.SessionUnlocked);
        Assert.Equal(source.Signals.StorageAvailable, result.Signals.StorageAvailable);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.Signals.CaptureIdentity
                .PublisherCertificateSha256Observation.State);
    }

    [Theory]
    [InlineData("WinDayFlow.App.exe")]
    [InlineData("windayflow.app.EXE")]
    public async Task WinDayFlowProcessIsAdmittedForUserRuleEvaluation(
        string executableName)
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Present(executableName));
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.NotSame(WindowsCapturePrivacyObservation.FailClosed, result);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.CaptureIdentity, result.Signals.CaptureIdentity);
        Assert.Same(source.Signals.Target, result.Signals.Target);
        Assert.Same(source.DisplayTarget, result.DisplayTarget);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(result));
    }

    [Theory]
    [MemberData(nameof(UnresolvedWinDayFlowIdentityObservations))]
    public async Task WinDayFlowProcessIsAdmittedWhenOptionalIdentityIsUnresolved(
        NativeCaptureObservation packageFamily,
        NativeCaptureObservation windowTitle)
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Present("WinDayFlow.App.exe"),
            packageFamily: packageFamily,
            windowTitle: windowTitle);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.Target, result.Signals.Target);
        Assert.Same(source.DisplayTarget, result.DisplayTarget);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(result));
    }

    [Fact]
    public async Task ResolvedPackagedForegroundTargetIsAdmittedForQa()
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Present("Contoso.App.exe"),
            packageFamily: NativeCaptureObservation.Present(
                "Contoso.App_123456789abcd"));
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.NotSame(WindowsCapturePrivacyObservation.FailClosed, result);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.CaptureIdentity, result.Signals.CaptureIdentity);
    }

    [Theory]
    [MemberData(nameof(RejectedExecutableObservations))]
    public async Task UnknownOrAbsentExecutableFailsClosed(
        NativeCaptureObservation executable)
    {
        var sampler = CreateSampler(CreateObservation(executable: executable));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
    }

    [Theory]
    [MemberData(nameof(UnresolvedPackageFamilyObservations))]
    public async Task UnresolvedPackageFamilyDoesNotBlockAStableExternalTarget(
        NativeCaptureObservation packageFamily)
    {
        var source = CreateObservation(packageFamily: packageFamily);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.CaptureIdentity, result.Signals.CaptureIdentity);
        Assert.Same(source.Signals.Target, result.Signals.Target);
        Assert.Same(source.DisplayTarget, result.DisplayTarget);
    }

    [Theory]
    [MemberData(nameof(UnresolvedWindowTitleObservations))]
    public async Task UnresolvedWindowTitleDoesNotBlockAStableExternalTarget(
        NativeCaptureObservation windowTitle)
    {
        var source = CreateObservation(windowTitle: windowTitle);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.CaptureIdentity, result.Signals.CaptureIdentity);
        Assert.Same(source.Signals.Target, result.Signals.Target);
        Assert.Same(source.DisplayTarget, result.DisplayTarget);
    }

    [Theory]
    [InlineData("LockApp.exe")]
    [InlineData("lockapp.EXE")]
    public async Task LockScreenProcessIsExplicitlyBlockedWithoutLosingTarget(
        string executableName)
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Present(executableName),
            packageFamily: NativeCaptureObservation.Unknown,
            windowTitle: NativeCaptureObservation.Unknown);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            result.Signals.WindowAllowed);
        Assert.Same(source.Signals.Target, result.Signals.Target);
        Assert.Same(source.DisplayTarget, result.DisplayTarget);
    }

    [Theory]
    [InlineData(NativeCaptureTargetIdentityState.Unknown)]
    [InlineData(NativeCaptureTargetIdentityState.Absent)]
    public async Task UnknownTargetFailsClosedAndAbsentTargetIsPreserved(
        NativeCaptureTargetIdentityState state)
    {
        var sampler = CreateSampler(CreateObservation(targetState: state));

        var result = await sampler.SampleAsync(CancellationToken.None);

        if (state == NativeCaptureTargetIdentityState.Absent)
        {
            Assert.Equal(
                NativeCaptureTargetIdentityState.Absent,
                result.Signals.Target.State);
            Assert.Equal(
                WindowsCaptureDisplayTargetState.Absent,
                result.DisplayTarget.State);
        }
        else
        {
            AssertFailClosed(result);
        }
    }

    [Theory]
    [InlineData(NativeCapturePolicyDecision.Block, NativeCapturePolicyDecision.Unknown)]
    [InlineData(NativeCapturePolicyDecision.Unknown, NativeCapturePolicyDecision.Block)]
    public async Task ExistingApplicationOrWindowBlockIsPreservedWithTarget(
        NativeCapturePolicyDecision applicationAllowed,
        NativeCapturePolicyDecision windowAllowed)
    {
        var source = CreateObservation(
            applicationAllowed: applicationAllowed,
            windowAllowed: windowAllowed);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Same(source, result);
        Assert.Equal(applicationAllowed, result.Signals.ApplicationAllowed);
        Assert.Equal(windowAllowed, result.Signals.WindowAllowed);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Present,
            result.Signals.Target.State);
    }

    [Fact]
    public async Task ExistingApplicationBlockIsPreservedForUnknownTarget()
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Unknown,
            targetState: NativeCaptureTargetIdentityState.Unknown,
            applicationAllowed: NativeCapturePolicyDecision.Block);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Same(source, result);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Unknown,
            result.Signals.Target.State);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExistingIndependentBlockIsPreservedForUnknownTarget(
        int blockedSignal)
    {
        var source = CreateObservation(
            executable: NativeCaptureObservation.Unknown,
            targetState: NativeCaptureTargetIdentityState.Unknown,
            sessionUnlocked: blockedSignal == 0
                ? NativeCapturePolicyDecision.Block
                : NativeCapturePolicyDecision.Allow,
            secureDesktopClear: blockedSignal == 1
                ? NativeCapturePolicyDecision.Block
                : NativeCapturePolicyDecision.Allow,
            storageAvailable: blockedSignal == 2
                ? NativeCapturePolicyDecision.Block
                : NativeCapturePolicyDecision.Allow);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.Same(source, result);
        Assert.False(
            WindowsCapturePrivacyMonitor
                .IsRecoverableTransientTargetObservation(result));
    }

    [Fact]
    public async Task QaAdmissionDoesNotPromoteIndependentPrivacySignals()
    {
        var source = CreateObservation(
            sessionUnlocked: NativeCapturePolicyDecision.Block,
            secureDesktopClear: NativeCapturePolicyDecision.Unknown,
            remoteSession: NativeCaptureConditionState.Active,
            presentationMode: NativeCaptureConditionState.Unknown,
            storageAvailable: NativeCapturePolicyDecision.Block);
        var sampler = CreateSampler(source);

        var result = await sampler.SampleAsync(CancellationToken.None);

        Assert.NotSame(WindowsCapturePrivacyObservation.FailClosed, result);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            result.Signals.SessionUnlocked);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            result.Signals.SecureDesktopClear);
        Assert.Equal(
            NativeCaptureConditionState.Active,
            result.Signals.RemoteSession);
        Assert.Equal(
            NativeCaptureConditionState.Unknown,
            result.Signals.PresentationMode);
        Assert.Equal(
            NativeCapturePolicyDecision.Block,
            result.Signals.StorageAvailable);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.ApplicationAllowed);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            result.Signals.WindowAllowed);
    }

    [Fact]
    public async Task InvalidationIsForwardedToTheInnerSampler()
    {
        var inner = new StubPrivacySampler(CreateObservation());
        var sampler = new DevLiveQaWindowsCapturePrivacySampler(inner);

        sampler.InvalidateTargetObservation();
        _ = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(1, inner.InvalidationCount);
        Assert.Equal(1, inner.SampleCount);
    }

    public static TheoryData<NativeCaptureObservation>
        RejectedExecutableObservations => new()
        {
            NativeCaptureObservation.Unknown,
            NativeCaptureObservation.Absent,
        };

    public static TheoryData<NativeCaptureObservation>
        UnresolvedPackageFamilyObservations => new()
        {
            NativeCaptureObservation.Unknown,
        };

    public static TheoryData<NativeCaptureObservation>
        UnresolvedWindowTitleObservations => new()
        {
            NativeCaptureObservation.Unknown,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Present(string.Empty),
            NativeCaptureObservation.Present("   "),
        };

    public static TheoryData<
        NativeCaptureObservation,
        NativeCaptureObservation> UnresolvedWinDayFlowIdentityObservations => new()
        {
            {
                NativeCaptureObservation.Unknown,
                NativeCaptureObservation.Present("WinDayFlow")
            },
            {
                NativeCaptureObservation.Absent,
                NativeCaptureObservation.Unknown
            },
        };

    private static DevLiveQaWindowsCapturePrivacySampler CreateSampler(
        WindowsCapturePrivacyObservation observation)
    {
        return new DevLiveQaWindowsCapturePrivacySampler(
            new StubPrivacySampler(observation));
    }

    private static WindowsCapturePrivacyObservation CreateObservation(
        NativeCaptureObservation? executable = null,
        NativeCaptureObservation? packageFamily = null,
        NativeCaptureObservation? windowTitle = null,
        NativeCaptureTargetIdentityState targetState =
            NativeCaptureTargetIdentityState.Present,
        NativeCapturePolicyDecision applicationAllowed =
            NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision windowAllowed =
            NativeCapturePolicyDecision.Unknown,
        NativeCapturePolicyDecision sessionUnlocked =
            NativeCapturePolicyDecision.Allow,
        NativeCapturePolicyDecision secureDesktopClear =
            NativeCapturePolicyDecision.Allow,
        NativeCaptureConditionState remoteSession =
            NativeCaptureConditionState.Inactive,
        NativeCaptureConditionState presentationMode =
            NativeCaptureConditionState.Inactive,
        NativeCapturePolicyDecision storageAvailable =
            NativeCapturePolicyDecision.Allow)
    {
        var identity = NativeCaptureIdentitySnapshot.FromObservations(
            executable ?? NativeCaptureObservation.Present("cmd.exe"),
            packageFamily ?? NativeCaptureObservation.Absent,
            NativeCaptureObservation.Unknown,
            windowTitle ?? NativeCaptureObservation.Present("Command Prompt"));
        var target = targetState switch
        {
            NativeCaptureTargetIdentityState.Unknown =>
                NativeCaptureTargetIdentity.Unknown,
            NativeCaptureTargetIdentityState.Absent =>
                NativeCaptureTargetIdentity.Absent,
            NativeCaptureTargetIdentityState.Present =>
                NativeCaptureTargetIdentity.Present(
                    windowHandle: 101,
                    processId: 102,
                    processCreationTime100ns: 103,
                    targetEpoch: 104,
                    displayMonitorHandle: 105,
                    displayDeviceKey: @"\\.\DISPLAY1"),
            _ => throw new ArgumentOutOfRangeException(nameof(targetState)),
        };
        var display = targetState switch
        {
            NativeCaptureTargetIdentityState.Unknown =>
                WindowsCaptureDisplayTarget.Unknown,
            NativeCaptureTargetIdentityState.Absent =>
                WindowsCaptureDisplayTarget.Absent,
            NativeCaptureTargetIdentityState.Present =>
                WindowsCaptureDisplayTarget.Present(
                    monitorHandle: 105,
                    deviceKey: @"\\.\DISPLAY1"),
            _ => throw new ArgumentOutOfRangeException(nameof(targetState)),
        };
        return new WindowsCapturePrivacyObservation(
            new NativeCapturePrivacySignals(
                sessionUnlocked,
                secureDesktopClear,
                remoteSession,
                presentationMode,
                applicationAllowed,
                windowAllowed,
                storageAvailable,
                identity,
                target),
            display);
    }

    private static void AssertFailClosed(
        WindowsCapturePrivacyObservation observation)
    {
        Assert.Same(WindowsCapturePrivacyObservation.FailClosed, observation);
        Assert.Same(NativeCapturePrivacySignals.FailClosed, observation.Signals);
        Assert.Equal(
            NativeCaptureTargetIdentityState.Unknown,
            observation.Signals.Target.State);
        Assert.Same(WindowsCaptureDisplayTarget.Unknown, observation.DisplayTarget);
    }

    private sealed class StubPrivacySampler : IWindowsCapturePrivacySampler
    {
        private readonly WindowsCapturePrivacyObservation _observation;
        private int _invalidationCount;
        private int _sampleCount;

        internal StubPrivacySampler(WindowsCapturePrivacyObservation observation)
        {
            _observation = observation;
        }

        internal int InvalidationCount => Volatile.Read(ref _invalidationCount);

        internal int SampleCount => Volatile.Read(ref _sampleCount);

        public void InvalidateTargetObservation()
        {
            Interlocked.Increment(ref _invalidationCount);
        }

        public ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _sampleCount);
            return ValueTask.FromResult(_observation);
        }
    }
}
#endif

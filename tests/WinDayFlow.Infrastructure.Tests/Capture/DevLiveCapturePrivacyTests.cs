#if WDF_DEV_LIVE_CAPTURE
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class DevLiveCapturePrivacyTests
{
    [Theory]
    [InlineData("windayflow.app.EXE")]
    [InlineData("CMD.EXE")]
    public async Task ExactClassicExecutableMatchPromotesOnlyApplicationAndWindow(
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
    [InlineData("cmd.exe.evil")]
    [InlineData("evil-cmd.exe")]
    [InlineData("WinDayFlow.App.exe.bak")]
    [InlineData("sample.exe")]
    public async Task ExecutableSuffixAndNonAllowlistedNamesFailClosed(
        string executableName)
    {
        var sampler = CreateSampler(CreateObservation(
            executable: NativeCaptureObservation.Present(executableName)));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
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
    [MemberData(nameof(RejectedPackageFamilyObservations))]
    public async Task UnknownOrPresentPackageFamilyFailsClosed(
        NativeCaptureObservation packageFamily)
    {
        var sampler = CreateSampler(CreateObservation(packageFamily: packageFamily));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
    }

    [Theory]
    [MemberData(nameof(RejectedWindowTitleObservations))]
    public async Task UnknownAbsentOrEmptyWindowTitleFailsClosed(
        NativeCaptureObservation windowTitle)
    {
        var sampler = CreateSampler(CreateObservation(windowTitle: windowTitle));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
    }

    [Theory]
    [InlineData(NativeCaptureTargetIdentityState.Unknown)]
    [InlineData(NativeCaptureTargetIdentityState.Absent)]
    public async Task UnknownOrAbsentTargetAndDisplayFailClosed(
        NativeCaptureTargetIdentityState state)
    {
        var sampler = CreateSampler(CreateObservation(targetState: state));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
    }

    [Theory]
    [InlineData(NativeCapturePolicyDecision.Block, NativeCapturePolicyDecision.Unknown)]
    [InlineData(NativeCapturePolicyDecision.Unknown, NativeCapturePolicyDecision.Block)]
    public async Task ExistingApplicationOrWindowBlockCannotBePromoted(
        NativeCapturePolicyDecision applicationAllowed,
        NativeCapturePolicyDecision windowAllowed)
    {
        var sampler = CreateSampler(CreateObservation(
            applicationAllowed: applicationAllowed,
            windowAllowed: windowAllowed));

        var result = await sampler.SampleAsync(CancellationToken.None);

        AssertFailClosed(result);
    }

    [Fact]
    public async Task InvalidationIsForwardedToTheInnerSampler()
    {
        var inner = new StubPrivacySampler(CreateObservation());
        var sampler = new DevAllowlistedWindowsCapturePrivacySampler(inner);

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
        RejectedPackageFamilyObservations => new()
        {
            NativeCaptureObservation.Unknown,
            NativeCaptureObservation.Present("packaged.app_family"),
        };

    public static TheoryData<NativeCaptureObservation>
        RejectedWindowTitleObservations => new()
        {
            NativeCaptureObservation.Unknown,
            NativeCaptureObservation.Absent,
            NativeCaptureObservation.Present(string.Empty),
            NativeCaptureObservation.Present("   "),
        };

    private static DevAllowlistedWindowsCapturePrivacySampler CreateSampler(
        WindowsCapturePrivacyObservation observation)
    {
        return new DevAllowlistedWindowsCapturePrivacySampler(
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
            NativeCapturePolicyDecision.Unknown)
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
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCaptureConditionState.Inactive,
                NativeCaptureConditionState.Inactive,
                applicationAllowed,
                windowAllowed,
                NativeCapturePolicyDecision.Allow,
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

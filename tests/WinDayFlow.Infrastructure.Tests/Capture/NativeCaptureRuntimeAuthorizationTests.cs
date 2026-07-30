using WinDayFlow.Application.Capture;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureRuntimeAuthorizationTests
{
    private const ulong DisplayMonitorHandle = 0x5678;
    private const string DisplayDeviceKey = @"\\.\DISPLAY1";

    [Fact]
    public void PresentTargetRequiresEveryStableIdentityComponent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(
                0, 1, 1, 1, DisplayMonitorHandle, DisplayDeviceKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 0, 1, 1, DisplayMonitorHandle, DisplayDeviceKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 1, 0, 1, DisplayMonitorHandle, DisplayDeviceKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 1, 1, 0, DisplayMonitorHandle, DisplayDeviceKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 1, 1, 1, 0, DisplayDeviceKey));
        Assert.Throws<ArgumentException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 1, 1, 1, DisplayMonitorHandle, string.Empty));
        Assert.Throws<ArgumentException>(() =>
            NativeCaptureTargetIdentity.Present(
                1, 1, 1, 1, DisplayMonitorHandle, "\ud800"));
    }

    [Fact]
    public void AuthorizationNormalizesTheTargetPresenceContract()
    {
        var present = NativeCaptureTargetIdentity.Present(
            0x1234,
            42,
            100,
            1,
            DisplayMonitorHandle,
            DisplayDeviceKey);

        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureRuntimeAuthorization(
                CreateAllowedContext(),
                NativeCaptureTargetIdentity.Unknown));
        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureRuntimeAuthorization(
                NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
                present));

        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureRuntimeAuthorization(CreateAllowedContext(), present));
        _ = new NativeCaptureRuntimeAuthorization(
            CreateAllowedContext(),
            NativeCaptureTargetIdentity.DisplayWide(
                1,
                DisplayMonitorHandle,
                DisplayDeviceKey));
        _ = new NativeCaptureRuntimeAuthorization(
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
            NativeCaptureTargetIdentity.Absent);
    }

    [Fact]
    public void DisplayDeviceKeyIdentityIsCaseInsensitiveButMonitorBound()
    {
        var first = NativeCaptureTargetIdentity.Present(
            0x1234,
            42,
            100,
            1,
            DisplayMonitorHandle,
            @"\\.\display1");
        var same = NativeCaptureTargetIdentity.Present(
            0x1234,
            42,
            100,
            1,
            DisplayMonitorHandle,
            DisplayDeviceKey);
        var otherMonitor = NativeCaptureTargetIdentity.Present(
            0x1234,
            42,
            100,
            1,
            DisplayMonitorHandle + 1,
            DisplayDeviceKey);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, otherMonitor);
    }

    [Fact]
    public void TargetAndAuthorizationTextNeverExposeIdentityValues()
    {
        var target = NativeCaptureTargetIdentity.DisplayWide(
            7,
            DisplayMonitorHandle,
            DisplayDeviceKey);
        var authorization = new NativeCaptureRuntimeAuthorization(
            CreateAllowedContext(),
            target);

        var text = target + " " + authorization;
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("22136", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY1", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChunkGenerationAndTargetEpochMustBeProvidedTogether()
    {
        Assert.Throws<ArgumentException>(() => new NativeCaptureChunkCommitted(
            sequence: 1,
            DateTimeOffset.UnixEpoch,
            "chunk.mp4",
            CaptureState.Recording,
            droppedBefore: 0,
            persistenceGeneration: 1,
            targetEpoch: 0));
    }

    private static NativeCapturePrivacyContext CreateAllowedContext()
    {
        return new NativeCapturePrivacyContext(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            RuntimePolicyRevision: 1);
    }
}

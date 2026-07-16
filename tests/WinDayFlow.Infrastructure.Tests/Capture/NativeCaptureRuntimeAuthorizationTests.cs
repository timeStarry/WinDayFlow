using WinDayFlow.Application.Capture;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureRuntimeAuthorizationTests
{
    [Fact]
    public void PresentTargetRequiresEveryStableIdentityComponent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeCaptureTargetIdentity.Present(1, 1, 1, 0));
    }

    [Fact]
    public void AuthorizationNormalizesTheTargetPresenceContract()
    {
        var present = NativeCaptureTargetIdentity.Present(0x1234, 42, 100, 1);

        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureRuntimeAuthorization(
                CreateAllowedContext(),
                NativeCaptureTargetIdentity.Unknown));
        Assert.Throws<ArgumentException>(() =>
            new NativeCaptureRuntimeAuthorization(
                NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
                present));

        _ = new NativeCaptureRuntimeAuthorization(CreateAllowedContext(), present);
        _ = new NativeCaptureRuntimeAuthorization(
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
            NativeCaptureTargetIdentity.Absent);
    }

    [Fact]
    public void TargetAndAuthorizationTextNeverExposeIdentityValues()
    {
        var target = NativeCaptureTargetIdentity.Present(0x1234, 42, 100, 7);
        var authorization = new NativeCaptureRuntimeAuthorization(
            CreateAllowedContext(),
            target);

        var text = target + " " + authorization;
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4660", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", text, StringComparison.Ordinal);
        Assert.DoesNotContain("100", text, StringComparison.Ordinal);
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

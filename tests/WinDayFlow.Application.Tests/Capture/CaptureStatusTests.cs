using WinDayFlow.Application.Capture;
using Xunit;

namespace WinDayFlow.Application.Tests.Capture;

public sealed class CaptureStatusTests
{
    private static readonly DateTimeOffset StatusTime =
        new(2026, 7, 16, 8, 30, 0, TimeSpan.Zero);
    private static readonly int[] ExpectedErrorValues =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 255];

    [Fact]
    public void ThreeArgumentConstructionRemainsCompatible()
    {
        var status = new CaptureStatus(
            State: CaptureState.Recording,
            ChangedAt: StatusTime,
            Detail: "Capturing display 1");

        Assert.Equal(CaptureState.Recording, status.State);
        Assert.Equal(StatusTime, status.ChangedAt);
        Assert.Equal("Capturing display 1", status.Detail);
        Assert.Equal(0UL, status.Sequence);
        Assert.Equal(CaptureReasonCode.None, status.Reason);
        Assert.Equal(CaptureErrorCode.None, status.ErrorCode);
    }

    [Fact]
    public void StableFieldsAreRetainedWhenDetailIsReplaced()
    {
        var original = new CaptureStatus(
            CaptureState.Faulted,
            StatusTime,
            "Native capture failed",
            42,
            CaptureReasonCode.BackendFault,
            CaptureErrorCode.NativeFailure);

        var updated = original with { Detail = "Localized diagnostic" };

        Assert.Equal(42UL, updated.Sequence);
        Assert.Equal(CaptureReasonCode.BackendFault, updated.Reason);
        Assert.Equal(CaptureErrorCode.NativeFailure, updated.ErrorCode);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void SequenceSupportsCompatibilitySnapshotsAndOrderedEvents(
        ulong sequence)
    {
        var status = new CaptureStatus(
            CaptureState.Paused,
            StatusTime,
            Sequence: sequence,
            Reason: CaptureReasonCode.SessionLocked);

        Assert.Equal(sequence, status.Sequence);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(0UL, 1UL)]
    [InlineData(1UL, 2UL)]
    [InlineData(1UL, ulong.MaxValue)]
    public void StatusChangeAcceptsCompatibleAndIncreasingSequences(
        ulong previousSequence,
        ulong currentSequence)
    {
        var previous = CreateStatus(previousSequence);
        var current = CreateStatus(currentSequence);

        var eventArgs = new CaptureStatusChangedEventArgs(previous, current);

        Assert.Same(previous, eventArgs.Previous);
        Assert.Same(current, eventArgs.Current);
    }

    [Theory]
    [InlineData(1UL, 0UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(2UL, 1UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue)]
    public void StatusChangeRejectsSequenceResetOrRegression(
        ulong previousSequence,
        ulong currentSequence)
    {
        var previous = CreateStatus(previousSequence);
        var current = CreateStatus(currentSequence);

        Assert.Throws<ArgumentException>(
            () => new CaptureStatusChangedEventArgs(previous, current));
    }

    [Fact]
    public void RejectsUndefinedState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CaptureStatus((CaptureState)int.MaxValue, StatusTime));
    }

    [Fact]
    public void RejectsUndefinedReasonCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CaptureStatus(
                CaptureState.Paused,
                StatusTime,
                Reason: (CaptureReasonCode)int.MaxValue));
    }

    [Fact]
    public void RejectsUndefinedErrorCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CaptureStatus(
                CaptureState.Faulted,
                StatusTime,
                ErrorCode: (CaptureErrorCode)int.MaxValue));
    }

    [Fact]
    public void FaultedStatusRequiresErrorCode()
    {
        Assert.Throws<ArgumentException>(
            () => new CaptureStatus(CaptureState.Faulted, StatusTime));
    }

    [Theory]
    [MemberData(nameof(NonFaultedStates))]
    public void NonFaultedStatusRejectsErrorCode(CaptureState state)
    {
        Assert.Throws<ArgumentException>(
            () => new CaptureStatus(
                state,
                StatusTime,
                ErrorCode: CaptureErrorCode.NativeFailure));
    }

    [Theory]
    [MemberData(nameof(OperationalStates))]
    public void OperationalSemanticsAreStable(CaptureState state, bool expected)
    {
        var errorCode = state == CaptureState.Faulted
            ? CaptureErrorCode.Unknown
            : CaptureErrorCode.None;
        var status = new CaptureStatus(
            state,
            StatusTime,
            ErrorCode: errorCode);

        Assert.Equal(expected, status.IsOperational);
    }

    [Fact]
    public void StableReasonAndErrorValuesMatchInteropContract()
    {
        Assert.Equal(
            Enumerable.Range(0, 18),
            Enum.GetValues<CaptureReasonCode>().Select(value => (int)value));
        Assert.Equal(
            ExpectedErrorValues,
            Enum.GetValues<CaptureErrorCode>().Select(value => (int)value));
    }

    [Theory]
    [InlineData(CaptureReasonCode.None, 0)]
    [InlineData(CaptureReasonCode.ConsentRequired, 1)]
    [InlineData(CaptureReasonCode.UserPaused, 2)]
    [InlineData(CaptureReasonCode.UserStopped, 3)]
    [InlineData(CaptureReasonCode.ExcludedApplication, 4)]
    [InlineData(CaptureReasonCode.ExcludedWindow, 5)]
    [InlineData(CaptureReasonCode.SessionLocked, 6)]
    [InlineData(CaptureReasonCode.SecureDesktop, 7)]
    [InlineData(CaptureReasonCode.RemoteSession, 8)]
    [InlineData(CaptureReasonCode.PresentationMode, 9)]
    [InlineData(CaptureReasonCode.SystemSleep, 10)]
    [InlineData(CaptureReasonCode.DisplayUnavailable, 11)]
    [InlineData(CaptureReasonCode.AccessLost, 12)]
    [InlineData(CaptureReasonCode.StorageConstrained, 13)]
    [InlineData(CaptureReasonCode.PolicyBlocked, 14)]
    [InlineData(CaptureReasonCode.BackendUnavailable, 15)]
    [InlineData(CaptureReasonCode.BackendFault, 16)]
    [InlineData(CaptureReasonCode.Shutdown, 17)]
    public void ReasonCodeValueIsStable(CaptureReasonCode code, int expected)
    {
        Assert.Equal(expected, (int)code);
    }

    [Theory]
    [InlineData(CaptureErrorCode.None, 0)]
    [InlineData(CaptureErrorCode.AbiVersionMismatch, 1)]
    [InlineData(CaptureErrorCode.InvalidConfiguration, 2)]
    [InlineData(CaptureErrorCode.InvalidState, 3)]
    [InlineData(CaptureErrorCode.DeviceUnavailable, 4)]
    [InlineData(CaptureErrorCode.AccessLost, 5)]
    [InlineData(CaptureErrorCode.EncoderUnavailable, 6)]
    [InlineData(CaptureErrorCode.EncoderFailure, 7)]
    [InlineData(CaptureErrorCode.StorageUnavailable, 8)]
    [InlineData(CaptureErrorCode.StorageFull, 9)]
    [InlineData(CaptureErrorCode.IoFailure, 10)]
    [InlineData(CaptureErrorCode.OperationTimedOut, 11)]
    [InlineData(CaptureErrorCode.NativeFailure, 12)]
    [InlineData(CaptureErrorCode.Unknown, 255)]
    public void ErrorCodeValueIsStable(CaptureErrorCode code, int expected)
    {
        Assert.Equal(expected, (int)code);
    }

    public static TheoryData<CaptureState> NonFaultedStates => new()
    {
        CaptureState.Unavailable,
        CaptureState.Stopped,
        CaptureState.Starting,
        CaptureState.Recording,
        CaptureState.Pausing,
        CaptureState.Paused,
        CaptureState.Resuming,
        CaptureState.Stopping,
        CaptureState.BlockedByConsent,
    };

    public static TheoryData<CaptureState, bool> OperationalStates => new()
    {
        { CaptureState.Unavailable, false },
        { CaptureState.Stopped, true },
        { CaptureState.Starting, true },
        { CaptureState.Recording, true },
        { CaptureState.Pausing, true },
        { CaptureState.Paused, true },
        { CaptureState.Resuming, true },
        { CaptureState.Stopping, true },
        { CaptureState.Faulted, false },
        { CaptureState.BlockedByConsent, false },
    };

    private static CaptureStatus CreateStatus(ulong sequence)
    {
        return new CaptureStatus(
            CaptureState.Recording,
            StatusTime,
            Sequence: sequence);
    }
}

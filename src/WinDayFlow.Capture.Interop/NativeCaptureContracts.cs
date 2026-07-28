using System.Runtime.InteropServices;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Capture.Interop;

[Flags]
public enum NativeCaptureCapabilities : ulong
{
    None = 0,
    PrivacyGuard = 1UL << 0,
    EventQueue = 1UL << 1,
    ScreenCapture = 1UL << 2,
    H264Chunks = 1UL << 3,
    EvidenceExtraction = 1UL << 4,
    TargetScopedAuthorization = 1UL << 5,
    PersistenceGenerationBarrier = 1UL << 6,
    DeterministicStop = 1UL << 7,
    CommandAdmission = 1UL << 8,
    DisplayScopedAuthorization = 1UL << 9,
    DisplayBoundCommandAdmission = 1UL << 10,
    CallbackTimeAuthorizationInvalidation = 1UL << 11,
    DisplayWideContinuousAuthorization = 1UL << 12,
}

public enum NativeCapturePolicyDecision
{
    Unknown = 0,
    Allow = 1,
    Block = 2,
}

public sealed record NativeCaptureConfiguration
{
    public NativeCaptureConfiguration(
        string outputDirectory,
        uint captureIntervalMilliseconds = 10_000,
        uint contextIntervalMilliseconds = 1_000,
        uint chunkDurationMilliseconds = 60_000,
        uint maximumWidth = 1_920,
        uint maximumHeight = 1_080,
        uint eventQueueCapacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException(
                "The native capture output directory must be fully qualified.",
                nameof(outputDirectory));
        }

        ValidateRange(
            captureIntervalMilliseconds,
            250,
            300_000,
            nameof(captureIntervalMilliseconds));
        ValidateRange(
            contextIntervalMilliseconds,
            250,
            60_000,
            nameof(contextIntervalMilliseconds));
        ValidateRange(
            chunkDurationMilliseconds,
            10_000,
            3_600_000,
            nameof(chunkDurationMilliseconds));
        if (captureIntervalMilliseconds > chunkDurationMilliseconds)
        {
            throw new ArgumentException(
                "The capture interval cannot exceed the chunk duration.",
                nameof(captureIntervalMilliseconds));
        }

        ValidateRange(maximumWidth, 320, 7_680, nameof(maximumWidth));
        ValidateRange(maximumHeight, 200, 4_320, nameof(maximumHeight));
        ValidateRange(eventQueueCapacity, 16, 4_096, nameof(eventQueueCapacity));

        OutputDirectory = Path.GetFullPath(outputDirectory);
        CaptureIntervalMilliseconds = captureIntervalMilliseconds;
        ContextIntervalMilliseconds = contextIntervalMilliseconds;
        ChunkDurationMilliseconds = chunkDurationMilliseconds;
        MaximumWidth = maximumWidth;
        MaximumHeight = maximumHeight;
        EventQueueCapacity = eventQueueCapacity;
    }

    public string OutputDirectory { get; }

    public uint CaptureIntervalMilliseconds { get; }

    public uint ContextIntervalMilliseconds { get; }

    public uint ChunkDurationMilliseconds { get; }

    public uint MaximumWidth { get; }

    public uint MaximumHeight { get; }

    public uint EventQueueCapacity { get; }

    private static void ValidateRange(
        uint value,
        uint minimum,
        uint maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {maximum}.");
        }
    }
}

public sealed record NativeCapturePrivacyContext(
    NativeCapturePolicyDecision ConsentGranted,
    NativeCapturePolicyDecision SessionUnlocked,
    NativeCapturePolicyDecision SecureDesktopClear,
    NativeCapturePolicyDecision RemoteSessionAllowed,
    NativeCapturePolicyDecision PresentationAllowed,
    NativeCapturePolicyDecision ApplicationAllowed,
    NativeCapturePolicyDecision WindowAllowed,
    NativeCapturePolicyDecision StorageAvailable,
    ulong RuntimePolicyRevision)
{
    public NativeCapturePolicyDecision ConsentGranted { get; } =
        ValidateDecision(ConsentGranted, nameof(ConsentGranted));

    public NativeCapturePolicyDecision SessionUnlocked { get; } =
        ValidateDecision(SessionUnlocked, nameof(SessionUnlocked));

    public NativeCapturePolicyDecision SecureDesktopClear { get; } =
        ValidateDecision(SecureDesktopClear, nameof(SecureDesktopClear));

    public NativeCapturePolicyDecision RemoteSessionAllowed { get; } =
        ValidateDecision(RemoteSessionAllowed, nameof(RemoteSessionAllowed));

    public NativeCapturePolicyDecision PresentationAllowed { get; } =
        ValidateDecision(PresentationAllowed, nameof(PresentationAllowed));

    public NativeCapturePolicyDecision ApplicationAllowed { get; } =
        ValidateDecision(ApplicationAllowed, nameof(ApplicationAllowed));

    public NativeCapturePolicyDecision WindowAllowed { get; } =
        ValidateDecision(WindowAllowed, nameof(WindowAllowed));

    public NativeCapturePolicyDecision StorageAvailable { get; } =
        ValidateDecision(StorageAvailable, nameof(StorageAvailable));

    public ulong RuntimePolicyRevision { get; } = RuntimePolicyRevision > 0
        ? RuntimePolicyRevision
        : throw new ArgumentOutOfRangeException(
            nameof(RuntimePolicyRevision),
            RuntimePolicyRevision,
            "The native privacy policy revision must be positive.");

    public static NativeCapturePrivacyContext FailClosed(
        ulong runtimePolicyRevision,
        bool consentGranted = false)
    {
        return new NativeCapturePrivacyContext(
            consentGranted
                ? NativeCapturePolicyDecision.Allow
                : NativeCapturePolicyDecision.Block,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            NativeCapturePolicyDecision.Unknown,
            runtimePolicyRevision);
    }

    private static NativeCapturePolicyDecision ValidateDecision(
        NativeCapturePolicyDecision decision,
        string parameterName)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                decision,
                "The native privacy policy decision is not defined.");
        }

        return decision;
    }
}

public sealed record NativeCaptureProbe(
    bool LibraryLoaded,
    bool AbiCompatible,
    uint AbiVersion,
    NativeCaptureCapabilities Capabilities,
    string? Failure);

public sealed record NativeCaptureChunkCommitted
{
    public NativeCaptureChunkCommitted(
        ulong sequence,
        DateTimeOffset committedAt,
        string artifactIdentifier,
        CaptureState state,
        uint droppedBefore,
        ulong persistenceGeneration = 0,
        ulong targetEpoch = 0)
    {
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "A committed native capture chunk requires a positive sequence.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(artifactIdentifier);
        if (artifactIdentifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A committed native capture chunk identifier cannot contain NUL characters.",
                nameof(artifactIdentifier));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The committed native capture chunk state is not defined.");
        }


        if ((persistenceGeneration == 0) != (targetEpoch == 0))
        {
            throw new ArgumentException(
                "A committed native capture chunk must provide both persistence generation and target epoch, or neither for a legacy source.",
                nameof(persistenceGeneration));
        }

        Sequence = sequence;
        CommittedAt = committedAt;
        ArtifactIdentifier = artifactIdentifier;
        State = state;
        DroppedBefore = droppedBefore;
        PersistenceGeneration = persistenceGeneration;
        TargetEpoch = targetEpoch;
    }

    public ulong Sequence { get; }

    public DateTimeOffset CommittedAt { get; }

    public string ArtifactIdentifier { get; }

    public CaptureState State { get; }

    public uint DroppedBefore { get; }

    public ulong PersistenceGeneration { get; }

    public ulong TargetEpoch { get; }
}

public sealed class NativeCaptureChunkCommittedEventArgs : EventArgs
{
    public NativeCaptureChunkCommittedEventArgs(NativeCaptureChunkCommitted chunk)
    {
        Chunk = chunk ?? throw new ArgumentNullException(nameof(chunk));
    }

    public NativeCaptureChunkCommitted Chunk { get; }
}

public readonly record struct NativeCaptureAbiLayout(
    int PointerSize,
    int ConfigSize,
    int ConfigOutputDirectoryOffset,
    int PrivacyContextSize,
    int PrivacyPolicyRevisionOffset,
    int RuntimeAuthorizationSize,
    int RuntimeAuthorizationRevisionOffset,
    int RuntimeAuthorizationTargetEpochOffset,
    int RuntimeAuthorizationDecisionOffset,
    int RuntimeAuthorizationDisplayMonitorOffset,
    int RuntimeAuthorizationDisplayDeviceKeyLengthOffset,
    int RuntimeAuthorizationDisplayDeviceKeyOffset,
    int CommandAdmissionSize,
    int CommandAdmissionRuntimeRevisionOffset,
    int CommandAdmissionPersistenceGenerationOffset,
    int CommandAdmissionTargetEpochOffset,
    int CommandAdmissionAuthorizationEpochOffset,
    int CommandAdmissionNonceOffset,
    int EventSize,
    int EventSequenceOffset,
    int EventPersistenceGenerationOffset,
    int EventTargetEpochOffset);

public static class NativeCaptureAbiContract
{
    public const uint AbiVersion = 1;
    public const int X64StructureSize = 80;
    public const int X64RuntimeAuthorizationStructureSize = 224;
    public const int DisplayDeviceKeyUtf8Capacity = 96;
    public const int DisplayDeviceKeyUtf8MaximumLength = 93;
    public const int CommandAdmissionStructureSize = 64;

    public const NativeCaptureCapabilities FoundationCapabilities =
        NativeCaptureCapabilities.PrivacyGuard
        | NativeCaptureCapabilities.EventQueue;

    public const NativeCaptureCapabilities RuntimeSafetyCapabilities =
        FoundationCapabilities
        | NativeCaptureCapabilities.TargetScopedAuthorization
        | NativeCaptureCapabilities.PersistenceGenerationBarrier
        | NativeCaptureCapabilities.DeterministicStop;

    public const NativeCaptureCapabilities DisplayScopedAuthorizationCapabilities =
        RuntimeSafetyCapabilities
        | NativeCaptureCapabilities.DisplayScopedAuthorization;

    public const NativeCaptureCapabilities CallbackSafeAuthorizationCapabilities =
        DisplayScopedAuthorizationCapabilities
        | NativeCaptureCapabilities.CallbackTimeAuthorizationInvalidation;

    public const NativeCaptureCapabilities RuntimeOwnerCapabilities =
        CallbackSafeAuthorizationCapabilities
        | NativeCaptureCapabilities.DisplayBoundCommandAdmission;

    public const NativeCaptureCapabilities SafeScreenCaptureCapabilities =
        RuntimeOwnerCapabilities
        | NativeCaptureCapabilities.ScreenCapture
        | NativeCaptureCapabilities.H264Chunks;

    public const NativeCaptureCapabilities DisplayWideContinuousCapabilities =
        RuntimeOwnerCapabilities
        | NativeCaptureCapabilities.DisplayWideContinuousAuthorization;

    public static NativeCaptureAbiLayout GetManagedLayout()
    {
        return new NativeCaptureAbiLayout(
            IntPtr.Size,
            Marshal.SizeOf<NativeCaptureConfigV1>(),
            checked((int)Marshal.OffsetOf<NativeCaptureConfigV1>(
                nameof(NativeCaptureConfigV1.OutputDirectoryUtf8))),
            Marshal.SizeOf<NativeCapturePrivacyContextV1>(),
            checked((int)Marshal.OffsetOf<NativeCapturePrivacyContextV1>(
                nameof(NativeCapturePrivacyContextV1.PolicyRevision))),
            Marshal.SizeOf<NativeCaptureRuntimeAuthorizationV1>(),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.RuntimePolicyRevision))),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.TargetEpoch))),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.ConsentGranted))),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.TargetDisplayMonitorHandle))),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.TargetDisplayDeviceKeyUtf8Length))),
            checked((int)Marshal.OffsetOf<NativeCaptureRuntimeAuthorizationV1>(
                nameof(NativeCaptureRuntimeAuthorizationV1.TargetDisplayDeviceKeyUtf8))),
            Marshal.SizeOf<NativeCaptureCommandAdmissionV1>(),
            checked((int)Marshal.OffsetOf<NativeCaptureCommandAdmissionV1>(
                nameof(NativeCaptureCommandAdmissionV1.RuntimePolicyRevision))),
            checked((int)Marshal.OffsetOf<NativeCaptureCommandAdmissionV1>(
                nameof(NativeCaptureCommandAdmissionV1.PersistenceGeneration))),
            checked((int)Marshal.OffsetOf<NativeCaptureCommandAdmissionV1>(
                nameof(NativeCaptureCommandAdmissionV1.TargetEpoch))),
            checked((int)Marshal.OffsetOf<NativeCaptureCommandAdmissionV1>(
                nameof(NativeCaptureCommandAdmissionV1.AuthorizationEpoch))),
            checked((int)Marshal.OffsetOf<NativeCaptureCommandAdmissionV1>(
                nameof(NativeCaptureCommandAdmissionV1.NonceLow))),
            Marshal.SizeOf<NativeCaptureEventV1>(),
            checked((int)Marshal.OffsetOf<NativeCaptureEventV1>(
                nameof(NativeCaptureEventV1.Sequence))),
            checked((int)Marshal.OffsetOf<NativeCaptureEventV1>(
                nameof(NativeCaptureEventV1.PersistenceGeneration))),
            checked((int)Marshal.OffsetOf<NativeCaptureEventV1>(
                nameof(NativeCaptureEventV1.TargetEpoch))));
    }
}

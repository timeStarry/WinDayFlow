namespace WinDayFlow.Application.Capture;

public enum CaptureErrorCode
{
    None = 0,
    AbiVersionMismatch = 1,
    InvalidConfiguration = 2,
    InvalidState = 3,
    DeviceUnavailable = 4,
    AccessLost = 5,
    EncoderUnavailable = 6,
    EncoderFailure = 7,
    StorageUnavailable = 8,
    StorageFull = 9,
    IoFailure = 10,
    OperationTimedOut = 11,
    NativeFailure = 12,
    Unknown = 255,
}

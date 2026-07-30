namespace WinDayFlow.Domain;

public enum AnalysisJobErrorCode
{
    None = 0,
    EvidenceMissing = 1,
    EvidenceInvalid = 2,
    ExtractionFailed = 3,
    ProviderUnavailable = 4,
    ProviderRateLimited = 5,
    ProviderRejected = 6,
    ProviderResponseInvalid = 7,
    OperationTimedOut = 8,
    PersistenceFailure = 9,
    LeaseExpired = 10,
    EvidenceSendBlocked = 11,
    Unknown = 255,
}

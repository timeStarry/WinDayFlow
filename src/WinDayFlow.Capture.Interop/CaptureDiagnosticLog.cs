using System.Text.Json;

namespace WinDayFlow.Capture.Interop;

public sealed class CaptureDiagnosticLog
{
    internal const long DefaultMaximumBytes = 1024 * 1024;
    internal const int DefaultBackupCount = 3;
    private const int MinimumMaximumBytes = 256;

    private readonly object _sync = new();
    private readonly long _maximumBytes;
    private readonly int _backupCount;

    private CaptureDiagnosticLog(
        string filePath,
        long maximumBytes,
        int backupCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException(
                "The capture diagnostic log path must be fully qualified.",
                nameof(filePath));
        }

        if (maximumBytes < MinimumMaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                maximumBytes,
                $"The capture diagnostic log must allow at least {MinimumMaximumBytes} bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(backupCount);
        FilePath = Path.GetFullPath(filePath);
        _maximumBytes = maximumBytes;
        _backupCount = backupCount;
    }

    public string FilePath { get; }

    public static CaptureDiagnosticLog CreateForDataDirectory(
        string dataDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectoryPath);
        if (!Path.IsPathFullyQualified(dataDirectoryPath))
        {
            throw new ArgumentException(
                "The application data directory must be fully qualified.",
                nameof(dataDirectoryPath));
        }

        var dataDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectoryPath));
        var applicationDirectory = Directory.GetParent(dataDirectory)?.FullName
            ?? dataDirectory;
        return new CaptureDiagnosticLog(
            Path.Combine(
                applicationDirectory,
                "Diagnostics",
                "capture.jsonl"),
            DefaultMaximumBytes,
            DefaultBackupCount);
    }

    internal static CaptureDiagnosticLog CreateForTests(
        string filePath,
        long maximumBytes,
        int backupCount) =>
        new(filePath, maximumBytes, backupCount);

    internal void Write(
        CaptureDiagnosticEvent eventName,
        params CaptureDiagnosticFieldValue[] fields)
    {
        var eventText = GetEventName(eventName);
        if (eventText is null)
        {
            return;
        }

        try
        {
            var record = SerializeRecord(eventText, fields);
            lock (_sync)
            {
                var directory = Path.GetDirectoryName(FilePath)
                    ?? throw new InvalidOperationException(
                        "The capture diagnostic log has no parent directory.");
                Directory.CreateDirectory(directory);
                RotateIfNeeded(record.Length);
                using var stream = new FileStream(
                    FilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(record);
            }
        }
        catch
        {
            // Diagnostics must never change capture availability or privacy behavior.
        }
    }

    private static byte[] SerializeRecord(
        string eventName,
        IReadOnlyList<CaptureDiagnosticFieldValue> fields)
    {
        using var stream = new MemoryStream(capacity: 512);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("timestampUtc", DateTimeOffset.UtcNow);
            writer.WriteString("event", eventName);

            HashSet<CaptureDiagnosticField> writtenFields = [];
            foreach (var field in fields)
            {
                var fieldName = GetFieldName(field.Field);
                if (fieldName is not null && writtenFields.Add(field.Field))
                {
                    writer.WriteNumber(fieldName, field.Value);
                }
            }

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private void RotateIfNeeded(int pendingBytes)
    {
        var current = new FileInfo(FilePath);
        if (!current.Exists || current.Length <= _maximumBytes - pendingBytes)
        {
            return;
        }

        if (_backupCount == 0)
        {
            File.Delete(FilePath);
            return;
        }

        for (var index = _backupCount; index >= 2; index--)
        {
            var target = $"{FilePath}.{index}";
            File.Delete(target);
            var source = $"{FilePath}.{index - 1}";
            if (File.Exists(source))
            {
                File.Move(source, target);
            }
        }

        var firstBackup = $"{FilePath}.1";
        File.Delete(firstBackup);
        File.Move(FilePath, firstBackup);
    }

    private static string? GetEventName(CaptureDiagnosticEvent eventName) =>
        eventName switch
        {
            CaptureDiagnosticEvent.PrivacyInvalidated => "privacy_invalidated",
            CaptureDiagnosticEvent.PrivacySampled => "privacy_sampled",
            CaptureDiagnosticEvent.PrivacyPublished => "privacy_published",
            CaptureDiagnosticEvent.PrivacyDecisionEvaluated =>
                "privacy_decision_evaluated",
            CaptureDiagnosticEvent.PrivacyRecoveryScheduled =>
                "privacy_recovery_scheduled",
            CaptureDiagnosticEvent.PrivacyMonitorFaulted =>
                "privacy_monitor_faulted",
            CaptureDiagnosticEvent.BackendStatusChanged =>
                "backend_status_changed",
            CaptureDiagnosticEvent.StopReconciliationStarted =>
                "stop_reconciliation_started",
            CaptureDiagnosticEvent.StopReconciliationCompleted =>
                "stop_reconciliation_completed",
            _ => null,
        };

    private static string? GetFieldName(CaptureDiagnosticField field) =>
        field switch
        {
            CaptureDiagnosticField.Generation => "generation",
            CaptureDiagnosticField.Reason => "reason",
            CaptureDiagnosticField.Holds => "holds",
            CaptureDiagnosticField.TargetState => "targetState",
            CaptureDiagnosticField.DisplayState => "displayState",
            CaptureDiagnosticField.SessionUnlocked => "sessionUnlocked",
            CaptureDiagnosticField.SecureDesktopClear => "secureDesktopClear",
            CaptureDiagnosticField.RemoteSession => "remoteSession",
            CaptureDiagnosticField.PresentationMode => "presentationMode",
            CaptureDiagnosticField.ApplicationAllowed => "applicationAllowed",
            CaptureDiagnosticField.WindowAllowed => "windowAllowed",
            CaptureDiagnosticField.StorageAvailable => "storageAvailable",
            CaptureDiagnosticField.Accepted => "accepted",
            CaptureDiagnosticField.RetryDelayMilliseconds =>
                "retryDelayMilliseconds",
            CaptureDiagnosticField.Fault => "fault",
            CaptureDiagnosticField.State => "state",
            CaptureDiagnosticField.Sequence => "sequence",
            CaptureDiagnosticField.Automatic => "automatic",
            CaptureDiagnosticField.Outcome => "outcome",
            CaptureDiagnosticField.ErrorCode => "errorCode",
            CaptureDiagnosticField.SinkGeneration => "sinkGeneration",
            CaptureDiagnosticField.ConsentGranted => "consentGranted",
            CaptureDiagnosticField.CaptureAllowed => "captureAllowed",
            _ => null,
        };
}

internal enum CaptureDiagnosticEvent
{
    PrivacyInvalidated = 1,
    PrivacySampled = 2,
    PrivacyPublished = 3,
    PrivacyRecoveryScheduled = 4,
    PrivacyMonitorFaulted = 5,
    BackendStatusChanged = 6,
    StopReconciliationStarted = 7,
    StopReconciliationCompleted = 8,
    PrivacyDecisionEvaluated = 9,
}

internal enum CaptureDiagnosticField
{
    Generation = 1,
    Reason = 2,
    Holds = 3,
    TargetState = 4,
    DisplayState = 5,
    SessionUnlocked = 6,
    SecureDesktopClear = 7,
    RemoteSession = 8,
    PresentationMode = 9,
    ApplicationAllowed = 10,
    WindowAllowed = 11,
    StorageAvailable = 12,
    Accepted = 13,
    RetryDelayMilliseconds = 14,
    Fault = 15,
    State = 16,
    Sequence = 17,
    Automatic = 18,
    Outcome = 19,
    ErrorCode = 20,
    SinkGeneration = 21,
    ConsentGranted = 22,
    CaptureAllowed = 23,
}

internal enum CaptureDiagnosticOutcome
{
    Failed = -1,
    Skipped = 0,
    Succeeded = 1,
}

internal readonly record struct CaptureDiagnosticFieldValue(
    CaptureDiagnosticField Field,
    long Value);

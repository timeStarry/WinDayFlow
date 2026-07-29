namespace WinDayFlow.Domain;

public sealed record CaptureProcessTelemetry
{
    public CaptureProcessTelemetry(
        string processName,
        uint processId,
        uint cpuUsageBasisPoints,
        long workingSetBytes,
        long privateMemoryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        if (processName.Length > 260
            || !string.Equals(processName, processName.Trim(), StringComparison.Ordinal)
            || processName.Any(char.IsControl))
        {
            throw new ArgumentException("The capture process name is invalid.", nameof(processName));
        }
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cpuUsageBasisPoints, 10_000U);
        ArgumentOutOfRangeException.ThrowIfNegative(workingSetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(privateMemoryBytes);

        ProcessName = processName;
        ProcessId = processId;
        CpuUsageBasisPoints = cpuUsageBasisPoints;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
    }

    public string ProcessName { get; }
    public uint ProcessId { get; }
    public uint CpuUsageBasisPoints { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
    public double CpuUsagePercent => CpuUsageBasisPoints / 100d;
}

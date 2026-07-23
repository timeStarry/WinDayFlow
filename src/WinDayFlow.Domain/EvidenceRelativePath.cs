namespace WinDayFlow.Domain;

public sealed record EvidenceRelativePath
{
    public const int MaximumLength = 1024;

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public EvidenceRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.Length,
                $"An evidence-relative path cannot exceed {MaximumLength} characters.");
        }

        if (value[0] == '/'
            || value[^1] == '/'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An evidence path must be a canonical root-relative path using forward slashes.",
                nameof(value));
        }

        var segments = value.Split('/');
        if (segments.Any(static segment => !IsValidSegment(segment)))
        {
            throw new ArgumentException(
                "An evidence path contains an unsafe or non-canonical segment.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0
            || segment is "." or ".."
            || !string.Equals(segment, segment.Trim(), StringComparison.Ordinal)
            || segment[^1] is '.' or ' ')
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (char.IsControl(character)
                || character is '<' or '>' or '"' or '|' or '*' or '?')
            {
                return false;
            }
        }

        var baseName = segment.Split('.', 2)[0];
        return !ReservedWindowsNames.Contains(baseName);
    }
}

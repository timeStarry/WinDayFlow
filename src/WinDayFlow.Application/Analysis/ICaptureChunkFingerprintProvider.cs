using WinDayFlow.Domain;

namespace WinDayFlow.Application.Analysis;

public sealed record CaptureChunkFingerprint
{
    public const int HexLength = 64;

    public CaptureChunkFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != HexLength
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "A capture chunk fingerprint must be a 256-bit uppercase hexadecimal value.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public interface ICaptureChunkFingerprintProvider
{
    Task<CaptureChunkFingerprint> ComputeAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default);
}

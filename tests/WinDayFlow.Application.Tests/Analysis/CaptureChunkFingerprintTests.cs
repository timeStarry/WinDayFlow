using WinDayFlow.Application.Analysis;
using Xunit;

namespace WinDayFlow.Application.Tests.Analysis;

public sealed class CaptureChunkFingerprintTests
{
    [Fact]
    public void PreservesCanonicalNativeSha256Output()
    {
        var value = string.Concat(
            Enumerable.Repeat("0123456789ABCDEF", 4));

        var fingerprint = new CaptureChunkFingerprint(value);

        Assert.Equal(value, fingerprint.Value);
        Assert.Equal(value, fingerprint.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ABCDEF")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEFF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void RejectsNonCanonicalFingerprintText(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new CaptureChunkFingerprint(value));
    }
}

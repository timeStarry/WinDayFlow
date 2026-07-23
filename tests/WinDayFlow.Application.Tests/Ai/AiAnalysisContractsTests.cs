using WinDayFlow.Application.Ai;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests.Ai;

public sealed class AiAnalysisContractsTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void ProviderProfileNormalizesSecureAndLoopbackEndpoints()
    {
        var remote = CreateProfile(new Uri("https://api.example.com/v1"));
        var loopback = CreateProfile(new Uri("http://localhost:11434/v1"));

        Assert.Equal("https://api.example.com/v1/", remote.BaseEndpoint.AbsoluteUri);
        Assert.Equal(
            "https://api.example.com/v1/chat/completions",
            remote.ChatCompletionsEndpoint.AbsoluteUri);
        Assert.False(remote.IsLoopback);
        Assert.True(loopback.IsLoopback);
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("ftp://localhost/v1")]
    [InlineData("https://user:secret@api.example.com/v1")]
    [InlineData("https://api.example.com/v1?tenant=one")]
    [InlineData("https://api.example.com/v1#models")]
    public void ProviderProfileRejectsUnsafeEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(new Uri(endpoint)));
    }

    [Fact]
    public void ProviderConfigurationRejectsUnpersistableMetadataAndWhitespaceCredentials()
    {
        var endpoint = new Uri(
            "https://api.example.com/" + new string('a', AiProviderProfile.MaximumEndpointLength));
        Assert.Throws<ArgumentException>(() => CreateProfile(endpoint));
        Assert.Throws<ArgumentException>(() => new AiProviderProfile(
            Guid.Parse("63dbf49e-33e2-4d94-85a6-b85ce76c3cef"),
            "Bad\nname",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://api.example.com/v1"),
            "vision-test-model",
            TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(
            () => AiProviderCredentialUpdate.Replace("sk-internal whitespace"));
    }

    [Fact]
    public void EvidenceImageRequiresJpegMarkersAndPerImageBound()
    {
        Assert.Throws<ArgumentException>(() => new AiEvidenceImage(
            "frame-1",
            Start,
            new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiEvidenceImage(
            "frame-1",
            Start,
            CreateJpeg(AiAnalysisContract.MaximumImageBytes + 1)));
    }

    [Fact]
    public void AnalysisRequestRejectsUnsupportedSchemaAndAggregateImageOverflow()
    {
        var range = new TimeRange(Start, Start.AddMinutes(1));
        var images = Enumerable.Range(1, 7)
            .Select(index => new AiEvidenceImage(
                $"frame-{index}",
                Start.AddSeconds(index),
                CreateJpeg(AiAnalysisContract.MaximumImageBytes)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => CreateRequest(
            range,
            [new AiEvidenceImage("frame-1", Start, CreateJpeg(4))],
            schemaVersion: "2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRequest(range, images));
    }

    internal static AiProviderProfile CreateProfile(Uri endpoint)
    {
        return new AiProviderProfile(
            Guid.Parse("63dbf49e-33e2-4d94-85a6-b85ce76c3cef"),
            "Test provider",
            AiProviderKind.OpenAiCompatible,
            endpoint,
            "vision-test-model",
            TimeSpan.FromSeconds(30));
    }

    internal static AiAnalysisRequest CreateRequest(
        TimeRange? range = null,
        IReadOnlyList<AiEvidenceImage>? images = null,
        string schemaVersion = AiAnalysisContract.CurrentSchemaVersion)
    {
        range ??= new TimeRange(Start, Start.AddMinutes(1));
        images ??= [new AiEvidenceImage("frame-1", Start, CreateJpeg(4))];
        return new AiAnalysisRequest(
            Guid.Parse("ffdd5537-34af-4db4-af14-54a92d0debaa"),
            Guid.Parse("0d105af6-f69b-4a0e-a02f-91b3293b845a"),
            attempt: 1,
            "chunk-1",
            "evidence/chunk-1.mp4",
            range,
            "prompt-v1",
            schemaVersion,
            "zh-CN",
            images,
            [new AiAnalysisContextSlice(range, "editor.exe", "Editor")]);
    }

    internal static byte[] CreateJpeg(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[^2] = 0xff;
        bytes[^1] = 0xd9;
        return bytes;
    }
}

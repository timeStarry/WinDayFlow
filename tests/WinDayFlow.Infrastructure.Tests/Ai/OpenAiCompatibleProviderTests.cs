using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinDayFlow.Application.Ai;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Ai;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Ai;

public sealed class OpenAiCompatibleProviderTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly string[] EditorApplicationIds = ["editor.exe"];
    private static readonly string[] CodingTags = ["coding"];

    [Fact]
    public async Task AnalyzeAsyncPostsStrictStructuredRequestAndMapsResponse()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(SuccessResponse()));
        using var provider = new OpenAiCompatibleProvider(
            CreateProfile(new Uri("https://api.example.com/v1")),
            "test-secret",
            handler);

        var result = await provider.AnalyzeAsync(CreateRequest());

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "https://api.example.com/v1/chat/completions",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("test-secret", handler.Authorization?.Parameter);
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Equal("vision-test-model", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        var responseFormat = root.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        var jsonSchema = responseFormat.GetProperty("json_schema");
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.False(
            jsonSchema.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        var activitiesSchema = jsonSchema
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("activities");
        Assert.Equal(1, activitiesSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(
            AiAnalysisContract.MaximumActivities,
            activitiesSchema.GetProperty("maxItems").GetInt32());
        var activityProperties = activitiesSchema
            .GetProperty("items")
            .GetProperty("properties");
        Assert.Equal(0, activityProperties.GetProperty("start_offset_ms")
            .GetProperty("minimum").GetInt64());
        Assert.Equal(60_000, activityProperties.GetProperty("end_offset_ms")
            .GetProperty("maximum").GetInt64());
        Assert.Equal(0, activityProperties.GetProperty("confidence")
            .GetProperty("minimum").GetDouble());
        Assert.Equal(1, activityProperties.GetProperty("confidence")
            .GetProperty("maximum").GetDouble());
        Assert.Equal(160, activityProperties.GetProperty("title")
            .GetProperty("maxLength").GetInt32());
        Assert.Contains(
            "focused_work",
            activityProperties.GetProperty("category").GetProperty("enum")
                .EnumerateArray().Select(static item => item.GetString()));
        Assert.Contains(
            "focused",
            activityProperties.GetProperty("productivity").GetProperty("enum")
                .EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(
            ["editor.exe"],
            activityProperties.GetProperty("application_ids").GetProperty("items")
                .GetProperty("enum").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(
            ["frame-1"],
            activityProperties.GetProperty("evidence_frame_ids").GetProperty("items")
                .GetProperty("enum").EnumerateArray().Select(static item => item.GetString()));
        var systemPrompt = root.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("first start_offset_ms must be 0", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("no time is omitted", systemPrompt, StringComparison.Ordinal);
        var content = root.GetProperty("messages")[1].GetProperty("content");
        var imagePart = content.EnumerateArray().Single(
            static part => part.GetProperty("type").GetString() == "image_url");
        Assert.Equal(
            "data:image/jpeg;base64,/9j/2Q==",
            imagePart.GetProperty("image_url").GetProperty("url").GetString());
        Assert.DoesNotContain("evidence/chunk-1.mp4", handler.Body, StringComparison.Ordinal);

        Assert.Equal("provider-request-1", result.ProviderRequestId);
        Assert.Equal("vision-test-model", result.Model);
        Assert.Equal(AiAnalysisContract.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Equal(15, result.TokenUsage?.TotalTokens);
        Assert.Equal("Implement provider adapter", Assert.Single(result.Activities).Title);
    }

    [Fact]
    public async Task ZeroFrameRequestUsesUnknownOnlySchemaAndAcceptsEmptyFrameReferences()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            SuccessResponse(frameId: null, category: "unknown", productivity: "unknown")));
        using var provider = CreateProvider(handler);

        var result = await provider.AnalyzeAsync(CreateRequest(includeFrame: false));

        using var body = JsonDocument.Parse(handler.Body!);
        var activityProperties = body.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("activities")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.Equal(
            ["unknown"],
            activityProperties.GetProperty("category").GetProperty("enum")
                .EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(
            ["unknown"],
            activityProperties.GetProperty("productivity").GetProperty("enum")
                .EnumerateArray().Select(static item => item.GetString()));
        var evidenceSchema = activityProperties.GetProperty("evidence_frame_ids");
        Assert.Equal(0, evidenceSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(0, evidenceSchema.GetProperty("maxItems").GetInt32());
        Assert.DoesNotContain(
            body.RootElement.GetProperty("messages")[1].GetProperty("content")
                .EnumerateArray(),
            static part => part.GetProperty("type").GetString() == "image_url");
        var activity = Assert.Single(result.Activities);
        Assert.Empty(activity.EvidenceFrameIds);
        Assert.Equal("unknown", activity.Category);
        Assert.Equal("unknown", activity.Productivity);
    }

    [Fact]
    public async Task LoopbackEndpointAllowsAnEmptyApiKeyWithoutAuthorizationHeader()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(SuccessResponse()));
        using var provider = new OpenAiCompatibleProvider(
            CreateProfile(new Uri("http://127.0.0.1:11434/v1")),
            apiKey: null,
            handler);

        _ = await provider.AnalyzeAsync(CreateRequest());

        Assert.Null(handler.Authorization);
    }

    [Fact]
    public void RemoteEndpointRequiresABoundedBearerKey()
    {
        var profile = CreateProfile(new Uri("https://api.example.com/v1"));

        var missing = Assert.Throws<AiProviderException>(() =>
            new OpenAiCompatibleProvider(profile, apiKey: null, new RecordingHandler()));
        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, missing.ErrorCode);
        Assert.False(missing.IsRetryable);
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleProvider(profile, new string('a', (8 * 1024) + 1), new RecordingHandler()));
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutFollowingIt()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://other.example/v1/chat/completions") },
        }));
        using var provider = new OpenAiCompatibleProvider(
            CreateProfile(new Uri("https://api.example.com/v1")),
            "test-secret",
            handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.InvalidConfiguration, exception.ErrorCode);
        Assert.Equal(307, exception.TransportStatusCode);
        Assert.False(exception.IsRetryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RateLimitMapsRetryAfterAndSafeRequestIdentifier()
    {
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            response.Headers.TryAddWithoutValidation("x-request-id", "provider-rate-1");
            return Task.FromResult(response);
        });
        using var provider = new OpenAiCompatibleProvider(
            CreateProfile(new Uri("https://api.example.com/v1")),
            "test-secret",
            handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.RateLimited, exception.ErrorCode);
        Assert.True(exception.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Equal("provider-rate-1", exception.ProviderRequestId);
        Assert.Equal(429, exception.TransportStatusCode);
    }

    [Theory]
    [InlineData(400, AiProviderErrorCode.RequestRejected, false)]
    [InlineData(401, AiProviderErrorCode.AuthenticationFailed, false)]
    [InlineData(403, AiProviderErrorCode.AccessDenied, false)]
    [InlineData(404, AiProviderErrorCode.ModelNotFound, false)]
    [InlineData(408, AiProviderErrorCode.Timeout, true)]
    [InlineData(413, AiProviderErrorCode.RequestTooLarge, false)]
    [InlineData(500, AiProviderErrorCode.ProviderUnavailable, true)]
    public async Task HttpFailuresHaveStableSemantics(
        int statusCode,
        AiProviderErrorCode expectedCode,
        bool expectedRetryable)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)statusCode)));
        using var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(expectedRetryable, exception.IsRetryable);
        Assert.Equal(statusCode, exception.TransportStatusCode);
    }

    [Fact]
    public async Task TransportFailureMapsToRetryableNetworkError()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException("Synthetic transport failure."));
        using var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.NetworkUnavailable, exception.ErrorCode);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task CallerCancellationRemainsOperationCanceledException()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var provider = new OpenAiCompatibleProvider(
            CreateProfile(new Uri("https://api.example.com/v1")),
            "test-secret",
            handler);
        using var cancellation = new CancellationTokenSource();

        var operation = provider.AnalyzeAsync(CreateRequest(), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task ProviderTimeoutHasStableRetryableError()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var profile = new AiProviderProfile(
            Guid.Parse("63dbf49e-33e2-4d94-85a6-b85ce76c3cef"),
            "Test provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://api.example.com/v1"),
            "vision-test-model",
            AiProviderProfile.MinimumRequestTimeout);
        using var provider = new OpenAiCompatibleProvider(profile, "test-secret", handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.Timeout, exception.ErrorCode);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task DeclaredOversizeResponseMapsToInvalidResponse()
    {
        var handler = new RecordingHandler((_, _) =>
        {
            var content = new ByteArrayContent([1]);
            content.Headers.ContentLength = OpenAiCompatibleProvider.MaximumResponseBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        });
        using var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.InvalidResponse, exception.ErrorCode);
        Assert.True(exception.IsRetryable);
        Assert.Equal(
            AiProviderResponseFailureKind.ResponseTooLarge,
            exception.ResponseFailureKind);
    }

    [Fact]
    public async Task StreamingOversizeResponseMapsToInvalidResponse()
    {
        var handler = new RecordingHandler((_, _) =>
        {
            var content = new UnknownLengthContent(
                new byte[OpenAiCompatibleProvider.MaximumResponseBytes + 1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        });
        using var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.InvalidResponse, exception.ErrorCode);
        Assert.True(exception.IsRetryable);
        Assert.Equal(
            AiProviderResponseFailureKind.ResponseTooLarge,
            exception.ResponseFailureKind);
    }

    [Fact]
    public async Task UnknownStructuredPropertyAndSemanticViolationAreInvalidResponses()
    {
        var unknownPropertyHandler = new RecordingHandler((_, _) => Task.FromResult(
            SuccessResponse(addUnknownProperty: true)));
        using var unknownPropertyProvider = CreateProvider(unknownPropertyHandler);
        var unknownProperty = await Assert.ThrowsAsync<AiProviderException>(() =>
            unknownPropertyProvider.AnalyzeAsync(CreateRequest()));
        Assert.Equal(AiProviderErrorCode.InvalidResponse, unknownProperty.ErrorCode);
        Assert.Equal(
            AiProviderResponseFailureKind.StructuredContentInvalid,
            unknownProperty.ResponseFailureKind);

        var badReferenceHandler = new RecordingHandler((_, _) => Task.FromResult(
            SuccessResponse(frameId: "not-in-request")));
        using var badReferenceProvider = CreateProvider(badReferenceHandler);
        var badReference = await Assert.ThrowsAsync<AiProviderException>(() =>
            badReferenceProvider.AnalyzeAsync(CreateRequest()));
        Assert.Equal(AiProviderErrorCode.InvalidResponse, badReference.ErrorCode);
        Assert.Equal(
            AiProviderResponseFailureKind.SemanticValidationFailed,
            badReference.ResponseFailureKind);
        Assert.DoesNotContain("not-in-request", badReference.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("leading_gap")]
    [InlineData("trailing_gap")]
    [InlineData("internal_gap")]
    public async Task IncompleteStructuredCoverageMapsToInvalidResponse(string coverage)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            SuccessResponse(coverage: coverage)));
        using var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.AnalyzeAsync(CreateRequest()));

        Assert.Equal(AiProviderErrorCode.InvalidResponse, exception.ErrorCode);
    }

    private static OpenAiCompatibleProvider CreateProvider(HttpMessageHandler handler)
    {
        return new OpenAiCompatibleProvider(
            CreateProfile(new Uri("https://api.example.com/v1")),
            "test-secret",
            handler);
    }

    private static AiProviderProfile CreateProfile(Uri endpoint)
    {
        return new AiProviderProfile(
            Guid.Parse("63dbf49e-33e2-4d94-85a6-b85ce76c3cef"),
            "Test provider",
            AiProviderKind.OpenAiCompatible,
            endpoint,
            "vision-test-model",
            TimeSpan.FromSeconds(30));
    }

    private static AiAnalysisRequest CreateRequest(bool includeFrame = true)
    {
        var range = new TimeRange(Start, Start.AddMinutes(1));
        return new AiAnalysisRequest(
            Guid.Parse("ffdd5537-34af-4db4-af14-54a92d0debaa"),
            Guid.Parse("0d105af6-f69b-4a0e-a02f-91b3293b845a"),
            attempt: 1,
            "chunk-1",
            "evidence/chunk-1.mp4",
            range,
            "prompt-v1",
            AiAnalysisContract.CurrentSchemaVersion,
            "zh-CN",
            includeFrame
                ? [new AiEvidenceImage(
                    "frame-1",
                    Start,
                    new byte[] { 0xff, 0xd8, 0xff, 0xd9 })]
                : [],
            [new AiAnalysisContextSlice(range, "editor.exe", "Editor")]);
    }

    private static HttpResponseMessage SuccessResponse(
        bool addUnknownProperty = false,
        string? frameId = "frame-1",
        string coverage = "complete",
        string category = "focused_work",
        string productivity = "focused")
    {
        IReadOnlyList<(long Start, long End)> intervals = coverage switch
        {
            "complete" => [(0, 60_000)],
            "empty" => [],
            "leading_gap" => [(1, 60_000)],
            "trailing_gap" => [(0, 59_999)],
            "internal_gap" => [(0, 20_000), (30_000, 60_000)],
            _ => throw new ArgumentOutOfRangeException(nameof(coverage)),
        };
        var activities = intervals
            .Select(interval => new Dictionary<string, object?>
            {
                ["start_offset_ms"] = interval.Start,
                ["end_offset_ms"] = interval.End,
                ["title"] = "Implement provider adapter",
                ["summary"] = "Build and test the OpenAI-compatible boundary.",
                ["category"] = category,
                ["productivity"] = productivity,
                ["application_ids"] = EditorApplicationIds,
                ["tags"] = CodingTags,
                ["confidence"] = 0.9,
                ["evidence_frame_ids"] = frameId is null
                    ? Array.Empty<string>()
                    : new[] { frameId },
            })
            .ToArray();
        if (addUnknownProperty)
        {
            activities[0]["unexpected"] = true;
        }

        var structured = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = AiAnalysisContract.CurrentSchemaVersion,
            ["activities"] = activities,
        });
        var envelope = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "provider-request-1",
            ["model"] = "vision-test-model",
            ["choices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["finish_reason"] = "stop",
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["content"] = structured,
                    },
                },
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 10,
                ["completion_tokens"] = 5,
                ["total_tokens"] = 15,
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _response;

        public RecordingHandler()
            : this(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        {
        }

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _response(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _content;

        public UnknownLengthContent(byte[] content)
        {
            _content = content;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return stream.WriteAsync(_content).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new MemoryStream(_content, writable: false));
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;

namespace Search.Tests.Infrastructure;

public class AiEmbeddingClientTests
{
    private const string Token = "test-internal-token";

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, JsonElement? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonElement? body = null;
            if (request.Content is not null)
            {
                using var doc = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                body = doc.RootElement.Clone();
            }

            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent(payload) };

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    // Answers /embed with [len, 1, 0] per text, dimension 3, like FakeEmbeddingClient.
    private static Task<HttpResponseMessage> EmbedEcho(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/info"))
        {
            return Task.FromResult(Json(new { model = "m", dimension = 3 }));
        }

        var texts = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("texts")
            .EnumerateArray().Select(t => t.GetString()!).ToArray();
        var vectors = texts.Select(t => new float[] { t.Length, 1f, 0f }).ToArray();
        return Task.FromResult(Json(new { model = "m", dimension = 3, vectors }));
    }

    private static AiEmbeddingClient Client(HttpMessageHandler handler, string url = "http://ai:6000", string token = Token) =>
        new(
            new HttpClient(handler),
            Options.Create(new AiServiceOptions { Url = url, InternalToken = token }),
            NullLogger<AiEmbeddingClient>.Instance);

    [Fact]
    public async Task EmbedAsync_SplitsIntoBatchesOf256AndKeepsInputOrder()
    {
        var handler = new StubHandler(EmbedEcho);
        var texts = Enumerable.Range(0, 300).Select(i => $"t{i}").ToArray();

        var vectors = await Client(handler).EmbedAsync(texts);

        var embedCalls = handler.Requests.Where(r => r.Path == "/api/internal/embed").ToList();
        embedCalls.Select(r => r.Body!.Value.GetProperty("texts").GetArrayLength()).Should().Equal(256, 44);
        vectors.Should().HaveCount(300);
        vectors[299][0].Should().Be("t299".Length);
    }

    [Fact]
    public async Task EmbedAsync_SendsBearerTokenAndCamelCaseBody()
    {
        var handler = new StubHandler(EmbedEcho);

        await Client(handler).EmbedAsync(["hello"]);

        var call = handler.Requests.Single(r => r.Path == "/api/internal/embed");
        call.Method.Should().Be(HttpMethod.Post);
        call.Auth.Should().Be($"Bearer {Token}");
        call.Body!.Value.GetProperty("texts")[0].GetString().Should().Be("hello");
    }

    [Fact]
    public async Task GetModelInfoAsync_ReturnsModelAndDimension()
    {
        var info = await Client(new StubHandler(EmbedEcho)).GetModelInfoAsync();

        info.Should().Be(new EmbeddingModelInfo("m", 3));
    }

    [Fact]
    public async Task EmbedAsync_VectorCountMismatch_Throws()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(new { model = "m", dimension = 3, vectors = new[] { new[] { 1f, 0f, 0f } } })));

        var act = () => Client(handler).EmbedAsync(["a", "b"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>();
    }

    [Fact]
    public async Task EmbedAsync_VectorSizeMismatch_Throws()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(new { model = "m", dimension = 3, vectors = new[] { new[] { 1f, 0f } } })));

        var act = () => Client(handler).EmbedAsync(["a"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>();
    }

    [Fact]
    public async Task EmbedAsync_ErrorStatus_Throws()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(new { code = "embedder_unavailable" }, HttpStatusCode.ServiceUnavailable)));

        var act = () => Client(handler).EmbedAsync(["a"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>().WithMessage("*503*");
    }

    [Fact]
    public async Task EmbedAsync_NetworkFailure_Throws()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var act = () => Client(handler).EmbedAsync(["a"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("__SET_VIA_INFISICAL__")]
    public async Task EmbedAsync_UnsetToken_FailsClosedWithoutCallingService(string token)
    {
        var handler = new StubHandler(EmbedEcho);

        var act = () => Client(handler, token: token).EmbedAsync(["a"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<set via AI_SERVICE_URL env var>")]
    public async Task EmbedAsync_UnsetOrInvalidUrl_FailsClosedWithoutCallingService(string url)
    {
        var handler = new StubHandler(EmbedEcho);

        var act = () => Client(handler, url: url).EmbedAsync(["a"]);

        await act.Should().ThrowAsync<EmbeddingUnavailableException>();
        handler.Requests.Should().BeEmpty();
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

/// <summary>Calls ai-svc POST /api/internal/embed. Fails closed: every error is an <see cref="EmbeddingUnavailableException"/>.</summary>
public class AiEmbeddingClient : IEmbeddingClient
{
    // ai-svc rejects batches above EMBED_MAX_TEXTS (app/internal.py) with 422.
    private const int MaxTextsPerRequest = 256;
    private const string InfoPath = "api/internal/embed/info";
    private const string EmbedPath = "api/internal/embed";

    private readonly HttpClient _http;
    private readonly AiServiceOptions _options;
    private readonly ILogger<AiEmbeddingClient> _logger;

    public AiEmbeddingClient(HttpClient http, IOptions<AiServiceOptions> options, ILogger<AiEmbeddingClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmbeddingModelInfo> GetModelInfoAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendAsync<InfoResponse>(HttpMethod.Get, InfoPath, null, cancellationToken);
        return new EmbeddingModelInfo(body.Model, body.Dimension);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += MaxTextsPerRequest)
        {
            var batch = texts.Skip(offset).Take(MaxTextsPerRequest).ToArray();
            var body = await SendAsync<EmbedResponse>(HttpMethod.Post, EmbedPath, new EmbedRequest(batch), cancellationToken);

            if (body.Vectors.Count != batch.Length)
            {
                throw new EmbeddingUnavailableException(
                    $"Embedding service returned {body.Vectors.Count} vectors for {batch.Length} texts.");
            }

            if (body.Vectors.Any(v => v.Length != body.Dimension))
            {
                throw new EmbeddingUnavailableException(
                    $"Embedding service returned vectors that do not match its reported dimension {body.Dimension}.");
            }

            vectors.AddRange(body.Vectors);
        }

        return vectors;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var baseUri))
        {
            throw new EmbeddingUnavailableException("AI_SERVICE_URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.InternalToken))
        {
            throw new EmbeddingUnavailableException("INTERNAL_SERVICE_TOKEN is not configured.");
        }

        using var request = new HttpRequestMessage(method, new Uri(baseUri, path))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _options.InternalToken) },
            Content = payload is null ? null : JsonContent.Create(payload, options: JsonSerializerOptions.Web),
        };

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Embedding service call {Path} failed with HTTP {Status}.", path, (int)response.StatusCode);
                throw new EmbeddingUnavailableException($"Embedding service returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return body ?? throw new EmbeddingUnavailableException("Embedding service returned an empty body.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Embedding service call {Path} failed.", path);
            throw new EmbeddingUnavailableException("Could not reach the embedding service.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Embedding service call {Path} timed out.", path);
            throw new EmbeddingUnavailableException("The embedding service timed out.", ex);
        }
        catch (JsonException ex)
        {
            throw new EmbeddingUnavailableException("Embedding service returned an unreadable body.", ex);
        }
    }

    private sealed record EmbedRequest(IReadOnlyList<string> Texts);

    private sealed record EmbedResponse(string Model, int Dimension, List<float[]> Vectors);

    private sealed record InfoResponse(string Model, int Dimension);
}

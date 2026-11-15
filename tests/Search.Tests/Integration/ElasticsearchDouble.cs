using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Elastic.Transport;

namespace Search.Tests.Integration;

/// <summary>
/// In-memory stand-in for the Elasticsearch HTTP API, plugged into the real client as its
/// <see cref="IRequestInvoker"/>. It understands only the calls the service makes (index
/// existence/create, document index/get/delete, bulk, search) and keeps documents as raw JSON so
/// a write made through the API can be read back through the API.
/// </summary>
public sealed class ElasticsearchDouble : IRequestInvoker
{
    private readonly ConcurrentDictionary<string, string> _documents = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ResponseBuilder _responses = new();

    // Nothing to release: the double holds only managed state.
    public void Dispose()
    {
    }

    public IReadOnlyCollection<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>Document writes and deletes (single or bulk) that reached the double.</summary>
    public IReadOnlyCollection<RecordedRequest> DocumentWrites =>
        _requests.Where(r => r.IsDocumentWrite).ToArray();

    public IReadOnlyDictionary<string, string> Documents => _documents.ToDictionary(kv => kv.Key, kv => kv.Value);

    public void Reset()
    {
        _documents.Clear();
        while (_requests.TryDequeue(out _))
        {
        }
    }

    public TResponse Request<TResponse>(RequestData requestData) where TResponse : TransportResponse, new() =>
        RequestAsync<TResponse>(requestData, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<TResponse> RequestAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken)
        where TResponse : TransportResponse, new()
    {
        var method = requestData.Method.ToString().ToUpperInvariant();
        var path = requestData.Uri.AbsolutePath.Trim('/');
        var body = ReadBody(requestData);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        _requests.Enqueue(new RecordedRequest(method, path, body));
        var (status, responseBody) = Route(method, segments, body);

        return await _responses.BuildAsync<TResponse>(
            requestData, cancellationToken, Encoding.UTF8.GetBytes(responseBody), status);
    }

    /// <summary>
    /// Only exists to reach the protected BuildResponseAsync helper, which turns bytes and a status
    /// into the transport's response object (ApiCallDetails and friends) without any I/O.
    /// </summary>
    private sealed class ResponseBuilder : InMemoryRequestInvoker
    {
        public Task<TResponse> BuildAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken, byte[] body, int status)
            where TResponse : TransportResponse, new() =>
            BuildResponseAsync<TResponse>(requestData, cancellationToken, body, status, "application/json");
    }

    // The transport writes the body only when a request is actually sent; this double never sends,
    // so the body is serialized here with the same PostData writer the transport would use.
    private static string ReadBody(RequestData requestData)
    {
        if (requestData.PostData is null)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        requestData.PostData.Write(buffer, requestData.ConnectionSettings);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private (int Status, string Body) Route(string method, string[] segments, string body)
    {
        // Index existence / creation (ElasticsearchInitializer).
        if (segments.Length == 1 && method == "HEAD")
        {
            return (404, "");
        }

        if (segments.Length == 1 && method == "PUT")
        {
            return (200, $$"""{"acknowledged":true,"shards_acknowledged":true,"index":"{{segments[0]}}"}""");
        }

        // Single document: /{index}/_doc/{id}
        if (segments is [_, "_doc", var id])
        {
            switch (method)
            {
                case "PUT":
                case "POST":
                    _documents[id] = body;
                    return (200, Write(id, "created"));
                case "DELETE":
                    var removed = _documents.TryRemove(id, out _);
                    return removed ? (200, Write(id, "deleted")) : (404, Write(id, "not_found"));
                case "GET":
                    return _documents.TryGetValue(id, out var source)
                        ? (200, $$"""{"_index":"jobs","_id":"{{id}}","found":true,"_source":{{source}}}""")
                        : (404, $$"""{"_index":"jobs","_id":"{{id}}","found":false}""");
            }
        }

        // Bulk: NDJSON pairs of action line and document line.
        if (segments is [_, "_bulk"] && method == "POST")
        {
            return (200, Bulk(body));
        }

        // Search (keyword and kNN): returns every stored document; the double does not evaluate queries.
        if (segments is [_, "_search"] && method is "POST" or "GET")
        {
            return (200, Search());
        }

        return (404, """{"error":"unhandled by Elasticsearch double"}""");
    }

    private static string Write(string id, string result) =>
        $$"""{"_index":"jobs","_id":"{{id}}","_version":1,"result":"{{result}}","_shards":{"total":1,"successful":1,"failed":0},"_seq_no":0,"_primary_term":1}""";

    private string Bulk(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var items = new List<string>();
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var id = JsonDocument.Parse(lines[i]).RootElement.GetProperty("index").GetProperty("_id").GetString()!;
            _documents[id] = lines[i + 1];
            items.Add($$"""{"index":{"_index":"jobs","_id":"{{id}}","_version":1,"result":"created","status":201,"_shards":{"total":1,"successful":1,"failed":0},"_seq_no":0,"_primary_term":1} }""");
        }

        return $$"""{"took":1,"errors":false,"items":[{{string.Join(',', items)}}]}""";
    }

    private string Search()
    {
        var hits = _documents.Select(kv =>
            $$"""{"_index":"jobs","_id":"{{kv.Key}}","_score":1.0,"_source":{{kv.Value}}}""");
        var count = _documents.Count;
        return $$"""{"took":1,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},"hits":{"total":{"value":{{count}},"relation":"eq"},"max_score":1.0,"hits":[{{string.Join(',', hits)}}]} }""";
    }
}

public sealed record RecordedRequest(string Method, string Path, string Body)
{
    /// <summary>A document write or delete (single document or bulk), not index setup or a search.</summary>
    public bool IsDocumentWrite =>
        Method is "PUT" or "POST" or "DELETE"
        && (Path.Contains("/_doc/", StringComparison.Ordinal) || Path.EndsWith("/_bulk", StringComparison.Ordinal));
}

using Microsoft.AspNetCore.Http;
using Search.Api.Endpoints;

namespace Search.Tests.Api;

public class BoundedBodyTests
{
    private static readonly byte[] Payload = "0123456789"u8.ToArray();

    [Fact]
    public void MaxIndexBodyBytes_IsFiveMegabytes()
    {
        // Single-document and bulk writes share this limit; the value is part of the API contract.
        IndexEndpoints.MaxIndexBodyBytes.Should().Be(5_000_000);
    }

    [Fact]
    public async Task ReadBounded_KnownLengthUnderLimit_ReturnsAllBytes()
    {
        var request = RequestWith(Payload, declaredLength: Payload.Length);

        var body = await IndexEndpoints.ReadBoundedBodyAsync(request, limit: 10, default);

        body.Should().Equal(Payload);
    }

    [Fact]
    public async Task ReadBounded_BodyExactlyAtLimit_ReturnsAllBytes()
    {
        var request = RequestWith(Payload, declaredLength: null);

        var body = await IndexEndpoints.ReadBoundedBodyAsync(request, limit: Payload.Length, default);

        body.Should().Equal(Payload);
    }

    [Fact]
    public async Task ReadBounded_DeclaredLengthOverLimit_ReturnsNullWithoutReadingBody()
    {
        // Content-Length alone decides: an oversized request must not cost any body reads.
        var stream = new ThrowOnReadStream();
        var request = RequestWith(stream, declaredLength: Payload.Length);

        var body = await IndexEndpoints.ReadBoundedBodyAsync(request, limit: 5, default);

        body.Should().BeNull();
        stream.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadBounded_UnknownLengthOverLimit_ReturnsNull()
    {
        // Chunked bodies have no Content-Length, so the limit is enforced while reading.
        var request = RequestWith(Payload, declaredLength: null);

        var body = await IndexEndpoints.ReadBoundedBodyAsync(request, limit: 9, default);

        body.Should().BeNull();
    }

    [Fact]
    public async Task ReadBounded_EmptyBody_ReturnsEmptyArray()
    {
        var request = RequestWith(Array.Empty<byte>(), declaredLength: 0);

        var body = await IndexEndpoints.ReadBoundedBodyAsync(request, limit: 10, default);

        body.Should().NotBeNull().And.BeEmpty();
    }

    private static HttpRequest RequestWith(byte[] bytes, long? declaredLength) =>
        RequestWith(new MemoryStream(bytes), declaredLength);

    private static HttpRequest RequestWith(Stream body, long? declaredLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        context.Request.ContentLength = declaredLength;
        return context.Request;
    }

    /// <summary>Fails the test if anything reads from it.</summary>
    private sealed class ThrowOnReadStream : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw Record();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Record();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private Exception Record()
        {
            ReadCount++;
            return new InvalidOperationException("body must not be read");
        }
    }
}

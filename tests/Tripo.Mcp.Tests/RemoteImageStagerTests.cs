using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class RemoteImageStagerTests
{
    // Minimal PNG signature + body.
    private static readonly byte[] PngBytes =
    [
        0x89,
        (byte)'P',
        (byte)'N',
        (byte)'G',
        0x0d,
        0x0a,
        0x1a,
        0x0a,
        0x00,
        0x01,
    ];

    // A real 1x1 lossy WebP fixture, rather than a marker-only RIFF prefix.
    private static readonly byte[] WebpBytes = Convert.FromBase64String(
        "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEAAUAmJaQAA3AA/v89WAAAAA==");

    private static readonly byte[] GifBytes =
    [
        (byte)'G',
        (byte)'I',
        (byte)'F',
        (byte)'8',
        0x39,
        0x61,
        0x00,
        0x01,
    ];

    private static readonly byte[] JpegBytes =
    [
        0xff,
        0xd8,
        0xff,
        0xe0,
        0x00,
        0x02,
        0x00,
        0x00,
    ];

    [Fact]
    public async Task StageUrlImageAsyncStagesOpaquePngAndReturnsDescriptor()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((request, call) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(BytesResponse(PngBytes));
        });
        RemoteImageStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedImageTransfer transfer =
            await stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None);

        Assert.Equal("image/png", transfer.MediaType);
        Assert.Equal(PngBytes.Length, transfer.ByteLength);
        string expectedSha = Convert.ToHexString(SHA256.HashData(PngBytes))
            .ToLowerInvariant();
        Assert.Equal(expectedSha, transfer.Sha256);
        // The staged artifact must exist and round-trip via the verifier.
        await using Stream verified =
            await Tripo.Bridge.ImageTransferStore.OpenVerifiedAsync(
                transfer,
                CancellationToken.None);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(PngBytes, copy.ToArray());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task StageUrlImageAsyncStagesOpaqueWebp()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(BytesResponse(WebpBytes)));
        RemoteImageStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedImageTransfer transfer =
            await stager.StageUrlImageAsync(
                new Uri("https://example.com/image.webp"),
                CancellationToken.None);

        Assert.Equal("image/webp", transfer.MediaType);
        Assert.Equal(WebpBytes.Length, transfer.ByteLength);
    }

    [Fact]
    public async Task StageUrlImageAsyncStagesJpegByMagicBytes()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(BytesResponse(JpegBytes)));
        RemoteImageStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedImageTransfer transfer =
            await stager.StageUrlImageAsync(
                new Uri("https://example.com/image-without-extension"),
                CancellationToken.None);

        Assert.Equal("image/jpeg", transfer.MediaType);
        Assert.Equal(JpegBytes.Length, transfer.ByteLength);
    }

    [Theory]
    [InlineData("http://example.com/image.png")] // non-HTTPS
    [InlineData("https://127.0.0.1/image.png")] // IP literal
    [InlineData("https://localhost/image.png")] // localhost
    [InlineData("https://host.local/image.png")] // .local
    [InlineData("https://localhost-dot/example.png")] // no dot in host
    [InlineData("https://user:password@example.com/image.png")] // credentials
    public async Task StageUrlImageAsyncRejectsDisallowedHosts(string url)
    {
        using TemporaryDataRoot dataRoot = new();
        RemoteImageStager stager =
            new(new HttpClient(new DelegateHttpMessageHandler(
                (_, _) => Task.FromResult(BytesResponse(PngBytes)))));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri(url),
                CancellationToken.None));
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsRedirectToLocalhost()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((request, call) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://localhost/evil.png") },
                });
            }

            return Task.FromResult(BytesResponse(PngBytes));
        });
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None));
        // The redirect target was rejected before any second fetch.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsBodyAboveTheLocalSizeLimit()
    {
        using TemporaryDataRoot dataRoot = new();
        long oversize =
            Tripo.Bridge.BridgeConstants.MaximumImageTransferBytes + 1;
        DelegateHttpMessageHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FixedLengthStream(oversize)),
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentLength = oversize;
            return Task.FromResult(response);
        });
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None));
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsChunkedBodyAboveTheRunningLimit()
    {
        using TemporaryDataRoot dataRoot = new();
        long oversize =
            Tripo.Bridge.BridgeConstants.MaximumImageTransferBytes + 1;
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new PrefixThenZerosStream(PngBytes, oversize)),
            }));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/chunked.png"),
                CancellationToken.None));

        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncTimesOutAStalledBodyAndCleansScratch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingReadStream()),
            }));
        RemoteImageStager stager = new(
            new HttpClient(handler),
            TimeSpan.FromMilliseconds(50));

        TripoApiException exception = await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/stalled.png"),
                CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncPropagatesCallerCancellationAndCleansScratch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingReadStream()),
            }));
        RemoteImageStager stager = new(new HttpClient(handler));
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/cancelled.png"),
                cancellation.Token));

        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncWrapsBodyReadFailureAndCleansScratch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream()),
            }));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/broken.png"),
                CancellationToken.None));

        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsHttpFailureWithoutScratch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/unavailable.png"),
                CancellationToken.None));

        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsUnsupportedMagicBytes()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(BytesResponse(GifBytes)));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.gif"),
                CancellationToken.None));
    }

    [Fact]
    public async Task StageUrlImageAsyncFollowsBoundedRedirects()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((request, call) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.Redirect)
                    {
                        Headers =
                        {
                            Location = new Uri("https://cdn.example.com/real.png"),
                        },
                    });
            }

            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(BytesResponse(PngBytes));
        });
        RemoteImageStager stager = new(new HttpClient(handler));

        Tripo.Bridge.StagedImageTransfer transfer =
            await stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None);

        Assert.Equal("image/png", transfer.MediaType);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsRedirectDowngradeBeforeSecondFetch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri("http://cdn.example.com/image.png"),
                },
            }));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsMissingRedirectLocation()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncEnforcesRedirectLimit()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, call) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri(
                        $"https://cdn{call}.example.com/image.png"),
                },
            }));
        RemoteImageStager stager = new(new HttpClient(handler));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(
                new Uri("https://example.com/image.png"),
                CancellationToken.None));

        Assert.Equal(4, handler.CallCount);
        AssertNoScratchFiles();
    }

    [Fact]
    public async Task StageUrlImageAsyncRejectsOverlongUrlBeforeFetch()
    {
        using TemporaryDataRoot dataRoot = new();
        DelegateHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(BytesResponse(PngBytes)));
        RemoteImageStager stager = new(new HttpClient(handler));
        Uri uri = new("https://example.com/" + new string('a', 4096));

        await Assert.ThrowsAsync<TripoApiException>(
            () => stager.StageUrlImageAsync(uri, CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private static void AssertNoScratchFiles()
    {
        string directory =
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory();
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    private sealed class PrefixThenZerosStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly long _length;
        private long _position;

        public PrefixThenZerosStream(byte[] prefix, long length)
        {
            _prefix = prefix;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> buffer)
        {
            int count = checked((int)Math.Min(buffer.Length, _length - _position));
            if (count <= 0)
            {
                return 0;
            }

            buffer[..count].Clear();
            if (_position < _prefix.Length)
            {
                int prefixCount = checked((int)Math.Min(
                    count,
                    _prefix.Length - _position));
                _prefix.AsSpan(checked((int)_position), prefixCount)
                    .CopyTo(buffer);
            }

            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    // A stream that reports a fixed length and returns zero bytes on read,
    // used only to exercise the content-length pre-check.
    private sealed class FixedLengthStream : Stream
    {
        private readonly long _length;

        public FixedLengthStream(long length) => _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

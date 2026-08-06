using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Tripo.Mcp;

public interface IRemoteImageStager
{
    Task<Tripo.Bridge.StagedImageTransfer> StageUrlImageAsync(
        Uri imageUri,
        CancellationToken cancellationToken);
}

public sealed class RemoteImageStager : IRemoteImageStager
{
    private const int MaximumRedirects = 3;
    private static readonly TimeSpan DefaultDownloadTimeout =
        TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _downloadTimeout;

    public RemoteImageStager(HttpClient httpClient)
        : this(httpClient, DefaultDownloadTimeout)
    {
    }

    internal RemoteImageStager(HttpClient httpClient, TimeSpan downloadTimeout)
    {
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            downloadTimeout,
            TimeSpan.Zero);
        _downloadTimeout = downloadTimeout;
    }

    public async Task<Tripo.Bridge.StagedImageTransfer> StageUrlImageAsync(
        Uri imageUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageUri);
        ValidateRemoteUri(imageUri);

        string scratchDirectory =
            Tripo.Bridge.BridgePaths.GetImageTransferDirectory();
        string token = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();
        string downloadedPath = Path.Combine(
            scratchDirectory,
            token + ".download");

        try
        {
            string extension = await DownloadDetectingExtensionAsync(
                    imageUri,
                    downloadedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            string stagedSourcePath = downloadedPath + extension;
            File.Move(downloadedPath, stagedSourcePath);
            try
            {
                return await Tripo.Bridge.ImageTransferStore
                    .StageAsync(stagedSourcePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Tripo.Bridge.BridgePaths.TryDelete(stagedSourcePath);
            }
        }
        finally
        {
            Tripo.Bridge.BridgePaths.TryDelete(downloadedPath);
        }
    }

    // Downloads to outputPath while running an IncrementalHash so the first
    // bytes can be inspected afterward to choose a content-proven extension.
    // The extension is derived strictly from magic bytes; a Content-Type
    // header is never trusted on its own.
    private async Task<string> DownloadDetectingExtensionAsync(
        Uri initialUri,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadTimeout);
        try
        {
            return await DownloadWithRedirectsAsync(
                    initialUri,
                    outputPath,
                    deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripoApiException(
                "The remote image download timed out.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw new TripoApiException(
                "The remote image download could not be completed.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TripoApiException(
                "The remote image download could not be completed.",
                innerException: exception);
        }
    }

    private async Task<string> DownloadWithRedirectsAsync(
        Uri initialUri,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateRemoteUri(currentUri);
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            // No Authorization header: the image host is untrusted and must
            // never receive the Tripo API credential.
            using HttpResponseMessage response = await SendDownloadRequestAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects ||
                    response.Headers.Location is null)
                {
                    throw new TripoApiException(
                        "The remote image download exceeded the redirect limit.",
                        response.StatusCode);
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TripoApiException(
                    $"The remote image download returned HTTP " +
                    $"{(int)response.StatusCode}.",
                    response.StatusCode);
            }

            long maximumBytes =
                Tripo.Bridge.BridgeConstants.MaximumImageTransferBytes;
            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new TripoApiException(
                    "The remote image download exceeded the local size limit.",
                    response.StatusCode);
            }

            byte[] prefix = await DownloadBoundedAsync(
                    response,
                    outputPath,
                    maximumBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return DetectExtension(prefix);
        }

        throw new InvalidOperationException(
            "The redirect loop terminated unexpectedly.");
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TripoApiException(
                "The remote image download could not be completed.",
                innerException: exception);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripoApiException(
                "The remote image download timed out.",
                innerException: exception);
        }
    }

    // Streams the response body into a private temp file, enforcing a hard
    // running-byte cap that matches the staging limit. Returns the captured
    // signature prefix so the caller can derive a content-proven extension.
    private static async Task<byte[]> DownloadBoundedAsync(
        HttpResponseMessage response,
        string outputPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream destination = Tripo.Bridge.ImageTransferStore
            .CreatePrivateFile(outputPath);
        byte[] buffer = new byte[64 * 1024];
        byte[] prefix = new byte[12];
        int prefixLength = 0;
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new TripoApiException(
                    "The remote image download exceeded the local size limit.",
                    response.StatusCode);
            }

            int prefixCopy = Math.Min(read, prefix.Length - prefixLength);
            if (prefixCopy > 0)
            {
                buffer.AsSpan(0, prefixCopy)
                    .CopyTo(prefix.AsSpan(prefixLength));
                prefixLength += prefixCopy;
            }

            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return prefix;
    }

    private static string DetectExtension(byte[] prefix)
    {
        ReadOnlySpan<byte> span = prefix;
        ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G',
                                  0x0d, 0x0a, 0x1a, 0x0a];
        if (span.Length >= png.Length && span[..png.Length].SequenceEqual(png))
        {
            return ".png";
        }

        if (span.Length >= 3 &&
            span[0] == 0xff && span[1] == 0xd8 && span[2] == 0xff)
        {
            return ".jpg";
        }

        ReadOnlySpan<byte> riff = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
        ReadOnlySpan<byte> webp = [(byte)'W', (byte)'E', (byte)'B', (byte)'P'];
        if (span.Length >= 12 &&
            span[..riff.Length].SequenceEqual(riff) &&
            span.Slice(8, webp.Length).SequenceEqual(webp))
        {
            return ".webp";
        }

        throw new TripoApiException(
            "The downloaded file is not a supported PNG, JPEG, or WebP image.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static void ValidateRemoteUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new TripoApiException(
                "Remote image downloads must use an absolute HTTPS URL without " +
                "user information.");
        }

        if (IPAddress.TryParse(uri.Host, out _) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.IndexOf('.') < 0)
        {
            throw new TripoApiException(
                "The remote image host is not allowed.");
        }
    }
}

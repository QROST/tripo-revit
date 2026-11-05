using System.Net;
using System.Text;
using Tripo.Bridge;

namespace Tripo.Catalog;

/// <summary>
/// Reads one explicit document. An injected client must disable automatic redirects,
/// cookies, credentials and proxies, and pin DNS through a public-address connector.
/// The default client provides these protections. HTML image URLs are not fetched.
/// </summary>
public sealed class CatalogImporter : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly CatalogImportLimits _limits;

    public CatalogImporter(HttpClient? httpClient = null, CatalogImportLimits? limits = null)
    {
        _limits = limits ?? new CatalogImportLimits();
        if (_limits.MaximumInputBytes is <= 0 or > 100 * 1024 * 1024 ||
            _limits.MaximumPages is <= 0 or > 1000 || _limits.MaximumImages is <= 0 or > 1000 ||
            _limits.MaximumProducts is <= 0 or > 1000 || _limits.MaximumTextCharacters is <= 0 or > 10_000_000 ||
            _limits.MaximumImageBytes is <= 0 or > 25 * 1024 * 1024 ||
            _limits.MaximumImagePixels is <= 0 or > 100_000_000 || _limits.MaximumTotalImageBytes is <= 0 or > 256 * 1024 * 1024 ||
            _limits.MaximumRedirects is < 0 or > 5 || _limits.DownloadTimeout <= TimeSpan.Zero ||
            _limits.DownloadTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(limits), "Catalog import limits are outside supported bounds.");

        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectCallback = Tripo.Mcp.PublicNetworkConnector.ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxResponseHeadersLength = 32,
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<CatalogDocument> ImportFileAsync(
        string path, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CatalogStorage.RejectLinks(path);
        await using FileStream stream = File.OpenRead(path);
        byte[] bytes = await CatalogStorage.ReadBoundedAsync(stream, _limits.MaximumInputBytes, cancellationToken)
            .ConfigureAwait(false);
        return await ImportBytesAsync(bytes, Path.GetFullPath(path), outputDirectory, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CatalogDocument> ImportUrlAsync(
        Uri uri, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ValidatePublicUri(uri);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_limits.DownloadTimeout);
        Uri current = uri;
        for (int redirect = 0; redirect <= _limits.MaximumRedirects; redirect++)
        {
            ValidatePublicUri(current);
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            using HttpResponseMessage response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri effective && effective != current)
                throw new InvalidDataException("The injected HTTP client followed a redirect. Disable automatic redirects.");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect == _limits.MaximumRedirects || response.Headers.Location is not Uri location)
                    throw new InvalidDataException("Catalog URL exceeded its redirect limit or omitted the redirect target.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidatePublicUri(current);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > _limits.MaximumInputBytes)
                throw new InvalidDataException("Catalog response exceeds the input byte limit.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] bytes = await CatalogStorage.ReadBoundedAsync(stream, _limits.MaximumInputBytes, timeout.Token)
                .ConfigureAwait(false);
            return await ImportBytesAsync(bytes, current.AbsoluteUri, outputDirectory, current, timeout.Token)
                .ConfigureAwait(false);
        }
        throw new InvalidDataException("Catalog URL exceeded its redirect limit.");
    }

    internal static void ValidatePublicUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsoluteUri.Length > 8192 ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new InvalidDataException("Only absolute public HTTPS URLs without credentials or fragments are supported.");
        string host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && !Tripo.Mcp.PublicNetworkConnector.IsPublicAddress(address)) ||
            (uri.HostNameType == UriHostNameType.Dns && !host.Contains('.')))
            throw new InvalidDataException("The URL must identify a public network host.");
    }

    private async Task<CatalogDocument> ImportBytesAsync(
        byte[] bytes, string location, string outputDirectory, Uri? baseUri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length == 0) throw new InvalidDataException("The catalog input is empty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string root = Path.GetFullPath(outputDirectory);
        CatalogStorage.RejectLinks(root);
        CatalogDocument document = new();
        CatalogSource source = new() { Location = location, Sha256 = CatalogStorage.Hash(bytes) };
        document.Sources.Add(source);
        if (bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            source.Format = "PDF";
            string imageDirectory = Path.Combine(root, document.Id);
            BridgePaths.EnsurePrivateDirectory(imageDirectory);
            try
            {
                await CatalogPdfParser.ParseAsync(bytes, imageDirectory, document, source, _limits, token).ConfigureAwait(false);
            }
            catch
            {
                Directory.Delete(imageDirectory, recursive: true);
                throw;
            }
        }
        else if (CatalogStorage.InspectImage(bytes) is { } imageInfo)
        {
            ValidateImage(bytes.Length, imageInfo.Width, imageInfo.Height, _limits);
            source.Format = imageInfo.MediaType;
            string imageDirectory = Path.Combine(root, document.Id);
            BridgePaths.EnsurePrivateDirectory(imageDirectory);
            string imagePath = await CatalogStorage.WriteImageAsync(imageDirectory, bytes, imageInfo.MediaType, token).ConfigureAwait(false);
            CatalogSourceReference reference = new() { SourceId = source.Id };
            document.Products.Add(new CatalogProduct
            {
                Title = Path.GetFileNameWithoutExtension(baseUri?.AbsolutePath ?? location),
                Sources = [reference],
                Images = [new CatalogImage { LocalPath = imagePath, SourceUrl = baseUri?.AbsoluteUri,
                    Sha256 = source.Sha256, MediaType = imageInfo.MediaType, PixelWidth = imageInfo.Width,
                    PixelHeight = imageInfo.Height, Source = reference }],
                Warnings = [new CatalogWarning { Code = "image_needs_description", Message = "Add a product identity and verified dimensions; image OCR is not available." }],
            });
        }
        else
        {
            string html = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff', ' ', '\r', '\n', '\t');
            if (!html.StartsWith('<')) throw new InvalidDataException("Supported inputs are PDF, HTML, PNG, JPEG and WebP.");
            source.Format = "HTML";
            await CatalogHtmlParser.ParseAsync(html, baseUri, document, source, _limits, token).ConfigureAwait(false);
        }
        if (document.Products.Count > _limits.MaximumProducts) throw new InvalidDataException("Catalog product count exceeds its limit.");
        foreach (CatalogProduct product in document.Products)
        {
            product.ReviewAccepted = false;
            if (product.Dimensions.Any(dimension => dimension.MinimumMillimeters is null))
                product.Warnings.Add(new CatalogWarning { Code = "dimension_unit_unknown", Message = "Some dimensions have no verified unit or contain an invalid range; enter verified millimeter values." });
            if (product.Dimensions.GroupBy(dimension => dimension.Kind).Any(group => group.Select(value =>
                    (value.MinimumMillimeters, value.MaximumMillimeters)).Distinct().Count() > 1))
                product.Warnings.Add(new CatalogWarning { Code = "dimension_conflict", Message = "Multiple measurements exist for the same dimension. Check variants, units and product versus packaging dimensions." });
        }
        return document;
    }

    internal static void ValidateImage(int byteLength, int width, int height, CatalogImportLimits limits)
    {
        if (byteLength > limits.MaximumImageBytes || width <= 0 || height <= 0 || (long)width * height > limits.MaximumImagePixels)
            throw new InvalidDataException("Image exceeds its byte or pixel limit, or has invalid dimensions.");
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}

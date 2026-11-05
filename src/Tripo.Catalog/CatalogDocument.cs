using System.Text.Json;
using System.Text.Json.Serialization;
using Tripo.Bridge;

namespace Tripo.Catalog;

/// <summary>Editable local evidence, never an authorization to create paid tasks.</summary>
public sealed class CatalogDocument
{
    private const int MaximumSavedBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<CatalogSource> Sources { get; set; } = [];
    public List<CatalogProduct> Products { get; set; } = [];
    public List<CatalogWarning> Warnings { get; set; } = [];
    public List<string> CandidateLinks { get; set; } = [];

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Validate();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (data.Length > MaximumSavedBytes)
        {
            throw new InvalidDataException("Catalog JSON exceeds the 32 MiB limit.");
        }

        string target = Path.GetFullPath(path);
        CatalogStorage.RejectLinks(target);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".catalog-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = ImageTransferStore.CreatePrivateFile(temporary))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            BridgePaths.SetPrivateFileMode(target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<CatalogDocument> LoadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        CatalogStorage.RejectLinks(path);
        await using FileStream stream = File.OpenRead(path);
        byte[] bytes = await CatalogStorage.ReadBoundedAsync(stream, MaximumSavedBytes, cancellationToken)
            .ConfigureAwait(false);
        CatalogDocument result;
        try
        {
            result = JsonSerializer.Deserialize<CatalogDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Catalog JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Catalog JSON contains invalid data or an unsupported value.", exception);
        }
        result.Validate();
        return result;
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Id) || Sources is null || Products is null ||
            Warnings is null || CandidateLinks is null || Products.Count > 1000 || Sources.Count > 1000 ||
            Warnings.Any(warning => !ValidWarning(warning)) || CandidateLinks.Any(link => link is null) ||
            Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Id) ||
                source.Location is null || source.Format is null || source.Sha256 is null || source.PageCount is <= 0 ||
                source.TextBlocks is null || source.TextBlocks.Any(block => block is null || block.Text is null ||
                    block.PageNumber <= 0 || !ValidBounds(block.Bounds))) ||
            Products.Any(product => product is null || string.IsNullOrWhiteSpace(product.Id) ||
                product.Title is null || product.Description is null || product.Images is null || product.Dimensions is null ||
                product.Sources is null || product.Warnings is null || !double.IsFinite(product.RotationDegrees) ||
                product.Sources.Any(reference => !ValidReference(reference)) || product.Warnings.Any(warning => !ValidWarning(warning)) ||
                !ValidDimension(product.ConfirmedWidthMillimeters) || !ValidDimension(product.ConfirmedDepthMillimeters) ||
                !ValidDimension(product.ConfirmedHeightMillimeters) || product.ScaleMode is not ("uniform" or "stretch") ||
                product.Images.Any(image => image is null || string.IsNullOrWhiteSpace(image.Id) || image.Role is null ||
                    image.PixelWidth is <= 0 || image.PixelHeight is <= 0 || !ValidReference(image.Source)) ||
                product.Dimensions.Any(dimension => dimension is null || dimension.RawText is null ||
                    !Enum.IsDefined(dimension.Kind) || !ValidReference(dimension.Source) ||
                    (dimension.MinimumMillimeters is double minimum && (!double.IsFinite(minimum) || minimum <= 0)) ||
                    (dimension.MaximumMillimeters is double maximum && (!double.IsFinite(maximum) || maximum <= 0)) ||
                    dimension.MaximumMillimeters < dimension.MinimumMillimeters)) ||
            Products.Select(product => product.Id).Distinct(StringComparer.Ordinal).Count() != Products.Count)
        {
            throw new InvalidDataException("Catalog JSON has an unsupported schema or invalid product data.");
        }
    }

    private static bool ValidDimension(double? value) => value is null || (double.IsFinite(value.Value) && value.Value > 0);
    private static bool ValidWarning(CatalogWarning? warning) => warning is not null && warning.Code is not null && warning.Message is not null;
    private static bool ValidReference(CatalogSourceReference? reference) => reference is not null && reference.SourceId is not null &&
        reference.PageNumber is not <= 0 && ValidBounds(reference.Bounds);
    private static bool ValidBounds(CatalogBounds? bounds) => bounds is null ||
        (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Bottom) && double.IsFinite(bounds.Width) &&
            double.IsFinite(bounds.Height) && bounds.Width >= 0 && bounds.Height >= 0);
}

public sealed class CatalogSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Location { get; set; } = "";
    public string Format { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset ImportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int? PageCount { get; set; }
    public List<CatalogTextBlock> TextBlocks { get; set; } = [];
}

public sealed class CatalogProduct
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Sku { get; set; }
    public string? Variant { get; set; }
    public string Description { get; set; } = "";
    public bool ReviewAccepted { get; set; }
    public string? GenerationImageId { get; set; }
    public double? ConfirmedWidthMillimeters { get; set; }
    public double? ConfirmedDepthMillimeters { get; set; }
    public double? ConfirmedHeightMillimeters { get; set; }
    public double RotationDegrees { get; set; }
    public string ScaleMode { get; set; } = "uniform";
    public List<CatalogDimension> Dimensions { get; set; } = [];
    public List<CatalogImage> Images { get; set; } = [];
    public List<CatalogSourceReference> Sources { get; set; } = [];
    public List<CatalogWarning> Warnings { get; set; } = [];
}

public enum CatalogDimensionKind { Unknown, Width, Depth, Height, SeatWidth, SeatDepth, SeatHeight, ArmHeight, Diameter }

public sealed class CatalogDimension
{
    public CatalogDimensionKind Kind { get; set; }
    public string RawText { get; set; } = "";
    public double? MinimumMillimeters { get; set; }
    public double? MaximumMillimeters { get; set; }
    public CatalogSourceReference Source { get; set; } = new();
}

public sealed class CatalogImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? LocalPath { get; set; }
    public string? SourceUrl { get; set; }
    public string? MediaType { get; set; }
    public string? Sha256 { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public string Role { get; set; } = "Unclassified";
    public CatalogSourceReference Source { get; set; } = new();
}

public sealed class CatalogSourceReference
{
    public string SourceId { get; set; } = "";
    public int? PageNumber { get; set; }
    public string? Text { get; set; }
    public CatalogBounds? Bounds { get; set; }
}

/// <summary>PDF coordinates are points, with the origin at the bottom left.</summary>
public sealed class CatalogBounds
{
    public double Left { get; set; }
    public double Bottom { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class CatalogTextBlock
{
    public string Text { get; set; } = "";
    public int PageNumber { get; set; }
    public CatalogBounds? Bounds { get; set; }
}

public sealed class CatalogWarning
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class CatalogImportLimits
{
    public int MaximumInputBytes { get; init; } = 25 * 1024 * 1024;
    public int MaximumPages { get; init; } = 100;
    public int MaximumImages { get; init; } = 128;
    public int MaximumProducts { get; init; } = 100;
    public int MaximumTextCharacters { get; init; } = 2_000_000;
    public int MaximumImageBytes { get; init; } = 10_000_000;
    public long MaximumImagePixels { get; init; } = 25_000_000;
    public long MaximumTotalImageBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumRedirects { get; init; } = 3;
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tripo.Bridge;

namespace Tripo.Catalog.Batch;

public sealed record CatalogBatchItem(
    string ProductId,
    string Name,
    StagedImageTransfer Image,
    CatalogFamilyOptions Family);

public sealed record CatalogBatchPlan(
    string BatchId,
    string DocumentSessionId,
    IReadOnlyList<CatalogBatchItem> Items,
    int FaceLimit,
    bool WithMaterials,
    string Model,
    double RowWidthMillimeters = 10_000,
    double GapMillimeters = 500)
{
    public const int MaximumProducts = 100;

    [JsonIgnore]
    public int MaximumPaidTasks => checked(Items.Count * 2);

    [JsonIgnore]
    public string Fingerprint => Hash(Prepare());

    /// <summary>Freeze inputs and place bottom-centred families in deterministic rows.</summary>
    public CatalogBatchPlan Prepare()
    {
        ValidateUuid(BatchId);
        ValidateText(DocumentSessionId, 256);
        if (Items is null || Items.Count is < 1 or > MaximumProducts ||
            FaceLimit is < 500 or > 200_000 || !TripoModelCatalog.IsKnown(Model) ||
            !FinitePositive(RowWidthMillimeters) || RowWidthMillimeters > 1_000_000 ||
            !double.IsFinite(GapMillimeters) || GapMillimeters is < 0 or > 100_000)
        {
            throw new InvalidDataException("catalog_batch_plan_invalid");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        List<CatalogBatchItem> frozen = new(Items.Count);
        double x = 0, y = 0, rowDepth = 0;
        foreach (CatalogBatchItem item in Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateText(item.ProductId, 128);
            ValidateText(item.Name, 128);
            ImageTransferStore.ValidateDescriptor(item.Image);
            CatalogFamilyOptions family = item.Family;
            ArgumentNullException.ThrowIfNull(family);
            CatalogFamilyPreparation.Validate(family);
            if (!identities.Add(item.ProductId) || family.ProductId != item.ProductId)
            {
                throw new InvalidDataException("catalog_batch_family_invalid");
            }

            double angle = family.RotationDegrees * Math.PI / 180;
            // Uniform fitting permits dimension error; reserve its largest accepted footprint.
            double margin = family.ScaleMode == "uniform" ? 1 + family.MaxDimensionErrorRatio : 1;
            double width = margin * (Math.Abs(Math.Cos(angle)) * family.WidthMillimeters +
                Math.Abs(Math.Sin(angle)) * family.DepthMillimeters);
            double depth = margin * (Math.Abs(Math.Sin(angle)) * family.WidthMillimeters +
                Math.Abs(Math.Cos(angle)) * family.DepthMillimeters);
            if (width > RowWidthMillimeters + 0.000001)
            {
                throw new InvalidDataException("catalog_batch_row_too_narrow");
            }

            if (x > 0 && x + width > RowWidthMillimeters + 0.000001)
            {
                x = 0;
                y += rowDepth + GapMillimeters;
                rowDepth = 0;
            }

            family = family with { XMillimeters = x + width / 2, YMillimeters = y + depth / 2 };
            CatalogFamilyPreparation.Validate(family);
            frozen.Add(item with { Family = family });
            x += width + GapMillimeters;
            rowDepth = Math.Max(rowDepth, depth);
        }

        return this with { Items = frozen.AsReadOnly() };
    }

    internal static string Hash(CatalogBatchPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan, BatchJson.Options)))
            .ToLowerInvariant();

    internal static void ValidateUuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed.ToString("D") != value)
        {
            throw new InvalidDataException("catalog_batch_identity_invalid");
        }
    }

    internal static void ValidateText(string value, int maximum, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
        {
            throw new InvalidDataException("catalog_batch_text_invalid");
        }
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
}

public enum CatalogBatchStatus { Ready, Running, Paused, Cancelled, Completed }
public enum CatalogBatchStage { Pending, Generating, Converting, Importing, Completed, Failed }

public sealed record CatalogBatchItemState(
    string ProductId,
    string GenerationOperationId,
    string ConversionOperationId,
    string ImportOperationId,
    CatalogBatchStage Stage = CatalogBatchStage.Pending,
    bool GenerationDispatchAttempted = false,
    string? GenerationTaskId = null,
    bool GenerationSucceeded = false,
    bool ConversionDispatchAttempted = false,
    string? ConversionTaskId = null,
    bool ConversionSucceeded = false,
    bool ImportDispatchAttempted = false,
    string? CreatedId = null,
    int Progress = 0,
    string? MessageCode = null,
    string? GenerationOperationState = null,
    DateTimeOffset? GenerationOperationUpdatedAtUtc = null,
    string? ConversionOperationState = null,
    DateTimeOffset? ConversionOperationUpdatedAtUtc = null);

public sealed record CatalogBatchSnapshot(
    int SchemaVersion,
    string PlanHash,
    CatalogBatchPlan Plan,
    IReadOnlyList<CatalogBatchItemState> Items,
    CatalogBatchStatus Status,
    string? MessageCode,
    DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore]
    public int CompletedCount => Items.Count(item => item.Stage == CatalogBatchStage.Completed);
    [JsonIgnore]
    public int FailedCount => Items.Count(item => item.Stage == CatalogBatchStage.Failed);
}

internal static class BatchJson
{
    internal static JsonSerializerOptions Options { get; } = new(BridgeJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };
}

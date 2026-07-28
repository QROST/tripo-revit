namespace Tripo.Bridge;

/// <summary>
/// Tripo does not expose a public list-models HTTP endpoint (authenticated
/// GET /v2/openapi/models and GET /v3/models both 404). This catalog mirrors
/// the documented <c>model</c> / <c>model_version</c> identifiers from
/// https://platform.tripo3d.ai/docs/generation so the UI can present choices
/// at panel start.
/// </summary>
public static class TripoModelCatalog
{
    public const string DefaultModelId = "v3.1-20260211";
    public const string SourceCurated = "curated-docs";

    public static IReadOnlyList<TripoModelInfo> Models { get; } =
    [
        new(
            "P1-20260311",
            "P1 (low-poly / game topology)",
            "Best topology control for low face counts."),
        new(
            "Turbo-v1.0-20250506",
            "Turbo v1.0",
            "Faster generation snapshot."),
        new(
            "v3.1-20260211",
            "v3.1 (default)",
            "Current default text-to-model snapshot."),
        new(
            "v3.0-20250812",
            "v3.0",
            null),
        new(
            "v2.5-20250123",
            "v2.5",
            null),
        new(
            "v2.0-20240919",
            "v2.0",
            null),
        new(
            "v1.4-20240625",
            "v1.4",
            null),
    ];

    public static HostControlModelCatalogReceipt CreateReceipt() =>
        new(
            DefaultModelId,
            Models,
            SourceCurated,
            "Tripo has no public list-models endpoint; " +
            "choices mirror the documented generation model versions.");

    public static bool IsKnown(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        for (int index = 0; index < Models.Count; index++)
        {
            if (string.Equals(
                    Models[index].Id,
                    modelId,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeOrDefault(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return DefaultModelId;
        }

        string trimmed = modelId.Trim();
        return IsKnown(trimmed) ? trimmed : DefaultModelId;
    }
}

public sealed record TripoModelInfo(
    string Id,
    string DisplayName,
    string? Description)
{
    public override string ToString() => DisplayName;
}

public sealed record HostControlModelCatalogReceipt(
    string DefaultModelId,
    IReadOnlyList<TripoModelInfo> Models,
    string Source,
    string? Note);

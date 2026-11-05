namespace Tripo.Bridge;

/// <summary>Confirmed catalogue dimensions describe a fixed mesh family, not driving geometry parameters.</summary>
public sealed record CatalogFamilyOptions(
    string ProductId,
    string Manufacturer,
    string ModelNumber,
    string Description,
    string SourceReference,
    double WidthMillimeters,
    double DepthMillimeters,
    double HeightMillimeters,
    double XMillimeters = 0,
    double YMillimeters = 0,
    double ZMillimeters = 0,
    double RotationDegrees = 0,
    string Category = "Furniture",
    string ScaleMode = "uniform",
    double MaxDimensionErrorRatio = 0.1);

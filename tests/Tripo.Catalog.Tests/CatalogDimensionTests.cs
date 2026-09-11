using Tripo.Catalog;
using Xunit;

namespace Tripo.Catalog.Tests;

public sealed class CatalogDimensionTests
{
    [Theory]
    [InlineData("Width: 54,50 cm / 21.50 in", CatalogDimensionKind.Width, 545, 545)]
    [InlineData("Seat depth: 46 cm", CatalogDimensionKind.SeatDepth, 460, 460)]
    [InlineData("Height (in): 31½", CatalogDimensionKind.Height, 800.1, 800.1)]
    [InlineData("Depth: 20 3/4″", CatalogDimensionKind.Depth, 527.05, 527.05)]
    [InlineData("Seat height 16\"–20\"", CatalogDimensionKind.SeatHeight, 406.4, 508)]
    [InlineData("Overall height: 780–950 mm", CatalogDimensionKind.Height, 780, 950)]
    [InlineData("Armrest height: 0.64 m", CatalogDimensionKind.ArmHeight, 640, 640)]
    public void ConvertsLabeledMeasurementsAndPreservesRanges(string raw, CatalogDimensionKind kind, double minimum, double maximum)
    {
        CatalogDimension result = Assert.Single(CatalogDimensionParser.Parse(raw));
        Assert.Equal(kind, result.Kind);
        Assert.Equal(minimum, result.MinimumMillimeters!.Value, 5);
        Assert.Equal(maximum, result.MaximumMillimeters!.Value, 5);
    }

    [Theory]
    [InlineData("H79 x W59 x D52")]
    [InlineData("Height: 90–50 cm")]
    [InlineData("Width: 1/0 in")]
    [InlineData("Depth: 20 cm–30 in")]
    public void AmbiguousOrInvalidValuesNeverAcquireAnAssumedUnit(string raw)
    {
        IReadOnlyList<CatalogDimension> results = CatalogDimensionParser.Parse(raw);
        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Null(result.MinimumMillimeters));
    }

    [Fact]
    public void SeparatesSeatDepthFromOverallDepth()
    {
        IReadOnlyList<CatalogDimension> results = CatalogDimensionParser.Parse("Depth: 80 cm; Seat depth: 55 cm");
        Assert.Equal(new[] { CatalogDimensionKind.Depth, CatalogDimensionKind.SeatDepth }, results.Select(value => value.Kind));
    }
}

using System.Text.Json;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class CatalogFamilyPreparationTests
{
    private const string Key = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void FitsTranslatedMeshAtBottomCenterWithoutApplyingInstanceTransform()
    {
        CatalogFamilyOptions options = Options() with { XMillimeters = 4500, YMillimeters = -2500, ZMillimeters = 300, RotationDegrees = 90 };
        PreparedMesh result = CatalogFamilyPreparation.Fit(Box(), options);
        Assert.Equal(new MeshPoint3(-1, -0.5, 0), result.VerticesInMeters[0]);
        Assert.Equal(new MeshPoint3(1, 0.5, 0.5), result.VerticesInMeters[6]);
        Assert.Equal(Box().Triangles, result.Triangles);
        Assert.Same(BoxMaterial, result.Materials[0]);
        Assert.Equal(2, result.RejectedTriangleCount);
    }

    [Fact]
    public void UniformModeRefusesMismatchedProportionsInsteadOfStretching()
    {
        BridgeCallException error = Assert.Throws<BridgeCallException>(() =>
            CatalogFamilyPreparation.Fit(Box(), Options() with { DepthMillimeters = 600 }));
        Assert.Equal("catalog_dimension_mismatch", error.Code);
    }

    [Fact]
    public void ExplicitStretchMatchesEachDimension()
    {
        PreparedMesh result = CatalogFamilyPreparation.Fit(Box(), Options() with { DepthMillimeters = 600, HeightMillimeters = 800, ScaleMode = "stretch" });
        Assert.Equal(new MeshPoint3(-1, -0.3, 0), result.VerticesInMeters[0]);
        Assert.Equal(new MeshPoint3(1, 0.3, 0.8), result.VerticesInMeters[6]);
    }

    [Fact]
    public void UniformToleranceIsMeasuredRelativeToEachConfirmedDimension()
    {
        CatalogFamilyOptions options = Options() with { DepthMillimeters = 950, MaxDimensionErrorRatio = 0.06 };
        PreparedMesh fitted = CatalogFamilyPreparation.Fit(Box(), options);
        Assert.Equal(1, fitted.VerticesInMeters.Max(p => p.Y) - fitted.VerticesInMeters.Min(p => p.Y));
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Fit(Box(), options with { MaxDimensionErrorRatio = 0.05 }));
    }

    [Fact]
    public void IgnoresUnusedOutliersAndPreservesUvAndMaterialCornerAssociation()
    {
        PreparedMesh box = Box();
        MeshPoint3[] positions = [new(1000, -1000, 9000), .. box.VerticesInMeters];
        MeshPoint2[] uvs = positions.Select((_, i) => new MeshPoint2(i / 10.0, i / 20.0)).ToArray();
        PreparedMesh input = box with
        {
            VerticesInMeters = positions,
            Uvs = uvs,
            Triangles = box.Triangles.Select(t => new MeshTriangle(t.A + 1, t.B + 1, t.C + 1, t.MaterialSlot)).ToArray(),
        };
        PreparedMesh result = CatalogFamilyPreparation.Fit(input, Options());
        Assert.Equal(8, result.VerticesInMeters.Count);
        Assert.Equal(uvs.Skip(1), result.Uvs);
        Assert.Equal(box.Triangles, result.Triangles);
        Assert.Equal(new MeshPoint3(-1, -0.5, 0), result.VerticesInMeters[0]);
    }

    [Fact]
    public void RejectsPlanarMeshAndNonfiniteGeometry()
    {
        PreparedMesh box = Box();
        Assert.Equal("catalog_mesh_invalid", Assert.Throws<BridgeCallException>(() =>
            CatalogFamilyPreparation.Fit(box with { VerticesInMeters = box.VerticesInMeters.Select(p => p with { Z = 0 }).ToArray() }, Options())).Code);
        MeshPoint3[] vertices = box.VerticesInMeters.ToArray();
        vertices[0] = vertices[0] with { X = double.PositiveInfinity };
        Assert.Equal("catalog_mesh_invalid", Assert.Throws<BridgeCallException>(() =>
            CatalogFamilyPreparation.Fit(box with { VerticesInMeters = vertices }, Options())).Code);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(100001)]
    public void RejectsInvalidDimensions(double value)
    {
        foreach (CatalogFamilyOptions invalid in new[]
        {
            Options() with { WidthMillimeters = value },
            Options() with { DepthMillimeters = value },
            Options() with { HeightMillimeters = value },
        })
        {
            Assert.Equal("catalog_options_invalid", Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(invalid)).Code);
        }
    }

    [Fact]
    public void RejectsUnsafePlacementAndAmbiguousPolicy()
    {
        CatalogFamilyOptions[] invalid =
        [
            Options() with { XMillimeters = double.NaN },
            Options() with { YMillimeters = 10_000_001 },
            Options() with { ZMillimeters = double.NegativeInfinity },
            Options() with { RotationDegrees = double.NaN },
            Options() with { RotationDegrees = 361 },
            Options() with { MaxDimensionErrorRatio = -0.1 },
            Options() with { MaxDimensionErrorRatio = 0.26 },
            Options() with { MaxDimensionErrorRatio = double.NaN },
            Options() with { ScaleMode = "auto" },
            Options() with { Category = "GenericModel" },
            Options() with { ProductId = " " },
            Options() with { Manufacturer = null! },
            Options() with { SourceReference = "bad\0source" },
        ];
        foreach (CatalogFamilyOptions options in invalid)
        {
            Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(options));
        }
    }

    [Fact]
    public void CatalogCannotBeSilentlyUsedAsMeshOrInteractivePlacement()
    {
        ImportMeshRequest valid = Request() with { Catalog = Options() };
        CatalogFamilyPreparation.Validate(valid);
        CatalogFamilyPreparation.Validate(valid with { RequireExisting = true });
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(valid with { ImportMode = "mesh" }));
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(valid with { PlacementMode = "pick" }));
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(valid with { AssociateLevel = true }));
        CatalogFamilyPreparation.Validate(valid with { Catalog = null, ImportMode = "mesh", PlacementMode = "pick" });
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.Validate(valid with { Catalog = null, RequireExisting = true }));
    }

    [Fact]
    public void NamesAreReadableSafeAndUseFullImportIdentity()
    {
        string name = CatalogFamilyPreparation.FamilyName("厂家 /Chair:*? \"A\"...", Key);
        Assert.StartsWith("厂家 Chair A_", name);
        Assert.EndsWith("11111111222233334444555555555555", name);
        string other = CatalogFamilyPreparation.FamilyName("厂家 /Chair:*? \"A\"...", "11111111-2222-3333-4444-555555555556");
        Assert.NotEqual(name, other);
        Assert.Equal("M1_2000x1000x500mm", CatalogFamilyPreparation.TypeName(Options()));
        Assert.True(CatalogFamilyPreparation.FamilyName(new string('长', 200), Key).Length <= 97);
        Assert.Throws<BridgeCallException>(() => CatalogFamilyPreparation.FamilyName("Chair", "../../escape"));
    }

    [Fact]
    public void OldRequestsKeepTheirSerializedShapeAndCatalogRoundTrips()
    {
        string legacy = JsonSerializer.Serialize(Request(), BridgeJson.Options);
        Assert.DoesNotContain("catalog", legacy);
        Assert.DoesNotContain("requireExisting", legacy);
        Assert.Null(JsonSerializer.Deserialize<ImportMeshRequest>(legacy, BridgeJson.Options)!.Catalog);
        ImportMeshRequest request = Request() with { Catalog = Options() };
        string json = JsonSerializer.Serialize(request, BridgeJson.Options);
        Assert.Equal(request.Catalog, JsonSerializer.Deserialize<ImportMeshRequest>(json, BridgeJson.Options)!.Catalog);
        Assert.NotEqual(json, JsonSerializer.Serialize(request with { Catalog = request.Catalog with { XMillimeters = 1 } }, BridgeJson.Options));
    }

    [Fact]
    public void FingerprintPreservesLegacyImportsAndExcludesRecoveryPolicyButIncludesCatalogIntent()
    {
        // Digest of the pre-catalogue canonical wire payload, captured before
        // adding either optional contract field.
        Assert.Equal("db189e7fa51d7c4ad5e57d2f92d1fb3a62fe1d6529b7c3bb6ffa1d3d96c96ff2", CatalogImportIdentity.Fingerprint(Request()));
        ImportMeshRequest request = Request() with { Catalog = Options() };
        string fingerprint = CatalogImportIdentity.Fingerprint(request);
        Assert.Equal(fingerprint, CatalogImportIdentity.Fingerprint(request with { RequireExisting = true, DocumentSessionId = Guid.NewGuid().ToString("D") }));
        foreach (CatalogFamilyOptions changed in new[]
        {
            Options() with { XMillimeters = 1 },
            Options() with { RotationDegrees = 90 },
            Options() with { Manufacturer = "Other" },
            Options() with { ProductId = "Other" },
            Options() with { WidthMillimeters = 1900 },
            Options() with { ScaleMode = "stretch" },
        })
        {
            Assert.NotEqual(fingerprint, CatalogImportIdentity.Fingerprint(request with { Catalog = changed }));
        }
    }

    [Fact]
    public void OfflineProbeIdentityBindsProductAndPlacementWithoutRequiringArtifactBytes()
    {
        ImportMeshRequest original = Request() with { Catalog = Options(), Entries = [new("model.obj", new string('a', 64), 100)] };
        string catalogIdentity = CatalogImportIdentity.CatalogFingerprint(original);
        ImportMeshRequest probe = original with
        {
            DocumentSessionId = Guid.NewGuid().ToString("D"),
            BundleId = string.Empty,
            ObjEntry = string.Empty,
            Entries = [],
            RequireExisting = true,
        };
        CatalogFamilyPreparation.Validate(probe);
        Assert.Equal(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe));
        Assert.NotEqual(CatalogImportIdentity.Fingerprint(original), CatalogImportIdentity.Fingerprint(probe));
        Assert.NotEqual(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe with { Name = "Different chair" }));
        Assert.NotEqual(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe with { IdempotencyKey = Guid.NewGuid().ToString("D") }));
        Assert.NotEqual(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe with { Catalog = Options() with { RotationDegrees = 90 } }));
        Assert.NotEqual(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe with { Catalog = Options() with { ModelNumber = "M2" } }));
        Assert.NotEqual(catalogIdentity, CatalogImportIdentity.CatalogFingerprint(probe with { Catalog = Options() with { WidthMillimeters = 2500 } }));
    }

    [Fact]
    public void OfflineProbeStillRejectsInvalidIdentityAndNameWithoutAnArtifactLoader()
    {
        ImportMeshRequest probe = Request() with { Catalog = Options(), BundleId = "", ObjEntry = "", Entries = [], RequireExisting = true };
        Assert.Equal("document_session_invalid", Assert.Throws<BridgeCallException>(() =>
            CatalogFamilyPreparation.Validate(probe with { DocumentSessionId = "not-a-session" })).Code);
        foreach (string importId in new[] { "../../escape", "AAAAAAAA-2222-3333-4444-555555555555", Key.Replace("-", "") })
        {
            Assert.Equal("idempotency_key_invalid", Assert.Throws<BridgeCallException>(() =>
                CatalogFamilyPreparation.Validate(probe with { IdempotencyKey = importId })).Code);
        }

        foreach (string name in new[] { " ", new string('a', 129) })
        {
            Assert.Equal("name_invalid", Assert.Throws<BridgeCallException>(() =>
                CatalogFamilyPreparation.Validate(probe with { Name = name })).Code);
        }
    }

    private static CatalogFamilyOptions Options() => new("product-1", "Maker", "M1", "Chair", "catalog.pdf p2", 2000, 1000, 500);

    private static ImportMeshRequest Request() => new(Key, Key, "model.obj", null, [], "m", "Z", "right", "Chair", Key, "family", false);

    private static readonly PreparedMaterial BoxMaterial = new("fabric", null, null);

    private static PreparedMesh Box() => new(
        [new(10, 20, 30), new(14, 20, 30), new(14, 22, 30), new(10, 22, 30),
         new(10, 20, 31), new(14, 20, 31), new(14, 22, 31), new(10, 22, 31)],
        [],
        [new(0, 1, 2, 0), new(0, 2, 3, 0), new(4, 6, 5, 0), new(4, 7, 6, 0),
         new(0, 4, 5, 0), new(0, 5, 1, 0), new(1, 5, 6, 0), new(1, 6, 2, 0),
         new(2, 6, 7, 0), new(2, 7, 3, 0), new(3, 7, 4, 0), new(3, 4, 0, 0)],
        [BoxMaterial], 2);
}

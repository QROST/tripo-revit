using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Tripo.Bridge;

namespace Tripo.Revit;

/// <summary>Catalogue metadata belongs to the same transaction as its geometry or placed instance.</summary>
internal static class CatalogFamilyAuthoring
{
    private static readonly Guid ArtifactSchemaId = new("503f1171-87eb-48f4-a56f-4c6e836a7024");

    public static void Configure(Document document, CatalogFamilyOptions options)
    {
        Family family = document.OwnerFamily;
        if (family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
        {
            throw new BridgeCallException("catalog_template_unsupported",
                "Catalogue furniture requires an unhosted, non-work-plane-based Generic Model family template.");
        }

        try
        {
            family.FamilyCategory = Category.GetCategory(document, BuiltInCategory.OST_Furniture);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException exception)
        {
            throw new BridgeCallException("catalog_template_unsupported",
                "The selected family template cannot use the Furniture category.", exception);
        }

        FamilyManager manager = document.FamilyManager;
        string typeName = CatalogFamilyPreparation.TypeName(options);
        FamilyType? existing = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == typeName);
        FamilyType catalogType = existing ?? manager.NewType(typeName);
        // A catalogue RFA represents one confirmed variant. Retaining a blank
        // template type would expose the same mesh with unpopulated metadata
        // when the user later loads the saved RFA outside this plugin.
        foreach (FamilyType templateType in manager.Types.Cast<FamilyType>().Where(t => t.Name != typeName).ToArray())
        {
            manager.CurrentType = templateType;
            manager.DeleteCurrentType();
        }

        manager.CurrentType = catalogType;

        SetIdentity(manager, BuiltInParameter.ALL_MODEL_MANUFACTURER, options.Manufacturer);
        SetIdentity(manager, BuiltInParameter.ALL_MODEL_MODEL, options.ModelNumber);
        SetIdentity(manager, BuiltInParameter.ALL_MODEL_DESCRIPTION, options.Description);
        SetIdentity(manager, BuiltInParameter.ALL_MODEL_URL, options.SourceReference);
        SetText(manager, "Tripo Product Id", options.ProductId);
        SetText(manager, "Tripo Source Reference", options.SourceReference);
        SetText(manager, "Tripo Geometry", "Fixed generated mesh; nominal dimensions do not drive geometry");
        SetText(manager, "Tripo Scale Mode", options.ScaleMode);
        SetLength(manager, "Tripo Nominal Width", options.WidthMillimeters);
        SetLength(manager, "Tripo Nominal Depth", options.DepthMillimeters);
        SetLength(manager, "Tripo Nominal Height", options.HeightMillimeters);
    }

    public static XYZ PlacementPoint(CatalogFamilyOptions options)
    {
        XYZ point = new(
            UnitUtils.ConvertToInternalUnits(options.XMillimeters, UnitTypeId.Millimeters),
            UnitUtils.ConvertToInternalUnits(options.YMillimeters, UnitTypeId.Millimeters),
            UnitUtils.ConvertToInternalUnits(options.ZMillimeters, UnitTypeId.Millimeters));
        if (!XYZ.IsWithinLengthLimits(point))
        {
            throw new BridgeCallException("revit_design_limit", "Catalogue placement exceeds Revit's design-coordinate limits.");
        }

        return point;
    }

    public static void Rotate(Document document, FamilyInstance instance, XYZ point, CatalogFamilyOptions options)
    {
        if (options.RotationDegrees != 0)
        {
            ElementTransformUtils.RotateElement(document, instance.Id,
                Line.CreateBound(point, point + XYZ.BasisZ), options.RotationDegrees * Math.PI / 180);
        }
    }

    public static void RecordArtifact(Element element, ImportMeshRequest request, string fingerprint, string path, PreparedMesh prepared)
    {
        Entity entity = new(ArtifactSchema());
        entity.Set("Fingerprint", fingerprint);
        entity.Set("CatalogIdentityHash", CatalogImportIdentity.CatalogFingerprint(request));
        entity.Set("SavedFamilyPath", path);
        entity.Set("VertexCount", prepared.VerticesInMeters.Count);
        entity.Set("TriangleCount", prepared.Triangles.Count);
        entity.Set("RejectedTriangleCount", prepared.RejectedTriangleCount);
        element.SetEntity(entity);
    }

    public static HostImportReceipt ExistingReceipt(FamilyInstance instance, ImportMeshRequest request,
        string fingerprint, int materialCount, int textureCount)
    {
        string path = ArtifactPath(instance, fingerprint, request);
        FamilySymbol symbol = instance.Symbol;
        string familyPath = ArtifactPath(symbol.Family, fingerprint, request);
        if (symbol.Name != CatalogFamilyPreparation.TypeName(request.Catalog!) ||
            symbol.Family.Name != CatalogFamilyPreparation.FamilyName(request.Name, request.IdempotencyKey) ||
            symbol.Family.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_Furniture) ||
            !string.Equals(path, familyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeCallException("idempotency_conflict",
                "The catalogue instance type, category or artifact record has changed since import.");
        }

        Entity entity = instance.GetEntity(ArtifactSchema());
        return new HostImportReceipt("revit", request.DocumentSessionId, request.IdempotencyKey,
            instance.UniqueId, entity.Get<int>("VertexCount"), entity.Get<int>("TriangleCount"),
            entity.Get<int>("RejectedTriangleCount"), "already_exists", request.ImportMode,
            materialCount, textureCount, File.Exists(path) ? path : null, "Already present; placement unchanged");
    }

    public static string ArtifactPath(Element element, string fingerprint, ImportMeshRequest request)
    {
        Entity entity = element.GetEntity(ArtifactSchema());
        if (!entity.IsValid() || entity.Get<string>("Fingerprint") != fingerprint ||
            entity.Get<string>("CatalogIdentityHash") != CatalogImportIdentity.CatalogFingerprint(request))
        {
            throw new BridgeCallException("idempotency_conflict",
                "The existing catalogue family does not match this confirmed product import.");
        }

        return CatalogFamilyArtifact.ValidateRecord(entity.Get<string>("Fingerprint"),
            entity.Get<string>("SavedFamilyPath"), fingerprint,
            CatalogFamilyPreparation.FamilyName(request.Name, request.IdempotencyKey) + ".rfa", BridgePaths.GetFamiliesDirectory());
    }

    private static Schema ArtifactSchema()
    {
        Schema? existing = Schema.Lookup(ArtifactSchemaId);
        if (existing is not null)
        {
            return existing;
        }

        SchemaBuilder builder = new(ArtifactSchemaId);
        builder.SetSchemaName("TripoCatalogFamilyArtifact");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Vendor);
        builder.SetVendorId("QROST");
        builder.AddSimpleField("Fingerprint", typeof(string));
        builder.AddSimpleField("CatalogIdentityHash", typeof(string));
        builder.AddSimpleField("SavedFamilyPath", typeof(string));
        builder.AddSimpleField("VertexCount", typeof(int));
        builder.AddSimpleField("TriangleCount", typeof(int));
        builder.AddSimpleField("RejectedTriangleCount", typeof(int));
        return builder.Finish();
    }

    private static void SetIdentity(FamilyManager manager, BuiltInParameter builtIn, string value)
    {
        FamilyParameter? parameter = manager.get_Parameter(builtIn);
        if (parameter is null || parameter.IsReadOnly)
        {
            throw new BridgeCallException("catalog_template_unsupported", "The family template is missing writable identity parameters.");
        }

        manager.Set(parameter, value);
    }

    private static void SetText(FamilyManager manager, string name, string value)
    {
        FamilyParameter parameter = manager.get_Parameter(name) ?? manager.AddParameter(
            name, GroupTypeId.IdentityData, SpecTypeId.String.Text, isInstance: false);
        manager.Set(parameter, value);
    }

    private static void SetLength(FamilyManager manager, string name, double millimeters)
    {
        FamilyParameter parameter = manager.get_Parameter(name) ?? manager.AddParameter(
            name, GroupTypeId.Geometry, SpecTypeId.Length, isInstance: false);
        manager.Set(parameter, UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters));
        manager.SetDescription(parameter, "Confirmed catalogue dimension; informational only and does not resize the generated mesh.");
    }
}

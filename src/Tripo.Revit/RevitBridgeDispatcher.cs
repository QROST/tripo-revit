using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;

namespace Tripo.Revit;

internal sealed class RevitBridgeDispatcher : Tripo.Bridge.IHostBridgeDispatcher
{
    private const string ApplicationIdentifier = "QROST.TripoMCP.Revit";
    private const string FamilyTemplateEnvironmentVariable = "TRIPO_REVIT_FAMILY_TEMPLATE";

    // Extensible Storage identity for family-mode idempotency. The GUID is a
    // fixed constant: it is the durable schema identity across sessions. The
    // schema shape changed (added MaterialCount/TextureCount), so this is a NEW
    // fixed GUID; nothing has shipped, so old-GUID entities are simply never
    // found by Schema.Lookup.
    private const string IdempotencySchemaGuid = "15b33ac5-5477-4af0-86af-6d316308b305";
    private const string IdempotencySchemaName = "TripoMcpImportIdempotency";
    private const string IdempotencySchemaVendorId = "QROST";
    private const string IdempotencyKeyField = "IdempotencyKey";
    private const string RequestFingerprintField = "RequestFingerprint";
    private const string MaterialCountField = "MaterialCount";
    private const string TextureCountField = "TextureCount";

    private static readonly IReadOnlyList<string> Capabilities =
    [
        Tripo.Bridge.BridgeConstants.ContextMethod,
        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
    ];

    // Revit-illegal element-name characters, stripped from user-supplied names.
    private static readonly char[] IllegalRevitNameChars =
    [
        '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~',
    ];

    // Probed in order under Application.FamilyTemplatePath; first existing wins.
    private static readonly string[] FamilyTemplateCandidates =
    [
        "Metric Generic Model.rft",
        "Generic Model.rft",
        "英制常规模型.rft",
        "公制常规模型.rft",
    ];

    private readonly RevitExternalEventPump _eventPump;
    private readonly RevitDocumentSessions _documentSessions;

    public RevitBridgeDispatcher(
        RevitExternalEventPump eventPump,
        RevitDocumentSessions documentSessions)
    {
        _eventPump = eventPump ?? throw new ArgumentNullException(nameof(eventPump));
        _documentSessions =
            documentSessions ?? throw new ArgumentNullException(nameof(documentSessions));
    }

    public Task<object> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken) =>
        method switch
        {
            Tripo.Bridge.BridgeConstants.ContextMethod =>
                GetContextAsync(cancellationToken),
            Tripo.Bridge.BridgeConstants.ImportMeshMethod =>
                ImportAsync(payload, cancellationToken),
            _ => throw new Tripo.Bridge.BridgeCallException(
                "method_not_allowed",
                "The requested Revit method is not allowed."),
        };

    private async Task<object> GetContextAsync(
        CancellationToken cancellationToken) =>
        await _eventPump.EnqueueAsync<object>(
                application => CreateContextReceipt(
                    application,
                    GetActiveDocument(application)),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<object> ImportAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.ImportMeshRequest request;
        try
        {
            request = payload.Deserialize<Tripo.Bridge.ImportMeshRequest>(
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new JsonException("The import payload was null.");
        }
        catch (JsonException exception)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "invalid_request",
                "The Revit import payload was invalid.",
                exception);
        }

        // Mode gate runs before any mutation: Revit supports mesh and family;
        // instance (and anything else) is rejected without touching the model.
        bool isMesh = string.Equals(request.ImportMode, "mesh", StringComparison.Ordinal);
        bool isFamily = string.Equals(request.ImportMode, "family", StringComparison.Ordinal);
        if (!isMesh && !isFamily)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "import_mode_unsupported",
                "This Revit build supports the mesh and family import modes.");
        }

        await _eventPump.EnqueueAsync(
                application =>
                {
                    Document document = GetActiveDocument(application);
                    EnsureWritableMatchingDocument(document, request.DocumentSessionId);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        Tripo.Bridge.PreparedMesh prepared =
            await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        return await _eventPump.EnqueueAsync<object>(
                application => isFamily
                    ? ImportFamilyInApiContext(application, request, prepared)
                    : ImportMeshInApiContext(
                        application,
                        request,
                        prepared),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Tripo.Bridge.HostImportReceipt ImportMeshInApiContext(
        UIApplication application,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared)
    {
        Document document = GetActiveDocument(application);
        EnsureWritableMatchingDocument(document, request.DocumentSessionId);
        string fingerprint = CreateRequestFingerprint(request);
        string expectedDataId = request.IdempotencyKey + ":" + fingerprint;
        DirectShape[] existing =
            CollectDirectShapesByKeyPrefix(document, request.IdempotencyKey);
        if (existing.Length > 1 ||
            (existing.Length == 1 &&
             !string.Equals(
                 existing[0].ApplicationDataId,
                 expectedDataId,
                 StringComparison.Ordinal)))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The Revit idempotency key is already bound to a different import.");
        }

        if (existing.Length == 1)
        {
            // A DirectShape persists no counts, so the actual materials/textures
            // on the pre-existing shape are unknowable here. Report zero rather
            // than re-deriving intent from the incoming request.
            return CreateReceipt(
                request,
                existing[0].UniqueId,
                prepared,
                "already_exists",
                0,
                0,
                null,
                "Already present; placement unchanged");
        }

        // Cross-mode idempotency: a prior replay of this key may have landed in
        // family mode, whose Extensible Storage entities are invisible to the
        // DirectShape scan above. Probe that storage before creating a second
        // object under the same key.
        Schema idempotencySchema = GetOrCreateIdempotencySchema();
        if (FindMatchingInstances(document, idempotencySchema, request.IdempotencyKey)
                .Count > 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The Revit idempotency key is already bound to a family-mode import.");
        }

        using Transaction transaction = new(document, "Import Tripo mesh");
        FailureHandlingOptions failureOptions = transaction
            .GetFailureHandlingOptions()
            .SetFailuresPreprocessor(new RevitRollbackFailuresPreprocessor())
            .SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(failureOptions);
        TransactionStatus startStatus = transaction.Start();
        if (startStatus != TransactionStatus.Started)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "transaction_start_failed",
                $"Revit could not start the import transaction ({startStatus}).");
        }

        DirectShape directShape;
        int materialCount = 0;
        int textureCount = 0;
        try
        {
            IReadOnlyList<ElementId> slotToMaterialId;
            if (request.ApplyMaterials && prepared.Materials.Count > 0)
            {
                slotToMaterialId = CreateMaterials(
                    document,
                    prepared,
                    out materialCount,
                    out textureCount);
            }
            else
            {
                slotToMaterialId = [];
            }

            IList<GeometryObject> geometry = BuildGeometry(prepared, slotToMaterialId);
            directShape = DirectShape.CreateElement(
                document,
                new ElementId(BuiltInCategory.OST_GenericModel));
            directShape.ApplicationId = ApplicationIdentifier;
            directShape.ApplicationDataId = expectedDataId;
            directShape.Name = SanitizeRevitName(request.Name, 64, "TripoModel");
            directShape.SetShape(geometry);

            TransactionStatus commitStatus = transaction.Commit();
            if (commitStatus != TransactionStatus.Committed)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "transaction_not_committed",
                    $"Revit did not commit the import transaction ({commitStatus}).");
            }
        }
        catch (Exception exception)
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
            {
                TransactionStatus rollbackStatus = transaction.RollBack();
                if (rollbackStatus != TransactionStatus.RolledBack)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "rollback_failed",
                        "Revit could not verify rollback of the failed import.",
                        exception);
                }
            }

            throw;
        }

        string? placementNote = ApplyPlacementAfterCommit(
            application,
            document,
            directShape.Id,
            request,
            isFamilyInstance: false);

        return CreateReceipt(
            request,
            directShape.UniqueId,
            prepared,
            "committed",
            materialCount,
            textureCount,
            null,
            placementNote);
    }

    private Tripo.Bridge.HostImportReceipt ImportFamilyInApiContext(
        UIApplication application,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared)
    {
        Document document = GetActiveDocument(application);
        EnsureWritableMatchingDocument(document, request.DocumentSessionId);
        string fingerprint = CreateRequestFingerprint(request);
        Schema schema = GetOrCreateIdempotencySchema();
        string rfaPath = Path.Combine(
            Tripo.Bridge.BridgePaths.GetFamiliesDirectory(),
            request.IdempotencyKey + ".rfa");

        // Idempotency lookup happens before anything is built or saved.
        List<FamilyInstance> matches = FindMatchingInstances(
            document,
            schema,
            request.IdempotencyKey);
        if (matches.Count > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "Multiple Revit family instances already carry this idempotency key.");
        }

        if (matches.Count == 1)
        {
            Entity storedEntity = matches[0].GetEntity(schema);
            string storedFingerprint = storedEntity.Get<string>(RequestFingerprintField);
            if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "idempotency_conflict",
                    "The Revit idempotency key is already bound to a different import.");
            }

            string? persistedPath = File.Exists(rfaPath) ? rfaPath : null;
            return CreateReceipt(
                request,
                matches[0].UniqueId,
                prepared,
                "already_exists",
                storedEntity.Get<int>(MaterialCountField),
                storedEntity.Get<int>(TextureCountField),
                persistedPath,
                "Already present; placement unchanged");
        }

        // Cross-mode idempotency: a prior replay of this key may have landed in
        // mesh mode, whose DirectShape carries the key on ApplicationDataId and
        // is invisible to the family-instance scan above. Probe that storage
        // before creating a second object under the same key.
        if (CollectDirectShapesByKeyPrefix(document, request.IdempotencyKey).Length > 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The Revit idempotency key is already bound to a mesh-mode import.");
        }

        string templatePath = ResolveFamilyTemplate(application);
        int materialCount;
        int textureCount;
        Document familyDocument = application.Application.NewFamilyDocument(templatePath);
        try
        {
            BuildFamilyDocument(
                familyDocument,
                request,
                prepared,
                rfaPath,
                out materialCount,
                out textureCount);
        }
        catch (Exception exception)
        {
            // family_close_failed surfaces only AFTER SaveAs completed: the .rfa
            // is a fully saved, valid file and must survive the close failure
            // rather than be deleted as a partial artifact. Every other failure
            // path leaves at most a partial .rfa, which is cleaned up here.
            if (exception is not Tripo.Bridge.BridgeCallException
                {
                    Code: "family_close_failed",
                })
            {
                Tripo.Bridge.BridgePaths.TryDelete(rfaPath);
            }

            throw;
        }

        try
        {
            return LoadFamilyIntoHost(
                application,
                document,
                schema,
                request,
                prepared,
                rfaPath,
                fingerprint,
                materialCount,
                textureCount);
        }
        catch
        {
            Tripo.Bridge.BridgePaths.TryDelete(rfaPath);
            throw;
        }
    }

    // Builds geometry (and materials) inside one family transaction, saves the
    // .rfa, and closes the family document. On every failure path up to and
    // including SaveAs, the caller deletes the partial .rfa. One residual: if
    // the post-save Close returns false, the background family document may stay
    // open and the fully saved .rfa is deliberately preserved (family_close_failed)
    // because the save already completed and the file on disk is valid.
    private static void BuildFamilyDocument(
        Document familyDocument,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared,
        string rfaPath,
        out int materialCount,
        out int textureCount)
    {
        materialCount = 0;
        textureCount = 0;
        try
        {
            using (Transaction familyTransaction =
                new(familyDocument, "Create Tripo family geometry"))
            {
                FailureHandlingOptions failureOptions = familyTransaction
                    .GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(new RevitRollbackFailuresPreprocessor())
                    .SetClearAfterRollback(true);
                familyTransaction.SetFailureHandlingOptions(failureOptions);
                TransactionStatus startStatus = familyTransaction.Start();
                if (startStatus != TransactionStatus.Started)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "transaction_start_failed",
                        $"Revit could not start the family geometry transaction ({startStatus}).");
                }

                try
                {
                    IReadOnlyList<ElementId> slotToMaterialId;
                    if (request.ApplyMaterials && prepared.Materials.Count > 0)
                    {
                        slotToMaterialId = CreateMaterials(
                            familyDocument,
                            prepared,
                            out materialCount,
                            out textureCount);
                    }
                    else
                    {
                        slotToMaterialId = [];
                    }

                    IList<GeometryObject> geometry = BuildGeometry(prepared, slotToMaterialId);
                    DirectShape directShape = DirectShape.CreateElement(
                        familyDocument,
                        new ElementId(BuiltInCategory.OST_GenericModel));
                    directShape.SetShape(geometry);

                    TransactionStatus commitStatus = familyTransaction.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        throw new Tripo.Bridge.BridgeCallException(
                            "transaction_not_committed",
                            $"Revit did not commit the family geometry transaction ({commitStatus}).");
                    }
                }
                catch
                {
                    if (familyTransaction.GetStatus() == TransactionStatus.Started)
                    {
                        familyTransaction.RollBack();
                    }

                    throw;
                }
            }

            SaveAsOptions saveOptions = new()
            {
                OverwriteExistingFile = true,
            };
            familyDocument.SaveAs(rfaPath, saveOptions);
        }
        catch
        {
            TryCloseFamilyDocument(familyDocument);
            throw;
        }

        if (!familyDocument.Close(false))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "family_close_failed",
                "The Tripo family was saved to disk, but Revit could not close the " +
                "background family document; the saved .rfa is preserved.");
        }
    }

    private static Tripo.Bridge.HostImportReceipt LoadFamilyIntoHost(
        UIApplication application,
        Document document,
        Schema schema,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared,
        string rfaPath,
        string fingerprint,
        int materialCount,
        int textureCount)
    {
        string familyName =
            SanitizeRevitName(request.Name, 64, "TripoModel") +
            "_" +
            request.IdempotencyKey[..8];

        using Transaction transaction = new(document, "Import Tripo family");
        FailureHandlingOptions failureOptions = transaction
            .GetFailureHandlingOptions()
            .SetFailuresPreprocessor(new RevitRollbackFailuresPreprocessor())
            .SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(failureOptions);
        TransactionStatus startStatus = transaction.Start();
        if (startStatus != TransactionStatus.Started)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "transaction_start_failed",
                $"Revit could not start the family load transaction ({startStatus}).");
        }

        string createdUniqueId;
        ElementId createdId;
        try
        {
            Family family;
            List<Family> existingFamilies = CollectFamiliesByName(document, familyName);
            if (existingFamilies.Count == 1)
            {
                // A prior retry already loaded this family, but its instance was
                // later deleted. Reuse the loaded family instead of re-loading:
                // LoadFamily followed by the rename would collide with this
                // surviving duplicate name and throw.
                family = existingFamilies[0];
                if (family.GetFamilySymbolIds().FirstOrDefault() is null)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "idempotency_conflict",
                        "A Revit family with the target name exists but has no usable type.");
                }
            }
            else if (!document.LoadFamily(
                         rfaPath,
                         new RevitFamilyLoadOptions(),
                         out Family loaded) ||
                     loaded is null)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "family_load_failed",
                    "Revit could not load the generated Tripo family.");
            }
            else
            {
                loaded.Name = familyName;
                family = loaded;
            }

            ISet<ElementId> symbolIds = family.GetFamilySymbolIds();
            ElementId? firstSymbolId = symbolIds.FirstOrDefault();
            if (firstSymbolId is null)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "family_symbol_missing",
                    "The generated Tripo family has no loadable type.");
            }

            FamilySymbol symbol = (FamilySymbol)document.GetElement(firstSymbolId);
            if (!symbol.IsActive)
            {
                // A freshly loaded symbol only finishes activating on regeneration;
                // NewFamilyInstance must not see a half-activated symbol.
                symbol.Activate();
                document.Regenerate();
            }

            FamilyInstance instance = document.Create.NewFamilyInstance(
                XYZ.Zero,
                symbol,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            Entity entity = new(schema);
            entity.Set<string>(IdempotencyKeyField, request.IdempotencyKey);
            entity.Set<string>(RequestFingerprintField, fingerprint);
            entity.Set<int>(MaterialCountField, materialCount);
            entity.Set<int>(TextureCountField, textureCount);
            instance.SetEntity(entity);

            TransactionStatus commitStatus = transaction.Commit();
            if (commitStatus != TransactionStatus.Committed)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "transaction_not_committed",
                    $"Revit did not commit the family import transaction ({commitStatus}).");
            }

            createdUniqueId = instance.UniqueId;
            createdId = instance.Id;
        }
        catch (Exception exception)
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
            {
                TransactionStatus rollbackStatus = transaction.RollBack();
                if (rollbackStatus != TransactionStatus.RolledBack)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "rollback_failed",
                        "Revit could not verify rollback of the failed family import.",
                        exception);
                }
            }

            throw;
        }

        string? placementNote = ApplyPlacementAfterCommit(
            application,
            document,
            createdId,
            request,
            isFamilyInstance: true);

        return CreateReceipt(
            request,
            createdUniqueId,
            prepared,
            "committed",
            materialCount,
            textureCount,
            rfaPath,
            placementNote);
    }

    private static IList<GeometryObject> BuildGeometry(
        Tripo.Bridge.PreparedMesh prepared,
        IReadOnlyList<ElementId> slotToMaterialId)
    {
        double metersToInternal = UnitUtils.ConvertToInternalUnits(
            1.0,
            UnitTypeId.Meters);
        List<XYZ> vertices = new(prepared.VerticesInMeters.Count);
        foreach (Tripo.Bridge.MeshPoint3 point in prepared.VerticesInMeters)
        {
            XYZ vertex = new(
                point.X * metersToInternal,
                point.Y * metersToInternal,
                point.Z * metersToInternal);
            if (!XYZ.IsWithinLengthLimits(vertex))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "revit_design_limit",
                    "The prepared mesh exceeds Revit's design-coordinate limits.");
            }

            vertices.Add(vertex);
        }

        using TessellatedShapeBuilder builder = new();
        builder.OpenConnectedFaceSet(isSolid: false);
        foreach (Tripo.Bridge.MeshTriangle triangle in prepared.Triangles)
        {
            ElementId materialId =
                triangle.MaterialSlot >= 0 && triangle.MaterialSlot < slotToMaterialId.Count
                    ? slotToMaterialId[triangle.MaterialSlot]
                    : ElementId.InvalidElementId;
            TessellatedFace face = new(
                [
                    vertices[triangle.A],
                    vertices[triangle.B],
                    vertices[triangle.C],
                ],
                materialId);
            if (!builder.DoesFaceHaveEnoughLoopsAndVertices(face))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "revit_face_invalid",
                    "Revit rejected a prepared triangle.");
            }

            builder.AddFace(face);
        }

        builder.CloseConnectedFaceSet();
        // Tripo produces a display mesh, not a guaranteed watertight BRep.
        // Ask Revit for a mesh directly and let it salvage valid facets from
        // non-manifold/open inputs instead of first trying to construct a
        // sheet or solid. HasInvalidData is diagnostic in this mode: Revit can
        // still return a usable mesh after discarding inconsistent facets.
        builder.Target = TessellatedShapeBuilderTarget.Mesh;
        builder.Fallback = TessellatedShapeBuilderFallback.Salvage;
        builder.Build();
        using TessellatedShapeBuilderResult result = builder.GetBuildResult();
        if (!result.AreObjectsAvailable ||
            result.Outcome == TessellatedShapeBuilderOutcome.Nothing)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "revit_geometry_invalid",
                "Revit could not build any geometry from the prepared mesh.");
        }

        IList<GeometryObject> geometry = result.GetGeometricalObjects();
        if (geometry.Count == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "revit_geometry_invalid",
                "Revit returned no geometry from the prepared mesh.");
        }

        return geometry;
    }

    // Creates one Revit material per prepared slot in the target document (host
    // or family). Returns slot -> material ElementId; slots without a material
    // stay InvalidElementId. Shared by both import modes.
    private static ElementId[] CreateMaterials(
        Document targetDocument,
        Tripo.Bridge.PreparedMesh prepared,
        out int materialCount,
        out int textureCount)
    {
        materialCount = 0;
        textureCount = 0;
        int slotCount = prepared.Materials.Count;
        ElementId[] slotToMaterialId = new ElementId[slotCount];
        for (int index = 0; index < slotCount; index++)
        {
            slotToMaterialId[index] = ElementId.InvalidElementId;
        }

        HashSet<string> usedMaterialNames = new(StringComparer.Ordinal);
        foreach (Material existingMaterial in new FilteredElementCollector(targetDocument)
            .OfClass(typeof(Material))
            .Cast<Material>())
        {
            usedMaterialNames.Add(existingMaterial.Name);
        }

        HashSet<string> usedAppearanceNames = new(StringComparer.Ordinal);
        AppearanceAssetElement? templateAppearance = null;
        AppearanceAssetElement? firstAppearance = null;
        foreach (AppearanceAssetElement appearance in new FilteredElementCollector(targetDocument)
            .OfClass(typeof(AppearanceAssetElement))
            .Cast<AppearanceAssetElement>())
        {
            usedAppearanceNames.Add(appearance.Name);
            // Prefer an appearance asset whose diffuse slot already has a
            // connected unified-bitmap (read-only probe, no EditScope). A
            // template without a bindable bitmap silently downgrades every
            // textured slot to color-only, so picking the first collector hit
            // (whose ordering is undefined) is too brittle when the document
            // also contains non-bitmap appearance assets. Fall back to the
            // first candidate if none qualifies.
            firstAppearance ??= appearance;
            if (templateAppearance is null && HasBindableDiffuseBitmap(appearance))
            {
                templateAppearance = appearance;
            }
        }

        templateAppearance ??= firstAppearance;

        for (int slot = 0; slot < slotCount; slot++)
        {
            Tripo.Bridge.PreparedMaterial preparedMaterial = prepared.Materials[slot];
            if (preparedMaterial.DiffuseTextureAbsolutePath is not null &&
                !IsReadableFile(preparedMaterial.DiffuseTextureAbsolutePath))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "mtl_invalid",
                    "A material texture file is missing or unreadable.");
            }

            string materialName = MakeUniqueName(
                SanitizeRevitName(preparedMaterial.Name, 64, "TripoMaterial"),
                usedMaterialNames);
            usedMaterialNames.Add(materialName);
            ElementId materialId = Material.Create(targetDocument, materialName);
            Material material = (Material)targetDocument.GetElement(materialId);
            if (preparedMaterial.DiffuseArgb is int argb)
            {
                material.Color = new Color(
                    (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
                material.Transparency = AlphaToTransparency((byte)((argb >> 24) & 0xFF));
            }

            materialCount++;
            slotToMaterialId[slot] = materialId;

            if (preparedMaterial.DiffuseTextureAbsolutePath is not null &&
                templateAppearance is not null &&
                TryBindTexture(
                    targetDocument,
                    templateAppearance,
                    preparedMaterial.DiffuseTextureAbsolutePath,
                    usedAppearanceNames,
                    out ElementId appearanceAssetId))
            {
                material.AppearanceAssetId = appearanceAssetId;
                textureCount++;
            }
        }

        return slotToMaterialId;
    }

    // Best-effort texture binding: duplicate an existing appearance asset and
    // repoint its diffuse unified-bitmap at the baked texture. A family template
    // that ships without a bindable appearance asset yields color-only materials,
    // which is an environmental limit, not an error.
    private static bool TryBindTexture(
        Document targetDocument,
        AppearanceAssetElement templateAppearance,
        string texturePath,
        HashSet<string> usedAppearanceNames,
        out ElementId appearanceAssetId)
    {
        appearanceAssetId = ElementId.InvalidElementId;
        AppearanceAssetElement? duplicated = null;
        try
        {
            string appearanceName = MakeUniqueName("Tripo Appearance", usedAppearanceNames);
            usedAppearanceNames.Add(appearanceName);
            duplicated = templateAppearance.Duplicate(appearanceName);
            bool bound;
            using (AppearanceAssetEditScope editScope = new(targetDocument))
            {
                Asset editableAsset = editScope.Start(duplicated.Id);
                bound = TrySetDiffuseBitmap(editableAsset, texturePath);
                if (bound)
                {
                    editScope.Commit(true);
                }
                else
                {
                    editScope.Cancel();
                }
            }

            if (!bound)
            {
                targetDocument.Delete(duplicated.Id);
                return false;
            }

            appearanceAssetId = duplicated.Id;
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            if (duplicated is not null)
            {
                try
                {
                    targetDocument.Delete(duplicated.Id);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // The partial appearance asset could not be removed; leave it.
                }
            }

            return false;
        }
    }

    private static bool TrySetDiffuseBitmap(Asset asset, string texturePath)
    {
        AssetProperty? diffuse = asset.FindByName(Generic.GenericDiffuse);
        if (diffuse is null || diffuse.NumberOfConnectedProperties == 0)
        {
            return false;
        }

        Asset? connectedBitmap = diffuse.GetSingleConnectedAsset();
        if (connectedBitmap is null)
        {
            return false;
        }

        if (connectedBitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap)
                is not AssetPropertyString bitmapPath)
        {
            return false;
        }

        bitmapPath.Value = texturePath;
        return true;
    }

    // Read-only probe mirroring TrySetDiffuseBitmap's bindability test, without
    // opening an AppearanceAssetEditScope. Used only to pick a template whose
    // diffuse slot already carries a connected unified-bitmap, so duplicating it
    // yields an asset that TrySetDiffuseBitmap can repoint at the baked texture
    // instead of silently downgrading to color-only.
    private static bool HasBindableDiffuseBitmap(AppearanceAssetElement appearance)
    {
        try
        {
            Asset? asset = appearance.GetAppearanceAsset();
            if (asset is null)
            {
                return false;
            }

            AssetProperty? diffuse = asset.FindByName(Generic.GenericDiffuse);
            if (diffuse is null || diffuse.NumberOfConnectedProperties == 0)
            {
                return false;
            }

            Asset? connectedBitmap = diffuse.GetSingleConnectedAsset();
            return connectedBitmap is not null &&
                connectedBitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap)
                    is AssetPropertyString;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // A partially-defined or locked appearance asset is not bindable;
            // treat it as a non-qualifying template rather than failing import.
            return false;
        }
    }

    // DirectShapes carrying this dispatcher's ApplicationId whose ApplicationDataId
    // begins with "<idempotencyKey>:". Serves both the mesh-mode own-key lookup and
    // the family-mode cross-mode idempotency probe.
    private static DirectShape[] CollectDirectShapesByKeyPrefix(
        Document document,
        string idempotencyKey)
    {
        string dataPrefix = idempotencyKey + ":";
        return new FilteredElementCollector(document)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(shape =>
                string.Equals(
                    shape.ApplicationId,
                    ApplicationIdentifier,
                    StringComparison.Ordinal) &&
                shape.ApplicationDataId?.StartsWith(
                    dataPrefix,
                    StringComparison.Ordinal) == true)
            .ToArray();
    }

    private static List<Family> CollectFamiliesByName(Document document, string familyName)
    {
        List<Family> matches = [];
        foreach (Family family in new FilteredElementCollector(document)
            .OfClass(typeof(Family))
            .Cast<Family>())
        {
            if (string.Equals(family.Name, familyName, StringComparison.Ordinal))
            {
                matches.Add(family);
            }
        }

        return matches;
    }

    private static List<FamilyInstance> FindMatchingInstances(
        Document document,
        Schema schema,
        string idempotencyKey)
    {
        List<FamilyInstance> matches = [];
        foreach (FamilyInstance instance in new FilteredElementCollector(document)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>())
        {
            Entity entity = instance.GetEntity(schema);
            if (!entity.IsValid())
            {
                continue;
            }

            string storedKey = entity.Get<string>(IdempotencyKeyField);
            if (string.Equals(storedKey, idempotencyKey, StringComparison.Ordinal))
            {
                matches.Add(instance);
            }
        }

        return matches;
    }

    private static Schema GetOrCreateIdempotencySchema()
    {
        Guid schemaGuid = new(IdempotencySchemaGuid);
        Schema? existing = Schema.Lookup(schemaGuid);
        if (existing is not null)
        {
            return existing;
        }

        SchemaBuilder builder = new(schemaGuid);
        builder.SetSchemaName(IdempotencySchemaName);
        builder.SetVendorId(IdempotencySchemaVendorId);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(IdempotencyKeyField, typeof(string));
        builder.AddSimpleField(RequestFingerprintField, typeof(string));
        builder.AddSimpleField(MaterialCountField, typeof(int));
        builder.AddSimpleField(TextureCountField, typeof(int));
        return builder.Finish();
    }

    private static string ResolveFamilyTemplate(UIApplication application)
    {
        List<string> probed = [];
        string? envTemplate = Environment.GetEnvironmentVariable(
            FamilyTemplateEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envTemplate))
        {
            probed.Add(envTemplate);
            if (Path.IsPathFullyQualified(envTemplate) && File.Exists(envTemplate))
            {
                return envTemplate;
            }
        }

        string templateDirectory = application.Application.FamilyTemplatePath;
        if (!string.IsNullOrWhiteSpace(templateDirectory))
        {
            foreach (string candidate in FamilyTemplateCandidates)
            {
                string candidatePath = Path.Combine(templateDirectory, candidate);
                probed.Add(candidatePath);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            // Some deployments nest the templates one level down (localized
            // folders); probe the immediate subdirectories deterministically
            // after the top level.
            foreach (string subdirectory in EnumerateBoundedSubdirectories(templateDirectory))
            {
                foreach (string candidate in FamilyTemplateCandidates)
                {
                    string candidatePath = Path.Combine(subdirectory, candidate);
                    probed.Add(candidatePath);
                    if (File.Exists(candidatePath))
                    {
                        return candidatePath;
                    }
                }
            }
        }

        throw new Tripo.Bridge.BridgeCallException(
            "family_template_unavailable",
            "No Revit family template was found. Probed: " + BoundedJoin(probed));
    }

    // Immediate subdirectories only (one level), ordinal-sorted for a
    // deterministic probe order, capped so a pathological template root cannot
    // explode the probe. An unreadable directory yields no subdirectories.
    private static string[] EnumerateBoundedSubdirectories(string directory)
    {
        const int MaxSubdirectories = 32;
        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        Array.Sort(subdirectories, StringComparer.Ordinal);
        return subdirectories.Length <= MaxSubdirectories
            ? subdirectories
            : subdirectories[..MaxSubdirectories];
    }

    private Tripo.Bridge.HostContextReceipt CreateContextReceipt(
        UIApplication application,
        Document document)
    {
        ForgeTypeId displayUnit = document
            .GetUnits()
            .GetFormatOptions(SpecTypeId.Length)
            .GetUnitTypeId();
        return new Tripo.Bridge.HostContextReceipt(
            "revit",
            application.Application.VersionNumber,
            Environment.ProcessId,
            _documentSessions.GetOrCreate(document),
            document.Title,
            displayUnit.TypeId,
            Capabilities);
    }

    private void EnsureWritableMatchingDocument(
        Document document,
        string requestedSessionId)
    {
        if (document.IsReadOnly || document.IsModifiable || document.IsFamilyDocument)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_not_writable",
                "The active Revit document is not a writable project document.");
        }

        string activeSession = _documentSessions.GetOrCreate(document);
        if (!string.Equals(
                activeSession,
                requestedSessionId,
                StringComparison.Ordinal))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_changed",
                "The active Revit document no longer matches the requested session.");
        }
    }

    private static Document GetActiveDocument(UIApplication application)
    {
        Document? document = application.ActiveUIDocument?.Document;
        if (document is null || !document.IsValidObject)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_unavailable",
                "Revit has no valid active document.");
        }

        return document;
    }

    private static void TryCloseFamilyDocument(Document familyDocument)
    {
        try
        {
            if (familyDocument.IsValidObject)
            {
                familyDocument.Close(false);
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Best-effort cleanup of the in-memory family document.
        }
    }

    private static int AlphaToTransparency(byte alpha)
    {
        // Revit Transparency is 0 (opaque) .. 100 (fully transparent); the MTL
        // alpha channel is 0 (transparent) .. 255 (opaque).
        int transparency = (int)Math.Round(
            (255 - alpha) * 100.0 / 255.0,
            MidpointRounding.AwayFromZero);
        return Math.Clamp(transparency, 0, 100);
    }

    private static bool IsReadableFile(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SanitizeRevitName(string raw, int maxLength, string fallback)
    {
        StringBuilder builder = new(raw.Length);
        foreach (char character in raw)
        {
            if (char.IsControl(character) ||
                Array.IndexOf(IllegalRevitNameChars, character) >= 0)
            {
                continue;
            }

            builder.Append(character);
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].Trim();
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string MakeUniqueName(string baseName, HashSet<string> used)
    {
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string BoundedJoin(List<string> values)
    {
        // The subdirectory probe can grow the list; cap both the number of
        // listed paths and the overall length, but stay honest about how many
        // were actually probed.
        const int MaxPaths = 12;
        const int MaxLength = 480;
        int shown = Math.Min(values.Count, MaxPaths);
        string joined = string.Join("; ", values.Take(shown));
        if (values.Count > shown)
        {
            joined += "; (+" +
                (values.Count - shown).ToString(CultureInfo.InvariantCulture) +
                " more probed)";
        }

        return joined.Length <= MaxLength ? joined : joined[..MaxLength] + "...";
    }

    private static Tripo.Bridge.HostImportReceipt CreateReceipt(
        Tripo.Bridge.ImportMeshRequest request,
        string createdId,
        Tripo.Bridge.PreparedMesh prepared,
        string transactionStatus,
        int materialCount,
        int textureCount,
        string? savedFamilyPath,
        string? placementNote = null) =>
        new(
            "revit",
            request.DocumentSessionId,
            request.IdempotencyKey,
            createdId,
            prepared.VerticesInMeters.Count,
            prepared.Triangles.Count,
            prepared.RejectedTriangleCount,
            transactionStatus,
            request.ImportMode,
            materialCount,
            textureCount,
            savedFamilyPath,
            placementNote);

    private static string? ApplyPlacementAfterCommit(
        UIApplication application,
        Document document,
        ElementId elementId,
        Tripo.Bridge.ImportMeshRequest request,
        bool isFamilyInstance)
    {
        bool wantsPick = string.Equals(
            request.PlacementMode,
            "pick",
            StringComparison.OrdinalIgnoreCase);
        bool wantsLevel = request.AssociateLevel && isFamilyInstance;
        if (!wantsPick && !wantsLevel)
        {
            return null;
        }

        string? note = null;
        XYZ target = XYZ.Zero;
        if (wantsPick)
        {
            UIDocument? uiDocument = application.ActiveUIDocument;
            if (uiDocument is null ||
                !ReferenceEquals(uiDocument.Document, document))
            {
                note = "Pick unavailable; left at project origin";
            }
            else
            {
                try
                {
                    target = uiDocument.Selection.PickPoint(
                        "Click to place the Tripo model (Esc keeps origin)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    note = "Pick cancelled; left at project origin";
                    target = XYZ.Zero;
                }
            }
        }

        using Transaction placementTransaction =
            new(document, "Place Tripo model");
        FailureHandlingOptions failureOptions = placementTransaction
            .GetFailureHandlingOptions()
            .SetFailuresPreprocessor(new RevitRollbackFailuresPreprocessor())
            .SetClearAfterRollback(true);
        placementTransaction.SetFailureHandlingOptions(failureOptions);
        if (placementTransaction.Start() != TransactionStatus.Started)
        {
            return note ?? "Placement transaction could not start";
        }

        try
        {
            if (!target.IsAlmostEqualTo(XYZ.Zero))
            {
                ElementTransformUtils.MoveElement(document, elementId, target);
                note ??= "Placed at picked point";
            }

            if (wantsLevel)
            {
                Element element = document.GetElement(elementId);
                if (element is not null &&
                    TryAssociateNearestLevel(document, element, target))
                {
                    note = string.IsNullOrWhiteSpace(note)
                        ? "Associated nearest level"
                        : note + "; associated nearest level";
                }
            }

            if (placementTransaction.Commit() != TransactionStatus.Committed)
            {
                return "Placement transaction did not commit; model left at origin";
            }
        }
        catch
        {
            if (placementTransaction.GetStatus() == TransactionStatus.Started)
            {
                placementTransaction.RollBack();
            }

            throw;
        }

        return note;
    }

    private static bool TryAssociateNearestLevel(
        Document document,
        Element element,
        XYZ point)
    {
        Level? nearest = null;
        double bestDistance = double.MaxValue;
        foreach (Level level in new FilteredElementCollector(document)
                     .OfClass(typeof(Level))
                     .Cast<Level>())
        {
            double distance = Math.Abs(level.Elevation - point.Z);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = level;
            }
        }

        if (nearest is null)
        {
            return false;
        }

        Parameter? levelParameter =
            element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
            element.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM) ??
            element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
        if (levelParameter is null || levelParameter.IsReadOnly)
        {
            return false;
        }

        levelParameter.Set(nearest.Id);
        return true;
    }

    private static string CreateRequestFingerprint(
        Tripo.Bridge.ImportMeshRequest request)
    {
        // Host-side import identity is content, not session: the session is
        // blanked so a cross-restart replay (same key, new session) recovers
        // instead of conflicting. The active-session check enforces session
        // correctness separately. Placement preferences are UX-only and must
        // not change the content fingerprint.
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            request with
            {
                DocumentSessionId = string.Empty,
                PlacementMode = "origin",
                AssociateLevel = false,
            },
            Tripo.Bridge.BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

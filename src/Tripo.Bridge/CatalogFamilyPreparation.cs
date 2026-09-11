using System.Globalization;
using System.Text;

namespace Tripo.Bridge;

/// <summary>Host-independent validation and fitting of a confirmed fixed furniture variant.</summary>
public static class CatalogFamilyPreparation
{
    public static void Validate(ImportMeshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Catalog is null)
        {
            if (request.RequireExisting)
            {
                throw Invalid("RequireExisting is only available for catalogue family imports.");
            }

            return;
        }

        Validate(request.Catalog);
        if (request.ImportMode != "family" || request.PlacementMode != "origin" || request.AssociateLevel)
        {
            throw Invalid("Catalogue imports require family mode and explicit catalogue coordinates, without point picking or level association.");
        }

        // Recovery bypasses the artifact loader, but must retain its import
        // metadata checks before consulting the document or a persisted path.
        if (!Guid.TryParseExact(request.DocumentSessionId, "D", out Guid documentSession) ||
            documentSession.ToString("D") != request.DocumentSessionId)
        {
            throw new BridgeCallException("document_session_invalid", "documentSessionId must be a canonical lowercase D-format UUID.");
        }

        if (!Guid.TryParseExact(request.IdempotencyKey, "D", out Guid importId) ||
            importId.ToString("D") != request.IdempotencyKey)
        {
            throw new BridgeCallException("idempotency_key_invalid", "idempotencyKey must be a canonical lowercase D-format UUID.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
        {
            throw new BridgeCallException("name_invalid", "The imported object name must contain 1 to 128 characters.");
        }
    }

    public static void Validate(CatalogFamilyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateText(options.ProductId, nameof(options.ProductId), 256, required: true);
        ValidateText(options.Manufacturer, nameof(options.Manufacturer), 256);
        ValidateText(options.ModelNumber, nameof(options.ModelNumber), 256);
        ValidateText(options.Description, nameof(options.Description), 4096, multiline: true);
        ValidateText(options.SourceReference, nameof(options.SourceReference), 2048);
        ValidateDimension(options.WidthMillimeters, nameof(options.WidthMillimeters));
        ValidateDimension(options.DepthMillimeters, nameof(options.DepthMillimeters));
        ValidateDimension(options.HeightMillimeters, nameof(options.HeightMillimeters));
        ValidatePosition(options.XMillimeters);
        ValidatePosition(options.YMillimeters);
        ValidatePosition(options.ZMillimeters);
        if (!double.IsFinite(options.RotationDegrees) || Math.Abs(options.RotationDegrees) > 360)
        {
            throw Invalid("RotationDegrees must be finite and between -360 and 360.");
        }

        if (options.Category != "Furniture" || options.ScaleMode is not "uniform" and not "stretch")
        {
            throw Invalid("Category must be Furniture; ScaleMode must be uniform or explicitly stretch.");
        }

        if (!double.IsFinite(options.MaxDimensionErrorRatio) ||
            options.MaxDimensionErrorRatio < 0 || options.MaxDimensionErrorRatio > 0.25)
        {
            throw Invalid("MaxDimensionErrorRatio must be finite and between 0 and 0.25.");
        }
    }

    public static PreparedMesh Fit(PreparedMesh mesh, CatalogFamilyOptions options)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Validate(options);
        // Only rendered vertices determine the product bounds. OBJ files may
        // contain unused positions far from the actual furniture geometry.
        HashSet<int> used = [];
        foreach (MeshTriangle triangle in mesh.Triangles)
        {
            used.Add(triangle.A);
            used.Add(triangle.B);
            used.Add(triangle.C);
        }

        if (used.Count == 0)
        {
            throw InvalidMesh("The furniture mesh has no geometry.");
        }

        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        foreach (int index in used)
        {
            if (index < 0 || index >= mesh.VerticesInMeters.Count)
            {
                throw InvalidMesh("The furniture mesh contains an invalid vertex reference.");
            }

            MeshPoint3 p = mesh.VerticesInMeters[index];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
            {
                throw InvalidMesh("The furniture mesh contains a non-finite coordinate.");
            }

            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        double[] actual = [maxX - minX, maxY - minY, maxZ - minZ];
        if (actual.Any(value => !double.IsFinite(value) || value < 1e-9))
        {
            throw InvalidMesh("The furniture mesh must have nonzero width, depth and height.");
        }

        double[] target = [options.WidthMillimeters / 1000, options.DepthMillimeters / 1000, options.HeightMillimeters / 1000];
        int anchor = 0;
        for (int axis = 1; axis < 3; axis++)
        {
            if (target[axis] > target[anchor])
            {
                anchor = axis;
            }
        }

        double uniformScale = target[anchor] / actual[anchor];
        double[] scale = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            scale[axis] = options.ScaleMode == "stretch" ? target[axis] / actual[axis] : uniformScale;
            double relativeError = Math.Abs(actual[axis] * scale[axis] - target[axis]) / target[axis];
            if (!double.IsFinite(scale[axis]) || !double.IsFinite(relativeError) ||
                relativeError > options.MaxDimensionErrorRatio + 1e-12)
            {
                throw new BridgeCallException(
                    "catalog_dimension_mismatch",
                    "The generated proportions do not match the confirmed dimensions within tolerance. Review width/depth orientation or explicitly choose stretch.");
            }
        }

        Dictionary<int, int> remap = new(used.Count);
        List<MeshPoint3> vertices = new(used.Count);
        List<MeshPoint2> uvs = [];
        bool hasUvs = mesh.Uvs.Count != 0;
        if (hasUvs && mesh.Uvs.Count != mesh.VerticesInMeters.Count)
        {
            throw InvalidMesh("The furniture mesh has inconsistent texture coordinates.");
        }

        foreach (int index in used.Order())
        {
            MeshPoint3 p = mesh.VerticesInMeters[index];
            remap.Add(index, vertices.Count);
            vertices.Add(new MeshPoint3(
                ((p.X - minX) - actual[0] / 2) * scale[0],
                ((p.Y - minY) - actual[1] / 2) * scale[1],
                (p.Z - minZ) * scale[2]));
            if (hasUvs)
            {
                uvs.Add(mesh.Uvs[index]);
            }
        }

        MeshTriangle[] triangles = mesh.Triangles.Select(t =>
            new MeshTriangle(remap[t.A], remap[t.B], remap[t.C], t.MaterialSlot)).ToArray();
        return mesh with { VerticesInMeters = vertices, Uvs = uvs, Triangles = triangles };
    }

    public static string FamilyName(string name, string idempotencyKey)
    {
        if (!Guid.TryParseExact(idempotencyKey, "D", out Guid key))
        {
            throw Invalid("The catalogue import key must be a UUID.");
        }

        return SafeName(name, 64, "Furniture") + "_" + key.ToString("N", CultureInfo.InvariantCulture);
    }

    public static string TypeName(CatalogFamilyOptions options)
    {
        Validate(options);
        string model = SafeName(string.IsNullOrWhiteSpace(options.ModelNumber) ? options.ProductId : options.ModelNumber, 48, "Product");
        return string.Create(CultureInfo.InvariantCulture,
            $"{model}_{options.WidthMillimeters:0.###}x{options.DepthMillimeters:0.###}x{options.HeightMillimeters:0.###}mm");
    }

    private static string SafeName(string? value, int limit, string fallback)
    {
        const string illegal = "\\/:{}[]|;<>?`~\"*";
        StringBuilder result = new();
        foreach (char c in value ?? string.Empty)
        {
            if (!char.IsControl(c) && !illegal.Contains(c))
            {
                result.Append(c);
            }

            if (result.Length == limit)
            {
                break;
            }
        }

        string name = result.ToString().Trim().TrimEnd('.');
        return name.Length == 0 ? fallback : name;
    }

    private static void ValidateText(string? value, string field, int max, bool required = false, bool multiline = false)
    {
        if (value is null || value.Length > max || (required && string.IsNullOrWhiteSpace(value)) ||
            value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t')))
        {
            throw Invalid($"{field} contains missing, excessive or unsupported text.");
        }
    }

    private static void ValidateDimension(double value, string field)
    {
        if (!double.IsFinite(value) || value < 1 || value > 100_000)
        {
            throw Invalid($"{field} must be between 1 and 100000 millimetres.");
        }
    }

    private static void ValidatePosition(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 10_000_000)
        {
            throw Invalid("Placement coordinates must be finite and within 10 kilometres of the project origin.");
        }
    }

    private static BridgeCallException Invalid(string message) => new("catalog_options_invalid", message);
    private static BridgeCallException InvalidMesh(string message) => new("catalog_mesh_invalid", message);
}

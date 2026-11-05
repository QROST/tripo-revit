using System.Security.Cryptography;
using System.Text.Json;

namespace Tripo.Bridge;

public static class CatalogImportIdentity
{
    public static string CatalogFingerprint(ImportMeshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Catalog is null)
        {
            throw new BridgeCallException("catalog_options_invalid", "A catalogue identity requires confirmed product options.");
        }

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Name,
            request.IdempotencyKey,
            request.Catalog,
        }, BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static string Fingerprint(ImportMeshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Preserve legacy fingerprints: absent catalogue/recovery fields are
        // omitted by BridgeJson. Explicit catalogue transforms remain identity.
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(request with
        {
            DocumentSessionId = string.Empty,
            PlacementMode = "origin",
            AssociateLevel = false,
            RequireExisting = false,
        }, BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

namespace Tripo.Bridge;

public static class BridgeConstants
{
    public const string ProtocolVersion = "4";
    public const int MaximumMessageBytes = 64 * 1024;
    // The upload guide advertises a 10 MB accepted maximum. Keep local and
    // remote staging at that conservative provider-bound limit so an image is
    // rejected before a paid-operation journal can be created.
    public const long MaximumImageTransferBytes = 10_000_000;
    public const long MaximumArtifactBytes = 128L * 1024 * 1024;
    public const int MaximumBundleFiles = 32;
    public const long MaximumBundleBytes = 256L * 1024 * 1024;
    public const int MaximumVertices = 250_000;
    public const int MaximumTriangles = 500_000;
    public const int MaximumConcurrentClients = 4;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(2);

    public const string ContextMethod = "host.context";
    public const string ImportMeshMethod = "host.import_mesh";
}

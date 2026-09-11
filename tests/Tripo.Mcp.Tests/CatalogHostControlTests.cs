using Tripo.Bridge;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed partial class HostControlDispatcherTests
{
    [Fact]
    public async Task CatalogPaidRequestsPreserveTheOriginalCredentialAnchor()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(new FakeCredentialService(), workflow);
        StagedImageTransfer image = new(Guid.NewGuid().ToString("D"), new string('a', 64), 100, "image/png");
        HostControlImageCredentialAnchor anchor = new(Guid.NewGuid().ToString("D"), image, 12000, true, TripoModelCatalog.DefaultModelId);
        HostControlCreateImageTaskRequest generation = new(image, 12000, true, Guid.NewGuid().ToString("D"),
            true, Model: TripoModelCatalog.DefaultModelId, CredentialAnchor: anchor);
        HostControlCreateObjConversionRequest conversion = new("task_image123", 12000, true,
            Guid.NewGuid().ToString("D"), true, CredentialAnchor: anchor);

        await dispatcher.DispatchAsync(HostControlConstants.CreateImageTaskMethod, BridgeJson.ToElement(generation), CancellationToken.None);
        await dispatcher.DispatchAsync(HostControlConstants.CreateObjConversionMethod, BridgeJson.ToElement(conversion), CancellationToken.None);

        Assert.Equal(generation, workflow.LastImageRequest);
        Assert.Equal(conversion, workflow.LastCatalogConversionRequest);
    }

    [Fact]
    public async Task CatalogImportPreservesConfirmedDimensionsMetadataPlacementAndDocument()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(new FakeCredentialService(), workflow);
        CatalogFamilyOptions family = new("product-42", "Manufacturer", "S001", "A chair", "https://example.com/chair#page=3",
            545, 560, 770, 1500, 2500, RotationDegrees: 90, ScaleMode: "stretch");
        HostControlImportObjTaskRequest request = new("task_conversion123", "Dining Chair", "target-document",
            Guid.NewGuid().ToString("D"), "family", true, Catalog: family, RequireExistingImport: true);

        await dispatcher.DispatchAsync(HostControlConstants.ImportObjTaskMethod,
            BridgeJson.ToElement(request), CancellationToken.None);

        Assert.Equal(request, workflow.LastCatalogRequest);
    }

    [Theory]
    [InlineData("directshape", "origin", false)]
    [InlineData("family", "pick", false)]
    [InlineData("family", "origin", true)]
    public async Task CatalogImportRejectsModesThatWouldDiscardPlacement(string mode, string placement, bool associateLevel)
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(new FakeCredentialService(), workflow);
        HostControlImportObjTaskRequest request = new("task_conversion123", "Chair", "document",
            Guid.NewGuid().ToString("D"), mode, false, placement, associateLevel,
            new CatalogFamilyOptions("p", "", "", "", "", 500, 500, 800));

        await Assert.ThrowsAsync<HostControlCallException>(() => dispatcher.DispatchAsync(
            HostControlConstants.ImportObjTaskMethod, BridgeJson.ToElement(request), CancellationToken.None));
        Assert.Null(workflow.LastCatalogRequest);
    }
}

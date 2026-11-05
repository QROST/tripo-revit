using Tripo.Bridge;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed partial class TripoWorkflowTests
{
    [Fact]
    public async Task CatalogRecoveryChecksExistingInstanceWithoutProviderOrArtifacts()
    {
        FakeApiClient api = new();
        FakeArtifactStager stager = new();
        FakeHostConnection host = new();
        string documentId = Guid.NewGuid().ToString("D");
        host.Contexts.Enqueue(new HostContextReceipt("revit", "2026", 1, documentId, "Project", "feet", []));
        CatalogFamilyOptions family = new("p", "Brand", "S1", "Chair", "", 545, 560, 770, 1000, 2000);
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await workflow.ImportCatalogObjTaskAsync("task_conversion123", "Chair", documentId,
            Guid.NewGuid().ToString("D"), true, family, CancellationToken.None, requireExistingImport: true);

        Assert.Equal(0, api.TotalCalls);
        Assert.Equal(0, stager.CallCount);
        Assert.Equal(1, host.ImportCalls);
        Assert.True(host.LastImportRequest!.RequireExisting);
        Assert.Equal(family, host.LastImportRequest.Catalog);
        Assert.Empty(host.LastImportRequest.Entries);
    }

    [Fact]
    public async Task CatalogRecoveryRefusesChangedDocumentWithoutDispatch()
    {
        FakeApiClient api = new();
        FakeArtifactStager stager = new();
        FakeHostConnection host = new();
        host.Contexts.Enqueue(new HostContextReceipt("revit", "2026", 1, "other-document", "Other", "feet", []));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(() => workflow.ImportCatalogObjTaskAsync(
            "task_conversion123", "Chair", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), true,
            new CatalogFamilyOptions("p", "", "", "", "", 500, 500, 800), CancellationToken.None,
            requireExistingImport: true));

        Assert.Equal(0, api.TotalCalls);
        Assert.Equal(0, stager.CallCount);
        Assert.Equal(0, host.ImportCalls);
    }
}

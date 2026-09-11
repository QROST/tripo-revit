using Tripo.Bridge;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed partial class TripoWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogCredentialAnchorRejectsChangedAccountBeforeNewJournalOrProviderCalls(bool conversion)
    {
        FakeApiClient api = new();
        FakeArtifactStager stager = new();
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);
        HostControlImageCredentialAnchor anchor = await CreateCatalogCredentialAnchorAsync(workflow);
        string anchorJournal = Path.Combine(_journalRoot, anchor.OperationId + ".jsonl");
        byte[] originalAnchor = await File.ReadAllBytesAsync(anchorJournal);
        string nextOperation = Guid.NewGuid().ToString("D");

        // The prior product has a durable ID. The next product/conversion has
        // never been journaled, so only the cross-product anchor can reject B.
        api.OperationScopeFingerprint = new string('c', 64);
        BridgeCallException error = await Assert.ThrowsAsync<BridgeCallException>(() =>
            CreateAnchoredCatalogTaskAsync(workflow, anchor, nextOperation, conversion));

        Assert.Equal("catalog_credential_anchor_unverified", error.Code);
        Assert.Equal(1, api.CreateImageCalls);
        Assert.Equal(1, api.ImageUploadCalls);
        Assert.Equal(1, api.ImageGenerationCalls);
        Assert.Equal(0, api.CreateConversionCalls);
        Assert.Equal(0, api.GetTaskCalls);
        Assert.Equal(0, stager.CallCount);
        Assert.Equal(0, host.ContextCalls);
        Assert.Equal(0, host.ImportCalls);
        AssertNoCatalogPaidJournal(nextOperation);
        Assert.Equal(originalAnchor, await File.ReadAllBytesAsync(anchorJournal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogCredentialAnchorRequiresAnExistingJournalAndNeverBootstrapsIt(bool conversion)
    {
        FakeApiClient api = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, new FakeArtifactStager(), new FakeHostConnection());
        HostControlImageCredentialAnchor missing = NewCatalogCredentialAnchor();
        string nextOperation = Guid.NewGuid().ToString("D");

        BridgeCallException error = await Assert.ThrowsAsync<BridgeCallException>(() =>
            CreateAnchoredCatalogTaskAsync(workflow, missing, nextOperation, conversion));

        Assert.Equal("catalog_credential_anchor_unverified", error.Code);
        Assert.Equal(0, api.TotalCalls);
        Assert.Equal(0, api.ImageUploadCalls);
        Assert.Equal(0, api.ImageGenerationCalls);
        Assert.False(File.Exists(Path.Combine(_journalRoot, missing.OperationId + ".jsonl")));
        AssertNoCatalogPaidJournal(nextOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogCredentialAnchorKeepsOneExecutionLeaseThroughValidationAndDurableCreation(bool conversion)
    {
        TrackingExecutionGate gate = new();
        FakeApiClient api = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, new FakeArtifactStager(), new FakeHostConnection(), gate);
        HostControlImageCredentialAnchor anchor = await CreateCatalogCredentialAnchorAsync(workflow);
        byte[] originalAnchor = await File.ReadAllBytesAsync(Path.Combine(_journalRoot, anchor.OperationId + ".jsonl"));
        string nextOperation = Guid.NewGuid().ToString("D");
        int acquiredBefore = gate.AcquireCalls;
        int imageFingerprintChecks = 0;
        bool durableChecked = false;
        api.OnImageFingerprint = () => { Assert.True(gate.IsHeld); imageFingerprintChecks++; };
        api.OnConversionFingerprint = () => Assert.True(gate.IsHeld);
        api.OnImageTaskIdDurable = api.OnConversionTaskIdDurable = () =>
        {
            Assert.True(gate.IsHeld);
            durableChecked = true;
        };
        if (conversion) api.TaskSnapshots.Enqueue(Success("task_image123", "image_to_model", null));

        await CreateAnchoredCatalogTaskAsync(workflow, anchor, nextOperation, conversion);

        Assert.True(imageFingerprintChecks > 0); // Both paths must verify the image anchor.
        Assert.True(durableChecked);
        Assert.Equal(acquiredBefore + 1, gate.AcquireCalls);
        Assert.False(gate.IsHeld);
        Assert.Equal(conversion ? 1 : 2, api.CreateImageCalls);
        Assert.Equal(conversion ? 1 : 0, api.CreateConversionCalls);
        Assert.True((await workflow.GetPaidOperationStatusAsync(nextOperation, CancellationToken.None)).TaskIdDurable);
        Assert.Equal(originalAnchor, await File.ReadAllBytesAsync(Path.Combine(_journalRoot, anchor.OperationId + ".jsonl")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogCredentialAnchorCannotSubstituteDifferentOriginalImageOptions(bool conversion)
    {
        FakeApiClient api = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, new FakeArtifactStager(), new FakeHostConnection());
        HostControlImageCredentialAnchor anchor = await CreateCatalogCredentialAnchorAsync(workflow);
        string nextOperation = Guid.NewGuid().ToString("D");

        BridgeCallException error = await Assert.ThrowsAsync<BridgeCallException>(() =>
            CreateAnchoredCatalogTaskAsync(workflow, anchor with { FaceLimit = anchor.FaceLimit + 1000 }, nextOperation, conversion));

        Assert.Equal("catalog_credential_anchor_unverified", error.Code);
        Assert.Equal(1, api.TotalCalls);
        AssertNoCatalogPaidJournal(nextOperation);
    }

    private static HostControlImageCredentialAnchor NewCatalogCredentialAnchor() =>
        new(Guid.NewGuid().ToString("D"), ImageTransfer(), 10_000, false, Tripo.Mcp.TripoV3Client.DefaultModel);

    private static async Task<HostControlImageCredentialAnchor> CreateCatalogCredentialAnchorAsync(Tripo.Mcp.TripoWorkflow workflow)
    {
        HostControlImageCredentialAnchor anchor = NewCatalogCredentialAnchor();
        await workflow.CreateImageTaskAsync(anchor.Image, anchor.FaceLimit, anchor.WithMaterials,
            anchor.OperationId, true, CancellationToken.None, model: anchor.Model);
        return anchor;
    }

    private static async Task CreateAnchoredCatalogTaskAsync(Tripo.Mcp.TripoWorkflow workflow,
        HostControlImageCredentialAnchor anchor, string operationId, bool conversion)
    {
        if (conversion)
        {
            await workflow.CreateCatalogObjConversionAsync(new HostControlCreateObjConversionRequest(
                "task_image123", 10_000, false, operationId, true, CredentialAnchor: anchor), CancellationToken.None);
        }
        else
        {
            StagedImageTransfer nextImage = ImageTransfer() with { Sha256 = new string('f', 64) };
            await workflow.CreateCatalogImageTaskAsync(new HostControlCreateImageTaskRequest(
                nextImage, 10_000, false, operationId, true,
                Model: anchor.Model, CredentialAnchor: anchor), CancellationToken.None);
        }
    }

    private void AssertNoCatalogPaidJournal(string operationId)
    {
        Assert.False(File.Exists(Path.Combine(_journalRoot, operationId + ".jsonl")));
        Assert.False(File.Exists(Path.Combine(_journalRoot, operationId + ".lock")));
    }
}

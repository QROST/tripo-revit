using System.Net;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class TripoWorkflowTests : IDisposable
{
    private readonly string _journalRoot;
    private readonly Tripo.Mcp.PaidOperationJournal _journal;

    public TripoWorkflowTests()
    {
        _journalRoot = Path.Combine(
            Path.GetTempPath(),
            "tripo-paid-operation-tests",
            Guid.NewGuid().ToString("N"));
        _journal = new Tripo.Mcp.PaidOperationJournal(_journalRoot);
    }

    [Fact]
    public async Task ConfirmationFalsePerformsNoExternalOrHostCalls()
    {
        FakeApiClient api = new();
        FakeArtifactStager stager = new();
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                Guid.NewGuid().ToString("D"),
                confirmExternalCost: false,
                CancellationToken.None));

        Assert.Equal(0, api.TotalCalls);
        Assert.Equal(0, stager.CallCount);
        Assert.Equal(0, host.ContextCalls);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task MissingCredentialPreflightCreatesNoPaidJournal()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailTextCredentialFingerprint = true,
        };
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            new FakeHostConnection());

        await Assert.ThrowsAsync<
            Tripo.Mcp.TripoCredentialPreflightException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));

        Assert.False(
            File.Exists(
                Path.Combine(
                    _journalRoot,
                    operationId + ".jsonl")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    _journalRoot,
                    operationId + ".lock")));
        Assert.Equal(0, api.CreateTextCalls);
        Assert.Equal(0, api.GetTaskCalls);
    }

    [Fact]
    public async Task CredentialLossAfterJournalCreationIsDurablyRejected()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailTextCredentialBeforeSend = true,
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await Assert.ThrowsAsync<
            Tripo.Mcp.TripoPaidRequestRejectedException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt status =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);

        Assert.Equal("request_rejected", status.State);
        Assert.False(status.MayHaveCreatedRemoteTask);
        Assert.Equal(0, api.CreateTextCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task ImageCredentialLossBeforeUploadIsDurablyRejected()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailImageCredentialBeforeSend = true,
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await Assert.ThrowsAsync<
            Tripo.Mcp.TripoPaidRequestRejectedException>(
            () => workflow.CreateImageTaskAsync(
                ImageTransfer(),
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt status =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);

        Assert.Equal("request_rejected", status.State);
        Assert.Equal("upload", status.FailureStage);
        Assert.False(status.MayHaveCreatedRemoteTask);
        Assert.Equal(0, api.CreateImageCalls);
        Assert.Equal(0, api.ImageUploadCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task TextCreationReturnsPaidTaskIdWithoutPolling()
    {
        FakeApiClient api = new();
        FakeHostConnection host = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.TextTaskCreationReceipt receipt =
            await workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal("task_source123", receipt.TaskId);
        Assert.Equal(Tripo.Mcp.TripoV3Client.DefaultModel, receipt.Model);
        Assert.Equal(1, api.CreateTextCalls);
        Assert.Equal(0, api.GetTaskCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task PaidTextWorkflowHoldsTheSidecarExecutionGateUntilDurable()
    {
        TrackingExecutionGate executionGate = new();
        FakeApiClient api = new()
        {
            OnTextFingerprint = () => Assert.True(executionGate.IsHeld),
            OnTextTaskIdDurable = () => Assert.True(executionGate.IsHeld),
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host,
            executionGate);

        await workflow.CreateTextTaskAsync(
            "a chair",
            10_000,
            withMaterials: false,
            Guid.NewGuid().ToString("D"),
            confirmExternalCost: true,
            CancellationToken.None);

        Assert.Equal(1, executionGate.AcquireCalls);
        Assert.False(executionGate.IsHeld);
    }

    [Fact]
    public async Task LostTextCreationResponseCanReplayTheDurableTaskId()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.TextTaskCreationReceipt first =
            await workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);
        Tripo.Mcp.TextTaskCreationReceipt replay =
            await workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(1, api.CreateTextCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task PersistedImageFileTokenResumesWithoutReupload()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailBeforeImageGenerationOnce = true,
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);
        Tripo.Bridge.StagedImageTransfer image = ImageTransfer();

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateImageTaskAsync(
                image,
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt resumable =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);

        Tripo.Mcp.ImageTaskCreationReceipt receipt =
            await workflow.CreateImageTaskAsync(
                image,
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal(
            "image_file_token_persisted",
            resumable.State);
        Assert.True(resumable.CanResumeCreation);
        Assert.Equal("task_image123", receipt.TaskId);
        Assert.Equal(image.Sha256, receipt.ImageSha256);
        Assert.Equal(2, api.CreateImageCalls);
        Assert.Equal(1, api.ImageUploadCalls);
        Assert.Equal(1, api.ImageGenerationCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Theory]
    [InlineData("upload", false)]
    [InlineData("generation", true)]
    public async Task ImageOutcomeUnknownCannotBeAutomaticallyResent(
        string failureStage,
        bool mayHaveCreatedRemoteTask)
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailImageStage = failureStage,
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);
        Tripo.Bridge.StagedImageTransfer image = ImageTransfer();

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => workflow.CreateImageTaskAsync(
                image,
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt status =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);
        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateImageTaskAsync(
                image,
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));

        Assert.Equal("outcome_unknown", status.State);
        Assert.Equal(failureStage, status.FailureStage);
        Assert.Equal(
            mayHaveCreatedRemoteTask,
            status.MayHaveCreatedRemoteTask);
        Assert.False(status.CanResumeCreation);
        Assert.Equal(1, api.CreateImageCalls);
        Assert.Equal(1, api.ImageUploadCalls);
        Assert.Equal(
            failureStage == "generation" ? 1 : 0,
            api.ImageGenerationCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task ReusingPaidOperationWithDifferentArgumentsFailsClosed()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await workflow.CreateTextTaskAsync(
            "a chair",
            10_000,
            withMaterials: false,
            operationId,
            confirmExternalCost: true,
            CancellationToken.None);
        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateTextTaskAsync(
                "a different chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));

        Assert.Equal(1, api.CreateTextCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task FlippingMaterialFlagOnPaidReplayFailsClosed()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await workflow.CreateTextTaskAsync(
            "a chair",
            10_000,
            withMaterials: false,
            operationId,
            confirmExternalCost: true,
            CancellationToken.None);
        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: true,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));

        Assert.Equal(1, api.CreateTextCalls);
    }

    [Fact]
    public async Task AmbiguousPaidPostOutcomeCannotBeAutomaticallyResent()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            FailTextAfterDispatch = true,
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoApiException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt status =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);
        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));

        Assert.Equal("outcome_unknown", status.State);
        Assert.True(status.MayHaveCreatedRemoteTask);
        Assert.False(status.CanResumeCreation);
        Assert.Equal(1, api.CreateTextCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task PaidOperationIsBoundToTheApiCredentialScope()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient firstApi = new()
        {
            OperationScopeFingerprint = new string('a', 64),
        };
        FakeHostConnection firstHost = new();
        Tripo.Mcp.TripoWorkflow firstWorkflow = CreateWorkflow(
            firstApi,
            new FakeArtifactStager(),
            firstHost);
        await firstWorkflow.CreateTextTaskAsync(
            "a chair",
            10_000,
            withMaterials: false,
            operationId,
            confirmExternalCost: true,
            CancellationToken.None);

        FakeApiClient secondApi = new()
        {
            OperationScopeFingerprint = new string('c', 64),
        };
        FakeHostConnection secondHost = new();
        Tripo.Mcp.TripoWorkflow secondWorkflow = CreateWorkflow(
            secondApi,
            new FakeArtifactStager(),
            secondHost);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => secondWorkflow.CreateTextTaskAsync(
                "a chair",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Assert.Equal(0, secondApi.TotalCalls);
        Assert.Equal(0, secondHost.ContextCalls);
    }

    [Fact]
    public async Task ConversionCreationDoesNotReadHostContext()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_source123",
            "text_to_model",
            modelUrl: null));
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjConversionCreationReceipt receipt =
            await workflow.CreateObjConversionAsync(
                "task_source123",
                10_000,
                withMaterials: false,
                Guid.NewGuid().ToString("D"),
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.Equal(1, api.GetTaskCalls);
        Assert.Equal(1, api.CreateConversionCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ConversionSourceReadRejectionCreatesNoPaidPost(
        HttpStatusCode statusCode)
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new()
        {
            TaskReadException = new Tripo.Mcp.TripoApiException(
                "The credential was rejected.",
                statusCode),
        };
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await Assert.ThrowsAsync<
            Tripo.Mcp.TripoPaidRequestRejectedException>(
            () => workflow.CreateObjConversionAsync(
                "task_source123",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None));
        Tripo.Mcp.PaidOperationStatusReceipt status =
            await workflow.GetPaidOperationStatusAsync(
                operationId,
                CancellationToken.None);

        Assert.Equal("request_rejected", status.State);
        Assert.False(status.MayHaveCreatedRemoteTask);
        Assert.Equal(1, api.GetTaskCalls);
        Assert.Equal(0, api.CreateConversionCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task ConversionCreationReturnsPaidTaskIdWithoutPollingIt()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_source123",
            "text_to_model",
            modelUrl: null));
        FakeHostConnection host = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjConversionCreationReceipt receipt =
            await workflow.CreateObjConversionAsync(
                "task_source123",
                10_000,
                withMaterials: true,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal("task_source123", receipt.SourceTaskId);
        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.Equal("OBJ", receipt.Format);
        Assert.Equal(1, api.GetTaskCalls);
        Assert.Equal(1, api.CreateConversionCalls);
        Assert.True(api.LastConversionWithMaterials);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task LostConversionResponseCanReplayTheDurableTaskId()
    {
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_source123",
            "text_to_model",
            modelUrl: null));
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjConversionCreationReceipt first =
            await workflow.CreateObjConversionAsync(
                "task_source123",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);
        Tripo.Mcp.ObjConversionCreationReceipt replay =
            await workflow.CreateObjConversionAsync(
                "task_source123",
                10_000,
                withMaterials: false,
                operationId,
                confirmExternalCost: true,
                CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(1, api.GetTaskCalls);
        Assert.Equal(1, api.CreateConversionCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task DocumentSwitchAfterStagingPreventsHostMutation()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(Guid.NewGuid().ToString("D")));
        FakeArtifactStager stager = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                Guid.NewGuid().ToString("D"),
                "mesh",
                applyMaterials: false,
                CancellationToken.None));

        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, host.ImportCalls);
        Assert.Equal(0, api.CreateConversionCalls);
    }

    [Fact]
    public async Task ImportStageUsesCallerOperationIdAndReturnsTypedReceipt()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj",
            credits: 5));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(requestedSession));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjTaskImportReceipt receipt =
            await workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                operationId,
                "mesh",
                applyMaterials: false,
                CancellationToken.None);

        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.Equal(5, receipt.ConversionCreditsConsumed);
        Assert.Equal(operationId, host.LastImportRequest?.IdempotencyKey);
        Assert.Equal("mesh", host.LastImportRequest?.ImportMode);
        Assert.Equal(1, host.ImportCalls);
    }

    [Fact]
    public async Task ImportStageWarnsWhenAssociateLevelIsDowngradedOnMeshMode()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(requestedSession));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        // associateLevel=true on a Rhino host with importMode=mesh resolves to
        // mode 'mesh', which is not 'family', so the request must downgrade to
        // associateLevel=false and surface a warning rather than silently drop.
        Tripo.Mcp.ObjTaskImportReceipt receipt =
            await workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                operationId,
                "mesh",
                applyMaterials: false,
                CancellationToken.None,
                placementMode: "origin",
                associateLevel: true);

        Assert.NotNull(receipt.Warnings);
        Assert.NotEmpty(receipt.Warnings);
        Assert.Contains(receipt.Warnings, w => w.Contains("associateLevel", StringComparison.Ordinal));
        // The host request must have actually received associateLevel=false.
        Assert.False(host.LastImportRequest?.AssociateLevel);
    }

    [Fact]
    public async Task ImportStageOmitsWarningWhenAssociateLevelAppliesOnFamilyMode()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        string operationId = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        // Revit host so importMode=native resolves to family.
        host.Contexts.Enqueue(Context(requestedSession, host: "revit"));
        host.Contexts.Enqueue(Context(requestedSession, host: "revit"));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjTaskImportReceipt receipt =
            await workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                operationId,
                "native",
                applyMaterials: false,
                CancellationToken.None,
                placementMode: "origin",
                associateLevel: true);

        // family mode keeps associateLevel=true and emits no downgrade warning.
        Assert.True(host.LastImportRequest?.AssociateLevel);
        Assert.Empty(receipt.Warnings);
    }

    [Fact]
    public async Task StageObjReturnsExactMeshDescriptorWithoutHostMutation()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj",
            credits: 5));
        FakeHostConnection host = new();
        FakeArtifactStager stager = new()
        {
            MtlEntry = "model.mtl",
        };
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        Tripo.Mcp.ObjTaskStageReceipt receipt =
            await workflow.StageObjTaskAsync(
                "task_conversion123",
                includeMaterials: true,
                CancellationToken.None);

        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.Equal(5, receipt.ConversionCreditsConsumed);
        Assert.Equal(new string('c', 64), receipt.Mesh.BundleId);
        Assert.Equal("model.obj", receipt.Mesh.ObjEntry);
        Assert.Equal("model.mtl", receipt.Mesh.MtlEntry);
        Assert.Equal(2, receipt.Mesh.Entries.Count);
        Assert.Equal("meters", receipt.Mesh.SourceUnit);
        Assert.Equal("Y", receipt.Mesh.UpAxis);
        Assert.Equal("right", receipt.Mesh.Handedness);
        Assert.True(receipt.Mesh.ApplyMaterials);
        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, host.ContextCalls);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task StageObjDoesNotReadHostContext()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        FakeArtifactStager stager = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        Tripo.Mcp.ObjTaskStageReceipt receipt =
            await workflow.StageObjTaskAsync(
                "task_conversion123",
                includeMaterials: false,
                CancellationToken.None);

        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, host.ContextCalls);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task StageObjWithMaterialsRequiresAnMtlEntry()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        FakeArtifactStager stager = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.StageObjTaskAsync(
                "task_conversion123",
                includeMaterials: true,
                CancellationToken.None));

        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, host.ContextCalls);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task ImportCanonicalizesUuidBeforeHostIdempotencyLookup()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        const string uppercaseOperationId =
            "ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB";
        const string canonicalOperationId =
            "abcdefab-cdef-abcd-efab-cdefabcdefab";
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(requestedSession));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.ObjTaskImportReceipt receipt =
            await workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                uppercaseOperationId,
                "mesh",
                applyMaterials: false,
                CancellationToken.None);

        Assert.Equal(canonicalOperationId, receipt.OperationId);
        Assert.Equal(
            canonicalOperationId,
            host.LastImportRequest?.IdempotencyKey);
    }

    [Fact]
    public async Task NativeImportModeResolvesToInstanceOnRhino()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession, "rhino"));
        host.Contexts.Enqueue(Context(requestedSession, "rhino"));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await workflow.ImportObjTaskAsync(
            "task_conversion123",
            "Chair",
            requestedSession,
            Guid.NewGuid().ToString("D"),
            "native",
            applyMaterials: false,
            CancellationToken.None);

        Assert.Equal("instance", host.LastImportRequest?.ImportMode);
    }

    [Fact]
    public async Task NativeImportModeResolvesToFamilyOnRevit()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession, "revit"));
        host.Contexts.Enqueue(Context(requestedSession, "revit"));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        await workflow.ImportObjTaskAsync(
            "task_conversion123",
            "Chair",
            requestedSession,
            Guid.NewGuid().ToString("D"),
            "native",
            applyMaterials: false,
            CancellationToken.None);

        Assert.Equal("family", host.LastImportRequest?.ImportMode);
    }

    [Fact]
    public async Task ApplyMaterialsWithoutMtlEntryFailsClosedBeforeHostCall()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(requestedSession));
        FakeArtifactStager stager = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => workflow.ImportObjTaskAsync(
                "task_conversion123",
                "Chair",
                requestedSession,
                Guid.NewGuid().ToString("D"),
                "mesh",
                applyMaterials: true,
                CancellationToken.None));

        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task ApplyMaterialsWithMtlEntryReachesTheHost()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "convert_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        host.Contexts.Enqueue(Context(requestedSession));
        FakeArtifactStager stager = new()
        {
            MtlEntry = "model.mtl",
        };
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        await workflow.ImportObjTaskAsync(
            "task_conversion123",
            "Chair",
            requestedSession,
            Guid.NewGuid().ToString("D"),
            "mesh",
            applyMaterials: true,
            CancellationToken.None);

        Assert.Equal(1, host.ImportCalls);
        Assert.True(host.LastImportRequest?.ApplyMaterials);
        Assert.Equal("model.mtl", host.LastImportRequest?.MtlEntry);
    }

    [Fact]
    public async Task PendingTaskMustBePolledBeforeStartingNextStage()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(new Tripo.Mcp.TripoTaskSnapshot(
            "task_source123",
            "text_to_model",
            "running",
            42,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null));
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => workflow.CreateObjConversionAsync(
                    "task_source123",
                    10_000,
                    withMaterials: false,
                    Guid.NewGuid().ToString("D"),
                    confirmExternalCost: true,
                    CancellationToken.None));

        Assert.Contains("tripo_task_status", exception.Message);
        Assert.Equal(0, api.CreateConversionCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task HungTaskReadIsBoundedByTheWorkflowDeadline()
    {
        FakeApiClient api = new()
        {
            HangTaskReads = true,
        };
        Tripo.Mcp.TripoWorkflow workflow = new(
            api,
            new FakeArtifactStager(),
            new FakeHostConnection(),
            _journal,
            new Tripo.Mcp.TripoWorkflowOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(2),
                MaximumReadRetries: 1));

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => workflow.GetTaskStatusAsync(
                    "task_source123",
                    CancellationToken.None));

        Assert.Contains("reached its deadline", exception.Message);
        Assert.Equal(1, api.GetTaskCalls);
    }

    [Fact]
    public async Task GetTaskStatusAsyncPassesThroughUnknownStatusVerbatim()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(new Tripo.Mcp.TripoTaskSnapshot(
            "task_source123",
            "text_to_model",
            "reviewing",
            50,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            new FakeHostConnection());

        Tripo.Mcp.TaskStatusReceipt receipt =
            await workflow.GetTaskStatusAsync("task_source123", CancellationToken.None);

        Assert.Equal("reviewing", receipt.Status);
        Assert.Equal(1, api.GetTaskCalls);
    }

    [Fact]
    public async Task UnknownStatusStillBlocksObjConversionCreation()
    {
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(new Tripo.Mcp.TripoTaskSnapshot(
            "task_source123",
            "text_to_model",
            "reviewing",
            50,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null));
        FakeHostConnection host = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => workflow.CreateObjConversionAsync(
                    "task_source123",
                    10_000,
                    withMaterials: false,
                    Guid.NewGuid().ToString("D"),
                    confirmExternalCost: true,
                    CancellationToken.None));

        Assert.Contains("unsupported status", exception.Message);
        Assert.Equal(0, api.CreateConversionCalls);
        Assert.Equal(0, host.ContextCalls);
    }

    [Fact]
    public async Task ImportRejectsTaskThatIsNotAnObjConversion()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(Success(
            "task_conversion123",
            "text_to_model",
            "https://cdn.example.test/model.obj"));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        FakeArtifactStager stager = new();
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(api, stager, host);

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => workflow.ImportObjTaskAsync(
                    "task_conversion123",
                    "Chair",
                    requestedSession,
                    Guid.NewGuid().ToString("D"),
                    "mesh",
                    applyMaterials: false,
                    CancellationToken.None));

        Assert.Contains(
            "conversion",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stager.CallCount);
        Assert.Equal(0, host.ImportCalls);
    }

    [Fact]
    public async Task UnknownStatusStillBlocksObjImport()
    {
        string requestedSession = Guid.NewGuid().ToString("D");
        FakeApiClient api = new();
        api.TaskSnapshots.Enqueue(new Tripo.Mcp.TripoTaskSnapshot(
            "task_conversion123",
            "convert_model",
            "reviewing",
            50,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null));
        FakeHostConnection host = new();
        host.Contexts.Enqueue(Context(requestedSession));
        Tripo.Mcp.TripoWorkflow workflow = CreateWorkflow(
            api,
            new FakeArtifactStager(),
            host);

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => workflow.ImportObjTaskAsync(
                    "task_conversion123",
                    "Chair",
                    requestedSession,
                    Guid.NewGuid().ToString("D"),
                    "mesh",
                    applyMaterials: false,
                    CancellationToken.None));

        Assert.Contains("unsupported status", exception.Message);
        Assert.Equal(0, host.ImportCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_journalRoot))
        {
            Directory.Delete(_journalRoot, recursive: true);
        }
    }

    private Tripo.Mcp.TripoWorkflow CreateWorkflow(
        FakeApiClient api,
        FakeArtifactStager stager,
        FakeHostConnection host,
        Tripo.Bridge.ICredentialWorkflowExecutionGate? executionGate = null) =>
        new(
            api,
            stager,
            host,
            _journal,
            new Tripo.Mcp.TripoWorkflowOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(2),
                MaximumReadRetries: 1),
            timeProvider: null,
            executionGate: executionGate);

    private static Tripo.Mcp.TripoTaskSnapshot Success(
        string taskId,
        string type,
        string? modelUrl,
        decimal? credits = null) =>
        new(
            taskId,
            type,
            "success",
            100,
            modelUrl is null ? null : new Tripo.Mcp.TripoTaskOutput(modelUrl, null),
            credits,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);

    private static Tripo.Bridge.HostContextReceipt Context(
        string sessionId,
        string host = "rhino") =>
        new(
            host,
            "8-test",
            Environment.ProcessId,
            sessionId,
            "Test",
            "Meters",
            [
                Tripo.Bridge.BridgeConstants.ContextMethod,
                Tripo.Bridge.BridgeConstants.ImportMeshMethod,
            ]);

    private static Tripo.Bridge.StagedImageTransfer ImageTransfer() =>
        new(
            "11111111-1111-1111-1111-111111111111",
            new string('d', 64),
            10,
            "image/png");

    private sealed class FakeApiClient : Tripo.Mcp.ITripoApiClient
    {
        public Queue<Tripo.Mcp.TripoTaskSnapshot> TaskSnapshots { get; } = new();

        public int CreateTextCalls { get; private set; }

        public int CreateConversionCalls { get; private set; }

        public int CreateImageCalls { get; private set; }

        public int ImageUploadCalls { get; private set; }

        public int ImageGenerationCalls { get; private set; }

        public int GetTaskCalls { get; private set; }

        public bool HangTaskReads { get; init; }

        public bool FailTextAfterDispatch { get; init; }

        public bool FailTextCredentialFingerprint { get; init; }

        public bool FailTextCredentialBeforeSend { get; init; }

        public bool FailImageCredentialBeforeSend { get; init; }

        public Tripo.Mcp.TripoApiException? TaskReadException { get; init; }

        public string? FailImageStage { get; init; }

        public bool FailBeforeImageGenerationOnce { get; set; }

        public bool LastConversionWithMaterials { get; private set; }

        public string OperationScopeFingerprint { get; init; } =
            new string('b', 64);

        public Action? OnTextFingerprint { get; init; }

        public Action? OnTextTaskIdDurable { get; init; }

        public int TotalCalls =>
            CreateTextCalls +
            CreateImageCalls +
            CreateConversionCalls +
            GetTaskCalls;

        public string ResolveEffectiveModel(string? requestedModel = null) =>
            string.IsNullOrWhiteSpace(requestedModel)
                ? Tripo.Mcp.TripoV3Client.DefaultModel
                : requestedModel.Trim();

        public string GetTextTaskOperationFingerprint(
            Tripo.Mcp.TextGenerationOptions options)
        {
            OnTextFingerprint?.Invoke();
            if (FailTextCredentialFingerprint)
            {
                throw new Tripo.Mcp.TripoCredentialException(
                    "The local credential is missing.");
            }

            return Fingerprint(
                $"text|{OperationScopeFingerprint}|" +
                $"{options.Model}|{options.Prompt}|{options.FaceLimit}|" +
                $"{options.WithMaterials}");
        }

        public string GetObjConversionOperationFingerprint(
            string taskId,
            int faceLimit,
            bool withMaterials) =>
            Fingerprint(
                $"convert|{OperationScopeFingerprint}|" +
                $"{taskId}|{faceLimit}|{withMaterials}");

        public string GetImageTaskOperationFingerprint(
            Tripo.Mcp.ImageGenerationOptions options) =>
            Fingerprint(
                $"image|{OperationScopeFingerprint}|" +
                $"{options.Model}|{options.Image.Sha256}|" +
                $"{options.Image.ByteLength}|{options.Image.MediaType}|" +
                $"{options.FaceLimit}|{options.WithMaterials}");

        public async Task<string> CreateTextModelAsync(
            Tripo.Mcp.TextGenerationOptions options,
            Tripo.Mcp.ITaskCreationCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                GetTextTaskOperationFingerprint(options),
                checkpoint.RequestFingerprint);
            if (FailTextCredentialBeforeSend)
            {
                throw new Tripo.Mcp.TripoCredentialException(
                    "The local credential disappeared before dispatch.");
            }

            await checkpoint.BeforeSendAsync(cancellationToken);
            CreateTextCalls++;
            if (FailTextAfterDispatch)
            {
                await checkpoint.OutcomeUnknownAsync(
                    "transport_failure",
                    "The paid request may have reached the provider.");
                throw new Tripo.Mcp.TripoApiException(
                    "The paid request outcome is unknown.");
            }

            await checkpoint.TaskIdReceivedAsync("task_source123");
            OnTextTaskIdDurable?.Invoke();
            return "task_source123";
        }

        public async Task<string> CreateObjConversionAsync(
            string taskId,
            int faceLimit,
            bool withMaterials,
            Tripo.Mcp.ITaskCreationCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                GetObjConversionOperationFingerprint(
                    taskId,
                    faceLimit,
                    withMaterials),
                checkpoint.RequestFingerprint);
            await checkpoint.BeforeSendAsync(cancellationToken);
            CreateConversionCalls++;
            LastConversionWithMaterials = withMaterials;
            await checkpoint.TaskIdReceivedAsync("task_conversion123");
            return "task_conversion123";
        }

        public async Task<string> CreateImageModelAsync(
            Tripo.Mcp.ImageGenerationOptions options,
            Tripo.Mcp.IImageTaskCreationCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            Assert.Equal(
                GetImageTaskOperationFingerprint(
                    options),
                checkpoint.RequestFingerprint);
            if (FailImageCredentialBeforeSend)
            {
                throw new Tripo.Mcp.TripoCredentialException(
                    "The local credential disappeared before image upload.");
            }

            CreateImageCalls++;
            if (checkpoint.FileToken is null)
            {
                await checkpoint.BeforeImageUploadAsync(cancellationToken);
                ImageUploadCalls++;
                if (FailImageStage == "upload")
                {
                    await checkpoint.ImageOutcomeUnknownAsync(
                        "upload",
                        "transport_failure",
                        "The image upload outcome is unknown.");
                    throw new Tripo.Mcp.TripoApiException(
                        "The image upload outcome is unknown.");
                }

                await checkpoint.ImageFileTokenReceivedAsync(
                    "file_resume123",
                    new string('e', 64));
            }

            if (FailBeforeImageGenerationOnce)
            {
                FailBeforeImageGenerationOnce = false;
                throw new Tripo.Mcp.TripoWorkflowException(
                    "Stopped before image generation dispatch.");
            }

            await checkpoint.BeforeImageGenerationAsync(cancellationToken);
            ImageGenerationCalls++;
            if (FailImageStage == "generation")
            {
                await checkpoint.ImageOutcomeUnknownAsync(
                    "generation",
                    "transport_failure",
                    "The image generation outcome is unknown.");
                throw new Tripo.Mcp.TripoApiException(
                    "The image generation outcome is unknown.");
            }

            await checkpoint.TaskIdReceivedAsync("task_image123");
            return "task_image123";
        }

        private static string Fingerprint(string value) =>
            Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();

        public Task<Tripo.Mcp.TripoTaskSnapshot> GetTaskAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            GetTaskCalls++;
            if (TaskReadException is not null)
            {
                return Task.FromException<
                    Tripo.Mcp.TripoTaskSnapshot>(TaskReadException);
            }

            if (HangTaskReads)
            {
                TaskCompletionSource<Tripo.Mcp.TripoTaskSnapshot> completion =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                return completion.Task;
            }

            return Task.FromResult(TaskSnapshots.Dequeue());
        }
    }

    private sealed class FakeArtifactStager : Tripo.Mcp.IArtifactStager
    {
        public int CallCount { get; private set; }

        public string? MtlEntry { get; init; }

        public Task<Tripo.Bridge.StagedBundle> StageBundleAsync(
            Uri modelUri,
            CancellationToken cancellationToken)
        {
            CallCount++;
            List<Tripo.Bridge.StagedBundleEntry> entries =
            [
                new Tripo.Bridge.StagedBundleEntry("model.obj", new string('a', 64), 42),
            ];
            if (MtlEntry is not null)
            {
                entries.Add(new Tripo.Bridge.StagedBundleEntry(
                    MtlEntry,
                    new string('b', 64),
                    24));
            }

            return Task.FromResult(
                new Tripo.Bridge.StagedBundle(
                    new string('c', 64),
                    "model.obj",
                    MtlEntry,
                    entries,
                    "/not-used-in-fake"));
        }
    }

    private sealed class FakeHostConnection : Tripo.Mcp.IHostConnection
    {
        public Queue<Tripo.Bridge.HostContextReceipt> Contexts { get; } = new();

        public int ContextCalls { get; private set; }

        public int ImportCalls { get; private set; }

        public Tripo.Bridge.ImportMeshRequest? LastImportRequest { get; private set; }

        public Task<Tripo.Bridge.HostContextReceipt> GetContextAsync(
            CancellationToken cancellationToken)
        {
            ContextCalls++;
            return Task.FromResult(Contexts.Dequeue());
        }

        public Task<Tripo.Bridge.HostImportReceipt> ImportMeshAsync(
            Tripo.Bridge.ImportMeshRequest request,
            CancellationToken cancellationToken)
        {
            ImportCalls++;
            LastImportRequest = request;
            return Task.FromResult(
                new Tripo.Bridge.HostImportReceipt(
                    "rhino",
                    request.DocumentSessionId,
                    request.IdempotencyKey,
                    Guid.NewGuid().ToString("D"),
                    3,
                    1,
                    0,
                    "committed",
                    request.ImportMode,
                    request.ApplyMaterials && request.MtlEntry is not null ? 1 : 0,
                    0,
                    null));
        }
    }

    private sealed class TrackingExecutionGate :
        Tripo.Bridge.ICredentialWorkflowExecutionGate
    {
        private int _held;

        public int AcquireCalls { get; private set; }

        public bool IsHeld => Volatile.Read(ref _held) == 1;

        public IDisposable Acquire()
        {
            Assert.Equal(0, Interlocked.Exchange(ref _held, 1));
            AcquireCalls++;
            return new ReleaseLease(this);
        }

        private sealed class ReleaseLease : IDisposable
        {
            private TrackingExecutionGate? _owner;

            public ReleaseLease(TrackingExecutionGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                TrackingExecutionGate? owner =
                    Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    Assert.Equal(1, Interlocked.Exchange(ref owner._held, 0));
                }
            }
        }
    }
}

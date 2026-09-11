using System.Text.Json;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed partial class HostControlDispatcherTests
{
    [Fact]
    public async Task CredentialSetResponseNeverContainsTheApiKey()
    {
        const string secret = "secret-that-must-not-be-returned";
        FakeCredentialService credentials = new();
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            credentials,
            workflow);
        Tripo.Bridge.HostControlSetApiKeyRequest request = new(
            secret,
            persist: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CredentialSetMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);
        string serialized = JsonSerializer.Serialize(
            result,
            Tripo.Bridge.BridgeJson.Options);

        Assert.Equal(secret, credentials.LastSetKey);
        Assert.True(credentials.LastPersist);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextWorkflowMethodMapsTheExactPaidArguments()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Bridge.HostControlCreateTextTaskRequest request = new(
            "a timber pavilion",
            25_000,
            WithMaterials: true,
            operationId,
            ConfirmExternalCost: true,
            RequireExistingOperation: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlTextTaskCreationReceipt receipt = Assert.IsType<
            Tripo.Bridge.HostControlTextTaskCreationReceipt>(result);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(request, workflow.LastTextRequest);
        Assert.Equal(1, workflow.CreateTextCalls);
    }

    [Fact]
    public async Task ImageWorkflowMethodMapsOpaqueIdentityAndRetryRequirement()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlCreateImageTaskRequest request = new(
            new Tripo.Bridge.StagedImageTransfer(
                "11111111-1111-1111-1111-111111111111",
                new string('a', 64),
                10,
                "image/png"),
            12_000,
            WithMaterials: true,
            Guid.NewGuid().ToString("D"),
            ConfirmExternalCost: true,
            RequireExistingOperation: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CreateImageTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlImageTaskCreationReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlImageTaskCreationReceipt>(
                result);
        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(request.Image.Sha256, receipt.ImageSha256);
        Assert.Equal(request, workflow.LastImageRequest);
    }

    [Fact]
    public async Task StageImageMethodDispatchesTheUrl()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlStageImageRequest request = new(
            "https://example.com/reference.png");

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.StageImageMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.StagedImageTransfer transfer =
            Assert.IsType<Tripo.Bridge.StagedImageTransfer>(result);
        Assert.NotNull(workflow.LastStageImageUrl);
        Assert.Equal(
            "https://example.com/reference.png",
            workflow.LastStageImageUrl!.ToString());
        Assert.Equal("image/png", transfer.MediaType);
    }

    [Fact]
    public async Task StageImageMethodRejectsRelativeUrl()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlStageImageRequest request =
            new("not-a-url");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => dispatcher.DispatchAsync(
                Tripo.Bridge.HostControlConstants.StageImageMethod,
                Tripo.Bridge.BridgeJson.ToElement(request),
                CancellationToken.None));
    }

    [Fact]
    public async Task StageObjMethodMapsStageOnlyReceipt()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlStageObjTaskRequest request = new(
            "task_conversion123",
            IncludeMaterials: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.StageObjTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlObjTaskStageReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlObjTaskStageReceipt>(
                result);
        Assert.Equal(request, workflow.LastStageRequest);
        Assert.Equal("task_conversion123", receipt.ConversionTaskId);
        Assert.True(receipt.Mesh.ApplyMaterials);
    }

    [Fact]
    public async Task ImportWorkflowMethodPreservesWarnings()
    {
        const string warning =
            "associateLevel was downgraded for this import.";
        FakeWorkflow workflow = new()
        {
            ImportWarnings = [warning],
        };
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlImportObjTaskRequest request = new(
            "task_conversion123",
            "Chair",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "mesh",
            ApplyMaterials: false,
            AssociateLevel: true);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.ImportObjTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);
        Tripo.Bridge.HostControlObjTaskImportReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlObjTaskImportReceipt>(
                result);
        string serialized = JsonSerializer.Serialize(
            receipt,
            Tripo.Bridge.BridgeJson.Options);

        Assert.Equal([warning], receipt.Warnings);
        Assert.Contains(warning, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCredentialPayloadReturnsTypedErrorWithoutPayload()
    {
        const string secret = "secret-never-in-error";
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            new FakeWorkflow());
        JsonElement invalidPayload = Tripo.Bridge.BridgeJson.ToElement(
            new
            {
                apiKey = secret,
                persist = "not-a-boolean",
            });

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => dispatcher.DispatchAsync(
                    Tripo.Bridge.HostControlConstants.CredentialSetMethod,
                    invalidPayload,
                    CancellationToken.None));

        Assert.Equal("invalid_request", exception.Code);
        Assert.DoesNotContain(
            secret,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalCredentialFailureHasADispatchSafeErrorCode()
    {
        FakeWorkflow workflow = new()
        {
            ThrowCredentialOnText = true,
        };
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlCreateTextTaskRequest request = new(
            "a chair",
            10_000,
            WithMaterials: false,
            Guid.NewGuid().ToString("D"),
            ConfirmExternalCost: true,
            RequireExistingOperation: false);

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => dispatcher.DispatchAsync(
                    Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
                    Tripo.Bridge.BridgeJson.ToElement(request),
                    CancellationToken.None));

        Assert.Equal(
            Tripo.Bridge.HostControlConstants.CredentialInvalidError,
            exception.Code);
        Assert.Equal(1, workflow.CreateTextCalls);
    }

    [Fact]
    public async Task DurablePaidRejectionHasARecoverableErrorCode()
    {
        FakeWorkflow workflow = new()
        {
            ThrowPaidCredentialRejectionOnText = true,
        };
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlCreateTextTaskRequest request = new(
            "a chair",
            10_000,
            WithMaterials: false,
            Guid.NewGuid().ToString("D"),
            ConfirmExternalCost: true,
            RequireExistingOperation: false);

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => dispatcher.DispatchAsync(
                    Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
                    Tripo.Bridge.BridgeJson.ToElement(request),
                    CancellationToken.None));

        Assert.Equal(
            Tripo.Bridge.HostControlConstants.CredentialRejectedError,
            exception.Code);
    }

    [Fact]
    public async Task ImagePaidRejectionHasARecoverableErrorCode()
    {
        FakeWorkflow workflow = new()
        {
            ThrowPaidCredentialRejectionOnImage = true,
        };
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        Tripo.Bridge.HostControlCreateImageTaskRequest request = new(
            new Tripo.Bridge.StagedImageTransfer(
                "11111111-1111-1111-1111-111111111111",
                new string('a', 64),
                10,
                "image/png"),
            10_000,
            WithMaterials: false,
            Guid.NewGuid().ToString("D"),
            ConfirmExternalCost: true,
            RequireExistingOperation: false);

        Tripo.Bridge.HostControlCallException exception =
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => dispatcher.DispatchAsync(
                    Tripo.Bridge.HostControlConstants.CreateImageTaskMethod,
                    Tripo.Bridge.BridgeJson.ToElement(request),
                    CancellationToken.None));

        Assert.Equal(
            Tripo.Bridge.HostControlConstants.CredentialRejectedError,
            exception.Code);
    }

    [Fact]
    public void HostControlCommandLineRequiresAnExactPositivePid()
    {
        Assert.False(
            Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                Array.Empty<string>(),
                out _));
        Assert.Throws<ArgumentException>(
            () => Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                ["--host-control"],
                out _));
        Assert.Throws<ArgumentException>(
            () => Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
                ["--host-control", "--host-pid", "0"],
                out _));

        bool parsed = Tripo.Mcp.SidecarCommandLine.TryParseHostControl(
            [
                "--host-control",
                "--host-pid",
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ],
            out Tripo.Mcp.HostControlCommandLineOptions? options);

        Assert.True(parsed);
        Assert.Equal(Environment.ProcessId, options?.HostProcessId);
    }

    [Fact]
    public async Task ListModelsReturnsCuratedCatalog()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.ListModelsMethod,
            Tripo.Bridge.BridgeJson.ToElement(new { }),
            CancellationToken.None);

        Tripo.Bridge.HostControlModelCatalogReceipt receipt =
            Assert.IsType<Tripo.Bridge.HostControlModelCatalogReceipt>(result);
        Assert.Equal(
            Tripo.Bridge.TripoModelCatalog.DefaultModelId,
            receipt.DefaultModelId);
        Assert.Equal(
            Tripo.Bridge.TripoModelCatalog.SourceCurated,
            receipt.Source);
        Assert.Contains(
            receipt.Models,
            model => string.Equals(
                model.Id,
                "P1-20260311",
                StringComparison.Ordinal));
        Assert.Contains(
            Tripo.Bridge.HostControlConstants.ListModelsMethod,
            Tripo.Bridge.HostControlConstants.WorkflowCapabilities);
    }

    [Fact]
    public async Task TextWorkflowMethodMapsOptionalModel()
    {
        FakeWorkflow workflow = new();
        Tripo.Mcp.HostControlDispatcher dispatcher = CreateDispatcher(
            new FakeCredentialService(),
            workflow);
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Bridge.HostControlCreateTextTaskRequest request = new(
            "a timber pavilion",
            25_000,
            WithMaterials: true,
            operationId,
            ConfirmExternalCost: true,
            RequireExistingOperation: false,
            Model: "P1-20260311");

        object result = await dispatcher.DispatchAsync(
            Tripo.Bridge.HostControlConstants.CreateTextTaskMethod,
            Tripo.Bridge.BridgeJson.ToElement(request),
            CancellationToken.None);

        Tripo.Bridge.HostControlTextTaskCreationReceipt receipt = Assert.IsType<
            Tripo.Bridge.HostControlTextTaskCreationReceipt>(result);
        Assert.Equal("P1-20260311", receipt.Model);
        Assert.Equal(request, workflow.LastTextRequest);
    }

    private static Tripo.Mcp.HostControlDispatcher CreateDispatcher(
        Tripo.Mcp.ITripoCredentialService credentials,
        Tripo.Mcp.ITripoWorkflow workflow) =>
        new(
            "rhino",
            Environment.ProcessId,
            Tripo.Bridge.HostControlConstants.WorkflowCapabilities,
            credentials,
            workflow,
            () => { });

    private sealed class FakeCredentialService :
        Tripo.Mcp.ITripoCredentialService
    {
        public string? LastSetKey { get; private set; }

        public bool LastPersist { get; private set; }

        public Tripo.Bridge.HostControlCredentialStatusReceipt GetStatus() =>
            Status();

        public Tripo.Bridge.HostControlCredentialMutationReceipt SetApiKey(
            string apiKey,
            bool persist)
        {
            LastSetKey = apiKey;
            LastPersist = persist;
            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                Status());
        }

        public Tripo.Bridge.HostControlCredentialMutationReceipt ClearApiKey() =>
            new(Status());

        private static Tripo.Bridge.HostControlCredentialStatusReceipt Status() =>
            new(
                HasApiKey: true,
                Source: "store",
                StoredKeyPresent: true,
                CanClearStoredKey: true,
                PersistenceBackend: "fake",
                UsesWeakerFileFallback: false);
    }

    private sealed class FakeWorkflow : Tripo.Mcp.ITripoWorkflow
    {
        public Tripo.Bridge.HostControlCreateObjConversionRequest? LastCatalogConversionRequest { get; private set; }

        public Task<Tripo.Mcp.ImageTaskCreationReceipt> CreateCatalogImageTaskAsync(
            Tripo.Bridge.HostControlCreateImageTaskRequest request, CancellationToken cancellationToken)
        {
            LastImageRequest = request;
            return Task.FromResult(new Tripo.Mcp.ImageTaskCreationReceipt(request.OperationId,
                "task_image123", request.Model!, request.Image.Sha256));
        }

        public Task<Tripo.Mcp.ObjConversionCreationReceipt> CreateCatalogObjConversionAsync(
            Tripo.Bridge.HostControlCreateObjConversionRequest request, CancellationToken cancellationToken)
        {
            LastCatalogConversionRequest = request;
            return Task.FromResult(new Tripo.Mcp.ObjConversionCreationReceipt(request.OperationId,
                request.SourceTaskId, "task_conversion123", "OBJ"));
        }

        public Tripo.Bridge.HostControlImportObjTaskRequest? LastCatalogRequest { get; private set; }

        public Task<Tripo.Mcp.ObjTaskImportReceipt> ImportCatalogObjTaskAsync(
            string conversionTaskId, string name, string documentSessionId, string operationId,
            bool applyMaterials, Tripo.Bridge.CatalogFamilyOptions catalog, CancellationToken cancellationToken,
            bool requireExistingImport = false)
        {
            LastCatalogRequest = new(conversionTaskId, name, documentSessionId, operationId,
                "family", applyMaterials, Catalog: catalog, RequireExistingImport: requireExistingImport);
            return ImportObjTaskAsync(conversionTaskId, name, documentSessionId, operationId,
                "family", applyMaterials, cancellationToken);
        }

        public int CreateTextCalls { get; private set; }

        public bool ThrowCredentialOnText { get; init; }

        public bool ThrowPaidCredentialRejectionOnText { get; init; }

        public bool ThrowPaidCredentialRejectionOnImage { get; init; }

        public IReadOnlyList<string> ImportWarnings { get; init; } = [];

        public Tripo.Bridge.HostControlCreateTextTaskRequest? LastTextRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlCreateImageTaskRequest? LastImageRequest
        {
            get;
            private set;
        }

        public Tripo.Bridge.HostControlStageObjTaskRequest? LastStageRequest
        {
            get;
            private set;
        }

        public Uri? LastStageImageUrl { get; private set; }

        public Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Bridge.HostContextReceipt(
                    "rhino",
                    "8-test",
                    Environment.ProcessId,
                    Guid.NewGuid().ToString("D"),
                    "Test",
                    "Meters",
                    []));

        public Task<Tripo.Mcp.TaskStatusReceipt> GetTaskStatusAsync(
            string taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Mcp.TaskStatusReceipt(
                    taskId,
                    "text_to_model",
                    "success",
                    100,
                    null,
                    null,
                    null,
                    null,
                    null));

        public Task<Tripo.Mcp.PaidOperationStatusReceipt>
            GetPaidOperationStatusAsync(
                string operationId,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Mcp.PaidOperationStatusReceipt(
                    operationId,
                    "text_task_creation",
                    "task_id_persisted",
                    null,
                    "task_source123",
                    null,
                    null,
                    TaskIdDurable: true,
                    MayHaveCreatedRemoteTask: true,
                    CanResumeCreation: false,
                    "Reuse the durable task ID.",
                    DateTimeOffset.UtcNow));

        public Task<Tripo.Mcp.TextTaskCreationReceipt> CreateTextTaskAsync(
            string prompt,
            int faceLimit,
            bool withMaterials,
            string operationId,
            bool confirmExternalCost,
            CancellationToken cancellationToken,
            bool requireExistingOperation = false,
            string? model = null)
        {
            CreateTextCalls++;
            if (ThrowCredentialOnText)
            {
                throw new Tripo.Mcp.TripoCredentialPreflightException(
                    "The credential was rejected before dispatch.",
                    new Tripo.Mcp.TripoCredentialException(
                        "The local credential was invalid."));
            }

            if (ThrowPaidCredentialRejectionOnText)
            {
                throw new Tripo.Mcp.TripoPaidRequestRejectedException(
                    new Tripo.Mcp.TripoApiException(
                        "The provider rejected the credential.",
                        System.Net.HttpStatusCode.Unauthorized));
            }

            LastTextRequest = new Tripo.Bridge.HostControlCreateTextTaskRequest(
                prompt,
                faceLimit,
                withMaterials,
                operationId,
                confirmExternalCost,
                requireExistingOperation,
                model);
            return Task.FromResult(
                new Tripo.Mcp.TextTaskCreationReceipt(
                    operationId,
                    "task_source123",
                    model ?? Tripo.Mcp.TripoV3Client.DefaultModel));
        }

        public Task<Tripo.Mcp.ObjConversionCreationReceipt>
            CreateObjConversionAsync(
                string sourceTaskId,
                int faceLimit,
                bool withMaterials,
                string operationId,
                bool confirmExternalCost,
                CancellationToken cancellationToken,
                bool requireExistingOperation = false) =>
            Task.FromResult(
                new Tripo.Mcp.ObjConversionCreationReceipt(
                    operationId,
                    sourceTaskId,
                    "task_conversion123",
                    "OBJ"));

        public Task<Tripo.Mcp.ImageTaskCreationReceipt> CreateImageTaskAsync(
            Tripo.Bridge.StagedImageTransfer image,
            int faceLimit,
            bool withMaterials,
            string operationId,
            bool confirmExternalCost,
            CancellationToken cancellationToken,
            bool requireExistingOperation = false,
            string? model = null)
        {
            if (ThrowPaidCredentialRejectionOnImage)
            {
                throw new Tripo.Mcp.TripoPaidRequestRejectedException(
                    new Tripo.Mcp.TripoApiException(
                        "The provider rejected the image credential.",
                        System.Net.HttpStatusCode.Unauthorized));
            }

            LastImageRequest =
                new Tripo.Bridge.HostControlCreateImageTaskRequest(
                    image,
                    faceLimit,
                    withMaterials,
                    operationId,
                    confirmExternalCost,
                    requireExistingOperation,
                    model);
            return Task.FromResult(
                new Tripo.Mcp.ImageTaskCreationReceipt(
                    operationId,
                    "task_image123",
                    model ?? Tripo.Mcp.TripoV3Client.DefaultModel,
                    image.Sha256));
        }

        public Task<Tripo.Mcp.ObjTaskStageReceipt> StageObjTaskAsync(
            string conversionTaskId,
            bool includeMaterials,
            CancellationToken cancellationToken)
        {
            LastStageRequest =
                new Tripo.Bridge.HostControlStageObjTaskRequest(
                    conversionTaskId,
                    includeMaterials);
            return Task.FromResult(
                new Tripo.Mcp.ObjTaskStageReceipt(
                    conversionTaskId,
                    1.25m,
                    new Tripo.Bridge.StagedMeshLoadRequest(
                        "bundle_123",
                        "model.obj",
                        includeMaterials ? "model.mtl" : null,
                        [],
                        "meter",
                        "Y",
                        "right",
                        includeMaterials)));
        }

        public Task<Tripo.Bridge.StagedImageTransfer> StageUrlImageAsync(
            Uri imageUri,
            CancellationToken cancellationToken)
        {
            LastStageImageUrl = imageUri;
            return Task.FromResult(
                new Tripo.Bridge.StagedImageTransfer(
                    "11111111-1111-1111-1111-111111111111",
                    new string('a', 64),
                    10,
                    "image/png"));
        }

        public Task<Tripo.Mcp.ObjTaskImportReceipt> ImportObjTaskAsync(
            string conversionTaskId,
            string name,
            string documentSessionId,
            string operationId,
            string importMode,
            bool applyMaterials,
            CancellationToken cancellationToken,
            string placementMode = "origin",
            bool associateLevel = false) =>
            Task.FromResult(
                new Tripo.Mcp.ObjTaskImportReceipt(
                    operationId,
                    conversionTaskId,
                    null,
                    new Tripo.Bridge.HostImportReceipt(
                        "rhino",
                        documentSessionId,
                        operationId,
                        Guid.NewGuid().ToString("D"),
                        3,
                        1,
                        0,
                        "committed",
                        "mesh",
                        0,
                        0,
                        null),
                    ImportWarnings));
    }
}

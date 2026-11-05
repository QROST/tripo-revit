namespace Tripo.Mcp;

public interface ITripoWorkflow
{
    Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
        CancellationToken cancellationToken);

    Task<TaskStatusReceipt> GetTaskStatusAsync(
        string taskId,
        CancellationToken cancellationToken);

    Task<PaidOperationStatusReceipt> GetPaidOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken);

    Task<Tripo.Bridge.StagedImageTransfer> StageLocalImageAsync(
        string localImagePath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This workflow does not support local image staging.");

    Task<Tripo.Bridge.StagedImageTransfer> StageUrlImageAsync(
        Uri imageUri,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This workflow does not support remote image staging.");

    Task<Tripo.Bridge.HostControlModelCatalogReceipt> ListModelsAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(Tripo.Bridge.TripoModelCatalog.CreateReceipt());

    Task<TextTaskCreationReceipt> CreateTextTaskAsync(
        string prompt,
        int faceLimit,
        bool withMaterials,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false,
        string? model = null);

    Task<ImageTaskCreationReceipt> CreateImageTaskAsync(
        Tripo.Bridge.StagedImageTransfer image,
        int faceLimit,
        bool withMaterials,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false,
        string? model = null) =>
        throw new NotSupportedException(
            "This workflow does not support image generation.");

    Task<ObjConversionCreationReceipt> CreateObjConversionAsync(
        string sourceTaskId,
        int faceLimit,
        bool withMaterials,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false);

    Task<ObjTaskImportReceipt> ImportObjTaskAsync(
        string conversionTaskId,
        string name,
        string documentSessionId,
        string operationId,
        string importMode,
        bool applyMaterials,
        CancellationToken cancellationToken,
        string placementMode = "origin",
        bool associateLevel = false);

    Task<ObjTaskStageReceipt> StageObjTaskAsync(
        string conversionTaskId,
        bool includeMaterials,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This workflow does not support OBJ staging.");

    Task<ObjTaskImportReceipt> ImportCatalogObjTaskAsync(
        string conversionTaskId,
        string name,
        string documentSessionId,
        string operationId,
        bool applyMaterials,
        Tripo.Bridge.CatalogFamilyOptions catalog,
        CancellationToken cancellationToken,
        bool requireExistingImport = false) =>
        throw new NotSupportedException(
            "This workflow does not support catalog family imports.");

    Task<ImageTaskCreationReceipt> CreateCatalogImageTaskAsync(
        Tripo.Bridge.HostControlCreateImageTaskRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This workflow does not support catalog credential anchors.");

    Task<ObjConversionCreationReceipt> CreateCatalogObjConversionAsync(
        Tripo.Bridge.HostControlCreateObjConversionRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This workflow does not support catalog credential anchors.");
}

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

    Task<TextTaskCreationReceipt> CreateTextTaskAsync(
        string prompt,
        int faceLimit,
        bool withMaterials,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false);

    Task<ImageTaskCreationReceipt> CreateImageTaskAsync(
        Tripo.Bridge.StagedImageTransfer image,
        int faceLimit,
        bool withMaterials,
        string operationId,
        bool confirmExternalCost,
        CancellationToken cancellationToken,
        bool requireExistingOperation = false) =>
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
}

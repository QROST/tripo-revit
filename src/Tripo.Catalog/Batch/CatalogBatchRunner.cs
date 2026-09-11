using Tripo.Bridge;

namespace Tripo.Catalog.Batch;

/// <summary>Serial, journal-first batch execution. All paid continuations retain their original UUID.</summary>
public sealed class CatalogBatchRunner
{
    private readonly IHostControlClient _client;
    private readonly CatalogBatchStore _store;
    private readonly Func<CatalogBatchPlan, CancellationToken, Task<IDisposable>> _acquireExecutionScope;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxPollsPerRun;
    private CatalogBatchSnapshot _snapshot = null!;
    private int _remainingPolls;

    public CatalogBatchRunner(
        IHostControlClient client,
        CatalogBatchStore store,
        Func<CatalogBatchPlan, CancellationToken, Task<IDisposable>> acquireExecutionScope,
        TimeSpan? pollInterval = null,
        int maxPollsPerRun = 300)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _acquireExecutionScope = acquireExecutionScope ?? throw new ArgumentNullException(nameof(acquireExecutionScope));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_pollInterval < TimeSpan.Zero || _pollInterval > TimeSpan.FromMinutes(1) || maxPollsPerRun is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maxPollsPerRun));
        _maxPollsPerRun = maxPollsPerRun;
    }

    public event Action<CatalogBatchSnapshot>? ProgressChanged;

    public async Task<CatalogBatchSnapshot> RunAsync(
        CatalogBatchPlan plan,
        bool confirmExternalCost,
        CancellationToken cancellationToken)
    {
        if (!confirmExternalCost) throw new InvalidOperationException("catalog_batch_confirmation_required");
        ArgumentNullException.ThrowIfNull(plan);
        CatalogBatchPlan frozen = plan.Prepare();
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable batchLock = _store.AcquireLock();
        _snapshot = _store.LoadOrCreate(frozen);
        if (_snapshot.Status == CatalogBatchStatus.Completed)
        {
            CatalogBatchRecoveryMarkers.Clear(frozen.BatchId, _snapshot.PlanHash, _store.RootDirectory);
            return _snapshot;
        }

        // The caller must acquire the existing UI intent/credential-workflow gate and check stale recovery.
        using IDisposable execution = await _acquireExecutionScope(frozen, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("catalog_batch_execution_scope_required");
        _remainingPolls = _maxPollsPerRun;
        try
        {
            if (_snapshot.Items.Any(item => item.GenerationDispatchAttempted))
                CatalogBatchRecoveryMarkers.MarkPending(frozen.BatchId, _snapshot.PlanHash, _store.RootDirectory);
            Checkpoint(CatalogBatchStatus.Running, null);
            for (int index = 0; index < frozen.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (State(index).Stage is CatalogBatchStage.Completed or CatalogBatchStage.Failed) continue;
                await EnsureDocumentAsync(cancellationToken).ConfigureAwait(false);
                await EnsurePaidTaskAsync(index, conversion: false, cancellationToken).ConfigureAwait(false);
                if (State(index).Stage == CatalogBatchStage.Failed) continue;
                await PollAsync(index, conversion: false, cancellationToken).ConfigureAwait(false);
                if (State(index).Stage == CatalogBatchStage.Failed) continue;
                await EnsurePaidTaskAsync(index, conversion: true, cancellationToken).ConfigureAwait(false);
                if (State(index).Stage == CatalogBatchStage.Failed) continue;
                await PollAsync(index, conversion: true, cancellationToken).ConfigureAwait(false);
                if (State(index).Stage == CatalogBatchStage.Failed) continue;
                await ImportAsync(index, cancellationToken).ConfigureAwait(false);
            }
            Checkpoint(CatalogBatchStatus.Completed, _snapshot.FailedCount == 0 ? null : "catalog_batch_some_items_failed");
            CatalogBatchRecoveryMarkers.Clear(frozen.BatchId, _snapshot.PlanHash, _store.RootDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Checkpoint(CatalogBatchStatus.Cancelled, "catalog_batch_cancelled_resume_same_plan");
        }
        catch (BatchPauseException pause)
        {
            Checkpoint(CatalogBatchStatus.Paused, pause.Code);
        }
        return _snapshot;
    }

    private async Task EnsurePaidTaskAsync(int index, bool conversion, CancellationToken token)
    {
        CatalogBatchItemState state = State(index);
        if ((conversion ? state.ConversionTaskId : state.GenerationTaskId) is not null) return;
        bool existing = conversion ? state.ConversionDispatchAttempted : state.GenerationDispatchAttempted;
        if (existing)
        {
            PaidResolution resolved = await RecoverPaidAsync(index, conversion, token).ConfigureAwait(false);
            if (resolved != PaidResolution.ResumeExisting) return;
        }
        token.ThrowIfCancellationRequested();
        await EnsureCredentialsAsync(token).ConfigureAwait(false);
        await EnsureDocumentAsync(token).ConfigureAwait(false);
        // Select from durable history before marking this call. Completed/failed items still bind the account.
        HostControlImageCredentialAnchor? credentialAnchor = FindCredentialAnchor();
        CatalogBatchRecoveryMarkers.MarkPending(_snapshot.Plan.BatchId, _snapshot.PlanHash, _store.RootDirectory);
        Update(index, conversion
            ? State(index) with { Stage = CatalogBatchStage.Converting, ConversionDispatchAttempted = true, Progress = 0 }
            : State(index) with { Stage = CatalogBatchStage.Generating, GenerationDispatchAttempted = true, Progress = 0 });
        token.ThrowIfCancellationRequested();

        string taskId;
        state = State(index);
        CatalogBatchItem item = _snapshot.Plan.Items[index];
        try
        {
            if (conversion)
            {
                HostControlObjConversionCreationReceipt receipt = await _client.CreateObjConversionAsync(
                    new HostControlCreateObjConversionRequest(state.GenerationTaskId!, _snapshot.Plan.FaceLimit,
                        _snapshot.Plan.WithMaterials, state.ConversionOperationId, true, existing, credentialAnchor), token).ConfigureAwait(false);
                if (receipt.OperationId != state.ConversionOperationId || receipt.SourceTaskId != state.GenerationTaskId ||
                    receipt.Format != "OBJ" && receipt.Format != "obj" || !TripoTaskId.IsValid(receipt.ConversionTaskId))
                    throw new BatchPauseException("catalog_batch_conversion_receipt_mismatch");
                taskId = receipt.ConversionTaskId;
            }
            else
            {
                HostControlImageTaskCreationReceipt receipt = await _client.CreateImageTaskAsync(
                    new HostControlCreateImageTaskRequest(item.Image, _snapshot.Plan.FaceLimit, _snapshot.Plan.WithMaterials,
                        state.GenerationOperationId, true, existing, _snapshot.Plan.Model, credentialAnchor), token).ConfigureAwait(false);
                if (receipt.OperationId != state.GenerationOperationId || receipt.ImageSha256 != item.Image.Sha256 ||
                    receipt.Model != _snapshot.Plan.Model || !TripoTaskId.IsValid(receipt.TaskId))
                    throw new BatchPauseException("catalog_batch_generation_receipt_mismatch");
                taskId = receipt.TaskId;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (BatchPauseException) { throw; }
        catch (HostControlCallException exception) when (!existing &&
            exception.Code is HostControlConstants.CredentialInvalidError or "catalog_credential_anchor_unverified")
        {
            // These sidecar errors explicitly precede this operation's journal and provider dispatch.
            Update(index, conversion
                ? State(index) with { ConversionDispatchAttempted = false }
                : State(index) with { GenerationDispatchAttempted = false, Stage = CatalogBatchStage.Pending });
            if (!_snapshot.Items.Any(value => value.GenerationDispatchAttempted))
                CatalogBatchRecoveryMarkers.Clear(_snapshot.Plan.BatchId, _snapshot.PlanHash, _store.RootDirectory);
            throw new BatchPauseException(exception.Code == HostControlConstants.CredentialInvalidError
                ? "catalog_batch_credentials_required" : "catalog_credential_anchor_unverified");
        }
        catch (Exception)
        {
            // A failed create call is never a reason to send a fresh UUID or automatically retry a POST.
            PaidResolution resolution = await RecoverPaidAsync(index, conversion, token).ConfigureAwait(false);
            if (resolution == PaidResolution.ResumeExisting)
                throw new BatchPauseException("catalog_batch_creation_requires_confirmed_resume");
            return;
        }
        // Cancellation after a receipt must not discard the accepted remote identity.
        Update(index, conversion ? State(index) with { ConversionTaskId = taskId } : State(index) with { GenerationTaskId = taskId });
    }

    private async Task<PaidResolution> RecoverPaidAsync(int index, bool conversion, CancellationToken token)
    {
        CatalogBatchItemState state = State(index);
        string operationId = conversion ? state.ConversionOperationId : state.GenerationOperationId;
        HostControlOperationStatusReceipt status;
        try { status = await _client.GetOperationStatusAsync(operationId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new BatchPauseException("catalog_batch_operation_unknown_preserve_uuid"); }
        if (status.OperationId != operationId || status.Kind != (conversion ? "obj_conversion_creation" : "image_task_creation") ||
            status.SourceTaskId != (conversion ? state.GenerationTaskId : null))
            throw new BatchPauseException("catalog_batch_operation_receipt_mismatch");
        ValidateOperationStatus(status, state, conversion);
        state = conversion ? state with
        {
            ConversionOperationState = status.State, ConversionOperationUpdatedAtUtc = status.UpdatedAtUtc,
        } : state with
        {
            GenerationOperationState = status.State, GenerationOperationUpdatedAtUtc = status.UpdatedAtUtc,
        };
        // A later journal rollback must not turn an observed ambiguous dispatch into permission to resend.
        Update(index, state);
        if (status.State == "task_id_persisted" && status.TaskIdDurable && status.MayHaveCreatedRemoteTask &&
            !status.CanResumeCreation && TripoTaskId.IsValid(status.CreatedTaskId) &&
            status.FailureCode is null && status.FailureMessage is null && status.FailureStage is null)
        {
            Update(index, conversion ? state with { ConversionTaskId = status.CreatedTaskId } : state with { GenerationTaskId = status.CreatedTaskId });
            return PaidResolution.Resolved;
        }
        if (status.State == "request_rejected" && !status.TaskIdDurable && !status.MayHaveCreatedRemoteTask &&
            !status.CanResumeCreation && !status.OperationInProgress && status.CreatedTaskId is null &&
            !string.IsNullOrWhiteSpace(status.FailureCode) && !string.IsNullOrWhiteSpace(status.FailureMessage) &&
            (conversion ? status.FailureStage is null : status.FailureStage is "upload" or "generation"))
        {
            Fail(index, conversion ? "catalog_batch_conversion_rejected" : "catalog_batch_generation_rejected");
            return PaidResolution.Resolved;
        }
        if ((status.State == "prepared" || !conversion && status.State == "image_file_token_persisted") &&
            status.CanResumeCreation && !status.TaskIdDurable && !status.MayHaveCreatedRemoteTask &&
            !status.OperationInProgress && status.CreatedTaskId is null && status.FailureCode is null &&
            status.FailureMessage is null && status.FailureStage is null)
            return PaidResolution.ResumeExisting;
        throw new BatchPauseException("catalog_batch_operation_unknown_preserve_uuid");
    }

    private static void ValidateOperationStatus(HostControlOperationStatusReceipt status, CatalogBatchItemState item, bool conversion)
    {
        bool prepared = status.State == "prepared";
        bool tokenPersisted = !conversion && status.State == "image_file_token_persisted";
        bool durable = status.State == "task_id_persisted";
        bool busy = status.State == "operation_in_progress";
        bool failure = status.State is "request_rejected" or "outcome_unknown";
        bool mayHaveCreated = busy || durable || status.State is "dispatching" or "image_generation_dispatching" ||
            status.State == "outcome_unknown" && (conversion || status.FailureStage == "generation");
        bool validFailure = failure
            ? !string.IsNullOrWhiteSpace(status.FailureCode) && !string.IsNullOrWhiteSpace(status.FailureMessage) &&
                (conversion ? status.FailureStage is null : status.FailureStage is "upload" or "generation")
            : status.FailureCode is null && status.FailureMessage is null && status.FailureStage is null;
        bool known = status.State is "prepared" or "operation_in_progress" or "dispatching" or "task_id_persisted" or
            "request_rejected" or "outcome_unknown" || !conversion &&
            status.State is "image_upload_dispatching" or "image_file_token_persisted" or "image_generation_dispatching";
        if (!known || !validFailure || status.TaskIdDurable != durable ||
            status.MayHaveCreatedRemoteTask != (mayHaveCreated || status.OperationInProgress) ||
            status.CanResumeCreation != ((prepared || tokenPersisted) && !status.OperationInProgress) ||
            busy && !status.OperationInProgress || prepared && status.OperationInProgress ||
            (durable ? !TripoTaskId.IsValid(status.CreatedTaskId) : status.CreatedTaskId is not null))
            throw new BatchPauseException("catalog_batch_operation_receipt_mismatch");
        string? previous = conversion ? item.ConversionOperationState : item.GenerationOperationState;
        DateTimeOffset? previousTime = conversion ? item.ConversionOperationUpdatedAtUtc : item.GenerationOperationUpdatedAtUtc;
        bool allowed = previous switch
        {
            null => true,
            "prepared" or "operation_in_progress" => true,
            "dispatching" => status.State is "dispatching" or "task_id_persisted" or "request_rejected" or "outcome_unknown",
            "image_upload_dispatching" => status.State is "image_upload_dispatching" or "image_file_token_persisted" or "request_rejected" or "outcome_unknown",
            "image_file_token_persisted" => status.State is "image_file_token_persisted" or "image_generation_dispatching" or "task_id_persisted" or "request_rejected" or "outcome_unknown",
            "image_generation_dispatching" => status.State is "image_generation_dispatching" or "task_id_persisted" or "request_rejected" or "outcome_unknown",
            _ => previous == status.State,
        };
        if (!allowed || previousTime.HasValue && status.UpdatedAtUtc < previousTime.Value)
            throw new BatchPauseException("catalog_batch_journal_regressed_preserve_uuid");
    }

    private async Task PollAsync(int index, bool conversion, CancellationToken token)
    {
        if (conversion ? State(index).ConversionSucceeded : State(index).GenerationSucceeded) return;
        string taskId = (conversion ? State(index).ConversionTaskId : State(index).GenerationTaskId)!;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_remainingPolls-- <= 0) throw new BatchPauseException("catalog_batch_poll_limit_resume_same_plan");
            HostControlTaskStatusReceipt receipt;
            try { receipt = await _client.GetTaskStatusAsync(taskId, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { throw new BatchPauseException("catalog_batch_poll_unavailable"); }
            if (receipt.TaskId != taskId || receipt.Type != (conversion ? "convert_model" : "image_to_model") ||
                receipt.Progress is < 0 or > 100 || receipt.CreditsConsumed is < 0)
                throw new BatchPauseException("catalog_batch_task_receipt_mismatch");
            if (receipt.Status is "failed" or "cancelled" or "banned" or "expired")
            {
                Fail(index, conversion ? "catalog_batch_conversion_failed" : "catalog_batch_generation_failed");
                return;
            }
            bool succeeded = receipt.Status == "success";
            if (!succeeded && receipt.Status is not ("queued" or "running" or "processing" or "initialized"))
                throw new BatchPauseException("catalog_batch_task_state_unknown");
            Update(index, conversion
                ? State(index) with { ConversionSucceeded = succeeded, Progress = receipt.Progress }
                : State(index) with { GenerationSucceeded = succeeded, Progress = receipt.Progress });
            if (succeeded) return;
            await Task.Delay(_pollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task ImportAsync(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await EnsureDocumentAsync(token).ConfigureAwait(false);
        CatalogBatchItemState state = State(index);
        bool requireExisting = state.ImportDispatchAttempted;
        CatalogBatchItem item = _snapshot.Plan.Items[index];
        Update(index, state with { Stage = CatalogBatchStage.Importing, ImportDispatchAttempted = true, Progress = 0 });
        token.ThrowIfCancellationRequested();
        HostControlObjTaskImportReceipt receipt;
        try
        {
            receipt = await _client.ImportObjTaskAsync(new HostControlImportObjTaskRequest(
                state.ConversionTaskId!, item.Name, _snapshot.Plan.DocumentSessionId, state.ImportOperationId,
                "family", _snapshot.Plan.WithMaterials, "origin", false, item.Family, requireExisting), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HostControlCallException exception) when (!requireExisting &&
            exception.Code is "catalog_options_invalid" or "catalog_mesh_invalid" or "catalog_dimension_mismatch")
        {
            // These specific host errors occur before family/project transactions begin.
            Fail(index, exception.Code);
            return;
        }
        catch (Exception) { throw new BatchPauseException("catalog_batch_import_unconfirmed_preserve_uuid"); }
        HostImportReceipt host = receipt.HostReceipt;
        if (receipt.OperationId != state.ImportOperationId || receipt.ConversionTaskId != state.ConversionTaskId ||
            host is null || host.Host != "revit" || host.DocumentSessionId != _snapshot.Plan.DocumentSessionId ||
            host.IdempotencyKey != state.ImportOperationId || host.ImportMode != "family" ||
            host.TransactionStatus is not ("committed" or "already_exists") ||
            requireExisting && host.TransactionStatus != "already_exists" ||
            string.IsNullOrWhiteSpace(host.CreatedId) || host.CreatedId.Length > 2048 ||
            host.VertexCount <= 0 || host.TriangleCount <= 0 || host.RejectedTriangleCount < 0 ||
            host.MaterialCount < 0 || host.TextureCount < 0 || receipt.ConversionCreditsConsumed is < 0 ||
            receipt.Warnings is null || receipt.Warnings.Count > 128 ||
            host.TransactionStatus == "committed" && string.IsNullOrWhiteSpace(host.SavedFamilyPath))
            throw new BatchPauseException("catalog_batch_import_receipt_mismatch");
        Update(index, State(index) with { Stage = CatalogBatchStage.Completed, CreatedId = host.CreatedId, Progress = 100 });
    }

    private async Task EnsureDocumentAsync(CancellationToken token)
    {
        HostContextReceipt context;
        try { context = await _client.GetHostContextAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new BatchPauseException("catalog_batch_document_unavailable"); }
        if (context.Host != "revit" || context.DocumentSessionId != _snapshot.Plan.DocumentSessionId)
            throw new BatchPauseException("catalog_batch_document_changed");
    }

    private async Task EnsureCredentialsAsync(CancellationToken token)
    {
        HostControlCredentialStatusReceipt status;
        try { status = await _client.GetCredentialStatusAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new BatchPauseException("catalog_batch_credentials_unavailable"); }
        if (status is null || !status.HasApiKey) throw new BatchPauseException("catalog_batch_credentials_required");
    }

    private HostControlImageCredentialAnchor? FindCredentialAnchor()
    {
        for (int index = 0; index < _snapshot.Items.Count; index++)
        {
            CatalogBatchItemState prior = State(index);
            if (prior.GenerationDispatchAttempted)
                return new HostControlImageCredentialAnchor(prior.GenerationOperationId, _snapshot.Plan.Items[index].Image,
                    _snapshot.Plan.FaceLimit, _snapshot.Plan.WithMaterials, _snapshot.Plan.Model);
        }
        return null;
    }

    private CatalogBatchItemState State(int index) => _snapshot.Items[index];
    private void Fail(int index, string code) => Update(index, State(index) with { Stage = CatalogBatchStage.Failed, MessageCode = code });
    private void Update(int index, CatalogBatchItemState value)
    {
        CatalogBatchItemState[] items = _snapshot.Items.ToArray();
        items[index] = value;
        _snapshot = _snapshot with { Items = Array.AsReadOnly(items) };
        Checkpoint(_snapshot.Status, _snapshot.MessageCode);
    }

    private void Checkpoint(CatalogBatchStatus status, string? code)
    {
        _snapshot = _snapshot with { Status = status, MessageCode = code, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _store.Save(_snapshot);
        try { ProgressChanged?.Invoke(_snapshot); }
        catch (Exception) { /* A UI observer cannot undo a durable checkpoint or authorize a resend. */ }
    }

    private enum PaidResolution { Resolved, ResumeExisting }
    private sealed class BatchPauseException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}

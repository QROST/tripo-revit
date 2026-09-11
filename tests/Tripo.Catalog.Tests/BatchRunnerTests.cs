using Tripo.Bridge;
using Tripo.Catalog.Batch;
using Tripo.HostUi;
using Xunit;

namespace Tripo.Catalog.Tests;

public sealed class BatchRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "catalog-batch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private static readonly string[] ExpectedSerialEffects = ["image", "conversion", "import", "image", "conversion", "import"];
    private CatalogBatchStore Store => new(_root);

    private CatalogBatchRunner Runner(int polls = 30) => new(_client, Store, GateAsync, TimeSpan.Zero, polls);

    private Task<IDisposable> GateAsync(CatalogBatchPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TripoPanelRecoveryStore recovery = new("revit", _root);
        IDisposable lease = recovery.AcquireCredentialWorkflowLease();
        if (recovery.LoadStale().HasBlock || recovery.LoadCredentialMutationBlocks(excludeCatalogBatchId: plan.BatchId).HasBlock)
        {
            lease.Dispose();
            throw new InvalidOperationException("recovery_blocked");
        }
        return Task.FromResult(lease);
    }

    [Fact]
    public async Task ConfirmationIsRequiredBeforeAnyEffectsAndOnEveryResume()
    {
        CatalogBatchPlan plan = Plan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(plan, false, default));
        Assert.False(Directory.Exists(_root));
        Assert.Empty(_client.Images);
        _client.ImageAction = (_, _) => throw new IOException("lost");
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(plan, false, default));
        Assert.Single(_client.Images);
        Assert.Single(_client.OperationReads);
    }

    [Fact]
    public async Task MissingKeyDoesNotDispatchOrBlockConfiguringCredentialsBeforeResume()
    {
        CatalogBatchPlan plan = Plan();
        _client.HasApiKey = false;
        CatalogBatchSnapshot paused = await Runner().RunAsync(plan, true, default);
        Assert.Equal("catalog_batch_credentials_required", paused.MessageCode);
        Assert.False(paused.Items[0].GenerationDispatchAttempted);
        Assert.Empty(_client.Images);
        Assert.Empty(CatalogBatchRecoveryMarkers.ListPending(_root));
        using TripoPanelRecoveryStore recovery = new("revit", _root);
        Assert.False(recovery.LoadCredentialMutationBlocks().HasBlock);
        _client.HasApiKey = true;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Equal(paused.Items[0].GenerationOperationId, Assert.Single(_client.Images).OperationId);
    }

    [Fact]
    public async Task DefinitiveLocalCredentialPreflightFailureDoesNotLeaveAnUnresolvableMarker()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new HostControlCallException(HostControlConstants.CredentialInvalidError, "Invalid local key");
        CatalogBatchSnapshot paused = await Runner().RunAsync(plan, true, default);
        Assert.Equal("catalog_batch_credentials_required", paused.MessageCode);
        Assert.False(paused.Items[0].GenerationDispatchAttempted);
        Assert.Empty(CatalogBatchRecoveryMarkers.ListPending(_root));
        Assert.Empty(_client.OperationReads);
        _client.ImageAction = null;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Equal(_client.Images[0].OperationId, _client.Images[1].OperationId);
    }

    [Fact]
    public async Task CompletesSeriallyAndCompletedRestartDoesNotReimportAfterUndo()
    {
        CatalogBatchPlan plan = Plan(2);
        CatalogBatchRunner runner = Runner();
        int observations = 0;
        runner.ProgressChanged += snapshot =>
        {
            Assert.Equal(snapshot.UpdatedAtUtc, Store.Load(plan.BatchId)!.UpdatedAtUtc);
            observations++;
        };
        _client.BeforeImage = request =>
        {
            CatalogBatchSnapshot saved = Store.Load(plan.BatchId)!;
            Assert.Contains(saved.Items, state => state.GenerationOperationId == request.OperationId && state.GenerationDispatchAttempted);
            Assert.Contains(plan.BatchId, CatalogBatchRecoveryMarkers.ListPending(_root));
        };
        CatalogBatchSnapshot result = await runner.RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Completed, result.Status);
        Assert.Equal(2, result.CompletedCount);
        Assert.True(observations > 8);
        Assert.Equal(ExpectedSerialEffects, _client.Effects);
        Assert.Empty(CatalogBatchRecoveryMarkers.ListPending(_root));
        await Runner().RunAsync(plan, true, default);
        Assert.Equal(2, _client.Imports.Count);
        Assert.Equal(2, _client.Images.Count);
    }

    [Fact]
    public async Task EveryLaterPaidCreationUsesTheFirstDurableGenerationAsAccountAnchor()
    {
        CatalogBatchPlan plan = Plan(2);
        CatalogBatchSnapshot completed = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Completed, completed.Status);
        Assert.Null(_client.Images[0].CredentialAnchor);
        HostControlImageCredentialAnchor expected = new(_client.Images[0].OperationId, plan.Items[0].Image,
            plan.FaceLimit, plan.WithMaterials, plan.Model);
        Assert.Equal(expected, _client.Images[1].CredentialAnchor);
        Assert.All(_client.Conversions, conversion => Assert.Equal(expected, conversion.CredentialAnchor));
        Assert.Equal(completed.Items[0].GenerationOperationId, expected.OperationId);
    }

    [Fact]
    public async Task ChangingRestoredAccountAfterFirstCompletedItemCannotChargeForTheNextItem()
    {
        CatalogBatchPlan plan = Plan(2);
        using CancellationTokenSource cancellation = new();
        _client.ImportAction = request =>
        {
            cancellation.Cancel();
            return FakeClient.ImportReceipt(request);
        };
        CatalogBatchSnapshot stopped = await Runner().RunAsync(plan, true, cancellation.Token);
        Assert.Equal(1, stopped.CompletedCount);
        _client.ImportAction = null;
        _client.Account = "different-account";
        CatalogBatchSnapshot blocked = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Paused, blocked.Status);
        Assert.Equal("catalog_credential_anchor_unverified", blocked.MessageCode);
        Assert.Equal(1, blocked.CompletedCount);
        Assert.False(blocked.Items[1].GenerationDispatchAttempted);
        Assert.Single(_client.Images);
        Assert.Single(_client.Conversions);
        Assert.Single(_client.Imports);
        Assert.Equal(stopped.Items[0].GenerationOperationId, _client.RejectedAnchor!.OperationId);
        Assert.Equal(stopped.PlanHash, Store.Load(plan.BatchId)!.PlanHash);
        _client.Account = "original-account";
        CatalogBatchSnapshot completed = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Completed, completed.Status);
        Assert.Equal(blocked.Items[1].GenerationOperationId, _client.Images[1].OperationId);
        Assert.Equal(stopped.Items[0].GenerationOperationId, _client.Images[1].CredentialAnchor!.OperationId);
    }

    [Fact]
    public async Task ReceiptIsSavedDespiteCancellationAndResumeRetainsPaidIdentities()
    {
        CatalogBatchPlan plan = Plan(2);
        using CancellationTokenSource cancellation = new();
        _client.ImageAction = (request, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(FakeClient.ImageReceipt(request));
        };
        CatalogBatchSnapshot cancelled = await Runner().RunAsync(plan, true, cancellation.Token);
        Assert.Equal(CatalogBatchStatus.Cancelled, cancelled.Status);
        Assert.NotNull(Store.Load(plan.BatchId)!.Items[0].GenerationTaskId);
        Assert.Single(_client.Images);
        Assert.Empty(_client.Conversions);
        Assert.True(new TripoPanelRecoveryStore("revit", _root).LoadCredentialMutationBlocks().HasBlock);
        _client.ImageAction = null;
        CatalogBatchSnapshot done = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Completed, done.Status);
        Assert.Equal(cancelled.Items[0].GenerationOperationId, done.Items[0].GenerationOperationId);
        Assert.Equal(2, _client.Images.Count);
    }

    [Fact]
    public async Task LostResponseRecoversDurableTaskWithoutRepeatingPaidCreate()
    {
        _client.ImageAction = (_, _) => throw new IOException("lost receipt");
        _client.OperationAction = id => FakeClient.DurableImage(id);
        CatalogBatchSnapshot result = await Runner().RunAsync(Plan(), true, default);
        Assert.Equal(CatalogBatchStatus.Completed, result.Status);
        Assert.Single(_client.Images);
        Assert.Equal(_client.Images[0].OperationId, Assert.Single(_client.OperationReads));
    }

    [Fact]
    public async Task UnknownPaidResultNeverAllocatesNewUuidAndOnlyPreparedStateMayResume()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new IOException("lost receipt");
        CatalogBatchSnapshot first = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Paused, first.Status);
        CatalogBatchSnapshot unresolved = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Paused, unresolved.Status);
        Assert.Single(_client.Images);
        _client.OperationAction = id => new(id, "image_task_creation", "prepared", null, null,
            null, null, false, false, true, "resume_creation", DateTimeOffset.UtcNow);
        _client.ImageAction = null;
        CatalogBatchSnapshot done = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Completed, done.Status);
        Assert.Equal(2, _client.Images.Count);
        Assert.False(_client.Images[0].RequireExistingOperation);
        Assert.True(_client.Images[1].RequireExistingOperation);
        Assert.Equal(_client.Images[0].OperationId, _client.Images[1].OperationId);
        Assert.Equal(_client.Images[0].OperationId, _client.Images[1].CredentialAnchor!.OperationId);
    }

    [Fact]
    public async Task UnverifiedAccountAnchorDoesNotEraseAnExistingDispatch()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new IOException("lost receipt");
        _client.OperationAction = id => new(id, "image_task_creation", "prepared", null, null,
            null, null, false, false, true, "resume_creation", DateTimeOffset.UtcNow);
        await Runner().RunAsync(plan, true, default);
        _client.ImageAction = null;
        _client.Account = "different-account";
        CatalogBatchSnapshot blocked = await Runner().RunAsync(plan, true, default);
        Assert.Equal(CatalogBatchStatus.Paused, blocked.Status);
        Assert.True(blocked.Items[0].GenerationDispatchAttempted);
        Assert.Single(_client.Images);
        Assert.Equal(blocked.Items[0].GenerationOperationId, _client.RejectedAnchor!.OperationId);
        _client.Account = "original-account";
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.True(_client.Images[1].RequireExistingOperation);
        Assert.Equal(_client.Images[0].OperationId, _client.Images[1].OperationId);
    }

    [Fact]
    public async Task ConversionCancellationRetainsItsReceiptAndDoesNotRepeatEitherPaidTask()
    {
        CatalogBatchPlan plan = Plan();
        using CancellationTokenSource cancellation = new();
        _client.ConversionAction = request =>
        {
            cancellation.Cancel();
            return FakeClient.ConversionReceipt(request);
        };
        CatalogBatchSnapshot stopped = await Runner().RunAsync(plan, true, cancellation.Token);
        Assert.Equal(CatalogBatchStatus.Cancelled, stopped.Status);
        Assert.NotNull(Store.Load(plan.BatchId)!.Items[0].ConversionTaskId);
        _client.ConversionAction = null;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
        Assert.Single(_client.Conversions);
    }

    [Fact]
    public async Task JournalCannotRegressFromObservedDispatchOrMoveBackInTime()
    {
        CatalogBatchPlan plan = Plan();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        _client.ImageAction = (_, _) => throw new IOException("lost");
        _client.OperationAction = id => new(id, "image_task_creation", "image_generation_dispatching", null, null,
            null, null, false, true, false, "inspect_operation", observedAt);
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Equal("image_generation_dispatching", Store.Load(plan.BatchId)!.Items[0].GenerationOperationState);
        _client.OperationAction = id => new(id, "image_task_creation", "prepared", null, null,
            null, null, false, false, true, "resume_creation", observedAt.AddSeconds(1));
        Assert.Equal("catalog_batch_journal_regressed_preserve_uuid", (await Runner().RunAsync(plan, true, default)).MessageCode);
        _client.OperationAction = id => FakeClient.DurableImage(id) with { UpdatedAtUtc = observedAt.AddSeconds(-1) };
        Assert.Equal("catalog_batch_journal_regressed_preserve_uuid", (await Runner().RunAsync(plan, true, default)).MessageCode);
        Assert.Single(_client.Images);
        _client.OperationAction = id => FakeClient.DurableImage(id) with { UpdatedAtUtc = observedAt.AddSeconds(2) };
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
    }

    [Fact]
    public async Task ContradictoryPreparedReceiptCannotAuthorizePaidResume()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new IOException("lost");
        _client.OperationAction = id => new(id, "image_task_creation", "prepared", null, null,
            null, "unexpected failure payload", false, false, true, "resume_creation", DateTimeOffset.UtcNow);
        Assert.Equal("catalog_batch_operation_receipt_mismatch", (await Runner().RunAsync(plan, true, default)).MessageCode);
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
    }

    [Fact]
    public async Task CancelledImportStillSavesConfirmedReceiptAndStopsFollowingProducts()
    {
        CatalogBatchPlan plan = Plan(2);
        using CancellationTokenSource cancellation = new();
        _client.ImportAction = request =>
        {
            cancellation.Cancel();
            return FakeClient.ImportReceipt(request);
        };
        CatalogBatchSnapshot stopped = await Runner().RunAsync(plan, true, cancellation.Token);
        Assert.Equal(CatalogBatchStatus.Cancelled, stopped.Status);
        Assert.Equal(1, Store.Load(plan.BatchId)!.CompletedCount);
        Assert.Single(_client.Images);
        _client.ImportAction = null;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Equal(2, _client.Imports.Count);
        Assert.NotEqual(_client.Imports[0].OperationId, _client.Imports[1].OperationId);
    }

    [Fact]
    public async Task DefinitiveProviderRejectionContinuesAndDoesNotPersistRemoteErrorText()
    {
        _client.ImageAction = (request, _) => _client.Images.Count == 1
            ? throw new IOException("lost rejection") : Task.FromResult(FakeClient.ImageReceipt(request));
        _client.OperationAction = id => new(id, "image_task_creation", "request_rejected", null, null,
            "invalid_input", "sensitive remote diagnostic", false, false, false, "new_operation", DateTimeOffset.UtcNow, "generation");
        CatalogBatchSnapshot result = await Runner().RunAsync(Plan(2), true, default);
        Assert.Equal(CatalogBatchStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.CompletedCount);
        Assert.DoesNotContain("sensitive remote diagnostic", await File.ReadAllTextAsync(
            Path.Combine(_root, "catalog-batches", result.Plan.BatchId + ".json")));
    }

    [Fact]
    public async Task KnownTaskFailureContinuesFollowingProducts()
    {
        _client.TaskAction = id => _client.TaskReceipt(id) with
        { Status = id == FakeClient.ImageTask(_client.Images[0].OperationId) ? "failed" : "success" };
        CatalogBatchSnapshot done = await Runner().RunAsync(Plan(2), true, default);
        Assert.Equal(CatalogBatchStatus.Completed, done.Status);
        Assert.Equal(1, done.FailedCount);
        Assert.Equal(1, done.CompletedCount);
        Assert.Equal(2, _client.Images.Count);
        Assert.Single(_client.Conversions);
        Assert.Equal(_client.Images[0].OperationId, _client.Images[1].CredentialAnchor!.OperationId);
    }

    [Fact]
    public async Task DocumentSwitchStopsImportAndResumeUsesTheSamePlan()
    {
        CatalogBatchPlan plan = Plan();
        _client.ConversionAction = request =>
        {
            _client.DocumentSession = "other-document";
            return FakeClient.ConversionReceipt(request);
        };
        CatalogBatchSnapshot paused = await Runner().RunAsync(plan, true, default);
        Assert.Equal("catalog_batch_document_changed", paused.MessageCode);
        Assert.Empty(_client.Imports);
        _client.DocumentSession = plan.DocumentSessionId;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
        Assert.Single(_client.Conversions);
        Assert.Equal(plan.DocumentSessionId, Assert.Single(_client.Imports).DocumentSessionId);
    }

    [Fact]
    public async Task DocumentSwitchDuringGenerationPollingPreventsTheNextPaidConversion()
    {
        CatalogBatchPlan plan = Plan();
        _client.TaskAction = id =>
        {
            _client.DocumentSession = "other-document";
            return _client.TaskReceipt(id);
        };
        CatalogBatchSnapshot paused = await Runner().RunAsync(plan, true, default);
        Assert.Equal("catalog_batch_document_changed", paused.MessageCode);
        Assert.Single(_client.Images);
        Assert.Empty(_client.Conversions);
        Assert.Empty(_client.Imports);
        Assert.False(paused.Items[0].ConversionDispatchAttempted);
        _client.TaskAction = null;
        _client.DocumentSession = plan.DocumentSessionId;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
        Assert.Single(_client.Conversions);
    }

    [Fact]
    public async Task AmbiguousImportOnlyQueriesExistingInstanceAndNeverRecreatesMissingOne()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImportAction = request => throw new HostControlCallException(
            request.RequireExistingImport ? "catalog_import_missing" : "host_disconnected", "not confirmed");
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.False(_client.Imports[0].RequireExistingImport);
        Assert.True(_client.Imports[1].RequireExistingImport);
        Assert.Equal(_client.Imports[0].OperationId, _client.Imports[1].OperationId);
        _client.HasApiKey = false; // An existing local import probe does not create a new paid task.
        _client.ImportAction = request => FakeClient.ImportReceipt(request, "already_exists");
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.True(_client.Imports[2].RequireExistingImport);
        Assert.Single(_client.Images);
        Assert.Single(_client.Conversions);
    }

    [Theory]
    [InlineData("catalog_options_invalid")]
    [InlineData("catalog_mesh_invalid")]
    [InlineData("catalog_dimension_mismatch")]
    public async Task KnownPreTransactionImportFailureContinuesOtherItems(string code)
    {
        _client.ImportAction = request => request.Catalog!.ProductId == "product-0"
            ? throw new HostControlCallException(code, "known local rejection") : FakeClient.ImportReceipt(request);
        CatalogBatchSnapshot done = await Runner().RunAsync(Plan(2), true, default);
        Assert.Equal(CatalogBatchStatus.Completed, done.Status);
        Assert.Equal(1, done.FailedCount);
        Assert.Equal(1, done.CompletedCount);
    }

    [Fact]
    public async Task ChangedPlanCannotReusePersistedBatchIdentity()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new IOException();
        await Runner().RunAsync(plan, true, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner().RunAsync(plan with { FaceLimit = 2000 }, true, default));
        Assert.Single(_client.Images);
    }

    [Fact]
    public async Task MissingSnapshotWithPendingMarkerCannotAllocateFreshPaidIdentities()
    {
        CatalogBatchPlan plan = Plan();
        _client.ImageAction = (_, _) => throw new IOException("lost");
        Assert.Equal(CatalogBatchStatus.Paused, (await Runner().RunAsync(plan, true, default)).Status);
        File.Move(Path.Combine(_root, "catalog-batches", plan.BatchId + ".json"), Path.Combine(_root, "moved-snapshot.json"));
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => Runner().RunAsync(plan, true, default));
        Assert.Equal("catalog_batch_state_missing_preserve_identity", failure.Message);
        Assert.Single(_client.Images);
        Assert.Single(_client.OperationReads);
        Assert.Null(Store.Load(plan.BatchId));
        Assert.Contains(plan.BatchId, CatalogBatchRecoveryMarkers.ListPending(_root));
    }

    [Fact]
    public async Task CredentialRestoreLeaseRejectsChangedOrCompletedPlanAndReleasesOnFailure()
    {
        CatalogBatchPlan plan = Plan();
        _client.HasApiKey = false;
        await Runner().RunAsync(plan, true, default);
        Assert.Throws<InvalidDataException>(() => Store.AcquireVerifiedPlanLease(plan with { FaceLimit = 2000 }));
        using (Store.AcquireVerifiedPlanLease(plan))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => Runner().RunAsync(plan, true, default));
        }
        _client.HasApiKey = true;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Throws<InvalidOperationException>(() => Store.AcquireVerifiedPlanLease(plan));
        Assert.Throws<InvalidDataException>(() => Store.AcquireVerifiedPlanLease(Plan()));
        Assert.Equal(plan.BatchId, Assert.Single(Store.List()).Plan.BatchId);
    }

    [Fact]
    public async Task PollBudgetPausesWithoutDiscardingTaskIdentity()
    {
        CatalogBatchPlan plan = Plan();
        _client.TaskAction = id => _client.TaskReceipt(id) with { Status = "running", Progress = 25 };
        CatalogBatchSnapshot paused = await Runner(1).RunAsync(plan, true, default);
        Assert.Equal("catalog_batch_poll_limit_resume_same_plan", paused.MessageCode);
        _client.TaskAction = null;
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
        Assert.Single(_client.Images);
    }

    [Fact]
    public async Task ExclusiveBatchLockPreventsConcurrentDispatch()
    {
        CatalogBatchPlan plan = Plan();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.ImageAction = async (request, _) =>
        {
            reached.SetResult();
            await release.Task;
            return FakeClient.ImageReceipt(request);
        };
        Task<CatalogBatchSnapshot> active = Runner().RunAsync(plan, true, default);
        try
        {
            await Task.WhenAny(reached.Task, active).WaitAsync(TimeSpan.FromSeconds(10));
            if (!reached.Task.IsCompleted)
            {
                // Surface storage/platform failures before dispatch instead of waiting forever.
                await active;
                Assert.Fail("Batch ended before reaching the blocked image dispatch.");
            }
            await Assert.ThrowsAnyAsync<IOException>(() => Runner().RunAsync(plan, true, default));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(CatalogBatchStatus.Completed, (await active.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Single(_client.Images);
    }

    [Fact]
    public async Task OtherBatchMarkersBlockNewWorkButOwnMarkerMayResume()
    {
        CatalogBatchPlan plan = Plan();
        CatalogBatchPlan other = Plan();
        CatalogBatchRecoveryMarkers.MarkPending(other.BatchId, other.Fingerprint, _root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(plan, true, default));
        Assert.Empty(_client.Images);
        TripoPanelRecoveryStore recovery = new("revit", _root);
        Assert.True(recovery.LoadCredentialMutationBlocks(excludeCatalogBatchId: plan.BatchId).HasBlock);
        CatalogBatchRecoveryMarkers.Clear(other.BatchId, other.Fingerprint, _root);
        CatalogBatchRecoveryMarkers.MarkPending(plan.BatchId, plan.Fingerprint, _root);
        Assert.False(recovery.LoadCredentialMutationBlocks(excludeCatalogBatchId: plan.BatchId).HasBlock);
        Assert.True(recovery.LoadCredentialMutationBlocks().HasBlock);
        Assert.Equal(CatalogBatchStatus.Completed, (await Runner().RunAsync(plan, true, default)).Status);
    }

    internal static CatalogBatchPlan Plan(int count = 1) => new(Guid.NewGuid().ToString("D"), "document-1",
        Enumerable.Range(0, count).Select(index => new CatalogBatchItem("product-" + index, "Chair " + index,
            new StagedImageTransfer(Guid.NewGuid().ToString("D"), new string('a', 64), 123, "image/png"),
            new CatalogFamilyOptions("product-" + index, "Maker", "Model", "Description", "Catalogue", 500, 600, 800))).ToArray(),
        1000, true, "v3.1-20260211");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeClient : IHostControlClient
    {
        private readonly Dictionary<string, string> _operationAccounts = new(StringComparer.Ordinal);
        public string Account { get; set; } = "original-account";
        public HostControlImageCredentialAnchor? RejectedAnchor { get; private set; }
        public bool HasApiKey { get; set; } = true;
        public string DocumentSession { get; set; } = "document-1";
        public List<HostControlCreateImageTaskRequest> Images { get; } = [];
        public List<HostControlCreateObjConversionRequest> Conversions { get; } = [];
        public List<HostControlImportObjTaskRequest> Imports { get; } = [];
        public List<string> OperationReads { get; } = [];
        public List<string> Effects { get; } = [];
        public Action<HostControlCreateImageTaskRequest>? BeforeImage { get; set; }
        public Func<HostControlCreateImageTaskRequest, CancellationToken, Task<HostControlImageTaskCreationReceipt>>? ImageAction { get; set; }
        public Func<HostControlCreateObjConversionRequest, HostControlObjConversionCreationReceipt>? ConversionAction { get; set; }
        public Func<HostControlImportObjTaskRequest, HostControlObjTaskImportReceipt>? ImportAction { get; set; }
        public Func<string, HostControlOperationStatusReceipt>? OperationAction { get; set; }
        public Func<string, HostControlTaskStatusReceipt>? TaskAction { get; set; }
        public static string ImageTask(string operation) => operation;
        public static HostControlImageTaskCreationReceipt ImageReceipt(HostControlCreateImageTaskRequest request) =>
            new(request.OperationId, ImageTask(request.OperationId), request.Model!, request.Image.Sha256);
        public static HostControlObjConversionCreationReceipt ConversionReceipt(HostControlCreateObjConversionRequest request) =>
            new(request.OperationId, request.SourceTaskId, request.OperationId, "OBJ");
        public static HostControlObjTaskImportReceipt ImportReceipt(HostControlImportObjTaskRequest request, string status = "committed") =>
            new(request.OperationId, request.ConversionTaskId, 0, new HostImportReceipt("revit", request.DocumentSessionId,
                request.OperationId, "element-" + request.OperationId, 3, 1, 0, status, "family", 0, 0, "family.rfa"), []);
        public HostControlTaskStatusReceipt TaskReceipt(string id) =>
            new(id, Conversions.Any(request => request.OperationId == id) ? "convert_model" : "image_to_model",
                "success", 100, 0, null, null, null, null);
        public static HostControlOperationStatusReceipt DurableImage(string id) =>
            new(id, "image_task_creation", "task_id_persisted", null, ImageTask(id), null, null,
                true, true, false, "poll_task", DateTimeOffset.UtcNow);
        public Task<HostControlImageTaskCreationReceipt> CreateImageTaskAsync(HostControlCreateImageTaskRequest request, CancellationToken token)
        {
            ValidateAnchor(request.CredentialAnchor);
            BeforeImage?.Invoke(request);
            Images.Add(request);
            Effects.Add("image");
            _operationAccounts.TryAdd(request.OperationId, Account);
            return ImageAction?.Invoke(request, token) ?? Task.FromResult(ImageReceipt(request));
        }
        public Task<HostControlObjConversionCreationReceipt> CreateObjConversionAsync(HostControlCreateObjConversionRequest request, CancellationToken token)
        {
            ValidateAnchor(request.CredentialAnchor);
            Conversions.Add(request);
            Effects.Add("conversion");
            return Task.FromResult(ConversionAction?.Invoke(request) ?? ConversionReceipt(request));
        }
        public Task<HostControlObjTaskImportReceipt> ImportObjTaskAsync(HostControlImportObjTaskRequest request, CancellationToken token)
        {
            Imports.Add(request);
            Effects.Add("import");
            return Task.FromResult(ImportAction?.Invoke(request) ?? ImportReceipt(request));
        }
        private void ValidateAnchor(HostControlImageCredentialAnchor? anchor)
        {
            if (anchor is not null && (!_operationAccounts.TryGetValue(anchor.OperationId, out string? original) || original != Account))
            {
                RejectedAnchor = anchor;
                throw new HostControlCallException("catalog_credential_anchor_unverified", "The original batch account is required; no new paid operation was created.");
            }
        }
        public Task<HostControlOperationStatusReceipt> GetOperationStatusAsync(string id, CancellationToken token)
        {
            OperationReads.Add(id);
            return Task.FromResult(OperationAction?.Invoke(id) ?? throw new HostControlCallException("operation_not_found", "Unknown"));
        }
        public Task<HostControlTaskStatusReceipt> GetTaskStatusAsync(string id, CancellationToken token) =>
            Task.FromResult(TaskAction?.Invoke(id) ?? TaskReceipt(id));
        public Task<HostContextReceipt> GetHostContextAsync(CancellationToken token) =>
            Task.FromResult(new HostContextReceipt("revit", "2026", 123, DocumentSession, "Project", "mm", []));
        public Task<HostControlHealthReceipt> GetHealthAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlCredentialStatusReceipt> GetCredentialStatusAsync(CancellationToken token) =>
            Task.FromResult(new HostControlCredentialStatusReceipt(HasApiKey, "session", false, false, "none", false));
        public Task<HostControlCredentialMutationReceipt> SetApiKeyAsync(string key, bool persist, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlCredentialMutationReceipt> ClearApiKeyAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlTextTaskCreationReceipt> CreateTextTaskAsync(HostControlCreateTextTaskRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlHealthReceipt> ShutdownAsync(CancellationToken token) => throw new NotSupportedException();
    }
}

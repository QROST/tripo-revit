using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using Tripo.Bridge;
using Tripo.Catalog.Batch;
using Tripo.HostUi;
using Xunit;

namespace Tripo.Catalog.Tests;

public sealed class BatchStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "catalog-storage-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LayoutIsDeterministicAccountsForRotationAndReservesUniformTolerance()
    {
        CatalogBatchPlan plan = BatchRunnerTests.Plan(3);
        CatalogBatchPlan prepared = (plan with
        {
            RowWidthMillimeters = 1800, GapMillimeters = 100,
            Items = plan.Items.Select((item, index) => item with
            {
                Family = item.Family with { WidthMillimeters = 1000, DepthMillimeters = 500, RotationDegrees = index == 1 ? 90 : 0 },
            }).ToArray(),
        }).Prepare();
        Assert.Equal(550, prepared.Items[0].Family.XMillimeters, 6);
        Assert.Equal(275, prepared.Items[0].Family.YMillimeters, 6);
        Assert.Equal(1475, prepared.Items[1].Family.XMillimeters, 6);
        Assert.Equal(550, prepared.Items[1].Family.YMillimeters, 6);
        Assert.Equal(550, prepared.Items[2].Family.XMillimeters, 6);
        Assert.Equal(1475, prepared.Items[2].Family.YMillimeters, 6);
        Assert.Equal(prepared.Fingerprint, prepared.Prepare().Fingerprint);
        CatalogBatchPlan reordered = prepared with { Items = prepared.Items.Reverse().ToArray() };
        Assert.NotEqual(prepared.Fingerprint, reordered.Fingerprint);
    }

    [Theory]
    [InlineData(0.5, 0.1)]
    [InlineData(500, 0.3)]
    [InlineData(double.NaN, 0.1)]
    public void PlanEnforcesSameFamilyValidationAsTheHostBeforePaidDispatch(double width, double error)
    {
        CatalogBatchPlan plan = BatchRunnerTests.Plan();
        CatalogBatchItem item = plan.Items[0];
        Assert.Throws<BridgeCallException>(() => (plan with
        {
            Items = [item with { Family = item.Family with { WidthMillimeters = width, MaxDimensionErrorRatio = error } }],
        }).Prepare());
    }

    [Fact]
    public void PlanRejectsOvercapacityAndRowsThatCannotFit()
    {
        Assert.Throws<InvalidDataException>(() => BatchRunnerTests.Plan(101).Prepare());
        Assert.Throws<InvalidDataException>(() => (BatchRunnerTests.Plan() with { RowWidthMillimeters = 100 }).Prepare());
    }

    [Fact]
    public async Task SnapshotRejectsModifiedDigestDuplicatePropertiesAndOversizedFiles()
    {
        CatalogBatchPlan plan = BatchRunnerTests.Plan();
        CatalogBatchStore store = new(_root);
        CatalogBatchRunner runner = new(new UnusedClient(), store,
            (_, _) => throw new InvalidOperationException("gate deliberately blocks before any dispatch"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, true, default));
        Assert.Equal(CatalogBatchStatus.Ready, store.LoadLatest()!.Status);
        Assert.Equal(plan.BatchId, Assert.Single(store.List()).Plan.BatchId);
        string path = Path.Combine(_root, "catalog-batches", plan.BatchId + ".json");
        string original = await File.ReadAllTextAsync(path);
        JsonNode changed = JsonNode.Parse(original)!;
        changed["snapshot"]!["plan"]!["faceLimit"] = 2000;
        await File.WriteAllTextAsync(path, changed.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load(plan.BatchId));
        await File.WriteAllTextAsync(path, original.Insert(1, "\"schemaVersion\":1,"));
        Assert.Throws<InvalidDataException>(() => store.Load(plan.BatchId));
        await File.WriteAllBytesAsync(path, new byte[CatalogBatchStore.MaximumSnapshotBytes + 1]);
        Assert.Throws<InvalidDataException>(() => store.Load(plan.BatchId));
    }

    [Fact]
    public void InvalidOrChangedMarkerBlocksCredentialChangesEvenWhenOwnIdIsExcluded()
    {
        CatalogBatchPlan plan = BatchRunnerTests.Plan();
        CatalogBatchRecoveryMarkers.MarkPending(plan.BatchId, plan.Fingerprint, _root);
        Assert.Throws<InvalidDataException>(() => CatalogBatchRecoveryMarkers.MarkPending(plan.BatchId, new string('b', 64), _root));
        string path = Path.Combine(_root, "catalog-batch-recovery", plan.BatchId + ".json");
        File.WriteAllText(path, "{}");
        TripoPanelRecoveryStore recovery = new("revit", _root);
        Assert.True(recovery.LoadCredentialMutationBlocks(excludeCatalogBatchId: plan.BatchId).HasBlock);
        Assert.True(recovery.LoadCredentialMutationBlocks().HasBlock);
    }

    [Fact]
    public void MarkerUsesPrivateModesAndRejectsAReparseDirectory()
    {
        CatalogBatchPlan plan = BatchRunnerTests.Plan();
        CatalogBatchRecoveryMarkers.MarkPending(plan.BatchId, plan.Fingerprint, _root);
        if (OperatingSystem.IsWindows()) return; // Windows ACLs are enforced by the same storage primitive.
        string directory = Path.Combine(_root, "catalog-batch-recovery");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, plan.BatchId + ".json")));
        CatalogBatchRecoveryMarkers.Clear(plan.BatchId, plan.Fingerprint, _root);
        Directory.Delete(directory, recursive: true);
        string target = Path.Combine(_root, "other");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(directory, target);
        Assert.Throws<InvalidDataException>(() => CatalogBatchRecoveryMarkers.ListPending(_root));
        Assert.True(new TripoPanelRecoveryStore("revit", _root).LoadCredentialMutationBlocks().HasBlock);
        Directory.Delete(directory);
    }

    [Fact]
    public void NewPrivateFileHasExplicitOwnerAndRemainsExclusivelyLocked()
    {
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_root);
        string path = Path.Combine(_root, "private.lock");
        using (FileStream stream = CatalogBatchStorageSafety.CreatePrivateFile(path))
        {
            CatalogBatchStorageSafety.ValidatePrivateFile(path);
            if (OperatingSystem.IsWindows())
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                FileSecurity security = stream.GetAccessControl();
                Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
                Assert.True(security.AreAccessRulesProtected);
                FileSystemAccessRule rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>());
                Assert.Equal(identity.User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            }
            Assert.ThrowsAny<IOException>(() => CatalogBatchStorageSafety.OpenLock(path));
        }
        using FileStream reopened = CatalogBatchStorageSafety.OpenLock(path);
        Assert.Equal(0, reopened.Length);
    }

    [Fact]
    public void WindowsNonUserDefaultOwnerIsStillRejectedForExistingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User!;
        SecurityIdentifier defaultOwner = identity.Owner!;
        // The elevated Windows CI runner has a group default owner; standard user tokens may not.
        if (defaultOwner.Equals(user)) return;
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_root);
        string path = Path.Combine(_root, "existing.lock");
        FileSecurity security = new();
        security.SetOwner(defaultOwner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        using (new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
            FileShare.None, 4096, FileOptions.None, security)) { }
        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(() =>
            CatalogBatchStorageSafety.ValidatePrivateFile(path));
        Assert.Equal("catalog_batch_owner_invalid", exception.Message);
        Assert.Throws<UnauthorizedAccessException>(() => CatalogBatchStorageSafety.OpenLock(path));
        Assert.Equal(defaultOwner, new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class UnusedClient : IHostControlClient
    {
        public Task<HostControlHealthReceipt> GetHealthAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlCredentialStatusReceipt> GetCredentialStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlCredentialMutationReceipt> SetApiKeyAsync(string key, bool persist, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlCredentialMutationReceipt> ClearApiKeyAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostContextReceipt> GetHostContextAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlTextTaskCreationReceipt> CreateTextTaskAsync(HostControlCreateTextTaskRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlTaskStatusReceipt> GetTaskStatusAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlOperationStatusReceipt> GetOperationStatusAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlObjConversionCreationReceipt> CreateObjConversionAsync(HostControlCreateObjConversionRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlObjTaskImportReceipt> ImportObjTaskAsync(HostControlImportObjTaskRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<HostControlHealthReceipt> ShutdownAsync(CancellationToken token) => throw new NotSupportedException();
    }
}

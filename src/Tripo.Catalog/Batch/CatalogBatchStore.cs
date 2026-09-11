using System.Security.Cryptography;
using System.Text.Json;
using Tripo.Bridge;

namespace Tripo.Catalog.Batch;

public sealed class CatalogBatchStore
{
    public const int MaximumSnapshotBytes = 2 * 1024 * 1024;
    public const int MaximumBatches = 128;
    private readonly string _directory;

    public CatalogBatchStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? BridgePaths.GetRootDirectory());
        _directory = Path.Combine(RootDirectory, "catalog-batches");
    }

    public string RootDirectory { get; }

    public CatalogBatchSnapshot? Load(string batchId)
    {
        CatalogBatchPlan.ValidateUuid(batchId);
        CatalogBatchStorageSafety.RejectLink(RootDirectory);
        CatalogBatchStorageSafety.RejectLink(_directory);
        if (!Directory.Exists(_directory)) return null;
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_directory);
        string path = StatePath(batchId);
        CatalogBatchStorageSafety.RejectLink(path);
        if (!File.Exists(path)) return null;
        CatalogBatchStorageSafety.ValidatePrivateFile(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is < 1 or > MaximumSnapshotBytes)
            throw new InvalidDataException("catalog_batch_state_size_invalid");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        RejectDuplicateProperties(bytes);
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, BatchJson.Options)
            ?? throw new InvalidDataException("catalog_batch_state_invalid");
        if (envelope.SchemaVersion != 1 || envelope.Snapshot is null ||
            envelope.Digest != Digest(envelope.Snapshot) || envelope.Snapshot.Plan is null ||
            envelope.Snapshot.Plan.BatchId != batchId)
            throw new InvalidDataException("catalog_batch_state_integrity_invalid");
        Validate(envelope.Snapshot);
        return envelope.Snapshot with
        {
            Plan = envelope.Snapshot.Plan.Prepare(),
            Items = Array.AsReadOnly(envelope.Snapshot.Items.ToArray()),
        };
    }

    public IReadOnlyList<CatalogBatchSnapshot> List()
    {
        CatalogBatchStorageSafety.RejectLink(RootDirectory);
        CatalogBatchStorageSafety.RejectLink(_directory);
        if (!Directory.Exists(_directory)) return [];
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_directory);
        string[] files = Directory.EnumerateFileSystemEntries(_directory).Take(MaximumBatches * 2 + 2).ToArray();
        if (files.Length > MaximumBatches * 2 + 1)
            throw new InvalidDataException("catalog_batch_storage_capacity");
        List<CatalogBatchSnapshot> snapshots = [];
        foreach (string path in files)
        {
            CatalogBatchStorageSafety.ValidatePrivateFile(path);
            string name = Path.GetFileName(path);
            if (name == "batch.lock") continue;
            if (name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                if (new FileInfo(path).Length > MaximumSnapshotBytes)
                    throw new InvalidDataException("catalog_batch_state_size_invalid");
                continue;
            }
            if (!name.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidDataException("catalog_batch_unknown_file");
            CatalogBatchSnapshot snapshot = Load(name[..^5])
                ?? throw new InvalidDataException("catalog_batch_state_disappeared");
            snapshots.Add(snapshot);
            if (snapshots.Count > MaximumBatches) throw new InvalidDataException("catalog_batch_storage_capacity");
        }
        return snapshots.OrderByDescending(snapshot => snapshot.UpdatedAtUtc).ToArray();
    }

    public CatalogBatchSnapshot? LoadLatest(bool unfinishedOnly = false) =>
        List().FirstOrDefault(snapshot => !unfinishedOnly || snapshot.Status != CatalogBatchStatus.Completed);

    /// <summary>Hold batch identity stable while restoring its original session-only credential.</summary>
    public IDisposable AcquireVerifiedPlanLease(CatalogBatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CatalogBatchPlan frozen = plan.Prepare();
        IDisposable lease = AcquireLock();
        try
        {
            CatalogBatchSnapshot saved = Load(frozen.BatchId)
                ?? throw new InvalidDataException("catalog_batch_not_found");
            if (saved.PlanHash != frozen.Fingerprint) throw new InvalidDataException("catalog_batch_plan_changed");
            if (saved.Status == CatalogBatchStatus.Completed) throw new InvalidOperationException("catalog_batch_already_completed");
            // Check the separate blocker identity before a caller excludes it from the credential gate.
            if (saved.Items.Any(item => item.GenerationDispatchAttempted) ||
                CatalogBatchRecoveryMarkers.ListPending(RootDirectory).Contains(frozen.BatchId, StringComparer.Ordinal))
                CatalogBatchRecoveryMarkers.MarkPending(frozen.BatchId, saved.PlanHash, RootDirectory);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal IDisposable AcquireLock()
    {
        CatalogBatchStorageSafety.RejectLink(RootDirectory);
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_directory);
        return CatalogBatchStorageSafety.OpenLock(Path.Combine(_directory, "batch.lock"));
    }

    internal CatalogBatchSnapshot LoadOrCreate(CatalogBatchPlan plan)
    {
        CatalogBatchSnapshot? existing = Load(plan.BatchId);
        string hash = CatalogBatchPlan.Hash(plan);
        if (existing is not null)
        {
            if (existing.PlanHash != hash) throw new InvalidDataException("catalog_batch_plan_changed");
            return existing;
        }
        if (CatalogBatchRecoveryMarkers.ListPending(RootDirectory).Contains(plan.BatchId, StringComparer.Ordinal))
            throw new InvalidDataException("catalog_batch_state_missing_preserve_identity");
        if (List().Count >= MaximumBatches) throw new InvalidDataException("catalog_batch_storage_capacity");
        CatalogBatchSnapshot created = new(1, hash, plan,
            plan.Items.Select(item => new CatalogBatchItemState(item.ProductId,
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"))).ToArray(),
            CatalogBatchStatus.Ready, null, DateTimeOffset.UtcNow);
        Save(created);
        return created;
    }

    internal void Save(CatalogBatchSnapshot snapshot)
    {
        Validate(snapshot);
        CatalogBatchStorageSafety.RejectLink(RootDirectory);
        CatalogBatchStorageSafety.EnsurePrivateDirectory(_directory);
        if (Directory.EnumerateFileSystemEntries(_directory).Take(MaximumBatches * 2 + 1).Count() >= MaximumBatches * 2 + 1)
            throw new InvalidDataException("catalog_batch_storage_capacity");
        string path = StatePath(snapshot.Plan.BatchId);
        CatalogBatchStorageSafety.RejectLink(path);
        if (File.Exists(path)) CatalogBatchStorageSafety.ValidatePrivateFile(path);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Digest(snapshot), snapshot), BatchJson.Options);
        if (bytes.Length > MaximumSnapshotBytes) throw new InvalidDataException("catalog_batch_state_size_invalid");
        string temporary = Path.Combine(_directory, snapshot.Plan.BatchId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (FileStream stream = CatalogBatchStorageSafety.CreatePrivateFile(temporary))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        CatalogBatchStorageSafety.RejectLink(path);
        File.Move(temporary, path, overwrite: true);
        CatalogBatchSnapshot observed = Load(snapshot.Plan.BatchId)
            ?? throw new IOException("catalog_batch_checkpoint_unconfirmed");
        if (Digest(observed) != Digest(snapshot)) throw new IOException("catalog_batch_checkpoint_unconfirmed");
    }

    private string StatePath(string batchId) => Path.Combine(_directory, batchId + ".json");
    private static string Digest(CatalogBatchSnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, BatchJson.Options))).ToLowerInvariant();

    private static void Validate(CatalogBatchSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.Plan is null || snapshot.Plan.Items is null || snapshot.Items is null ||
            !Enum.IsDefined(snapshot.Status) || snapshot.Items.Count != snapshot.Plan.Items.Count ||
            snapshot.PlanHash != snapshot.Plan.Fingerprint)
            throw new InvalidDataException("catalog_batch_state_invalid");
        if (snapshot.MessageCode is not null) CatalogBatchPlan.ValidateText(snapshot.MessageCode, 128);
        HashSet<string> operations = new(StringComparer.Ordinal);
        for (int index = 0; index < snapshot.Items.Count; index++)
        {
            CatalogBatchItemState item = snapshot.Items[index];
            if (item is null || item.ProductId != snapshot.Plan.Items[index].ProductId || !Enum.IsDefined(item.Stage) ||
                item.Progress is < 0 or > 100 ||
                item.GenerationTaskId is not null && (!item.GenerationDispatchAttempted || !TripoTaskId.IsValid(item.GenerationTaskId)) ||
                item.GenerationSucceeded && item.GenerationTaskId is null ||
                item.ConversionDispatchAttempted && !item.GenerationSucceeded ||
                item.ConversionTaskId is not null && (!item.ConversionDispatchAttempted || !TripoTaskId.IsValid(item.ConversionTaskId)) ||
                item.ConversionSucceeded && item.ConversionTaskId is null ||
                item.ImportDispatchAttempted && !item.ConversionSucceeded ||
                item.GenerationOperationState is not null && !item.GenerationDispatchAttempted ||
                item.ConversionOperationState is not null && !item.ConversionDispatchAttempted ||
                (item.GenerationOperationState is null) != (item.GenerationOperationUpdatedAtUtc is null) ||
                (item.ConversionOperationState is null) != (item.ConversionOperationUpdatedAtUtc is null) ||
                item.Stage == CatalogBatchStage.Completed && (!item.ImportDispatchAttempted || string.IsNullOrWhiteSpace(item.CreatedId)))
                throw new InvalidDataException("catalog_batch_item_state_invalid");
            foreach (string operation in new[] { item.GenerationOperationId, item.ConversionOperationId, item.ImportOperationId })
            {
                CatalogBatchPlan.ValidateUuid(operation);
                if (!operations.Add(operation)) throw new InvalidDataException("catalog_batch_operation_collision");
            }
            if (item.MessageCode is not null) CatalogBatchPlan.ValidateText(item.MessageCode, 128);
            if (item.CreatedId is not null) CatalogBatchPlan.ValidateText(item.CreatedId, 2048);
            if (item.GenerationOperationState is not null) CatalogBatchPlan.ValidateText(item.GenerationOperationState, 64);
            if (item.ConversionOperationState is not null) CatalogBatchPlan.ValidateText(item.ConversionOperationState, 64);
        }
        if (snapshot.Status == CatalogBatchStatus.Completed &&
            snapshot.Items.Any(item => item.Stage is not (CatalogBatchStage.Completed or CatalogBatchStage.Failed)))
            throw new InvalidDataException("catalog_batch_completion_invalid");
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        Walk(document.RootElement);
        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("catalog_batch_duplicate_property");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement child in element.EnumerateArray()) Walk(child);
        }
    }

    private sealed record Envelope(int SchemaVersion, string Digest, CatalogBatchSnapshot Snapshot);
}

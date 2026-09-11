using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Tripo.Bridge;

/// <summary>Account-bound batch blockers readable by HostUi without a Catalog dependency.</summary>
public static class CatalogBatchRecoveryMarkers
{
    public const int MaximumMarkers = 128;

    public static void MarkPending(string batchId, string planHash, string? rootDirectory = null)
    {
        ValidateIdentity(batchId, planHash);
        string directory = DirectoryPath(rootDirectory);
        CatalogBatchStorageSafety.EnsurePrivateDirectory(directory);
        string path = Path.Combine(directory, batchId + ".json");
        using FileStream lease = CatalogBatchStorageSafety.OpenLock(Path.Combine(directory, "markers.lock"));
        if (File.Exists(path))
        {
            (string existingId, string existingHash) = Read(path);
            if (existingId != batchId || existingHash != planHash)
            {
                throw new InvalidDataException("catalog_batch_recovery_identity_changed");
            }
            return;
        }

        if (ListPending(rootDirectory).Count >= MaximumMarkers)
        {
            throw new InvalidDataException("catalog_batch_recovery_capacity");
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, batchId, planHash });
        using FileStream stream = CatalogBatchStorageSafety.CreatePrivateFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static void Clear(string batchId, string planHash, string? rootDirectory = null)
    {
        ValidateIdentity(batchId, planHash);
        string directory = DirectoryPath(rootDirectory);
        if (!Directory.Exists(directory)) return;
        CatalogBatchStorageSafety.EnsurePrivateDirectory(directory);
        using FileStream lease = CatalogBatchStorageSafety.OpenLock(Path.Combine(directory, "markers.lock"));
        string path = Path.Combine(directory, batchId + ".json");
        if (!File.Exists(path)) return;
        (string existingId, string existingHash) = Read(path);
        if (existingId != batchId || existingHash != planHash)
        {
            throw new InvalidDataException("catalog_batch_recovery_identity_changed");
        }
        File.Delete(path);
    }

    public static IReadOnlyList<string> ListPending(string? rootDirectory = null)
    {
        string directory = DirectoryPath(rootDirectory);
        CatalogBatchStorageSafety.RejectLink(directory);
        if (!Directory.Exists(directory)) return [];
        CatalogBatchStorageSafety.EnsurePrivateDirectory(directory);
        string[] entries = Directory.EnumerateFileSystemEntries(directory).Take(MaximumMarkers + 2).ToArray();
        if (entries.Length > MaximumMarkers + 1)
        {
            throw new InvalidDataException("catalog_batch_recovery_capacity");
        }
        List<string> result = [];
        foreach (string path in entries)
        {
            if (Path.GetFileName(path) == "markers.lock")
            {
                CatalogBatchStorageSafety.ValidatePrivateFile(path);
                continue;
            }
            (string id, _) = Read(path);
            if (Path.GetFileName(path) != id + ".json")
            {
                throw new InvalidDataException("catalog_batch_recovery_file_invalid");
            }
            result.Add(id);
        }
        return result.AsReadOnly();
    }

    private static (string BatchId, string PlanHash) Read(string path)
    {
        CatalogBatchStorageSafety.ValidatePrivateFile(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 1024) throw new InvalidDataException("catalog_batch_recovery_file_invalid");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Name is not ("schemaVersion" or "batchId" or "planHash"))
                throw new InvalidDataException("catalog_batch_recovery_file_invalid");
        }
        if (names.Count != 3 || root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("catalog_batch_recovery_file_invalid");
        string id = root.GetProperty("batchId").GetString() ?? string.Empty;
        string hash = root.GetProperty("planHash").GetString() ?? string.Empty;
        ValidateIdentity(id, hash);
        return (id, hash);
    }

    private static string DirectoryPath(string? root)
    {
        string directory = Path.GetFullPath(root ?? BridgePaths.GetRootDirectory());
        CatalogBatchStorageSafety.RejectLink(directory);
        return Path.Combine(directory, "catalog-batch-recovery");
    }

    private static void ValidateIdentity(string id, string hash)
    {
        if (!Guid.TryParseExact(id, "D", out Guid parsed) || parsed.ToString("D") != id ||
            hash is null || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("catalog_batch_recovery_identity_invalid");
    }
}

/// <summary>Private non-reparse storage primitives shared by batch state and its recovery marker.</summary>
public static class CatalogBatchStorageSafety
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModePrivate = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsurePrivateDirectory(string path)
    {
        RejectLink(path);
        bool existed = Directory.Exists(path);
        if (!existed)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
                DirectorySecurity security = new();
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                SecurityIdentifier owner = identity.User ?? throw new UnauthorizedAccessException();
                security.SetOwner(owner);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else Directory.CreateDirectory(path, DirectoryMode);
        }
        RejectLink(path);
        if (OperatingSystem.IsWindows()) ValidateWindowsAccess(new DirectoryInfo(path).GetAccessControl());
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != DirectoryMode)
            throw new UnauthorizedAccessException("catalog_batch_directory_not_private");
    }

    public static void RejectLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("catalog_batch_reparse_path");
    }

    public static void ValidatePrivateFile(string path)
    {
        RejectLink(path);
        FileInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("catalog_batch_file_invalid");
        if (OperatingSystem.IsWindows()) ValidateWindowsAccess(info.GetAccessControl());
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != FileModePrivate)
            throw new UnauthorizedAccessException("catalog_batch_file_not_private");
    }

    public static FileStream CreatePrivateFile(string path)
    {
        RejectLink(path);
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModePrivate;
        FileStream stream = new(path, options);
        try
        {
            ValidatePrivateFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static FileStream OpenLock(string path)
    {
        RejectLink(path);
        FileStream stream;
        try { stream = CreatePrivateFile(path); }
        catch (IOException) when (File.Exists(path))
        {
            ValidatePrivateFile(path);
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        if (stream.Length != 0)
        {
            stream.Dispose();
            throw new InvalidDataException("catalog_batch_lock_invalid");
        }
        return stream;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsAccess(FileSystemSecurity security)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User ?? throw new UnauthorizedAccessException();
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("catalog_batch_owner_invalid");
        bool hasOwnerRule = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException("catalog_batch_access_not_private");
            if (rule.AccessControlType == AccessControlType.Allow && owner.Equals(rule.IdentityReference)) hasOwnerRule = true;
        }
        // An absent/null DACL must not be mistaken for an empty private allow-list.
        if (!hasOwnerRule) throw new UnauthorizedAccessException("catalog_batch_access_not_private");
    }
}

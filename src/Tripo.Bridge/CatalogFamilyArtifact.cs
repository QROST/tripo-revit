namespace Tripo.Bridge;

/// <summary>Checks a persisted catalogue artifact before it can authorize family reuse.</summary>
public static class CatalogFamilyArtifact
{
    public static string ValidateRecord(
        string? storedFingerprint,
        string? storedPath,
        string expectedFingerprint,
        string expectedFileName,
        string familiesDirectory)
    {
        if (expectedFingerprint is null || expectedFingerprint.Length != 64 ||
            expectedFingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !string.Equals(storedFingerprint, expectedFingerprint, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(storedPath) || !Path.IsPathFullyQualified(storedPath))
        {
            throw Conflict();
        }

        try
        {
            string path = Path.GetFullPath(storedPath);
            string? directory = Path.GetDirectoryName(path);
            string? parent = directory is null ? null : Path.GetDirectoryName(directory);
            string attempt = directory is null ? string.Empty : Path.GetFileName(directory);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (directory is null || parent is null ||
                !string.Equals(path, storedPath, comparison) ||
                !string.Equals(Path.GetFileName(path), expectedFileName, comparison) ||
                !string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(familiesDirectory)), comparison) ||
                !attempt.StartsWith("catalog-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(attempt["catalog-".Length..], "N", out _))
            {
                throw Conflict();
            }

            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BridgeCallException("idempotency_conflict", "The saved catalogue artifact path is invalid.", exception);
        }
    }

    private static BridgeCallException Conflict() => new("idempotency_conflict",
        "The saved catalogue artifact identity or product path does not match this import.");
}

using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class CatalogFamilyArtifactTests
{
    private static readonly string Fingerprint = new('a', 64);
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-record-tests", "families"));
    private const string Attempt = "catalog-11111111222233334444555555555555";
    private const string FileName = "Maker_Chair_11111111222233334444555555555555.rfa";

    [Fact]
    public void MatchingRecordValidatesWithoutRequiringTheExportToRemainOnDisk()
    {
        string path = Path.Combine(Root, Attempt, FileName);
        Assert.Equal(path, CatalogFamilyArtifact.ValidateRecord(Fingerprint, path, Fingerprint, FileName, Root));
        // A removed export does not imply that an existing project instance
        // should be recreated; host recovery reports a missing export separately.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void MismatchedOrMissingFingerprintCannotAuthorizeReuse(string? stored)
    {
        Assert.Equal("idempotency_conflict", Assert.Throws<BridgeCallException>(() =>
            CatalogFamilyArtifact.ValidateRecord(stored, Path.Combine(Root, Attempt, FileName), Fingerprint, FileName, Root)).Code);
    }

    [Fact]
    public void ProductMismatchExternalDirectoryTraversalAndMissingPathAreRejected()
    {
        string?[] invalid =
        [
            null,
            "",
            Path.Combine(Attempt, FileName),
            Path.Combine(Root, Attempt, "AnotherProduct.rfa"),
            Path.Combine(Root, Attempt, FileName + ".bak"),
            Path.Combine(Root + "-other", Attempt, FileName),
            Path.Combine(Root, "untracked-attempt", FileName),
            Path.Combine(Root, "catalog-not-a-uuid", FileName),
            Path.Combine(Root, "extra", "..", Attempt, FileName),
            Path.Combine(Root, Attempt, "bad\0name.rfa"),
        ];
        foreach (string? path in invalid)
        {
            Assert.Equal("idempotency_conflict", Assert.Throws<BridgeCallException>(() =>
                CatalogFamilyArtifact.ValidateRecord(Fingerprint, path, Fingerprint, FileName, Root)).Code);
        }
    }
}

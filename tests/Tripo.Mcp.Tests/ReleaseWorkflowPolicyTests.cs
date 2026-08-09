using System.Xml.Linq;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class ReleaseWorkflowPolicyTests
{
    [Fact]
    public void CandidateWorkflowUsesAnImmutableExistingTagAndNeverOverwrites()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(
            Path.Combine(
                root,
                ".github",
                "workflows",
                "release-candidate.yml"));

        int resolveIndex = workflow.IndexOf(
            "- name: Resolve version",
            StringComparison.Ordinal);
        int checkoutIndex = workflow.IndexOf(
            "- uses: actions/checkout@",
            StringComparison.Ordinal);
        int verifyIndex = workflow.IndexOf(
            "- name: Verify immutable release identity",
            StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0);
        Assert.True(checkoutIndex > resolveIndex);
        Assert.True(verifyIndex > checkoutIndex);
        Assert.Contains(
            "ref: ${{ steps.version.outputs.ref }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "persist-credentials: false",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not match tag '$tag' commit",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not match VERSION",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Existing releases and assets are immutable",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--verify-tag\"",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--clobber",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"release\", \"upload\"",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"release\", \"edit\"",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateVersionSurfacesStayAligned()
    {
        string root = FindRepositoryRoot();
        string version = File.ReadAllText(
                Path.Combine(root, "VERSION"))
            .Trim();
        XDocument buildProps = XDocument.Load(
            Path.Combine(root, "Directory.Build.props"));
        string? versionPrefix = buildProps
            .Descendants("VersionPrefix")
            .SingleOrDefault()
            ?.Value;
        string clientSource = File.ReadAllText(
            Path.Combine(root, "src", "Tripo.Mcp", "TripoV3Client.cs"));
        string workflow = File.ReadAllText(
            Path.Combine(
                root,
                ".github",
                "workflows",
                "release-candidate.yml"));

        Assert.Equal(version, versionPrefix);
        Assert.Contains(
            $"tripo-revit/{version}",
            clientSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"default: \"v{version}\"",
            workflow,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
}

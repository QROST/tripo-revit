using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class ReleaseWorkflowPolicyTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CandidateWorkflowUsesAnImmutableExistingTagAndNeverOverwrites(string checkoutLineEnding)
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(
            Path.Combine(
                root,
                ".github",
                "workflows",
                "release-candidate.yml"))
            .ReplaceLineEndings(checkoutLineEnding);
        // The policy concerns YAML content, independent of Git's checkout EOL.
        workflow = workflow.ReplaceLineEndings("\n");
        string identityPolicy = File.ReadAllText(
            Path.Combine(
                root,
                "packaging",
                "Test-ReleaseIdentity.ps1"));

        int candidateIndex = workflow.IndexOf(
            "  candidate:",
            StringComparison.Ordinal);
        int publishIndex = workflow.IndexOf(
            "  publish:",
            StringComparison.Ordinal);
        Assert.True(candidateIndex >= 0);
        Assert.True(publishIndex > candidateIndex);

        string candidateJob = workflow[candidateIndex..publishIndex];
        string publishJob = workflow[publishIndex..];
        int resolveIndex = candidateJob.IndexOf(
            "- name: Resolve version",
            StringComparison.Ordinal);
        int checkoutIndex = candidateJob.IndexOf(
            "- uses: actions/checkout@",
            StringComparison.Ordinal);
        int verifyIndex = candidateJob.IndexOf(
            "- name: Verify immutable release identity",
            StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0);
        Assert.True(checkoutIndex > resolveIndex);
        Assert.True(verifyIndex > checkoutIndex);
        Assert.Contains(
            "ref: ${{ steps.version.outputs.ref }}",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "persist-credentials: false",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains("id: identity", candidateJob, StringComparison.Ordinal);
        Assert.Contains(
            "$env:DISPATCH_VERSION.Trim()",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:PUSH_REF_NAME.Trim()",
            candidateJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"${{ inputs.version }}\".Trim()",
            candidateJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"${{ github.ref_name }}\".Trim()",
            candidateJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "./packaging/Test-ReleaseIdentity.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "$expectedEventCommit",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "Tag-push event commit",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_commit=$headCommit",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "tag_ref_sha=$tagRefSha",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not match tag '$Tag' commit",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not match VERSION",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "Tag-push event commit",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "/git/tags/$($remoteObject.sha)",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "/git/ref/tags/$encodedTag",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not resolve to checked-out commit",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "Existing releases and assets are immutable",
            identityPolicy,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--verify-tag\"",
            publishJob,
            StringComparison.Ordinal);

        Assert.Single(
            Regex.Matches(workflow, "contents: write").Cast<Match>());
        Assert.Contains(
            "permissions:\n      contents: read",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "permissions:\n      contents: write",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains("needs: candidate", publishJob, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "actions/checkout@",
            publishJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "./packaging/",
            publishJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ", publishJob, StringComparison.Ordinal);
        Assert.Contains(
            "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "SOURCE_COMMIT: ${{ needs.candidate.outputs.source_commit }}",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "TAG_REF_SHA: ${{ needs.candidate.outputs.tag_ref_sha }}",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "Downloaded candidate manifest does not match the trusted job outputs or assets.",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remote tag '$tag' moved after candidate verification.",
            publishJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--repo\", $repository",
            publishJob,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/commits/$encodedTag",
            workflow,
            StringComparison.Ordinal);
        AssertExactlyTwoMatches(
            workflow,
            "https://api\\.github\\.com/repos/\\$repository/git/tags/\\$\\(\\$remoteObject\\.sha\\)");
        AssertExactlyTwoMatches(
            workflow,
            "https://api\\.github\\.com/repos/\\$repository/releases/tags/\\$encodedTag");
        AssertExactlyTwoMatches(
            workflow,
            "https://api\\.github\\.com/repos/\\$repository/git/ref/tags/\\$encodedTag");
        Assert.Contains(
            "sourceCommit = $env:SOURCE_COMMIT",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "tagRefSha = $env:TAG_REF_SHA",
            candidateJob,
            StringComparison.Ordinal);
        Assert.Contains(
            "artifacts/*.manifest.json",
            candidateJob,
            StringComparison.Ordinal);

        int restore2025Index = candidateJob.IndexOf(
            "- name: Restore Revit 2025 host",
            StringComparison.Ordinal);
        int build2025Index = candidateJob.IndexOf(
            "- name: Build Revit 2025 host",
            StringComparison.Ordinal);
        int restore2026Index = candidateJob.IndexOf(
            "- name: Restore full solution (Revit 2026 host)",
            StringComparison.Ordinal);
        int build2026Index = candidateJob.IndexOf(
            "- name: Build full solution (Revit 2026 host)",
            StringComparison.Ordinal);
        Assert.True(restore2025Index > verifyIndex);
        Assert.True(build2025Index > restore2025Index);
        Assert.True(restore2026Index > build2025Index);
        Assert.True(build2026Index > restore2026Index);
        Assert.Contains(
            "Tripo.Revit.csproj --configuration Release --no-restore " +
            "/p:RevitYear=2025",
            candidateJob,
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
        string? restoreGraphPath = buildProps
            .Descendants("MSBuildProjectExtensionsPath")
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
        Assert.NotNull(restoreGraphPath);
        Assert.Contains(
            "obj/nuget/$(RevitYear)/",
            restoreGraphPath,
            StringComparison.Ordinal);
        Assert.Contains(
            $"tripo-revit/{version}",
            clientSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"default: \"v{version}\"",
            workflow,
            StringComparison.Ordinal);
    }

    private static void AssertExactlyTwoMatches(string input, string pattern) =>
        Assert.Collection(
            Regex.Matches(input, pattern).Cast<Match>(),
            _ => { },
            _ => { });

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

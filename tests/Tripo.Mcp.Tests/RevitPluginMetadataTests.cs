using System.Xml.Linq;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class RevitPluginMetadataTests
{
    private static readonly Guid ExpectedAddInId =
        new("8D522C7A-8E6D-4C80-A760-7886B2B44151");

    [Fact]
    public void AddinManifestPreservesTheInstalledApplicationIdentity()
    {
        string root = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
        string manifestPath = Path.Combine(
            root,
            "src",
            "Tripo.Revit",
            "Tripo.Revit.addin");
        Assert.True(
            File.Exists(manifestPath),
            $"The Revit add-in manifest was not found at {manifestPath}.");

        XElement addIn = Assert.Single(
            XDocument.Load(manifestPath)
                .Root!
                .Elements("AddIn"));

        Assert.Equal("Application", (string?)addIn.Attribute("Type"));
        Assert.Equal("Tripo MCP Bridge", (string?)addIn.Element("Name"));
        Assert.Equal(
            @"TripoRevit\Tripo.Revit.dll",
            (string?)addIn.Element("Assembly"));
        Assert.Equal(
            ExpectedAddInId,
            Guid.Parse((string?)addIn.Element("AddInId") ?? string.Empty));
        Assert.Equal(
            "Tripo.Revit.TripoRevitApplication",
            (string?)addIn.Element("FullClassName"));
        Assert.Equal("QROST", (string?)addIn.Element("VendorId"));
    }
}

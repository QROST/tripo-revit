using System.Net;
using System.Text;
using Tripo.Catalog;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Tripo.Catalog.Tests;

public sealed class CatalogImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "catalog-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] PixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    public CatalogImportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ReadsCollapsedMetricTableAndComponentGalleryWithoutExecutingScripts()
    {
        const string html = """
            <html><head><title>Product</title></head><body><main>
            <catalog-card product-name="Sample Armchair" product-name-subheadline="Tube Base"
              :presentation-tiles='[{"media":{"type":"image","src":"https://images.example.com/chair.webp"},"isPackshot":true},{"isDesigner":true,"media":{"type":"image","src":"https://images.example.com/portrait.jpg"}}]'></catalog-card>
            <dd aria-hidden="true"><table>
            <tr><td>Old item no.:</td><td>123</td></tr><tr><td>New item no.:</td><td>CHAIR-BLACK</td></tr>
            <tr><td>Height:</td><td>77,00 cm / 30.30 in</td></tr><tr><td>Width:</td><td>54,50 cm / 21.50 in</td></tr>
            <tr><td>Depth:</td><td>56,00 cm / 22.00 in</td></tr><tr><td>Seat depth:</td><td>46,00 cm</td></tr>
            <tr><td>Packaging Height:</td><td>0.54 m</td></tr></table></dd>
            <script>fetch('https://evil.example.com')</script></main></body></html>
            """;
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("sample.html", Encoding.UTF8.GetBytes(html)), _directory);
        CatalogProduct product = Assert.Single(result.Products);
        Assert.Equal("Sample Armchair Tube Base", product.Title);
        Assert.Equal("CHAIR-BLACK", product.Sku);
        Assert.False(product.ReviewAccepted);
        Assert.Equal(545, product.Dimensions.Single(value => value.Kind == CatalogDimensionKind.Width).MinimumMillimeters);
        Assert.Equal(770, product.Dimensions.Single(value => value.Kind == CatalogDimensionKind.Height).MinimumMillimeters);
        Assert.Equal(460, product.Dimensions.Single(value => value.Kind == CatalogDimensionKind.SeatDepth).MinimumMillimeters);
        Assert.Equal("https://images.example.com/chair.webp", Assert.Single(product.Images).SourceUrl);
        Assert.Null(product.Images[0].LocalPath);
    }

    [Fact]
    public async Task ReadsStructuredProductWithInchListAndRejectsLocalImageUrls()
    {
        const string html = """
            <html><head><script type="application/ld+json">{"@context":"https://schema.org","@type":"Product","name":"Wire Sofa","sku":"S-3","brand":{"name":"Sample"},"image":["https://cdn.example.com/sofa.jpg","http://localhost/private.jpg"],"width":{"@type":"QuantitativeValue","value":84,"unitCode":"INH"}}</script></head>
            <body><main><h1>Wire Sofa</h1><ul><li>Height (in): 30½</li><li>Depth (in): 34¾</li><li>Seat Height (in): 17</li><li>Seat Depth (in): 22</li></ul></main></body></html>
            """;
        using CatalogImporter importer = new();
        CatalogProduct product = Assert.Single((await importer.ImportFileAsync(await WriteAsync("sofa.html", Encoding.UTF8.GetBytes(html)), _directory)).Products);
        Assert.Equal("S-3", product.Sku);
        Assert.Equal("Sample", product.Manufacturer);
        Assert.Equal(2133.6, product.Dimensions.Single(value => value.Kind == CatalogDimensionKind.Width).MinimumMillimeters!.Value, 5);
        Assert.Equal(558.8, product.Dimensions.Single(value => value.Kind == CatalogDimensionKind.SeatDepth).MinimumMillimeters!.Value, 5);
        Assert.Single(product.Images);
    }

    [Fact]
    public async Task AssociatesDistinctSizeVariantsWithTheirOwnLabeledSpecificationSection()
    {
        const string html = """
            <script type="application/ld+json">{"@type":"ProductGroup","hasVariant":[
              {"@type":"Product","name":"Sofa two seat blue","sku":"S2B","size":"2-Seat Sofa"},
              {"@type":"Product","name":"Sofa three seat blue","sku":"S3B","size":"3-Seat Sofa"}]}</script>
            <main><h1>Sofa</h1><section><p>2-Seat Sofa</p><ul><li>Width (in): 63</li><li>Seat Depth (in): 21&frac12;</li></ul></section>
            <section><p>3-Seat Sofa</p><ul><li>Width (in): 89&frac14;</li><li>Seat Depth (in): 21&frac12;</li></ul></section></main>
            """;
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("variants.html", Encoding.UTF8.GetBytes(html)), _directory);
        Assert.Equal(2, result.Products.Count);
        Assert.Equal(1600.2, Assert.Single(result.Products[0].Dimensions, value => value.Kind == CatalogDimensionKind.Width).MinimumMillimeters!.Value, 4);
        Assert.Equal(2266.95, Assert.Single(result.Products[1].Dimensions, value => value.Kind == CatalogDimensionKind.Width).MinimumMillimeters!.Value, 4);
        Assert.All(result.Products, product => Assert.False(product.ReviewAccepted));
    }

    [Fact]
    public async Task SavedHtmlResolvesStructuredImagesAgainstItsPublicCanonicalUrl()
    {
        const string html = """
            <head><link rel="canonical" href="https://example.com/chairs/model-a">
            <script type="application/ld+json">{"@type":"Product","name":"Chair A","image":"../images/chair.jpg"}</script></head>
            <body><main><h1>Chair A</h1></main></body>
            """;
        using CatalogImporter importer = new();
        CatalogProduct product = Assert.Single((await importer.ImportFileAsync(await WriteAsync("saved.html", Encoding.UTF8.GetBytes(html)), _directory)).Products);
        Assert.Equal("https://example.com/images/chair.jpg", Assert.Single(product.Images).SourceUrl);
    }

    [Fact]
    public async Task CollectionPageProducesLinksAndExplicitGapInsteadOfInventingProduct()
    {
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("collection.html", Encoding.UTF8.GetBytes(
            "<h1>Collection</h1><main><a href='https://example.com/chair'>Chair</a><iframe src='https://example.com/book'></iframe></main>")), _directory);
        Assert.Empty(result.Products);
        Assert.Equal("https://example.com/chair", Assert.Single(result.CandidateLinks));
        Assert.Contains(result.Warnings, warning => warning.Code == "collection_or_dynamic_page");
        Assert.Contains(result.Warnings, warning => warning.Code == "embedded_content_not_loaded");
    }

    [Fact]
    public async Task PdfGroupsOnlyMatchingExplicitSkuAndKeepsPageCoordinatesAndImages()
    {
        PdfDocumentBuilder builder = new();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PageSize.A4);
        first.AddText("Sample Chair", 22, new PdfPoint(30, 780), font);
        first.AddText("SKU: CHAIR-1", 12, new PdfPoint(30, 740), font);
        first.AddText("Width: 55 cm", 12, new PdfPoint(30, 700), font);
        first.AddPng(PixelPng, new PdfRectangle(30, 500, 130, 600));
        var second = builder.AddPage(PageSize.A4);
        second.AddText("Sample Chair", 22, new PdfPoint(30, 780), font);
        second.AddText("SKU: CHAIR-1", 12, new PdfPoint(30, 740), font);
        second.AddText("Seat depth: 46 cm", 12, new PdfPoint(30, 700), font);
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("two-pages.pdf", builder.Build()), _directory);
        CatalogProduct product = Assert.Single(result.Products);
        Assert.Equal("CHAIR-1", product.Sku);
        Assert.Equal(2, product.Sources.Count);
        Assert.Equal(2, result.Sources[0].PageCount);
        Assert.All(result.Sources[0].TextBlocks, block => Assert.NotNull(block.Bounds));
        CatalogImage image = Assert.Single(product.Images);
        Assert.True(File.Exists(image.LocalPath));
        Assert.Equal(1, image.Source.PageNumber);
        Assert.NotNull(image.Source.Bounds);
        Assert.Contains(product.Dimensions, value => value.Kind == CatalogDimensionKind.SeatDepth && value.MinimumMillimeters == 460);
    }

    [Fact]
    public async Task ScannedPdfRequiresOcrReview()
    {
        PdfDocumentBuilder builder = new();
        builder.AddPage(PageSize.A4).AddPng(PixelPng, new PdfRectangle(20, 20, 120, 120));
        using CatalogImporter importer = new();
        CatalogProduct product = Assert.Single((await importer.ImportFileAsync(await WriteAsync("scan.pdf", builder.Build()), _directory)).Products);
        Assert.Contains(product.Warnings, warning => warning.Code == "ocr_required");
        Assert.False(product.ReviewAccepted);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SplitsUniqueCaptionsBelowSeparateProductImages(int count)
    {
        PdfDocumentBuilder builder = new();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Product Vocabulary", 22, new PdfPoint(20, 790), font);
        for (int index = 0; index < count; index++)
        {
            int left = 20 + index * 180;
            page.AddPng(PixelPng, new PdfRectangle(left, 600, left + 140, 730));
            page.AddText($"Chair Model {index + 1}", 10, new PdfPoint(left, 580), font);
        }
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("grid.pdf", builder.Build()), _directory);
        Assert.Equal(count, result.Products.Count);
        Assert.All(result.Products, product =>
        {
            Assert.StartsWith("Chair Model", product.Title);
            Assert.Single(product.Images);
            Assert.Null(product.Sku);
            Assert.False(product.ReviewAccepted);
            Assert.Contains(product.Warnings, warning => warning.Code == "caption_candidate_needs_review");
        });
    }

    [Fact]
    public async Task CompoundCaptionRemainsOnPageWhileUniqueProductIsSeparated()
    {
        PdfDocumentBuilder builder = new();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Product Vocabulary", 22, new PdfPoint(20, 790), font);
        page.AddPng(PixelPng, new PdfRectangle(20, 600, 180, 730));
        page.AddText("Chair and Table", 10, new PdfPoint(20, 580), font);
        page.AddPng(PixelPng, new PdfRectangle(300, 600, 460, 730));
        page.AddText("Chair Model A", 10, new PdfPoint(300, 580), font);
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(await WriteAsync("compound-grid.pdf", builder.Build()), _directory);
        Assert.Equal(2, result.Products.Count);
        CatalogProduct pending = Assert.Single(result.Products, product => product.Title.StartsWith("Page 1", StringComparison.Ordinal));
        Assert.Single(pending.Images);
        Assert.Contains(pending.Warnings, warning => warning.Code == "ambiguous_page_group");
        Assert.Single(result.Products, product => product.Title == "Chair Model A");
        Assert.DoesNotContain(result.Products, product => product.Title == "Chair and Table");
    }

    [Fact]
    public async Task DifferentlySizedEditorialImagesDoNotBecomeProductCandidates()
    {
        PdfDocumentBuilder builder = new();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(PixelPng, new PdfRectangle(20, 600, 180, 730));
        page.AddText("Design story", 10, new PdfPoint(20, 580), font);
        page.AddPng(PixelPng, new PdfRectangle(300, 600, 400, 665));
        page.AddText("More possibilities", 10, new PdfPoint(300, 580), font);
        using CatalogImporter importer = new();
        CatalogProduct product = Assert.Single((await importer.ImportFileAsync(await WriteAsync("editorial.pdf", builder.Build()), _directory)).Products);
        Assert.Equal(2, product.Images.Count);
        Assert.DoesNotContain(product.Warnings, warning => warning.Code == "caption_candidate_needs_review");
    }

    [Fact]
    public async Task Jpeg2000PdfImageIsDecodedByTheManagedFilter()
    {
        byte[] jp2 = Convert.FromBase64String("AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAAgAAAAIAAwcHAAAAAAAPY29scgEAAAAAABAAAACbanAyY/9P/1EALwAAAAAAAgAAAAIAAAAAAAAAAAAAAAIAAAACAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAEEBAAB/1wAB0BASEhQ/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAIAAB/5PPtAgDM8+0CAifz7QIBcmAgID/2Q==");
        using MemoryStream output = new();
        void Text(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        List<long> offsets = [0];
        void Object(int number, string value) { offsets.Add(output.Position); Text($"{number} 0 obj\n{value}\nendobj\n"); }
        Text("%PDF-1.5\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>");
        string content = "q 200 0 0 200 20 20 cm /Im0 Do Q";
        Object(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        offsets.Add(output.Position);
        Text($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {jp2.Length} >>\nstream\n");
        output.Write(jp2);
        Text("\nendstream\nendobj\n");
        long xref = output.Position;
        Text("xref\n0 6\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Text($"{offset:0000000000} 00000 n \n");
        Text($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        using CatalogImporter importer = new();
        CatalogProduct product = Assert.Single((await importer.ImportFileAsync(await WriteAsync("jpx.pdf", output.ToArray()), _directory)).Products);
        CatalogImage image = Assert.Single(product.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(2, image.PixelWidth);
        Assert.True(File.Exists(image.LocalPath));
        Assert.DoesNotContain(product.Warnings, warning => warning.Code == "pdf_image_unsupported");
    }

    [Fact]
    public async Task ImageInputIsCopiedToPrivateOutputAndRoundTripsReviewedEdits()
    {
        string input = await WriteAsync("photo.png", PixelPng);
        using CatalogImporter importer = new();
        CatalogDocument result = await importer.ImportFileAsync(input, _directory);
        CatalogProduct product = Assert.Single(result.Products);
        Assert.NotEqual(input, product.Images[0].LocalPath);
        product.Title = "Reviewed chair";
        product.GenerationImageId = product.Images[0].Id;
        product.ConfirmedWidthMillimeters = 550;
        product.ReviewAccepted = true;
        string saved = Path.Combine(_directory, "review.json");
        await result.SaveAsync(saved);
        CatalogDocument loaded = await CatalogDocument.LoadAsync(saved);
        Assert.True(loaded.Products[0].ReviewAccepted);
        Assert.Equal("Reviewed chair", loaded.Products[0].Title);
        Assert.Equal(550, loaded.Products[0].ConfirmedWidthMillimeters);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(saved));
    }

    [Fact]
    public async Task EnforcesBytePageAndPixelLimits()
    {
        using CatalogImporter small = new(limits: new CatalogImportLimits { MaximumInputBytes = 20 });
        await Assert.ThrowsAsync<InvalidDataException>(() => small.ImportFileAsync(Write("large.html", new byte[21]), _directory));
        PdfDocumentBuilder builder = new();
        builder.AddPage(PageSize.A4);
        builder.AddPage(PageSize.A4);
        using CatalogImporter pages = new(limits: new CatalogImportLimits { MaximumPages = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => pages.ImportFileAsync(Write("pages.pdf", builder.Build()), _directory));
        byte[] largePixels = PixelPng.ToArray();
        largePixels[16] = 127;
        using CatalogImporter normal = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => normal.ImportFileAsync(Write("pixels.png", largePixels), _directory));
    }

    [Theory]
    [InlineData("https://127.0.0.1/private")]
    [InlineData("https://[::1]/private")]
    [InlineData("https://name.local/private")]
    [InlineData("https://user:pass@example.com/private")]
    [InlineData("http://example.com/product")]
    public async Task RejectsUnsafeRedirectBeforeSecondRequest(string destination)
    {
        int requests = 0;
        using HttpClient client = new Handler(request =>
        {
            requests++;
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(destination);
            return response;
        }).Client();
        using CatalogImporter importer = new(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportUrlAsync(new Uri("https://example.com/catalog"), _directory));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ChunkedResponseIsBoundedAndRequestDoesNotFollowGallery()
    {
        int requests = 0;
        using HttpClient client = new Handler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[100])) };
        }).Client();
        using CatalogImporter importer = new(client, new CatalogImportLimits { MaximumInputBytes = 20 });
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportUrlAsync(new Uri("https://example.com/catalog"), _directory));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task PersistenceRejectsNestedNullEvidenceBeforeItReachesTheReviewUi()
    {
        string[] damagedDocuments =
        [
            "{\"Sources\":[null]}",
            "{\"Sources\":[{\"TextBlocks\":null}]}",
            "{\"Sources\":[{\"TextBlocks\":[null]}]}",
            "{\"Products\":[{\"Images\":[{\"Source\":null}]}]}",
            "{\"Products\":[{\"Dimensions\":[{\"Source\":null}]}]}",
            "{\"Products\":[{\"Sources\":[null]}]}",
            "{\"Products\":[{\"Warnings\":[null]}]}",
            "{\"Warnings\":[null]}",
        ];
        foreach (string json in damagedDocuments)
            await Assert.ThrowsAsync<InvalidDataException>(() => CatalogDocument.LoadAsync(Write("damaged.json", Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public async Task PersistenceRejectsUnknownDimensionEnumValuesConsistently()
    {
        foreach (string value in new[] { "999", "\"FutureDimension\"" })
        {
            string json = "{\"Products\":[{\"Dimensions\":[{\"Kind\":" + value + "}]}]}";
            await Assert.ThrowsAsync<InvalidDataException>(() => CatalogDocument.LoadAsync(Write("unknown-enum.json", Encoding.UTF8.GetBytes(json))));
        }
    }

    [Fact]
    public async Task PersistenceRejectsFutureSchemaAndInvalidConfirmedDimensions()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => CatalogDocument.LoadAsync(Write("future.json", Encoding.UTF8.GetBytes("{\"SchemaVersion\":99}"))));
        CatalogDocument document = new() { Products = [new CatalogProduct { ConfirmedWidthMillimeters = -1 }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => document.SaveAsync(Path.Combine(_directory, "invalid.json")));
    }

    private string Write(string name, byte[] data) { string path = Path.Combine(_directory, name); File.WriteAllBytes(path, data); return path; }
    private async Task<string> WriteAsync(string name, byte[] data) { string path = Path.Combine(_directory, name); await File.WriteAllBytesAsync(path, data); return path; }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpClient Client() => new(this);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

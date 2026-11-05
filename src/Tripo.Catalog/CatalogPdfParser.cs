using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Tripo.Catalog;

internal static class CatalogPdfParser
{
    private static readonly Regex Sku = new(@"\b(?:SKU|Item\s*(?:no\.?|number)|Product\s*code|Model\s*(?:no\.?|number))\s*[:#]?\s*([A-Z0-9][A-Z0-9_.-]{2,79})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> GenericHeadings = new(StringComparer.OrdinalIgnoreCase)
    { "dimensions", "materials", "features", "specifications", "colours", "colors", "finishes", "warranty", "overview", "product information", "product vocabulary", "details", "sustainability" };

    public static async Task ParseAsync(byte[] bytes, string directory, CatalogDocument catalog,
        CatalogSource source, CatalogImportLimits limits, CancellationToken token)
    {
        using PdfDocument pdf = PdfDocument.Open(bytes, new ParsingOptions { MaxStackDepth = 128, FilterProvider = CatalogPdfFilterProvider.Instance });
        if (pdf.NumberOfPages > limits.MaximumPages) throw new InvalidDataException("PDF exceeds its page limit.");
        source.PageCount = pdf.NumberOfPages;
        int textCharacters = 0;
        int imageCount = 0;
        long imageBytes = 0;
        Dictionary<string, CatalogProduct> explicitGroups = new(StringComparer.OrdinalIgnoreCase);
        for (int pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            Page page = pdf.GetPage(pageNumber);
            if (page.Letters.Count > limits.MaximumTextCharacters - textCharacters)
                throw new InvalidDataException("PDF text exceeds its character limit.");
            Word[] words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            var lines = DocstrumBoundingBoxes.Instance.GetBlocks(words).SelectMany(block => block.TextLines)
                .OrderByDescending(line => line.BoundingBox.Top).ThenBy(line => line.BoundingBox.Left).ToArray();
            foreach (var line in lines)
            {
                textCharacters += line.Text.Length;
                if (textCharacters > limits.MaximumTextCharacters) throw new InvalidDataException("PDF text exceeds its character limit.");
                source.TextBlocks.Add(new CatalogTextBlock { PageNumber = pageNumber, Text = line.Text, Bounds = Bounds(line.BoundingBox) });
            }
            string pageText = string.Join("\n", lines.Select(line => line.Text));
            Match[] skus = Sku.Matches(pageText).Cast<Match>().ToArray();
            string[] distinctSkus = skus.Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string? heading = lines.Where(line => line.BoundingBox.Top > page.Height * 0.45)
                .OrderByDescending(line => line.BoundingBox.Height).FirstOrDefault()?.Text.Trim();
            if (heading is not null && !IsHeading(heading)) heading = null;
            string? key = distinctSkus.Length == 1 ? "sku:" + distinctSkus[0] : distinctSkus.Length == 0 && heading is not null ? "title:" + heading : null;
            bool ambiguous = key is null || distinctSkus.Length > 1;
            if (key is null || !explicitGroups.TryGetValue(key, out CatalogProduct? product))
            {
                if (catalog.Products.Count >= limits.MaximumProducts) throw new InvalidDataException("PDF exceeds its product limit.");
                product = new CatalogProduct
                {
                    Title = heading ?? $"Page {pageNumber} - review product grouping",
                    Sku = distinctSkus.Length == 1 ? distinctSkus[0] : null,
                    Warnings = [new CatalogWarning { Code = ambiguous ? "ambiguous_page_group" : "pdf_group_needs_review",
                        Message = ambiguous ? "This page cannot be assigned to one explicit product. Split or merge its text and images before generation."
                            : "Pages were grouped only by an exact SKU or heading. Verify that images and measurements belong to the same variant." }],
                };
                catalog.Products.Add(product);
                if (key is not null) explicitGroups.Add(key, product);
            }
            product.Sources.Add(new CatalogSourceReference { SourceId = source.Id, PageNumber = pageNumber, Text = heading });
            product.Description += (product.Description.Length > 0 ? "\n\n" : "") + pageText;
            foreach (var line in lines)
            {
                CatalogSourceReference reference = new() { SourceId = source.Id, PageNumber = pageNumber, Text = line.Text, Bounds = Bounds(line.BoundingBox) };
                product.Dimensions.AddRange(CatalogDimensionParser.Parse(line.Text, reference));
            }
            if (page.NumberOfImages + imageCount > limits.MaximumImages) throw new InvalidDataException("PDF exceeds its image count limit.");
            foreach (IPdfImage image in page.GetImages())
            {
                token.ThrowIfCancellationRequested();
                imageCount++;
                CatalogImporter.ValidateImage(image.RawMemory.Length, image.WidthInSamples, image.HeightInSamples, limits);
                byte[] imageData = image.RawMemory.ToArray();
                string? mediaType = CatalogStorage.InspectImage(imageData)?.MediaType;
                if (mediaType is null)
                {
                    if (!image.TryGetPng(out byte[]? converted) || converted is null)
                    {
                        product.Warnings.Add(new CatalogWarning { Code = "pdf_image_unsupported", Message = $"An image on page {pageNumber} uses an unsupported encoding. Supply a screenshot or source photo." });
                        continue;
                    }
                    imageData = converted;
                    mediaType = "image/png";
                }
                CatalogImporter.ValidateImage(imageData.Length, image.WidthInSamples, image.HeightInSamples, limits);
                imageBytes += imageData.Length;
                if (imageBytes > limits.MaximumTotalImageBytes) throw new InvalidDataException("PDF extracted images exceed their total byte limit.");
                string path = await CatalogStorage.WriteImageAsync(directory, imageData, mediaType, token).ConfigureAwait(false);
                product.Images.Add(new CatalogImage { LocalPath = path, Sha256 = CatalogStorage.Hash(imageData), MediaType = mediaType,
                    PixelWidth = image.WidthInSamples, PixelHeight = image.HeightInSamples,
                    Source = new CatalogSourceReference { SourceId = source.Id, PageNumber = pageNumber, Bounds = Bounds(image.BoundingBox) } });
            }
            SplitCaptionedImages(catalog, source, product, pageNumber, explicitGroups, limits);
            if (words.Length == 0)
                product.Warnings.Add(new CatalogWarning { Code = "ocr_required", Message = $"Page {pageNumber} has no extractable text. OCR is not implemented; add its product name and dimensions manually." });
        }
        catalog.Warnings.Add(new CatalogWarning { Code = "pdf_visual_review_required", Message = "PDF text and embedded raster images were extracted. Vector drawings, clipped image placement, masks and full-page rendering are not reconstructed; review the original pages." });
    }

    private static void SplitCaptionedImages(CatalogDocument catalog, CatalogSource source, CatalogProduct pageProduct,
        int pageNumber, Dictionary<string, CatalogProduct> groups, CatalogImportLimits limits)
    {
        CatalogImage[] images = pageProduct.Images.Where(image => image.Source.SourceId == source.Id && image.Source.PageNumber == pageNumber).ToArray();
        if (images.Length < 2) return;
        // A product grid has similarly sized image cells. Different-sized lifestyle spreads
        // and adjacent editorial headings are insufficient evidence for individual products.
        CatalogBounds[] cells = images.Select(image => image.Source.Bounds).OfType<CatalogBounds>().ToArray();
        if (cells.Length != images.Length || cells.Any(cell => cell.Width <= 0 || cell.Height <= 0) ||
            cells.Max(cell => cell.Width) > cells.Min(cell => cell.Width) * 1.2 ||
            cells.Max(cell => cell.Height) > cells.Min(cell => cell.Height) * 1.2) return;
        CatalogTextBlock[] captions = source.TextBlocks.Where(block => block.PageNumber == pageNumber && IsCaption(block.Text)).ToArray();
        Dictionary<CatalogImage, CatalogTextBlock> matches = [];
        foreach (CatalogImage image in images)
        {
            if (image.Source.Bounds is not CatalogBounds imageBounds) continue;
            CatalogTextBlock[] nearby = captions.Where(caption =>
            {
                CatalogBounds text = caption.Bounds!;
                double gap = imageBounds.Bottom - (text.Bottom + text.Height);
                double overlap = Math.Min(text.Left + text.Width, imageBounds.Left + imageBounds.Width) - Math.Max(text.Left, imageBounds.Left);
                return gap is >= -1 && gap <= Math.Min(48, Math.Max(20, imageBounds.Height * 0.3)) &&
                    overlap >= Math.Min(text.Width, imageBounds.Width) * 0.8 && text.Width <= imageBounds.Width * 1.2;
            }).ToArray();
            if (nearby.Length == 1) matches.Add(image, nearby[0]);
        }
        foreach ((CatalogImage image, CatalogTextBlock caption) in matches)
        {
            // A caption shared by two images is not enough evidence for an individual product.
            if (matches.Values.Count(value => ReferenceEquals(value, caption)) != 1) continue;
            string title = caption.Text.Trim();
            string key = "title:" + title;
            if (!groups.TryGetValue(key, out CatalogProduct? candidate))
            {
                if (catalog.Products.Count >= limits.MaximumProducts) throw new InvalidDataException("PDF exceeds its product limit.");
                candidate = new CatalogProduct
                {
                    Title = title, Description = title,
                    Warnings = [new CatalogWarning { Code = "caption_candidate_needs_review", Message = "A unique caption immediately below this image was associated by page position. Verify identity, variant and dimensions; no SKU was inferred." }],
                };
                groups.Add(key, candidate);
                catalog.Products.Add(candidate);
            }
            if (ReferenceEquals(candidate, pageProduct)) continue;
            candidate.Sources.Add(new CatalogSourceReference { SourceId = source.Id, PageNumber = pageNumber, Text = title, Bounds = caption.Bounds });
            candidate.Images.Add(image);
            pageProduct.Images.Remove(image);
        }
        if (pageProduct.Images.Count == 0 && pageProduct.Sources.Count == 1)
        {
            catalog.Products.Remove(pageProduct);
            foreach (string key in groups.Where(pair => ReferenceEquals(pair.Value, pageProduct)).Select(pair => pair.Key).ToArray()) groups.Remove(key);
        }
    }

    private static bool IsCaption(string text) => IsHeading(text) && text.Length <= 85 && !text.Contains(',') &&
        !text.Contains(';') && !text.Contains(" and ", StringComparison.OrdinalIgnoreCase) && !text.Contains(" & ", StringComparison.Ordinal);

    private static bool IsHeading(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length is >= 3 and <= 100 && !GenericHeadings.Contains(trimmed) &&
            trimmed.Any(char.IsLetter) && !trimmed.Contains(':') && !trimmed.Contains("www.", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.EndsWith('.') && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10 &&
            CatalogDimensionParser.Parse(trimmed).Count == 0;
    }

    private static CatalogBounds Bounds(PdfRectangle value) => new()
    { Left = value.Left, Bottom = value.Bottom, Width = value.Width, Height = value.Height };
}

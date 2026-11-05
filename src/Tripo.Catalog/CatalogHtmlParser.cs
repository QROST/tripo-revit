using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Tripo.Catalog;

internal static class CatalogHtmlParser
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Sku = new(@"^(?<key>new item no\.?|item no\.?|sku|product code|model number)\s*[:#]?\s*(?<sku>[A-Za-z0-9][A-Za-z0-9_. /-]{1,79})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static async Task ParseAsync(string html, Uri? baseUri, CatalogDocument catalog,
        CatalogSource source, CatalogImportLimits limits, CancellationToken token)
    {
        // No browsing context or resource loader: scripts, stylesheets, frames and images never execute/fetch.
        using var document = await new HtmlParser().ParseDocumentAsync(html, token).ConfigureAwait(false);
        string? canonical = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href");
        // Resolve the base before JSON-LD as well as visible galleries in saved local HTML.
        if (baseUri is null && Uri.TryCreate(canonical, UriKind.Absolute, out Uri? candidate) && SafeUrl(candidate)) baseUri = candidate;
        foreach (IElement script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { MaxDepth = 32 });
                foreach (JsonElement productJson in FindProducts(json.RootElement))
                {
                    if (catalog.Products.Count >= limits.MaximumProducts) throw new InvalidDataException("Too many catalog products.");
                    CatalogProduct product = ReadProduct(productJson, source, baseUri, limits);
                    if (!string.IsNullOrWhiteSpace(product.Title)) catalog.Products.Add(product);
                }
            }
            catch (JsonException)
            {
                catalog.Warnings.Add(new CatalogWarning { Code = "invalid_structured_data", Message = "A JSON-LD block could not be parsed; visible product details may still be available." });
            }
        }

        // Collapsed, user-expandable specification accordions often use aria-hidden=true.
        // Keep their data; removing them loses the only dimensional source on many catalogs.
        foreach (IElement invisible in document.QuerySelectorAll("script,style,template,noscript,nav,footer,[hidden]")) invisible.Remove();
        IElement content = document.QuerySelector("main") ?? document.Body ?? document.DocumentElement;
        if (content.TextContent.Length > limits.MaximumTextCharacters) throw new InvalidDataException("HTML text exceeds its limit.");
        string title = Clean(document.QuerySelector("h1")?.TextContent ??
            document.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? document.Title ?? "");
        if (title.Contains("${", StringComparison.Ordinal) || title.Contains("{{", StringComparison.Ordinal)) title = "";
        IElement? component = content.QuerySelector("[product-name],[data-product-name]");
        if (component is not null && (title.Length == 0 || title.Equals("Product", StringComparison.OrdinalIgnoreCase)))
            title = Clean((component.GetAttribute("product-name") ?? component.GetAttribute("data-product-name") ?? "") + " " +
                (component.GetAttribute("product-name-subheadline") ?? ""));
        bool productEvidence = content.QuerySelector("[itemprop='sku'],[itemprop='width'],[itemprop='height']") is not null ||
            content.QuerySelectorAll("tr,dt,li,p").Any(element => Sku.IsMatch(Clean(element.TextContent))) ||
            CatalogDimensionParser.Parse(Clean(content.TextContent)).Count > 0;
        if (catalog.Products.Count == 0 && title.Length > 0 && productEvidence)
            catalog.Products.Add(new CatalogProduct { Title = title, Sources = [new CatalogSourceReference { SourceId = source.Id, Text = title }] });

        if (catalog.Products.Count == 1)
        {
            CatalogProduct product = catalog.Products[0];
            EnrichVisible(product, content, document, source, baseUri, limits);
        }
        else
        {
            foreach (CatalogProduct product in catalog.Products)
            {
                if (string.IsNullOrWhiteSpace(product.Variant)) continue;
                foreach (IElement heading in content.QuerySelectorAll("h2,h3,h4,h5,p,dt").Where(element =>
                             Clean(element.TextContent).Equals(product.Variant, StringComparison.OrdinalIgnoreCase)))
                {
                    IElement? specification = heading.NextElementSibling;
                    if (specification?.LocalName is not ("ul" or "table" or "dl")) continue;
                    foreach (IElement row in specification.QuerySelectorAll("li,tr,dt"))
                    {
                        string raw = Clean(row.TextContent + (row.LocalName == "dt" && row.NextElementSibling?.LocalName == "dd" ? " " + row.NextElementSibling.TextContent : ""));
                        AddDimensions(product, CatalogDimensionParser.Parse(raw,
                            new CatalogSourceReference { SourceId = source.Id, Text = product.Variant + ": " + raw }));
                    }
                }
            }
            catalog.Warnings.Add(new CatalogWarning
            {
                Code = catalog.Products.Count == 0 ? "collection_or_dynamic_page" : "multiple_products_need_review",
                Message = catalog.Products.Count == 0
                    ? "No unambiguous product details were found. Add a product page, PDF or images; JavaScript applications, flipbooks and OCR are not supported."
                    : "Multiple structured products were found. Only specification sections whose headings exactly match a variant label were associated; page-wide dimensions and gallery images were not assigned.",
            });
        }
        foreach (IElement link in content.QuerySelectorAll("a[href]"))
        {
            if (catalog.CandidateLinks.Count >= limits.MaximumProducts) break;
            string? url = ResolveUrl(link.GetAttribute("href"), baseUri);
            if (url is not null && !catalog.CandidateLinks.Contains(url, StringComparer.Ordinal)) catalog.CandidateLinks.Add(url);
        }
        if (document.QuerySelector("iframe") is not null)
            catalog.Warnings.Add(new CatalogWarning { Code = "embedded_content_not_loaded", Message = "Embedded frames and flipbooks were not loaded. Supply their PDF or individual product URL." });
        if (catalog.Products.Sum(product => product.Images.Count) > limits.MaximumImages) throw new InvalidDataException("Too many catalog image references.");
    }

    private static void EnrichVisible(CatalogProduct product, IElement content, IDocument document,
        CatalogSource source, Uri? baseUri, CatalogImportLimits limits)
    {
        List<string> rows = [];
        foreach (IElement row in content.QuerySelectorAll("tr"))
            rows.Add(Clean(string.Join(" ", row.Children.Select(cell => cell.TextContent))));
        foreach (IElement label in content.QuerySelectorAll("dt"))
            if (label.NextElementSibling?.LocalName == "dd")
            {
                string row = Clean(label.TextContent + " " + label.NextElementSibling.TextContent);
                if (row.Length <= 500) rows.Add(row);
            }
        foreach (IElement row in content.QuerySelectorAll("li,p,[itemprop='width'],[itemprop='height'],[itemprop='depth']"))
        {
            string value = Clean(row.TextContent);
            if (value.Length is > 0 and <= 500) rows.Add(value);
        }
        foreach (string row in rows.Distinct(StringComparer.Ordinal))
        {
            if (row.Contains("packaging", StringComparison.OrdinalIgnoreCase) || row.Contains("shipping", StringComparison.OrdinalIgnoreCase)) continue;
            Match sku = Sku.Match(row);
            if (sku.Success && (product.Sku is null || sku.Groups["key"].Value.StartsWith("new", StringComparison.OrdinalIgnoreCase)))
                product.Sku = sku.Groups["sku"].Value.Trim();
            AddDimensions(product, CatalogDimensionParser.Parse(row, new CatalogSourceReference { SourceId = source.Id, Text = row }));
        }
        if (product.Dimensions.Count == 0)
        {
            product.Warnings.Add(new CatalogWarning { Code = "dimensions_not_found", Message = "No labeled product dimensions were extracted. Enter measurements from the specification source." });
        }
        product.Description = product.Description.Length > 0 ? product.Description : Clean(
            document.QuerySelector("[itemprop='description']")?.TextContent ??
            content.QuerySelector("[class*='product-description']")?.TextContent ??
            document.QuerySelector("meta[name='description']")?.GetAttribute("content") ?? "");
        IElement? component = content.QuerySelector("[product-name],[data-product-name]");
        if (component is not null)
        {
            foreach (IAttr attribute in component.Attributes.Where(attribute =>
                         attribute.Name.Contains("gallery", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Name.Contains("presentation", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Name.Contains("images", StringComparison.OrdinalIgnoreCase)))
            {
                if (!attribute.Value.TrimStart().StartsWith('[')) continue;
                try
                {
                    using JsonDocument galleryData = JsonDocument.Parse(attribute.Value, new JsonDocumentOptions { MaxDepth = 16 });
                    foreach (JsonElement tile in ArrayOrOne(galleryData.RootElement))
                    {
                        if (tile.ValueKind != JsonValueKind.Object || (tile.TryGetProperty("isDesigner", out JsonElement designer) && designer.ValueKind == JsonValueKind.True)) continue;
                        JsonElement media = tile.TryGetProperty("media", out JsonElement nested) ? nested : tile;
                        if (String(media, "type") is string type && type != "image") continue;
                        AddImage(product, String(media, "src") ?? String(media, "url"), baseUri, source,
                            String(media, "alt") ?? "Embedded gallery image", limits);
                    }
                }
                catch (JsonException)
                {
                    product.Warnings.Add(new CatalogWarning { Code = "gallery_data_unreadable", Message = "Embedded gallery metadata could not be parsed." });
                }
            }
        }
        AddImage(product, document.QuerySelector("meta[property='og:image']")?.GetAttribute("content"), baseUri, source, "Open Graph image", limits);
        IElement[] gallery = content.QuerySelectorAll("[class*='gallery'] img,[class*='product-image'] img,[itemprop='image'],picture img").ToArray();
        if (gallery.Length == 0 && product.Images.Count == 0) gallery = content.QuerySelectorAll("img").ToArray();
        foreach (IElement img in gallery)
        {
            string alt = img.GetAttribute("alt") ?? "";
            string url = img.GetAttribute("content") ?? img.GetAttribute("src") ?? img.GetAttribute("data-src") ?? "";
            string identity = alt + " " + url;
            if (identity.Contains("logo", StringComparison.OrdinalIgnoreCase) || identity.Contains("swatch", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;
            AddImage(product, url, baseUri, source, alt, limits);
        }
        if (product.Images.Count > 0)
            product.Warnings.Add(new CatalogWarning { Code = "gallery_needs_selection", Message = "Select the matching product image. Gallery pictures may include related products, colour variants or dimension drawings; view order is unverified." });
    }

    private static CatalogProduct ReadProduct(JsonElement json, CatalogSource source, Uri? baseUri, CatalogImportLimits limits)
    {
        CatalogProduct product = new()
        {
            Title = Clean(String(json, "name") ?? ""), Description = Clean(String(json, "description") ?? ""),
            Sku = String(json, "sku") ?? String(json, "mpn"), Variant = String(json, "size"),
            Sources = [new CatalogSourceReference { SourceId = source.Id, Text = "JSON-LD Product" }],
        };
        if (json.TryGetProperty("brand", out JsonElement brand))
            product.Manufacturer = brand.ValueKind == JsonValueKind.String ? brand.GetString() : String(brand, "name");
        foreach (string key in new[] { "width", "height", "depth" })
        {
            if (!json.TryGetProperty(key, out JsonElement value)) continue;
            string? raw = value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : String(value, "value");
            string? unit = value.ValueKind == JsonValueKind.Object ? String(value, "unitText") ?? String(value, "unitCode") : null;
            AddDimensions(product, CatalogDimensionParser.Parse(key + ": " + raw, new CatalogSourceReference { SourceId = source.Id, Text = value.ToString() }, unit));
        }
        if (json.TryGetProperty("additionalProperty", out JsonElement properties))
            foreach (JsonElement property in ArrayOrOne(properties))
                AddDimensions(product, CatalogDimensionParser.Parse((String(property, "name") ?? "") + ": " + String(property, "value"),
                    new CatalogSourceReference { SourceId = source.Id, Text = property.ToString() }, String(property, "unitText") ?? String(property, "unitCode")));
        if (json.TryGetProperty("image", out JsonElement images))
            foreach (JsonElement image in ArrayOrOne(images))
                AddImage(product, image.ValueKind == JsonValueKind.String ? image.GetString() : String(image, "contentUrl") ?? String(image, "url"),
                    baseUri, source, "JSON-LD product image", limits);
        return product;
    }

    private static IEnumerable<JsonElement> FindProducts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray()) foreach (JsonElement found in FindProducts(child)) yield return found;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out JsonElement type) && ArrayOrOne(type).Any(value => value.ValueKind == JsonValueKind.String &&
                    (value.GetString() == "Product" || value.GetString() == "https://schema.org/Product")))
                yield return element;
            else foreach (JsonProperty property in element.EnumerateObject()) foreach (JsonElement found in FindProducts(property.Value)) yield return found;
        }
    }

    private static IEnumerable<JsonElement> ArrayOrOne(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : new[] { element };
    private static string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    private static string Clean(string value) => Whitespace.Replace(value, " ").Trim();

    private static void AddDimensions(CatalogProduct product, IReadOnlyList<CatalogDimension> dimensions)
    {
        foreach (CatalogDimension dimension in dimensions)
            if (!product.Dimensions.Any(existing => existing.Kind == dimension.Kind && existing.MinimumMillimeters == dimension.MinimumMillimeters &&
                existing.MaximumMillimeters == dimension.MaximumMillimeters && existing.RawText == dimension.RawText)) product.Dimensions.Add(dimension);
    }

    private static void AddImage(CatalogProduct product, string? raw, Uri? baseUri, CatalogSource source, string label, CatalogImportLimits limits)
    {
        string? url = ResolveUrl(raw, baseUri);
        if (url is null || product.Images.Any(image => image.SourceUrl == url)) return;
        if (product.Images.Count >= limits.MaximumImages) throw new InvalidDataException("Too many catalog image references.");
        product.Images.Add(new CatalogImage { SourceUrl = url, Source = new CatalogSourceReference { SourceId = source.Id, Text = label } });
    }

    private static string? ResolveUrl(string? value, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (baseUri is null || !Uri.TryCreate(baseUri, value, out uri))) return null;
        return SafeUrl(uri) ? uri.AbsoluteUri : null;
    }

    private static bool SafeUrl(Uri uri)
    {
        try { CatalogImporter.ValidatePublicUri(uri); return true; }
        catch (InvalidDataException) { return false; }
    }
}

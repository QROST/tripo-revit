using Tripo.Catalog;

if (args.Length is not 2 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: Tripo.Catalog.Cli <local PDF/HTML/image or public HTTPS URL> <new output directory>");
    Console.WriteLine("Extracts review candidates and catalog.json locally. Does not call Tripo or create Revit families.");
    return args.Length == 1 ? 0 : 2;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    string output = Path.GetFullPath(args[1]);
    if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        throw new InvalidOperationException("Use a new or empty output directory to preserve earlier evidence.");
    using CatalogImporter importer = new();
    CatalogDocument catalog = Uri.TryCreate(args[0], UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "https" or "http"
        ? await importer.ImportUrlAsync(uri, output, cancellation.Token)
        : await importer.ImportFileAsync(Path.GetFullPath(args[0]), output, cancellation.Token);
    string manifest = Path.Combine(output, "catalog.json");
    await catalog.SaveAsync(manifest, cancellation.Token);
    Console.WriteLine($"Saved {catalog.Products.Count} review candidates to {manifest}");
    foreach (CatalogProduct product in catalog.Products)
    {
        Console.WriteLine($"- {product.Title}: {product.Images.Count} image candidates, {product.Dimensions.Count} dimensions; review required");
    }
    foreach (CatalogWarning warning in catalog.Warnings)
        Console.WriteLine($"Warning [{warning.Code}]: {warning.Message}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Catalog extraction cancelled. Any partial evidence remains in the output directory.");
    return 130;
}
catch (Exception exception) when (exception is IOException or ArgumentException or
    InvalidOperationException or HttpRequestException or NotSupportedException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tripo.Bridge;
using Tripo.Catalog;
using Tripo.Catalog.Batch;
using Tripo.HostUi;

namespace Tripo.Revit;

/// <summary>Modeless review UI. Only the sidecar's ExternalEvent bridge mutates Revit.</summary>
internal sealed class TripoCatalogWindow : Window, IDisposable
{
    private readonly IHostSidecarConnector _connector;
    private readonly TripoPanelRecoveryStore _recovery = new("revit");
    private readonly CatalogBatchStore _batches = new();
    private readonly ObservableCollection<CatalogProduct> _products = [];
    private readonly List<Control> _idleControls = [];
    private readonly DataGrid _grid = new();
    private readonly TextBox _url = new() { MinWidth = 300 };
    private readonly TextBox _title = new();
    private readonly TextBox _brand = new();
    private readonly TextBox _sku = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _width = new();
    private readonly TextBox _depth = new();
    private readonly TextBox _height = new();
    private readonly TextBox _rotation = new() { Text = "0" };
    private readonly ComboBox _scale = new() { ItemsSource = new[] { "uniform", "stretch" }, SelectedIndex = 0 };
    private readonly ComboBox _images = new() { DisplayMemberPath = nameof(ImageChoice.Label) };
    private readonly Image _preview = new() { Height = 160, Stretch = Stretch.Uniform };
    private readonly TextBox _evidence = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _faceLimit = new() { Text = "12000", Width = 80 };
    private readonly TextBox _rowWidth = new() { Text = "10000", Width = 80 };
    private readonly TextBox _gap = new() { Text = "500", Width = 65 };
    private readonly CheckBox _materials = new() { Content = "Materials", IsChecked = true };
    private readonly TextBox _status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 105 };
    private readonly Button _pause = new() { Content = "Pause", IsEnabled = false };
    private CatalogDocument _catalog = new();
    private CatalogProduct? _editing;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private bool _busy;
    private bool _closed;
    private bool _loadingFields;
    private string? _catalogPath;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public TripoCatalogWindow(IHostSidecarConnector connector)
    {
        _connector = connector;
        Title = "Tripo · Catalog Families";
        Width = 1160; Height = 880; MinWidth = 900; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DockPanel root = new() { Margin = new Thickness(14) };
        Content = root;
        StackPanel header = new();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = "Catalog → review products → generate mesh furniture families",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });
        header.Children.Add(new TextBlock
        {
            Text = "PDF, HTML and images become editable candidates. Review page groups, choose one generation image and confirm overall dimensions in mm. Descriptions and other images remain reference evidence. Saved dimensions describe a fixed mesh; they do not drive parametric geometry.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        WrapPanel sources = new(); header.Children.Add(sources);
        sources.Children.Add(Button("Add files", AddFilesAsync));
        sources.Children.Add(Button("Open catalog", LoadCatalogAsync));
        sources.Children.Add(Button("Save catalog", SaveCatalogAsync));
        sources.Children.Add(_url); _idleControls.Add(_url);
        sources.Children.Add(Button("Add public URL", AddUrlAsync));

        StackPanel footer = new(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        WrapPanel settings = new() { Margin = new Thickness(0, 8, 0, 8) }; footer.Children.Add(settings);
        AddInline(settings, "Face limit", _faceLimit);
        AddInline(settings, "Row width mm", _rowWidth);
        AddInline(settings, "Gap mm", _gap);
        settings.Children.Add(_materials); _idleControls.Add(_materials);
        settings.Children.Add(Button("Generate reviewed products…", GenerateAsync));
        settings.Children.Add(Button("Resume batch…", ResumeAsync));
        settings.Children.Add(Button("Restore original session key…", RestoreSessionKeyAsync));
        _pause.Click += (_, _) => _cancellation?.Cancel(); settings.Children.Add(_pause);
        footer.Children.Add(_status);
        _status.Text = "Add a source, then inspect and confirm each product. No Tripo task is created while extracting or reviewing.";

        Grid body = new(); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) }); root.Children.Add(body);
        DockPanel productPanel = new() { Margin = new Thickness(0, 10, 12, 0) }; body.Children.Add(productPanel);
        WrapPanel edit = new(); DockPanel.SetDock(edit, Dock.Top); productPanel.Children.Add(edit);
        edit.Children.Add(Button("Merge selected", () => EditAsync(MergeSelected)));
        edit.Children.Add(Button("Duplicate / split", () => EditAsync(DuplicateProduct)));
        edit.Children.Add(Button("Remove", () => EditAsync(RemoveSelected)));
        edit.Children.Add(Button("↑", () => EditAsync(() => MoveProduct(-1))));
        edit.Children.Add(Button("↓", () => EditAsync(() => MoveProduct(1))));
        _grid.AutoGenerateColumns = false; _grid.CanUserAddRows = false; _grid.CanUserDeleteRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Extended; _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.ItemsSource = _products; _grid.IsReadOnly = true;
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Reviewed", Binding = new Binding(nameof(CatalogProduct.ReviewAccepted)) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Product", Binding = new Binding(nameof(CatalogProduct.Title)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Model", Binding = new Binding(nameof(CatalogProduct.Sku)), Width = 110 });
        _grid.SelectionChanged += (_, _) => ShowProduct(_grid.SelectedItem as CatalogProduct);
        productPanel.Children.Add(_grid); _idleControls.Add(_grid);

        StackPanel fields = new();
        ScrollViewer scroll = new() { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        AddField(fields, "Product / family name", _title); AddField(fields, "Manufacturer", _brand);
        AddField(fields, "Model / SKU", _sku); AddField(fields, "Description", _description);
        WrapPanel dimensions = new(); fields.Children.Add(dimensions);
        _width.Width = 90; _depth.Width = 90; _height.Width = 90;
        AddInline(dimensions, "W mm", _width); AddInline(dimensions, "D mm", _depth); AddInline(dimensions, "H mm", _height);
        AddField(fields, "Image used for generation (one per product)", _images);
        _images.SelectionChanged += (_, _) => _preview.Source = null;
        fields.Children.Add(Button("Preview selected image", PreviewImageAsync));
        fields.Children.Add(_preview);
        WrapPanel images = new(); fields.Children.Add(images);
        images.Children.Add(Button("Add product image", AddProductImageAsync));
        images.Children.Add(Button("Remove selected image", () => EditAsync(RemoveImage)));
        AddField(fields, "Scale (uniform rejects aspect errors >10%; stretch alters proportions)", _scale);
        AddField(fields, "Rotation in project, degrees", _rotation);
        AddField(fields, "Extracted evidence and warnings (read only)", _evidence);
        fields.Children.Add(Button("Apply fields & mark reviewed", ApplyReviewAsync));
        fields.Children.Add(Button("Mark as needing review", () => EditAsync(() =>
        {
            if (_editing is not null) _editing.ReviewAccepted = false;
            _grid.Items.Refresh();
        })));
        foreach (TextBox field in new[] { _title, _brand, _sku, _description, _width, _depth, _height, _rotation })
            field.TextChanged += (_, _) => InvalidateReview();
        _images.SelectionChanged += (_, _) => InvalidateReview();
        _scale.SelectionChanged += (_, _) => InvalidateReview();
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _cancellation?.Cancel();
        // The click handler owns its cancellation source; durable worker receipts finish
        // on the pool before recovery locks are released. No UI continuation is awaited.
        Completion = _worker.ContinueWith(_ => _recovery.Dispose(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        GC.SuppressFinalize(this);
    }

    private Button Button(string text, Func<Task> action)
    {
        Button result = new() { Content = text, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 5, 5) };
        result.Click += async (_, _) =>
        {
            if (_busy || _closed) return;
            _busy = true; SetBusy(true);
            _cancellation = new CancellationTokenSource();
            try { await action(); }
            catch (OperationCanceledException) { if (!_closed) _status.Text = "Paused. Saved task identities are retained; use Resume batch for any dispatched work."; }
            catch (Exception exception) { if (!_closed) _status.Text = "Could not continue: " + exception.Message; }
            finally
            {
                _cancellation.Dispose(); _cancellation = null; _busy = false;
                if (!_closed) SetBusy(false);
            }
        };
        _idleControls.Add(result); return result;
    }

    private void AddField(Panel parent, string label, Control control)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 3), TextWrapping = TextWrapping.Wrap });
        parent.Children.Add(control); _idleControls.Add(control);
    }

    private void AddInline(Panel parent, string label, Control control)
    {
        parent.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 5) });
        control.Margin = new Thickness(0, 0, 5, 5); parent.Children.Add(control); _idleControls.Add(control);
    }

    private void SetBusy(bool busy)
    {
        foreach (Control control in _idleControls) control.IsEnabled = !busy;
        _pause.IsEnabled = busy;
    }

    private async Task<T> WorkAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        CancellationToken token = _cancellation?.Token ?? CancellationToken.None;
        Task<T> task = Task.Run(() => action(token), token);
        _worker = task; Completion = task;
        T result = await task;
        if (_closed) throw new OperationCanceledException();
        return result;
    }

    private static string NewImportDirectory()
    {
        string directory = Path.Combine(BridgePaths.GetRootDirectory(), "catalogs", Guid.NewGuid().ToString("N"));
        BridgePaths.EnsurePrivateDirectory(directory); return directory;
    }

    private async Task AddFilesAsync()
    {
        OpenFileDialog dialog = new() { Multiselect = true, Filter = "Catalog inputs|*.pdf;*.html;*.htm;*.png;*.jpg;*.jpeg;*.webp|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length > 30) throw new InvalidOperationException("Add at most 30 files at a time.");
        foreach (string file in dialog.FileNames)
        {
            _status.Text = "Extracting " + Path.GetFileName(file);
            CatalogDocument extracted = await WorkAsync(async ct =>
            {
                using CatalogImporter importer = new();
                return await importer.ImportFileAsync(file, NewImportDirectory(), ct).ConfigureAwait(false);
            });
            Append(extracted);
        }
        await AutoSaveAsync();
    }

    private async Task AddUrlAsync()
    {
        if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Enter a public HTTPS product page or PDF URL.");
        _status.Text = "Reading catalog source…";
        CatalogDocument extracted = await WorkAsync(async ct =>
        {
            using CatalogImporter importer = new();
            return await importer.ImportUrlAsync(uri, NewImportDirectory(), ct).ConfigureAwait(false);
        });
        Append(extracted); await AutoSaveAsync();
    }

    private void Append(CatalogDocument extracted)
    {
        if (_catalog.Products.Count + extracted.Products.Count > 1000)
            throw new InvalidOperationException("A review catalog can contain at most 1000 candidates.");
        _catalog.Sources.AddRange(extracted.Sources);
        _catalog.Warnings.AddRange(extracted.Warnings);
        _catalog.CandidateLinks.AddRange(extracted.CandidateLinks);
        _catalog.Products.AddRange(extracted.Products);
        foreach (CatalogProduct product in extracted.Products) _products.Add(product);
        _status.Text = $"Added {extracted.Products.Count} candidates. " + string.Join(" ", extracted.Warnings.Select(w => w.Message)) +
            (extracted.CandidateLinks.Count > 0 ? $" {extracted.CandidateLinks.Count} linked candidates retained in catalog.json; add individual detail URLs to inspect them." : "");
        if (_grid.SelectedItem is null) _grid.SelectedItem = _products.FirstOrDefault();
    }

    private async Task LoadCatalogAsync()
    {
        OpenFileDialog dialog = new() { Filter = "Catalog JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        CatalogDocument loaded = await WorkAsync(ct => CatalogDocument.LoadAsync(dialog.FileName, ct));
        _catalog = loaded; _catalogPath = dialog.FileName; _products.Clear();
        foreach (CatalogProduct product in loaded.Products) _products.Add(product);
        _grid.SelectedItem = _products.FirstOrDefault();
        _status.Text = $"Loaded {_products.Count} products. Review selected images and source paths before generation.";
    }

    private async Task SaveCatalogAsync()
    {
        SaveFileDialog dialog = new() { Filter = "Catalog JSON|*.json", FileName = "catalog.json" };
        if (dialog.ShowDialog(this) != true) return;
        _catalogPath = dialog.FileName; await AutoSaveAsync(); _status.Text = "Catalog saved to " + _catalogPath;
    }

    private async Task AutoSaveAsync()
    {
        _catalogPath ??= Path.Combine(NewImportDirectory(), "catalog.json");
        await WorkAsync(async ct => { await _catalog.SaveAsync(_catalogPath, ct).ConfigureAwait(false); return true; });
    }

    private void ShowProduct(CatalogProduct? product)
    {
        _loadingFields = true;
        _editing = product; _preview.Source = null;
        _title.Text = product?.Title ?? ""; _brand.Text = product?.Manufacturer ?? "";
        _sku.Text = product?.Sku ?? ""; _description.Text = product?.Description ?? "";
        _width.Text = DimensionText(product, CatalogDimensionKind.Width, product?.ConfirmedWidthMillimeters);
        _depth.Text = DimensionText(product, CatalogDimensionKind.Depth, product?.ConfirmedDepthMillimeters);
        _height.Text = DimensionText(product, CatalogDimensionKind.Height, product?.ConfirmedHeightMillimeters);
        _rotation.Text = (product?.RotationDegrees ?? 0).ToString(CultureInfo.InvariantCulture);
        _scale.SelectedItem = product?.ScaleMode ?? "uniform";
        _images.ItemsSource = product?.Images.Select((im, index) => new ImageChoice(im,
            $"{index + 1}. {im.Role} · page {im.Source.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "web"} · {im.PixelWidth}×{im.PixelHeight}")).ToArray();
        _images.SelectedItem = (_images.ItemsSource as ImageChoice[])?.FirstOrDefault(x => x.Image.Id == product?.GenerationImageId);
        if (_images.SelectedItem is null && product?.Images.Count == 1) _images.SelectedIndex = 0;
        _evidence.Text = product is null ? "" : string.Join("\n", product.Dimensions.Select(d =>
            $"{d.Kind}: {d.RawText} → {d.MinimumMillimeters}…{d.MaximumMillimeters} mm")) + "\n" +
            string.Join("\n", product.Sources.Select(s => $"Page {s.PageNumber}: {s.Text}")) + "\n" +
            string.Join("\n", product.Warnings.Select(w => $"[{w.Code}] {w.Message}"));
        _loadingFields = false;
    }

    private void InvalidateReview()
    {
        if (_loadingFields || _editing is null || !_editing.ReviewAccepted) return;
        _editing.ReviewAccepted = false;
        _grid.Items.Refresh();
    }

    private static string DimensionText(CatalogProduct? product, CatalogDimensionKind kind, double? confirmed)
    {
        if (confirmed.HasValue) return confirmed.Value.ToString(CultureInfo.InvariantCulture);
        double[] values = product?.Dimensions.Where(d => d.Kind == kind && d.MinimumMillimeters.HasValue &&
            d.MinimumMillimeters == d.MaximumMillimeters).Select(d => d.MinimumMillimeters!.Value).Distinct().ToArray() ?? [];
        return values.Length == 1 ? values[0].ToString(CultureInfo.InvariantCulture) : "";
    }

    private static double Number(string text, string label, bool positive = true)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || (positive && value <= 0))
            throw new InvalidOperationException($"{label} needs a {(positive ? "positive " : "")}number; use a decimal point.");
        return value;
    }

    private async Task ApplyReviewAsync()
    {
        CatalogProduct product = _editing ?? throw new InvalidOperationException("Select a product.");
        if (string.IsNullOrWhiteSpace(_title.Text) || _images.SelectedItem is not ImageChoice image)
            throw new InvalidOperationException("A product name and a selected generation image are required.");
        if (string.IsNullOrWhiteSpace(image.Image.Sha256))
            throw new InvalidOperationException("Preview this URL image before marking the product reviewed, so generation uses the exact image you inspected.");
        if (_title.Text.Trim().Length > 128 || _brand.Text.Trim().Length > 256 ||
            _sku.Text.Trim().Length > 256 || _description.Text.Trim().Length > 4000)
            throw new InvalidOperationException("Use a name up to 128 characters, manufacturer/model up to 256, and description up to 4000. Original extracted text is preserved in source evidence.");
        double width = Number(_width.Text, "Overall width"); double depth = Number(_depth.Text, "Overall depth");
        double height = Number(_height.Text, "Overall height"); double rotation = Number(_rotation.Text, "Rotation", false);
        if (width > 100000 || depth > 100000 || height > 100000 || Math.Abs(rotation) > 360)
            throw new InvalidOperationException("Dimensions must be ≤100000 mm; rotation must be between −360° and 360°.");
        product.Title = _title.Text.Trim(); product.Manufacturer = _brand.Text.Trim(); product.Sku = _sku.Text.Trim();
        product.Description = _description.Text.Trim(); product.GenerationImageId = image.Image.Id;
        product.ConfirmedWidthMillimeters = width; product.ConfirmedDepthMillimeters = depth; product.ConfirmedHeightMillimeters = height;
        product.RotationDegrees = rotation; product.ScaleMode = (string)_scale.SelectedItem;
        product.ReviewAccepted = true; _grid.Items.Refresh(); await AutoSaveAsync();
        _status.Text = "Reviewed and saved: " + product.Title;
    }

    private async Task AddProductImageAsync()
    {
        CatalogProduct product = _editing ?? throw new InvalidOperationException("Select a product.");
        OpenFileDialog dialog = new() { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp" };
        if (dialog.ShowDialog(this) != true) return;
        CatalogDocument source = await WorkAsync(async ct =>
        {
            using CatalogImporter importer = new();
            return await importer.ImportFileAsync(dialog.FileName, NewImportDirectory(), ct).ConfigureAwait(false);
        });
        _catalog.Sources.AddRange(source.Sources);
        product.Images.AddRange(source.Products.SelectMany(p => p.Images));
        product.Sources.AddRange(source.Products.SelectMany(p => p.Sources));
        product.ReviewAccepted = false; ShowProduct(product); await AutoSaveAsync();
    }

    private async Task PreviewImageAsync()
    {
        if (_images.SelectedItem is not ImageChoice selected) throw new InvalidOperationException("Select an image.");
        ImagePreview preview = await WorkAsync(async ct =>
        {
            StagedImageTransfer transfer = await StageAsync(selected.Image, ct).ConfigureAwait(false);
            await using Stream data = await ImageTransferStore.OpenVerifiedAsync(transfer, ct).ConfigureAwait(false);
            try
            {
                BitmapImage bitmap = new(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 800; bitmap.StreamSource = data; bitmap.EndInit(); bitmap.Freeze();
                return new ImagePreview(bitmap, null, transfer.Sha256);
            }
            catch (Exception exception) when (transfer.MediaType == "image/webp" &&
                exception is NotSupportedException or FileFormatException)
            {
                // WebP WIC codecs are optional on Windows. A local, data-only HTML
                // preview lets the default browser display the exact staged bytes.
                await using Stream verified = await ImageTransferStore.OpenVerifiedAsync(transfer, ct).ConfigureAwait(false);
                using MemoryStream copy = new(); await verified.CopyToAsync(copy, ct).ConfigureAwait(false);
                string path = Path.Combine(NewImportDirectory(), "reference-preview.html");
                string html = "<!doctype html><meta charset=utf-8><meta http-equiv=Content-Security-Policy content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\"><title>Catalog reference preview</title><style>body{font:16px sans-serif;margin:24px}img{max-width:90vw;max-height:80vh}</style><p>Inspect this reference, then return to Catalog Families to confirm the product.</p><img alt=\"Selected product reference\" src=\"data:image/webp;base64," + Convert.ToBase64String(copy.ToArray()) + "\">";
                await using FileStream file = ImageTransferStore.CreatePrivateFile(path);
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(html), ct).ConfigureAwait(false);
                return new ImagePreview(null, path, transfer.Sha256);
            }
        });
        if (preview.HtmlPath is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preview.HtmlPath) { UseShellExecute = true });
        selected.Image.Sha256 = preview.Sha256;
        _preview.Source = preview.Bitmap;
        await AutoSaveAsync();
        if (preview.HtmlPath is not null) _status.Text = "The exact WebP reference opened in your browser. Inspect it before marking this product reviewed.";
    }

    private async Task<StagedImageTransfer> StageAsync(CatalogImage image, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(image.LocalPath))
        {
            StagedImageTransfer staged = await ImageTransferStore.StageAsync(image.LocalPath, ct).ConfigureAwait(false);
            if (image.Sha256 is { Length: > 0 } hash && !string.Equals(hash, staged.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reference image changed after extraction. Re-add and review it.");
            return staged;
        }
        IHostControlClient client = await _connector.EnsureConnectedAsync(ct).ConfigureAwait(false);
        StagedImageTransfer downloaded = await client.StageImageAsync(new HostControlStageImageRequest(image.SourceUrl ?? ""), ct).ConfigureAwait(false);
        if (image.Sha256 is { Length: > 0 } expected && !string.Equals(expected, downloaded.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The URL image changed after preview. Re-add and review its current image before generation.");
        return downloaded;
    }

    private async Task EditAsync(Action edit) { edit(); await AutoSaveAsync(); }

    private CatalogProduct[] Selected() => _products.Where(p => _grid.SelectedItems.Contains(p)).ToArray();

    private void MergeSelected()
    {
        CatalogProduct[] selected = Selected();
        if (selected.Length < 2) throw new InvalidOperationException("Select two or more candidates to group as one product.");
        CatalogProduct target = selected[0];
        foreach (CatalogProduct source in selected.Skip(1))
        {
            target.Description += "\n" + source.Description; target.Images.AddRange(source.Images);
            target.Sources.AddRange(source.Sources); target.Dimensions.AddRange(source.Dimensions); target.Warnings.AddRange(source.Warnings);
            _products.Remove(source); _catalog.Products.Remove(source);
        }
        target.Images = target.Images.DistinctBy(x => x.Id).ToList(); target.ReviewAccepted = false;
        _grid.SelectedItem = target; ShowProduct(target); _grid.Items.Refresh();
    }

    private void DuplicateProduct()
    {
        CatalogProduct source = _editing ?? throw new InvalidOperationException("Select a product to split.");
        CatalogProduct product = new()
        {
            Title = source.Title + " (variant)", Manufacturer = source.Manufacturer, Description = source.Description,
            Images = [.. source.Images], Dimensions = [.. source.Dimensions], Sources = [.. source.Sources],
            Warnings = [.. source.Warnings],
        };
        _catalog.Products.Add(product); _products.Add(product); _grid.SelectedItem = product;
    }

    private void RemoveSelected()
    {
        foreach (CatalogProduct product in Selected()) { _products.Remove(product); _catalog.Products.Remove(product); }
        _grid.SelectedItem = _products.FirstOrDefault();
    }

    private void MoveProduct(int direction)
    {
        if (_editing is null) return;
        int index = _products.IndexOf(_editing); int target = index + direction;
        if (target < 0 || target >= _products.Count) return;
        _products.Move(index, target); _catalog.Products = _products.ToList();
    }

    private void RemoveImage()
    {
        if (_editing is null || _images.SelectedItem is not ImageChoice image) return;
        _editing.Images.Remove(image.Image); _editing.ReviewAccepted = false;
        if (_editing.GenerationImageId == image.Image.Id) _editing.GenerationImageId = null;
        ShowProduct(_editing);
    }

    private async Task GenerateAsync()
    {
        CatalogProduct[] products = _products.Where(p => p.ReviewAccepted).ToArray();
        if (products.Length is < 1 or > 100) throw new InvalidOperationException("Review between 1 and 100 products for one batch.");
        int faces = checked((int)Number(_faceLimit.Text, "Face limit"));
        if (faces is < 1000 or > 50000) throw new InvalidOperationException("Face limit must be between 1000 and 50000.");
        double rowWidth = Number(_rowWidth.Text, "Row width"); double gap = Number(_gap.Text, "Gap");
        bool materials = _materials.IsChecked == true;
        CatalogBatchPlan plan = await WorkAsync(async ct =>
        {
            IHostControlClient client = await _connector.EnsureConnectedAsync(ct).ConfigureAwait(false);
            HostContextReceipt context = await client.GetHostContextAsync(ct).ConfigureAwait(false);
            if (context.Host != "revit") throw new InvalidOperationException("An active Revit project is required.");
            List<CatalogBatchItem> items = [];
            foreach (CatalogProduct product in products)
            {
                CatalogImage image = product.Images.SingleOrDefault(i => i.Id == product.GenerationImageId)
                    ?? throw new InvalidOperationException($"Choose a generation image for {product.Title}.");
                if (string.IsNullOrWhiteSpace(image.Sha256))
                    throw new InvalidOperationException($"Preview and review the reference image for {product.Title} before generation.");
                string source = string.Join("; ", product.Sources.Select(s =>
                    (_catalog.Sources.FirstOrDefault(x => x.Id == s.SourceId)?.Location ?? s.SourceId) +
                    (s.PageNumber.HasValue ? $"#page={s.PageNumber}" : "")).Distinct(StringComparer.Ordinal));
                CatalogFamilyOptions family = new(product.Id, product.Manufacturer ?? "", product.Sku ?? "", product.Description,
                    source, product.ConfirmedWidthMillimeters ?? 0, product.ConfirmedDepthMillimeters ?? 0,
                    product.ConfirmedHeightMillimeters ?? 0, RotationDegrees: product.RotationDegrees, ScaleMode: product.ScaleMode);
                StagedImageTransfer staged = await StageAsync(image, ct).ConfigureAwait(false);
                items.Add(new CatalogBatchItem(product.Id, product.Title, staged, family));
            }
            return new CatalogBatchPlan(Guid.NewGuid().ToString("D"), context.DocumentSessionId, items,
                faces, materials, TripoModelCatalog.DefaultModelId, rowWidth, gap).Prepare();
        });
        await RunPlanAsync(plan);
    }

    private async Task ResumeAsync()
    {
        CatalogBatchSnapshot? selected = await ChooseBatchAsync();
        if (selected is not null) await RunPlanAsync(selected.Plan);
    }

    private async Task<CatalogBatchSnapshot?> ChooseBatchAsync()
    {
        CatalogBatchSnapshot[] batches = await WorkAsync(_ => Task.FromResult(_batches.List().ToArray()));
        if (batches.Length == 0) throw new InvalidOperationException("No saved batches are available.");
        Window chooser = new() { Owner = this, Title = "Choose saved batch", Width = 680, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        DockPanel panel = new() { Margin = new Thickness(12) }; chooser.Content = panel;
        Button open = new() { Content = "Select batch", Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(open, Dock.Bottom); panel.Children.Add(open);
        ListBox list = new() { ItemsSource = batches.Select(b => new BatchChoice(b, $"{b.UpdatedAtUtc.LocalDateTime:g} · {b.Status} · {b.Plan.Items.Count} products · {b.Plan.Items[0].Name}")).ToArray(), DisplayMemberPath = nameof(BatchChoice.Label) };
        panel.Children.Add(list); open.Click += (_, _) => { if (list.SelectedItem is not null) chooser.DialogResult = true; };
        return chooser.ShowDialog() == true && list.SelectedItem is BatchChoice choice ? choice.Snapshot : null;
    }

    private async Task RestoreSessionKeyAsync()
    {
        CatalogBatchSnapshot? selected = await ChooseBatchAsync();
        if (selected is null) return;
        Window prompt = new() { Owner = this, Title = "Restore original batch key", Width = 520, Height = 245, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        StackPanel panel = new() { Margin = new Thickness(16) }; prompt.Content = panel;
        panel.Children.Add(new TextBlock
        {
            Text = "If the sidecar restarted and lost a session-only key, enter the original key used for this batch. It stays in this session. A different account cannot continue existing paid operations. Other unfinished workflows still block this change.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        PasswordBox password = new() { MaxLength = 4096 }; panel.Children.Add(password);
        Button restore = new() { Content = "Restore original key for this session", Padding = new Thickness(8), Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(restore); restore.Click += (_, _) => { if (password.Password.Length > 0) prompt.DialogResult = true; };
        if (prompt.ShowDialog() != true) { password.Clear(); return; }
        string apiKey = password.Password; password.Clear();
        try
        {
            await WorkAsync(async ct =>
            {
                using IDisposable batch = _batches.AcquireVerifiedPlanLease(selected.Plan);
                using IDisposable scope = await AcquireExecutionScopeAsync(selected.Plan, ct).ConfigureAwait(false);
                IHostControlClient client = await _connector.EnsureConnectedAsync(ct).ConfigureAwait(false);
                await client.SetApiKeyAsync(apiKey, persist: false, ct).ConfigureAwait(false);
                return true;
            });
            _status.Text = "Session key restored. Resume the selected batch; its saved operation identities still apply.";
        }
        finally { apiKey = ""; }
    }

    private async Task RunPlanAsync(CatalogBatchPlan plan)
    {
        string summary = $"{plan.Items.Count} furniture products, in the saved order.\nAt most {plan.MaximumPaidTasks} paid task creations (generation + OBJ conversion); existing tasks are reused on resume. Charges follow your Tripo account.\nModel: {plan.Model}; face limit: {plan.FaceLimit}; materials: {plan.WithMaterials}; row width: {plan.RowWidthMillimeters} mm; gap: {plan.GapMillimeters} mm.\n\n" +
            string.Join("\n", plan.Items.Select(i => $"{i.Name}: {i.Family.WidthMillimeters} × {i.Family.DepthMillimeters} × {i.Family.HeightMillimeters} mm ({i.Family.ScaleMode})")) +
            "\n\nThe target document is fixed to this batch. Keep it open and active. Confirm upload of these selected images and paid generation?";
        Window confirmation = new() { Owner = this, Title = "Confirm catalog batch", Width = 720, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        DockPanel content = new() { Margin = new Thickness(16) }; confirmation.Content = content;
        Button accept = new() { Content = "Confirm image upload and paid generation", Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(accept, Dock.Bottom); content.Children.Add(accept);
        content.Children.Add(new TextBox { Text = summary, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        accept.Click += (_, _) => confirmation.DialogResult = true;
        if (confirmation.ShowDialog() != true) return;
        _status.Text = "Running catalog batch. Pause retains task identities and completed imports.";
        CatalogBatchSnapshot result = await WorkAsync(async ct =>
        {
            IHostControlClient client = await _connector.EnsureConnectedAsync(ct).ConfigureAwait(false);
            CatalogBatchRunner runner = new(client, _batches, AcquireExecutionScopeAsync, maxPollsPerRun: 10_000);
            runner.ProgressChanged += state =>
            {
                if (!_closed) Dispatcher.BeginInvoke(new Action(() => { if (!_closed) _status.Text = FormatProgress(state); }));
            };
            return await runner.RunAsync(plan, confirmExternalCost: true, ct).ConfigureAwait(false);
        });
        _status.Text = FormatProgress(result) + "\nFamilies: " + BridgePaths.GetFamiliesDirectory();
    }

    private Task<IDisposable> AcquireExecutionScopeAsync(CatalogBatchPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IDisposable lease = _recovery.AcquireCredentialWorkflowLease();
        try
        {
            if (_recovery.LoadStale().HasBlock ||
                _recovery.LoadCredentialMutationBlocks(excludeCatalogBatchId: plan.BatchId).HasBlock)
                throw new InvalidOperationException("Resolve pending work or recovery records in the Tripo panel before running this batch.");
            return Task.FromResult(lease);
        }
        catch { lease.Dispose(); throw; }
    }

    private static string FormatProgress(CatalogBatchSnapshot state) =>
        $"{state.Status}: {state.CompletedCount} of {state.Items.Count} completed; {state.FailedCount} failed. {RecoveryMessage(state.MessageCode)}\n" +
        string.Join("\n", state.Items.Select((item, index) => $"{state.Plan.Items[index].Name}: {item.Stage} ({item.Progress}%) {RecoveryMessage(item.MessageCode)}"));

    private static string RecoveryMessage(string? code) => code switch
    {
        null => "",
        "catalog_batch_document_changed" or "catalog_batch_document_unavailable" => "Return to the original open Revit project, then resume this batch.",
        "catalog_batch_poll_limit_resume_same_plan" or "catalog_batch_poll_unavailable" => "Polling paused. Resume to check the existing tasks.",
        "catalog_batch_cancelled_resume_same_plan" => "Paused by request. Resume uses the saved tasks and product order.",
        "catalog_batch_import_unconfirmed_preserve_uuid" => "Import has no confirmed receipt. Resume checks only for the existing instance; it will not recreate a deleted or undone import.",
        "catalog_batch_some_items_failed" => "Inspect the failed products before starting any new generation.",
        "catalog_batch_generation_failed" or "catalog_batch_generation_rejected" => "Generation failed or was rejected.",
        "catalog_batch_conversion_failed" or "catalog_batch_conversion_rejected" => "OBJ conversion failed or was rejected.",
        "catalog_dimension_mismatch" => "Generated proportions do not match the reviewed dimensions.",
        "catalog_credential_anchor_unverified" => "The original batch account could not be verified. Restore the original session key; no new paid task was created.",
        _ => "This step needs recovery; the saved task identities are retained. Details: " + code,
    };

    private sealed record ImageChoice(CatalogImage Image, string Label);
    private sealed record ImagePreview(BitmapImage? Bitmap, string? HtmlPath, string Sha256);
    private sealed record BatchChoice(CatalogBatchSnapshot Snapshot, string Label);
}

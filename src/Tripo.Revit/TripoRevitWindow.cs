using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Tripo.Revit;

internal sealed class TripoRevitWindow : Window
{
    private const string ApiKeyInstructions =
        "Paste a Tripo v3 API key below. The key is sent only to the local " +
        "Tripo sidecar. It is never saved " +
        "in this .rvt or .rfa document.\n\n" +
        "Replacing the effective key can make same-UUID recovery fail closed " +
        "for unfinished UI or MCP paid operations. Reconcile them first.";

    private readonly Tripo.HostUi.TripoPanelSession _session;
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        Height = 88,
        MaxLength = 1024,
        Text = "A buildable architectural pavilion",
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _faceLimit = new()
    {
        MaxLength = 6,
        Text = "20000",
        Width = 110,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly CheckBox _withMaterials = WrappingCheckBox(
        "Generate baked materials",
        "Request Tripo texture/PBR output and bake it during OBJ conversion.");
    private readonly TextBox _name = new()
    {
        MaxLength = 128,
        Text = "Tripo Model",
    };
    private readonly ComboBox _importMode = new()
    {
        ItemsSource = ImportModeOption.All,
        SelectedIndex = 0,
        MinWidth = 220,
    };
    private readonly ComboBox _tripoModel = new()
    {
        ItemsSource = Tripo.Bridge.TripoModelCatalog.Models,
        MinWidth = 280,
    };
    private readonly TextBlock _tripoModelHelp = StatusBlock();
    private readonly CheckBox _applyMaterials = WrappingCheckBox(
        "Apply baked materials from the OBJ bundle",
        "Requires an MTL material library. The import receipt reports actual " +
        "material and texture counts; review a zero count.");
    private readonly ComboBox _placementMode = new()
    {
        ItemsSource = PlacementModeOption.All,
        SelectedIndex = 0,
        MinWidth = 220,
    };
    private readonly CheckBox _associateLevel = WrappingCheckBox(
        "Associate nearest level (family only)",
        "After import, set the family instance level parameter to the nearest " +
        "level by elevation. Ignored for DirectShape mesh imports.");
    private readonly TextBlock _documentStatus = StatusBlock();
    private readonly TextBox _documentSession = OperationStatusBox();
    private readonly TextBlock _credentialStatus = StatusBlock();
    private readonly TextBlock _resultStatus = StatusBlock();
    private readonly TextBlock _pipelineStage = StatusBlock();
    private readonly ProgressBar _pipelineProgress = WorkflowProgressBar();
    private readonly TextBlock _categoryNote = StatusBlock();
    private readonly TextBlock _recoveryHeader = StatusBlock();
    private readonly TextBox _recoveryStatus = RecoveryStatusBox();
    private readonly TextBox _generationOperation = OperationStatusBox();
    private readonly TextBox _generationTaskId = OperationStatusBox();
    private readonly TextBlock _generationTask = StatusBlock();
    private readonly TextBox _generationDiagnostic = DiagnosticBox();
    private readonly ProgressBar _generationProgress = WorkflowProgressBar();
    private readonly TextBox _conversionOperation = OperationStatusBox();
    private readonly TextBox _conversionTaskId = OperationStatusBox();
    private readonly TextBlock _conversionTask = StatusBlock();
    private readonly TextBox _conversionDiagnostic = DiagnosticBox();
    private readonly ProgressBar _conversionProgress = WorkflowProgressBar();
    private readonly TextBox _importOperation = OperationStatusBox();
    private readonly TextBlock _importStatus = StatusBlock();
    private readonly TextBox _importReceipt = DiagnosticBox();
    private readonly TextBlock _promptValidation = ValidationBlock();
    private readonly TextBlock _faceLimitValidation = ValidationBlock();
    private readonly TextBlock _objectNameValidation = ValidationBlock();
    private readonly TextBlock _importModeHelp = StatusBlock();
    private readonly Expander _settingsExpander = new();
    private readonly Expander _recoveryExpander = new();
    private readonly Expander _advancedExpander = new();
    private readonly Expander _detailsExpander = new();
    private readonly Button _create = PrimaryActionButton("Create in Revit");
    private readonly Button _connect = ActionButton("Connect / Refresh");
    private readonly Button _apiKey = ActionButton("API key…");
    private readonly Button _clearApiKey =
        ActionButton("Remove saved key…");
    private readonly Button _checkRecovery =
        ActionButton("Inspect recovered IDs");
    private readonly Button _acknowledgeRecovery =
        ActionButton("Acknowledge reviewed IDs…");
    private readonly Button _generate = ActionButton("Generate");
    private readonly Button _refreshGeneration =
        ActionButton("Refresh generation");
    private readonly Button _convert = ActionButton("Convert to OBJ");
    private readonly Button _refreshConversion =
        ActionButton("Refresh conversion");
    private readonly Button _import = ActionButton("Import into Revit");
    private readonly Button _reset = ActionButton("New");
    private readonly StackPanel _generationDiagnosticBlock;
    private readonly StackPanel _conversionDiagnosticBlock;
    private readonly StackPanel _importReceiptBlock;
    private Task _sessionDisposal = Task.CompletedTask;
    private string? _recoveryInspection;
    private bool _recoveryInspectionHadFailures;
    private string? _reviewedRecoveryToken;
    private string _displayedRecoveryToken =
        Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty.PresentationToken;
    private string? _displayedPreparedOperationId;
    private string? _currentAction;
    private string? _pipelineStageLabel;
    private int? _pipelineProgressValue;
    private bool _generationRefreshFailed;
    private bool _conversionRefreshFailed;
    private bool _generationJournalUpdatedOnFailedRefresh;
    private bool _conversionJournalUpdatedOnFailedRefresh;
    private bool _recoveryWasBlocked;
    private bool _allowClose;
    private bool _closing;
    private CancellationTokenSource? _pipelineCancellation;

    internal Task SessionDisposal => _sessionDisposal;

    public TripoRevitWindow(Tripo.HostUi.TripoPanelSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _generationDiagnosticBlock = FieldBlock(
            "Generation diagnostic",
            _generationDiagnostic);
        _conversionDiagnosticBlock = FieldBlock(
            "Conversion diagnostic",
            _conversionDiagnostic);
        _importReceiptBlock = FieldBlock(
            "Revit import receipt",
            _importReceipt);
        _session.StateChanged += OnStateChanged;
        _session.RecoveryChanged += OnRecoveryChanged;
        Title = "Tripo 3D for Revit";
        MinWidth = 380;
        MinHeight = 300;
        Width = Math.Min(
            520,
            Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        Height = Math.Min(
            640,
            Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TextOptions.SetTextFormattingMode(
            this,
            TextFormattingMode.Display);
        ApplyUserSettings(TripoPanelUserSettings.Load());
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildContent(),
        };
        _create.Click += OnCreate;
        _connect.Click += OnConnect;
        _apiKey.Click += OnApiKey;
        _clearApiKey.Click += OnClearApiKey;
        _checkRecovery.Click += OnCheckRecovery;
        _acknowledgeRecovery.Click += OnAcknowledgeRecovery;
        _generate.Click += OnGenerate;
        _refreshGeneration.Click += OnRefreshGeneration;
        _convert.Click += OnConvert;
        _refreshConversion.Click += OnRefreshConversion;
        _import.Click += OnImport;
        _reset.Click += OnReset;
        _prompt.TextChanged += OnFormInputChanged;
        _faceLimit.TextChanged += OnFormInputChanged;
        _name.TextChanged += OnFormInputChanged;
        _importMode.SelectionChanged += OnImportModeChanged;
        _placementMode.SelectionChanged += OnPlacementModeChanged;
        _tripoModel.SelectionChanged += OnSettingsChanged;
        _withMaterials.Checked += OnSettingsChanged;
        _withMaterials.Unchecked += OnSettingsChanged;
        _applyMaterials.Checked += OnSettingsChanged;
        _applyMaterials.Unchecked += OnSettingsChanged;
        _associateLevel.Checked += OnSettingsChanged;
        _associateLevel.Unchecked += OnSettingsChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        AutomationProperties.SetName(
            this,
            "Tripo 3D for Revit");
        AutomationProperties.SetLiveSetting(
            _resultStatus,
            AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(
            _pipelineStage,
            AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(
            _recoveryHeader,
            AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(
            _generationTask,
            AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(
            _conversionTask,
            AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(
            _importStatus,
            AutomationLiveSetting.Polite);
        UpdateImportModeHelp();
        UpdatePlacementModeHelp();
        UpdateControls(_session.State);
    }

    internal void CloseForApplicationShutdown()
    {
        _allowClose = true;
        Close();
    }

    private StackPanel BuildContent()
    {
        StackPanel panel = new()
        {
            Margin = new Thickness(16),
        };

        panel.Children.Add(new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Text = "Tripo 3D for Revit",
        });
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 8),
            Text = "Text to 3D in the active project.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_documentStatus);
        _resultStatus.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(_resultStatus);

        _prompt.MinHeight = 96;
        panel.Children.Add(FieldBlock("Describe the model", _prompt));
        panel.Children.Add(_promptValidation);

        _create.Margin = new Thickness(0, 8, 0, 4);
        _create.HorizontalAlignment = HorizontalAlignment.Stretch;
        _create.MinHeight = 40;
        _create.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(_create);

        _pipelineStage.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(_pipelineStage);
        panel.Children.Add(_pipelineProgress);
        panel.Children.Add(ActionRow(_reset));

        _categoryNote.Text =
            "Category: Generic Models · Format: OBJ";
        _categoryNote.Opacity = 0.75;

        StackPanel settingsContent = new();
        settingsContent.Children.Add(
            FieldBlock("Tripo model", _tripoModel));
        settingsContent.Children.Add(_tripoModelHelp);
        settingsContent.Children.Add(
            FieldBlock("Face limit (500–200,000)", _faceLimit));
        settingsContent.Children.Add(_faceLimitValidation);
        settingsContent.Children.Add(_withMaterials);
        settingsContent.Children.Add(
            FieldBlock("Revit object name", _name));
        settingsContent.Children.Add(_objectNameValidation);
        settingsContent.Children.Add(
            FieldBlock("Revit import type", _importMode));
        settingsContent.Children.Add(_importModeHelp);
        settingsContent.Children.Add(_applyMaterials);
        settingsContent.Children.Add(
            FieldBlock("Placement", _placementMode));
        settingsContent.Children.Add(_associateLevel);
        settingsContent.Children.Add(_categoryNote);
        settingsContent.Children.Add(_credentialStatus);
        settingsContent.Children.Add(
            ActionRow(_apiKey, _clearApiKey));
        _settingsExpander.Header = "Settings";
        _settingsExpander.Margin = new Thickness(0, 12, 0, 0);
        _settingsExpander.Content = settingsContent;
        panel.Children.Add(_settingsExpander);

        StackPanel recoveryContent = new();
        recoveryContent.Children.Add(
            FieldBlock("Recovered operation details", _recoveryStatus));
        recoveryContent.Children.Add(
            ActionRow(_checkRecovery, _acknowledgeRecovery));
        _recoveryExpander.Header = _recoveryHeader;
        _recoveryExpander.Margin = new Thickness(0, 8, 0, 0);
        _recoveryExpander.Content = recoveryContent;
        panel.Children.Add(_recoveryExpander);

        _generationTask.FontWeight = FontWeights.SemiBold;
        _conversionTask.FontWeight = FontWeights.SemiBold;
        _importStatus.FontWeight = FontWeights.SemiBold;
        StackPanel advancedContent = new();
        advancedContent.Children.Add(ActionRow(_connect));
        advancedContent.Children.Add(Section(
            "Generate",
            _generationTask,
            _generationProgress,
            ActionRow(_generate, _refreshGeneration)));
        advancedContent.Children.Add(Section(
            "Convert to OBJ",
            _conversionTask,
            _conversionProgress,
            ActionRow(_convert, _refreshConversion)));
        advancedContent.Children.Add(Section(
            "Import",
            _importStatus,
            ActionRow(_import)));

        StackPanel detailsContent = new();
        detailsContent.Children.Add(
            FieldBlock("Document session ID", _documentSession));
        detailsContent.Children.Add(
            FieldBlock("Generation operation UUID", _generationOperation));
        detailsContent.Children.Add(
            FieldBlock("Generation task ID", _generationTaskId));
        detailsContent.Children.Add(_generationDiagnosticBlock);
        detailsContent.Children.Add(
            FieldBlock("Conversion operation UUID", _conversionOperation));
        detailsContent.Children.Add(
            FieldBlock("Conversion task ID", _conversionTaskId));
        detailsContent.Children.Add(_conversionDiagnosticBlock);
        detailsContent.Children.Add(
            FieldBlock("Import operation UUID", _importOperation));
        detailsContent.Children.Add(_importReceiptBlock);
        _detailsExpander.Header = "Workflow details and durable IDs";
        _detailsExpander.Margin = new Thickness(0, 8, 0, 0);
        _detailsExpander.Content = detailsContent;
        advancedContent.Children.Add(_detailsExpander);

        _advancedExpander.Header = "Advanced";
        _advancedExpander.Margin = new Thickness(0, 8, 0, 0);
        _advancedExpander.Content = advancedContent;
        panel.Children.Add(_advancedExpander);

        _reset.ToolTip =
            "Clear the resolved panel workflow. This does not cancel remote " +
            "tasks or delete imported Revit objects.";
        _refreshGeneration.ToolTip =
            "Read the current generation task state; this sends no paid request.";
        _refreshConversion.ToolTip =
            "Read the current conversion task state; this sends no paid request.";
        _checkRecovery.ToolTip =
            "Read local journal state for recovered paid UUIDs; this sends no " +
            "paid request and performs no import.";
        _create.ToolTip =
            "Generate, convert to OBJ, and import into the active Revit project.";
        return panel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) =>
        await ConnectSafelyAsync();

    private async void OnConnect(object sender, RoutedEventArgs args) =>
        await ConnectSafelyAsync();

    private async Task ConnectSafelyAsync()
    {
        if (_closing || _session.State.Busy)
        {
            return;
        }

        try
        {
            await RunWithStatusAsync(
                "Connecting to the local Tripo sidecar…",
                () => _session.ConnectAsync());
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnApiKey(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!_session.State.Connected)
            {
                await RunWithStatusAsync(
                    "Connecting to the local Tripo sidecar…",
                    () => _session.ConnectAsync());
            }

            Tripo.HostUi.TripoPanelState state = _session.State;
            if (state.CredentialStatus is
                {
                    HasApiKey: true,
                    Source: "environment",
                })
            {
                throw new InvalidOperationException(
                    "TRIPO_API_KEY from the Revit process environment " +
                    "overrides panel keys. Change it outside Revit, restart " +
                    "the process, then reconnect.");
            }

            bool recoveryMode = state.RequiresCredentialRecovery;
            bool exactOriginalKeyRequired =
                state.HasUnresolvedPaidDispatch;
            if (recoveryMode &&
                MessageBox.Show(
                    this,
                    (exactOriginalKeyRequired
                        ? "A paid dispatch has no durable task ID. Restore the " +
                          "exact original API key; the journal fingerprint is " +
                          "bound to that key."
                        : "An unresolved import or accepted remote task still " +
                          "needs access. Use a key for the same Tripo account.") +
                    "\n\n" +
                    WorkflowIdentitySummary(state) +
                    "\n\nThe recovery key remains session-only.",
                    "Restore workflow access with an API key?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            await PromptForApiKeyAsync(
                recoveryMode,
                exactOriginalKeyRequired);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task PromptForApiKeyAsync(
        bool recoveryMode,
        bool exactOriginalKeyRequired)
    {
        RevitApiKeyDialog dialog = new(
            recoveryMode,
            exactOriginalKeyRequired)
        {
            Owner = this,
        };
        string? secret = null;
        try
        {
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            secret = dialog.TakeAndClearApiKey();
            string key = secret ??
                throw new InvalidOperationException(
                    "Enter a Tripo API key before saving.");
            await RunWithStatusAsync(
                dialog.Persist
                    ? "Saving the API key in the OS credential store…"
                    : "Using the API key for this session…",
                () => _session.SetApiKeyAsync(key, dialog.Persist));
        }
        finally
        {
            dialog.ClearApiKey();
            secret = null;
        }
    }

    private async void OnClearApiKey(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            if (MessageBox.Show(
                    this,
                    "Remove the API key saved for this user? An environment " +
                    "key, if configured outside Revit, remains effective.",
                    "Remove saved Tripo API key?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            await RunWithStatusAsync(
                "Removing the saved API key…",
                () => _session.ClearApiKeyAsync());
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnCheckRecovery(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            if (!_session.State.Connected)
            {
                await RunWithStatusAsync(
                    "Connecting to the local Tripo sidecar…",
                    () => _session.ConnectAsync());
            }

            await RunWithStatusAsync(
                "Inspecting recovered operation IDs…",
                async () =>
                {
                    Tripo.HostUi.TripoPanelRecoveryLoadResult snapshot =
                        _session.Recovery;
                    string inspectedToken = snapshot.PresentationToken;
                    _recoveryInspection = null;
                    _recoveryInspectionHadFailures = false;
                    _reviewedRecoveryToken = null;
                    bool allPaidInspectionsSucceeded = true;
                    List<string> lines = [];
                    foreach (
                        Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded in
                        snapshot.Hints)
                    {
                        allPaidInspectionsSucceeded &=
                            await AppendPaidRecoveryStatusAsync(
                            lines,
                            "Generation",
                            loaded.Hint.Generation);
                        allPaidInspectionsSucceeded &=
                            await AppendPaidRecoveryStatusAsync(
                            lines,
                            "Conversion",
                            loaded.Hint.Conversion);
                        if (loaded.Hint.Import is not null)
                        {
                            lines.Add(
                                "Import: manual Revit reconciliation required; " +
                                "UUID " +
                                loaded.Hint.Import.OperationId +
                                "; target session " +
                                loaded.Hint.Import.DocumentSessionId);
                        }
                    }

                    if (!string.Equals(
                            inspectedToken,
                            _session.Recovery.PresentationToken,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recovered operation list changed during " +
                            "inspection. Inspect the refreshed list again.");
                    }

                    _recoveryInspection =
                        string.Join(Environment.NewLine, lines);
                    _recoveryInspectionHadFailures =
                        !allPaidInspectionsSucceeded;
                    _reviewedRecoveryToken = inspectedToken;
                });
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task<bool> AppendPaidRecoveryStatusAsync(
        List<string> lines,
        string stage,
        Tripo.HostUi.TripoPanelPaidRecoveryHint? hint)
    {
        if (hint is null)
        {
            return true;
        }

        try
        {
            Tripo.Bridge.HostControlOperationStatusReceipt status =
                await _session.InspectPaidRecoveryAsync(
                    hint.OperationId,
                    stage.ToLowerInvariant());
            lines.Add(
                $"{stage}: {hint.OperationId} — {status.State}; " +
                $"task: {status.CreatedTaskId ?? "not durable"}; " +
                status.NextAction);
            return true;
        }
        catch (Exception exception)
        {
            lines.Add(
                $"{stage}: {hint.OperationId} — inspection failed: " +
                BoundMessage(exception.Message));
            return false;
        }
    }

    private async void OnAcknowledgeRecovery(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
                _session.Recovery;
            string reviewedToken = _reviewedRecoveryToken ??
                throw new InvalidOperationException(
                    "Inspect the current recovered operation list before " +
                    "acknowledging it.");
            if (!string.Equals(
                    reviewedToken,
                    recovery.PresentationToken,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Inspect the current recovered operation list before " +
                    "acknowledging it.");
            }

            RevitRecoveryAcknowledgeDialog dialog = new(
                recovery,
                _recoveryInspectionHadFailures)
            {
                Owner = this,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string reviewReceiptId =
                await _session.CreateRecoveryReviewReceiptAsync(
                    reviewedToken,
                    dialog.PaidResultsReviewed,
                    dialog.ImportsReviewed,
                    dialog.FailedInspectionsReviewed);
            await _session.AcknowledgeRecoveredOperationsAsync(
                reviewReceiptId);
            _recoveryInspection = null;
            _recoveryInspectionHadFailures = false;
            _reviewedRecoveryToken = null;
            UpdateControls(_session.State);
        }
        catch (Exception exception)
        {
            _reviewedRecoveryToken = null;
            _recoveryInspectionHadFailures = false;
            _recoveryInspection =
                "Recovery acknowledgement revalidation failed: " +
                BoundMessage(exception.Message) +
                Environment.NewLine +
                "Inspect the current recovered IDs again before acknowledging.";
            UpdateControls(_session.State);
            ShowError(exception);
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs args)
    {
        CancellationTokenSource? pipelineCts = null;
        try
        {
            _generationRefreshFailed = false;
            _conversionRefreshFailed = false;
            _generationJournalUpdatedOnFailedRefresh = false;
            _conversionJournalUpdatedOnFailedRefresh = false;
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());

            if (_session.State.CredentialStatus?.HasApiKey != true)
            {
                await PromptForApiKeyAsync(
                    recoveryMode: false,
                    exactOriginalKeyRequired: false);
                if (_session.State.CredentialStatus?.HasApiKey != true)
                {
                    return;
                }
            }

            if (!Tripo.HostUi.TripoPanelPresentation.TryParseFaceLimit(
                    _faceLimit.Text,
                    out int faceLimit))
            {
                _settingsExpander.IsExpanded = true;
                UpdateControls(_session.State);
                return;
            }

            string objectName = ResolveObjectName();
            if (string.IsNullOrWhiteSpace(objectName))
            {
                _settingsExpander.IsExpanded = true;
                UpdateControls(_session.State);
                return;
            }

            ImportModeOption importMode =
                _importMode.SelectedItem as ImportModeOption ??
                ImportModeOption.All[0];
            PlacementModeOption placement =
                _placementMode.SelectedItem as PlacementModeOption ??
                PlacementModeOption.All[0];
            string modelId = ResolveSelectedModelId();
            bool withMaterials = _withMaterials.IsChecked == true;
            bool applyMaterials =
                withMaterials && _applyMaterials.IsChecked == true;
            bool associateLevel =
                _associateLevel.IsChecked == true &&
                string.Equals(importMode.Value, "family", StringComparison.Ordinal);

            if (!ConfirmPaidDispatch(
                    "Create model in Revit?",
                    operationId: null,
                    retry: false,
                    "This runs generation and OBJ conversion, then imports into " +
                    "the active project. Generation and conversion may consume " +
                    "Tripo credits." +
                    Environment.NewLine +
                    Environment.NewLine +
                    "Prompt: " + BoundSummary(_prompt.Text) +
                    Environment.NewLine +
                    "Model: " + modelId +
                    Environment.NewLine +
                    $"Face limit: {faceLimit}" +
                    Environment.NewLine +
                    "Import type: " + importMode.Label +
                    Environment.NewLine +
                    "Placement: " + placement.Label))
            {
                return;
            }

            PersistUserSettings();
            pipelineCts = new CancellationTokenSource();
            _pipelineCancellation = pipelineCts;
            Progress<Tripo.HostUi.TripoPipelineProgress> progress = new(
                update =>
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _pipelineStageLabel = update.StageLabel;
                        _pipelineProgressValue = update.ProgressPercent;
                        _currentAction = update.StageLabel;
                        UpdateControls(_session.State);
                    });
                });

            _currentAction = "Creating…";
            _pipelineStageLabel = "Creating…";
            _pipelineProgressValue = 0;
            UpdateControls(_session.State);
            try
            {
                await _session.RunCreatePipelineAsync(
                    _prompt.Text,
                    faceLimit,
                    withMaterials,
                    objectName,
                    importMode.Value,
                    applyMaterials,
                    userConfirmedExternalCost: true,
                    placement.Value,
                    associateLevel,
                    modelId,
                    progress,
                    pipelineCts.Token);
            }
            finally
            {
                _currentAction = null;
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            if (ReferenceEquals(_pipelineCancellation, pipelineCts))
            {
                _pipelineCancellation = null;
            }

            pipelineCts?.Dispose();
            _pipelineStageLabel = null;
            _pipelineProgressValue = null;
            UpdateControls(_session.State);
        }
    }

    private async void OnGenerate(object sender, RoutedEventArgs args)
    {
        try
        {
            _generationRefreshFailed = false;
            _generationJournalUpdatedOnFailedRefresh = false;
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());
            EnsureApiKeyAvailable();
            bool retry = _session.State.GenerationRetryAllowed;
            Tripo.HostUi.PreparedTextGeneration prepared =
                _session.State.PreparedGeneration ??
                _session.PrepareGeneration(
                    _prompt.Text,
                    ParseFaceLimit(),
                    _withMaterials.IsChecked == true,
                    ResolveSelectedModelId());
            UpdateControls(_session.State);
            if (!ConfirmPaidDispatch(
                    retry
                        ? "Retry same generation operation?"
                        : "Create Tripo generation task?",
                    prepared.OperationId,
                    retry,
                    "Prompt: " + BoundSummary(prepared.Prompt) +
                    Environment.NewLine +
                    "Model: " + prepared.Model +
                    Environment.NewLine +
                    $"Face limit: {prepared.FaceLimit}" +
                    Environment.NewLine +
                    "Baked materials: " +
                    (prepared.WithMaterials ? "requested" : "not requested")))
            {
                return;
            }

            await RunWithStatusAsync(
                retry
                    ? "Retrying generation with the same UUID…"
                    : "Submitting the generation request…",
                () => _session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshGeneration(object sender, RoutedEventArgs args)
    {
        _generationRefreshFailed = false;
        _generationJournalUpdatedOnFailedRefresh = false;
        Tripo.Bridge.HostControlOperationStatusReceipt? operationBefore =
            _session.State.GenerationOperationStatus;
        bool refreshAttempted = false;
        try
        {
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());
            if (!CanInspectAmbiguousGenerationLocally(_session.State))
            {
                EnsureApiKeyAvailable();
            }

            refreshAttempted = true;
            await RunWithStatusAsync(
                "Refreshing generation status…",
                () => _session.RefreshGenerationStatusAsync());
        }
        catch (Exception exception)
        {
            _generationRefreshFailed = true;
            _generationJournalUpdatedOnFailedRefresh =
                refreshAttempted &&
                !Equals(
                    operationBefore,
                    _session.State.GenerationOperationStatus);
            UpdateControls(_session.State);
            ShowError(exception);
        }
    }

    private async void OnConvert(object sender, RoutedEventArgs args)
    {
        try
        {
            _conversionRefreshFailed = false;
            _conversionJournalUpdatedOnFailedRefresh = false;
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());
            EnsureApiKeyAvailable();
            bool retry = _session.State.ConversionRetryAllowed;
            Tripo.HostUi.PreparedObjConversion prepared =
                _session.State.PreparedConversion ??
                _session.PrepareConversion(
                    ParseFaceLimit(),
                    _withMaterials.IsChecked == true);
            UpdateControls(_session.State);
            if (!ConfirmPaidDispatch(
                    retry
                        ? "Retry same OBJ conversion operation?"
                        : "Create Tripo OBJ conversion task?",
                    prepared.OperationId,
                    retry,
                    "Source task: " + prepared.SourceTaskId +
                    Environment.NewLine +
                    $"Face limit: {prepared.FaceLimit}" +
                    Environment.NewLine +
                    "Bake materials: " +
                    (prepared.WithMaterials ? "yes" : "no")))
            {
                return;
            }

            await RunWithStatusAsync(
                retry
                    ? "Retrying conversion with the same UUID…"
                    : "Submitting the OBJ conversion request…",
                () => _session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshConversion(object sender, RoutedEventArgs args)
    {
        _conversionRefreshFailed = false;
        _conversionJournalUpdatedOnFailedRefresh = false;
        Tripo.Bridge.HostControlOperationStatusReceipt? operationBefore =
            _session.State.ConversionOperationStatus;
        bool refreshAttempted = false;
        try
        {
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());
            if (!CanInspectAmbiguousConversionLocally(_session.State))
            {
                EnsureApiKeyAvailable();
            }

            refreshAttempted = true;
            await RunWithStatusAsync(
                "Refreshing conversion status…",
                () => _session.RefreshConversionStatusAsync());
        }
        catch (Exception exception)
        {
            _conversionRefreshFailed = true;
            _conversionJournalUpdatedOnFailedRefresh =
                refreshAttempted &&
                !Equals(
                    operationBefore,
                    _session.State.ConversionOperationStatus);
            UpdateControls(_session.State);
            ShowError(exception);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs args)
    {
        try
        {
            await RunWithStatusAsync(
                "Verifying the active Revit document…",
                () => _session.ConnectAsync());
            EnsureApiKeyAvailable();
            ImportModeOption mode =
                _importMode.SelectedItem as ImportModeOption ??
                ImportModeOption.All[0];
            Tripo.HostUi.PreparedObjImport prepared =
                _session.State.PreparedImport is null ||
                _session.State.HasConfirmedImportReceipt
                    ? _session.PrepareImport(
                    _name.Text,
                    mode.Value,
                    _applyMaterials.IsChecked == true,
                    (_placementMode.SelectedItem as PlacementModeOption ??
                     PlacementModeOption.All[0]).Value,
                    _associateLevel.IsChecked == true)
                    : _session.State.PreparedImport;
            UpdateControls(_session.State);
            await RunWithStatusAsync(
                "Importing the prepared OBJ into " +
                prepared.TargetDocumentTitle +
                "…",
                () => _session.ImportPreparedAsync());
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void OnReset(object sender, RoutedEventArgs args)
    {
        try
        {
            Tripo.HostUi.TripoPanelState state = _session.State;
            if (!state.HasWorkflowState ||
                MessageBox.Show(
                    this,
                    "Clear this resolved panel workflow?\n\n" +
                    WorkflowIdentitySummary(state) +
                    "\n\nThis does not cancel remote Tripo tasks, remove " +
                    "journal evidence, or delete imported Revit objects.",
                    "Start a new Tripo workflow?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            _session.ResetWorkflow();
            _generationRefreshFailed = false;
            _conversionRefreshFailed = false;
            _generationJournalUpdatedOnFailedRefresh = false;
            _conversionJournalUpdatedOnFailedRefresh = false;
            _detailsExpander.IsExpanded = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private bool ConfirmPaidDispatch(
        string caption,
        string? operationId,
        bool retry,
        string requestSummary)
    {
        string body = (retry
                ? "This retries the existing durable operation ID; it does not " +
                  "create a replacement ID. The sidecar journal will replay a " +
                  "receipt or fail closed. If no paid request was accepted " +
                  "previously, this retry can consume Tripo credits.\n\n"
                : "This action can consume Tripo credits.\n\n") +
            requestSummary;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            body += "\n\n" +
                    $"Durable operation ID:\n{operationId}\n\n" +
                    "Keep this ID if the response is lost.";
        }

        return MessageBox.Show(
            this,
            body,
            caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task RunWithStatusAsync(
        string currentAction,
        Func<Task> operation)
    {
        _currentAction = currentAction;
        UpdateControls(_session.State);
        try
        {
            await operation();
        }
        finally
        {
            _currentAction = null;
            UpdateControls(_session.State);
        }
    }

    private void OnFormInputChanged(
        object sender,
        TextChangedEventArgs args) =>
        UpdateControls(_session.State);

    private void OnImportModeChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        UpdateImportModeHelp();
        UpdateAssociateLevelEnabled();
        PersistUserSettings();
        UpdateControls(_session.State);
    }

    private void OnPlacementModeChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        UpdatePlacementModeHelp();
        PersistUserSettings();
        UpdateControls(_session.State);
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs args)
    {
        PersistUserSettings();
        UpdateControls(_session.State);
    }

    private void UpdateImportModeHelp()
    {
        ImportModeOption mode =
            _importMode.SelectedItem as ImportModeOption ??
            ImportModeOption.All[0];
        _importModeHelp.Text = mode.Description;
        AutomationProperties.SetHelpText(
            _importMode,
            mode.Description);
        UpdateAssociateLevelEnabled();
    }

    private void UpdatePlacementModeHelp()
    {
        PlacementModeOption mode =
            _placementMode.SelectedItem as PlacementModeOption ??
            PlacementModeOption.All[0];
        AutomationProperties.SetHelpText(
            _placementMode,
            mode.Description);
    }

    private void UpdateAssociateLevelEnabled()
    {
        ImportModeOption mode =
            _importMode.SelectedItem as ImportModeOption ??
            ImportModeOption.All[0];
        bool family = string.Equals(
            mode.Value,
            "family",
            StringComparison.Ordinal);
        if (!family)
        {
            _associateLevel.IsChecked = false;
        }
    }

    private string ResolveObjectName()
    {
        if (!string.IsNullOrWhiteSpace(_name.Text))
        {
            return _name.Text.Trim();
        }

        string prompt = _prompt.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Tripo Model";
        }

        string derived = prompt.Length <= 64 ? prompt : prompt[..64];
        return string.IsNullOrWhiteSpace(derived) ? "Tripo Model" : derived;
    }

    private void ApplyUserSettings(TripoPanelUserSettings settings)
    {
        _faceLimit.Text = settings.FaceLimit.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        _withMaterials.IsChecked = settings.WithMaterials;
        _name.Text = string.IsNullOrWhiteSpace(settings.ObjectName)
            ? "Tripo Model"
            : settings.ObjectName;
        _applyMaterials.IsChecked = settings.ApplyMaterials;
        _associateLevel.IsChecked = settings.AssociateLevel;
        SelectImportMode(settings.ImportMode);
        SelectPlacementMode(settings.PlacementMode);
        SelectTripoModel(settings.Model);
    }

    private void PersistUserSettings()
    {
        try
        {
            ImportModeOption importMode =
                _importMode.SelectedItem as ImportModeOption ??
                ImportModeOption.All[0];
            PlacementModeOption placement =
                _placementMode.SelectedItem as PlacementModeOption ??
                PlacementModeOption.All[0];
            int faceLimit =
                Tripo.HostUi.TripoPanelPresentation.TryParseFaceLimit(
                    _faceLimit.Text,
                    out int parsed)
                    ? parsed
                    : 20_000;
            new TripoPanelUserSettings(
                faceLimit,
                _withMaterials.IsChecked == true,
                ResolveObjectName(),
                importMode.Value,
                _applyMaterials.IsChecked == true,
                placement.Value,
                _associateLevel.IsChecked == true,
                ResolveSelectedModelId()).Save();
        }
        catch
        {
            // Settings persistence is best-effort.
        }
    }

    private void SelectImportMode(string value)
    {
        for (int index = 0; index < ImportModeOption.All.Count; index++)
        {
            if (string.Equals(
                    ImportModeOption.All[index].Value,
                    value,
                    StringComparison.Ordinal))
            {
                _importMode.SelectedIndex = index;
                return;
            }
        }

        _importMode.SelectedIndex = 0;
    }

    private void SelectPlacementMode(string value)
    {
        for (int index = 0; index < PlacementModeOption.All.Count; index++)
        {
            if (string.Equals(
                    PlacementModeOption.All[index].Value,
                    value,
                    StringComparison.Ordinal))
            {
                _placementMode.SelectedIndex = index;
                return;
            }
        }

        _placementMode.SelectedIndex = 0;
    }

    private void SelectTripoModel(string value)
    {
        IReadOnlyList<Tripo.Bridge.TripoModelInfo> models =
            _tripoModel.ItemsSource as IReadOnlyList<Tripo.Bridge.TripoModelInfo> ??
            Tripo.Bridge.TripoModelCatalog.Models;
        string normalized =
            Tripo.Bridge.TripoModelCatalog.NormalizeOrDefault(value);
        for (int index = 0; index < models.Count; index++)
        {
            if (string.Equals(
                    models[index].Id,
                    normalized,
                    StringComparison.Ordinal))
            {
                _tripoModel.SelectedIndex = index;
                return;
            }
        }

        if (models.Count > 0)
        {
            _tripoModel.SelectedIndex = 0;
        }
    }

    private string ResolveSelectedModelId()
    {
        if (_tripoModel.SelectedItem is Tripo.Bridge.TripoModelInfo selected)
        {
            return selected.Id;
        }

        return Tripo.Bridge.TripoModelCatalog.DefaultModelId;
    }

    private void RefreshTripoModelChoices(Tripo.HostUi.TripoPanelState state)
    {
        string current = ResolveSelectedModelId();
        IReadOnlyList<Tripo.Bridge.TripoModelInfo> models =
            state.AvailableModels is { Count: > 0 }
                ? state.AvailableModels
                : Tripo.Bridge.TripoModelCatalog.Models;
        if (!ReferenceEquals(_tripoModel.ItemsSource, models))
        {
            _tripoModel.ItemsSource = models;
        }

        SelectTripoModel(current);
        _tripoModelHelp.Text = string.IsNullOrWhiteSpace(state.ModelCatalogNote)
            ? "Loaded from Tripo's documented model versions at panel start. " +
              "TRIPO_MODEL overrides the selection when set for the sidecar."
            : state.ModelCatalogNote;
    }

    private void OnStateChanged(
        object? sender,
        Tripo.HostUi.TripoPanelState state)
    {
        _ = Dispatcher.BeginInvoke(() => UpdateControls(state));
    }

    private void OnRecoveryChanged(
        object? sender,
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(
                    _displayedRecoveryToken,
                    recovery.PresentationToken,
                    StringComparison.Ordinal))
            {
                _recoveryInspection = null;
                _recoveryInspectionHadFailures = false;
                _reviewedRecoveryToken = null;
            }

            UpdateControls(_session.State);
        });
    }

    private void UpdateControls(Tripo.HostUi.TripoPanelState state)
    {
        if (_closing)
        {
            return;
        }

        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            _session.Recovery;
        bool reviewedCurrentRecovery = string.Equals(
            _reviewedRecoveryToken,
            recovery.PresentationToken,
            StringComparison.Ordinal);
        Tripo.HostUi.TripoPanelPresentation presentation =
            Tripo.HostUi.TripoPanelPresentation.Create(
                state,
                recovery,
                _recoveryInspection,
                reviewedCurrentRecovery,
                _prompt.Text,
                _faceLimit.Text,
                ResolveObjectName(),
                "Revit",
                _currentAction,
                _generationRefreshFailed,
                _conversionRefreshFailed,
                _generationJournalUpdatedOnFailedRefresh,
                _conversionJournalUpdatedOnFailedRefresh,
                _recoveryInspectionHadFailures,
                _pipelineStageLabel,
                _pipelineProgressValue);

        _documentStatus.Text = presentation.DocumentStatus;
        _documentSession.Text = presentation.DocumentSessionId;
        _credentialStatus.Text = presentation.CredentialStatus;
        SetLiveText(
            _resultStatus,
            presentation.ResultStatus,
            VisibleWhen(presentation.ResultVisible));
        AutomationProperties.SetHelpText(
            _resultStatus,
            presentation.ResultKind.ToString());

        SetLiveText(
            _pipelineStage,
            presentation.PipelineStageLabel,
            VisibleWhen(
                presentation.PipelineProgressVisible ||
                !string.IsNullOrWhiteSpace(presentation.PipelineStageLabel)));
        SetProgress(
            _pipelineProgress,
            presentation.PipelineProgress,
            presentation.PipelineStageLabel);

        SetLiveText(_recoveryHeader, presentation.RecoveryHeader);
        _recoveryStatus.Text = presentation.RecoveryDetails;
        _recoveryExpander.Visibility = VisibleWhen(
            presentation.RecoveryHasBlock ||
            presentation.CheckRecoveryEnabled);
        if (presentation.RecoveryHasBlock && !_recoveryWasBlocked)
        {
            _recoveryExpander.IsExpanded = true;
        }
        else if (!presentation.RecoveryHasBlock && _recoveryWasBlocked)
        {
            _recoveryExpander.IsExpanded = false;
        }

        _recoveryWasBlocked = presentation.RecoveryHasBlock;

        _generationOperation.Text = presentation.GenerationOperationId;
        _generationTaskId.Text = presentation.GenerationTaskId;
        SetLiveText(_generationTask, presentation.GenerationStatus);
        _generationDiagnostic.Text = presentation.GenerationDiagnostic;
        _generationDiagnosticBlock.Visibility =
            VisibleWhen(presentation.GenerationDiagnosticVisible);
        SetProgress(
            _generationProgress,
            presentation.GenerationProgress,
            presentation.GenerationStatus);

        _conversionOperation.Text = presentation.ConversionOperationId;
        _conversionTaskId.Text = presentation.ConversionTaskId;
        SetLiveText(_conversionTask, presentation.ConversionStatus);
        _conversionDiagnostic.Text = presentation.ConversionDiagnostic;
        _conversionDiagnosticBlock.Visibility =
            VisibleWhen(presentation.ConversionDiagnosticVisible);
        SetProgress(
            _conversionProgress,
            presentation.ConversionProgress,
            presentation.ConversionStatus);

        _importOperation.Text = presentation.ImportOperationId;
        SetLiveText(_importStatus, presentation.ImportStatus);
        _importReceipt.Text = presentation.ImportReceiptDetails;
        _importReceiptBlock.Visibility =
            VisibleWhen(presentation.ImportReceiptDetailsVisible);

        SetLiveText(
            _promptValidation,
            presentation.PromptValidation,
            VisibleWhen(presentation.PromptValidationVisible));
        SetLiveText(
            _faceLimitValidation,
            presentation.FaceLimitValidation,
            VisibleWhen(presentation.FaceLimitValidationVisible));
        SetLiveText(
            _objectNameValidation,
            presentation.ObjectNameValidation,
            VisibleWhen(presentation.ObjectNameValidationVisible));

        _create.IsEnabled = presentation.PrimaryActionEnabled;
        _create.Content = presentation.PrimaryActionText;
        SetActionHelp(_create, presentation.PrimaryActionHelp);
        _connect.IsEnabled = presentation.ConnectEnabled;
        SetActionHelp(_connect, presentation.ConnectHelp);
        _apiKey.IsEnabled = presentation.ApiKeyEnabled;
        SetActionHelp(_apiKey, presentation.ApiKeyHelp);
        _clearApiKey.IsEnabled = presentation.ClearApiKeyEnabled;
        SetActionHelp(
            _clearApiKey,
            presentation.ClearApiKeyHelp);
        _checkRecovery.IsEnabled = presentation.CheckRecoveryEnabled;
        SetActionHelp(
            _checkRecovery,
            presentation.CheckRecoveryHelp);
        _acknowledgeRecovery.IsEnabled =
            presentation.AcknowledgeRecoveryEnabled;
        SetActionHelp(
            _acknowledgeRecovery,
            presentation.AcknowledgeRecoveryHelp);
        _generate.IsEnabled = presentation.GenerateEnabled;
        _generate.Content = presentation.GenerateText;
        SetActionHelp(_generate, presentation.GenerateHelp);
        _refreshGeneration.IsEnabled =
            presentation.RefreshGenerationEnabled;
        SetActionHelp(
            _refreshGeneration,
            presentation.RefreshGenerationHelp);
        _convert.IsEnabled = presentation.ConvertEnabled;
        _convert.Content = presentation.ConvertText;
        SetActionHelp(_convert, presentation.ConvertHelp);
        _refreshConversion.IsEnabled =
            presentation.RefreshConversionEnabled;
        SetActionHelp(
            _refreshConversion,
            presentation.RefreshConversionHelp);
        _import.IsEnabled = presentation.ImportEnabled;
        _import.Content = presentation.ImportText;
        SetActionHelp(_import, presentation.ImportHelp);
        _reset.IsEnabled = presentation.ResetEnabled;
        SetActionHelp(_reset, presentation.ResetHelp);
        _prompt.IsEnabled = presentation.PromptEnabled;
        bool settingsUnlocked =
            presentation.FaceLimitEnabled ||
            presentation.NameEnabled ||
            !string.IsNullOrWhiteSpace(_currentAction);
        RefreshTripoModelChoices(state);
        _tripoModel.IsEnabled = presentation.FaceLimitEnabled;
        _faceLimit.IsEnabled = presentation.FaceLimitEnabled;
        _withMaterials.IsEnabled = presentation.WithMaterialsEnabled;
        _name.IsEnabled =
            presentation.NameEnabled ||
            presentation.PromptEnabled;
        _importMode.IsEnabled =
            presentation.ImportModeEnabled ||
            presentation.PromptEnabled;
        _applyMaterials.IsEnabled =
            presentation.ApplyMaterialsEnabled ||
            (presentation.PromptEnabled &&
             _withMaterials.IsChecked == true);
        _placementMode.IsEnabled =
            presentation.ImportModeEnabled ||
            presentation.PromptEnabled;
        _associateLevel.IsEnabled =
            (presentation.ImportModeEnabled ||
             presentation.PromptEnabled) &&
            string.Equals(
                (_importMode.SelectedItem as ImportModeOption)?.Value ??
                "family",
                "family",
                StringComparison.Ordinal);
        _ = settingsUnlocked;
        if (state.PreparedConversion?.WithMaterials != true &&
            !presentation.PromptEnabled)
        {
            _applyMaterials.IsChecked = false;
        }

        if (!string.IsNullOrWhiteSpace(
                presentation.LatestPreparedOperationId) &&
            !string.Equals(
                _displayedPreparedOperationId,
                presentation.LatestPreparedOperationId,
                StringComparison.Ordinal))
        {
            _detailsExpander.IsExpanded = true;
        }

        _displayedPreparedOperationId =
            presentation.LatestPreparedOperationId;
        _displayedRecoveryToken = presentation.RecoveryToken;
    }

    private int ParseFaceLimit()
    {
        if (!Tripo.HostUi.TripoPanelPresentation.TryParseFaceLimit(
                _faceLimit.Text,
                out int faceLimit))
        {
            throw new ArgumentException(
                "Face limit must be a whole number from 500 through 200000.");
        }

        return faceLimit;
    }

    private void EnsureApiKeyAvailable()
    {
        if (_session.State.CredentialStatus?.HasApiKey != true)
        {
            throw new InvalidOperationException(
                "Configure a Tripo API key before continuing this workflow.");
        }
    }

    private static bool CanInspectAmbiguousGenerationLocally(
        Tripo.HostUi.TripoPanelState state) =>
        state.GenerationDispatchAttempted &&
        state.GenerationReceipt is null &&
        state.GenerationOperationStatus?.TaskIdDurable != true;

    private static bool CanInspectAmbiguousConversionLocally(
        Tripo.HostUi.TripoPanelState state) =>
        state.ConversionDispatchAttempted &&
        state.ConversionReceipt is null &&
        state.ConversionOperationStatus?.TaskIdDurable != true;

    private void ShowError(Exception exception)
    {
        if (!_closing)
        {
            MessageBox.Show(
                this,
                BoundMessage(exception.Message),
                "Tripo",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose || !_session.State.HasWorkflowState)
        {
            return;
        }

        // Keep workflow state; reopen from the ribbon without a modal.
        args.Cancel = true;
        Hide();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _session.StateChanged -= OnStateChanged;
        _session.RecoveryChanged -= OnRecoveryChanged;
        _sessionDisposal = _session.DisposeAsync().AsTask();
    }

    private static Button PrimaryActionButton(string content)
    {
        Button button = ActionButton(content);
        button.FontSize = 14;
        return button;
    }

    private static Button ActionButton(string content)
    {
        Button button = new()
        {
            Content = content,
            Margin = new Thickness(0, 4, 8, 4),
            MinHeight = 32,
            Padding = new Thickness(12, 5, 12, 5),
        };
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    private static TextBlock StatusBlock() =>
        new()
        {
            Margin = new Thickness(0, 5, 0, 5),
            TextWrapping = TextWrapping.Wrap,
        };

    private static TextBlock ValidationBlock()
    {
        TextBlock block = new()
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 5),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(
            block,
            AutomationLiveSetting.Polite);
        return block;
    }

    private static CheckBox WrappingCheckBox(
        string text,
        string? toolTip = null)
    {
        CheckBox checkBox = new()
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = toolTip,
        };
        AutomationProperties.SetName(checkBox, text);
        ToolTipService.SetShowOnDisabled(checkBox, true);
        return checkBox;
    }

    private static TextBox OperationStatusBox() =>
        new()
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(0, 5, 0, 5),
            Padding = new Thickness(5),
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap,
        };

    private static TextBox DiagnosticBox() =>
        new()
        {
            AcceptsReturn = true,
            Height = 92,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Padding = new Thickness(6),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

    private static TextBox RecoveryStatusBox()
    {
        TextBox status = DiagnosticBox();
        status.Height = 170;
        return status;
    }

    private static ProgressBar WorkflowProgressBar() =>
        new()
        {
            Height = 8,
            Margin = new Thickness(0, 3, 0, 5),
            Maximum = 100,
            Minimum = 0,
            Visibility = Visibility.Collapsed,
        };

    private static GroupBox Section(
        string heading,
        params UIElement[] children)
    {
        StackPanel content = new();
        foreach (UIElement child in children)
        {
            content.Children.Add(child);
        }

        return new GroupBox
        {
            Header = heading,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10, 6, 10, 8),
            Content = content,
        };
    }

    private static StackPanel FieldBlock(string caption, Control control)
    {
        Label label = new()
        {
            Content = caption,
            Padding = new Thickness(0),
            Target = control,
        };
        AutomationProperties.SetLabeledBy(control, label);
        StackPanel block = new()
        {
            Margin = new Thickness(0, 4, 0, 4),
        };
        block.Children.Add(label);
        block.Children.Add(control);
        return block;
    }

    private static WrapPanel ActionRow(params Button[] buttons)
    {
        WrapPanel panel = new();
        foreach (Button button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static Visibility VisibleWhen(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetActionHelp(Button button, string help)
    {
        button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
    }

    private static void SetLiveText(
        TextBlock block,
        string text,
        Visibility? visibility = null)
    {
        Visibility nextVisibility = visibility ?? block.Visibility;
        bool changed =
            !string.Equals(block.Text, text, StringComparison.Ordinal) ||
            block.Visibility != nextVisibility;
        block.Text = text;
        block.Visibility = nextVisibility;
        if (!changed ||
            !block.IsLoaded ||
            !AutomationPeer.ListenerExists(
                AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        AutomationPeer? peer =
            UIElementAutomationPeer.FromElement(block) ??
            UIElementAutomationPeer.CreatePeerForElement(block);
        peer?.RaiseAutomationEvent(
            AutomationEvents.LiveRegionChanged);
    }

    private static void SetProgress(
        ProgressBar progressBar,
        int? reportedProgress,
        string status)
    {
        bool valid = reportedProgress is >= 0 and <= 100;
        progressBar.Visibility = VisibleWhen(valid);
        if (valid)
        {
            progressBar.Value = reportedProgress!.Value;
        }

        AutomationProperties.SetName(
            progressBar,
            valid
                ? $"{status}; {reportedProgress!.Value} percent"
                : status);
    }

    private static string WorkflowIdentitySummary(
        Tripo.HostUi.TripoPanelState state)
    {
        List<string> lines = [];
        AddIdentity(
            lines,
            "Generation operation",
            state.PreparedGeneration?.OperationId);
        AddIdentity(
            lines,
            "Generation task",
            state.GenerationReceipt?.TaskId ??
            (state.GenerationOperationStatus is { TaskIdDurable: true }
                ? state.GenerationOperationStatus.CreatedTaskId
                : null));
        AddIdentity(
            lines,
            "Conversion operation",
            state.PreparedConversion?.OperationId);
        AddIdentity(
            lines,
            "Conversion task",
            state.ConversionReceipt?.ConversionTaskId ??
            (state.ConversionOperationStatus is { TaskIdDurable: true }
                ? state.ConversionOperationStatus.CreatedTaskId
                : null));
        AddIdentity(
            lines,
            "Import operation",
            state.PreparedImport?.OperationId);
        AddIdentity(
            lines,
            "Imported Revit object",
            state.ImportReceipt?.HostReceipt.CreatedId);
        return lines.Count == 0
            ? "The current panel workflow state will be cleared."
            : string.Join(Environment.NewLine, lines);
    }

    private static void AddIdentity(
        List<string> lines,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }

    private static string BoundSummary(string value)
    {
        string normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= 180
            ? normalized
            : normalized[..180] + "…";
    }

    private static string BoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The Tripo operation failed.";
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private sealed record ImportModeOption(
        string Label,
        string Value,
        string Description)
    {
        public static IReadOnlyList<ImportModeOption> All { get; } =
        [
            new(
                "Revit family (recommended)",
                "family",
                "Creates a Generic Models family, saves its .rfa, loads it, " +
                "and places one instance at the project origin."),
            new(
                "DirectShape mesh",
                "mesh",
                "Creates one non-solid Generic Models DirectShape in the " +
                "current project; no reusable family file is saved."),
        ];

        public override string ToString() => Label;
    }

    private sealed record PlacementModeOption(
        string Label,
        string Value,
        string Description)
    {
        public static IReadOnlyList<PlacementModeOption> All { get; } =
        [
            new(
                "Project origin",
                "origin",
                "Places the imported model at the project origin (0,0,0)."),
            new(
                "Pick point in view",
                "pick",
                "After import, click a point in the active Revit view. " +
                "Esc keeps the model at the origin."),
        ];

        public override string ToString() => Label;
    }

    private sealed class RevitApiKeyDialog : Window
    {
        private readonly PasswordBox _password = new()
        {
            MaxLength = 2048,
        };
        private readonly CheckBox _persist = new()
        {
            Content = "Save in this user's OS credential store",
            IsChecked = true,
        };

        public RevitApiKeyDialog(
            bool recoveryMode,
            bool exactOriginalKeyRequired)
        {
            Title = "Tripo API key";
            MinWidth = 380;
            Width = Math.Min(
                540,
                Math.Max(
                    MinWidth,
                    SystemParameters.WorkArea.Width - 48));
            MaxHeight = Math.Max(
                300,
                SystemParameters.WorkArea.Height - 48);
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Button save = ActionButton("Save");
            Button cancel = ActionButton("Cancel");
            save.IsDefault = true;
            save.IsEnabled = false;
            cancel.IsCancel = true;
            save.Click += (_, _) => DialogResult = true;
            _password.PasswordChanged += (_, _) =>
                save.IsEnabled =
                    !string.IsNullOrWhiteSpace(_password.Password);
            AutomationProperties.SetName(
                _password,
                "Tripo API key");

            TextBlock linkText = new()
            {
                Margin = new Thickness(0, 0, 0, 12),
            };
            Hyperlink apiKeyLink = new(new Run("Open Tripo API key settings"))
            {
                NavigateUri =
                    new Uri("https://platform.tripo3d.ai/api-keys"),
            };
            apiKeyLink.RequestNavigate += OpenExternalLink;
            linkText.Inlines.Add(apiKeyLink);

            StackPanel content = new()
            {
                Margin = new Thickness(16),
            };
            content.Children.Add(new TextBlock
            {
                Text = ApiKeyInstructions,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            content.Children.Add(linkText);
            if (recoveryMode)
            {
                _persist.IsChecked = false;
                _persist.IsEnabled = false;
                _persist.ToolTip =
                    "Recovery credentials remain session-only until the " +
                    "account-bound workflow is reconciled and explicitly reset.";
                ToolTipService.SetShowOnDisabled(_persist, true);
                content.Children.Add(new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    Text =
                        exactOriginalKeyRequired
                            ? "Recovery mode: this key is session-only. Restore " +
                              "the exact original key for the unresolved paid UUID."
                            : "Recovery mode: this key is session-only. Use a key " +
                              "for the same Tripo account as the unfinished workflow.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            content.Children.Add(FieldBlock("API key", _password));
            content.Children.Add(_persist);
            content.Children.Add(ActionRow(save, cancel));
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };
            Loaded += (_, _) => Keyboard.Focus(_password);
        }

        public bool Persist => _persist.IsChecked == true;

        public string TakeAndClearApiKey()
        {
            string value = _password.Password;
            _password.Clear();
            return value;
        }

        public void ClearApiKey() => _password.Clear();

        private static void OpenExternalLink(
            object sender,
            RequestNavigateEventArgs args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    BoundMessage(exception.Message),
                    "Could not open Tripo API key settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                args.Handled = true;
            }
        }
    }

    private sealed class RevitRecoveryAcknowledgeDialog : Window
    {
        private readonly CheckBox _paidReviewed = WrappingCheckBox(
            "I reviewed every paid generation/conversion UUID against the " +
            "local journal or provider history.");
        private readonly CheckBox _importsReviewed = WrappingCheckBox(
            "I manually reconciled every import UUID in the target Revit " +
            "document.");
        private readonly CheckBox _failedInspectionsReviewed =
            WrappingCheckBox(
                "I manually reconciled every paid UUID whose in-panel " +
                "inspection failed.");
        private readonly Button _acknowledge =
            ActionButton("Archive reviewed hints");
        private readonly bool _hasPaidIds;
        private readonly bool _hasImportIds;
        private readonly bool _inspectionHadFailures;

        public RevitRecoveryAcknowledgeDialog(
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery,
            bool inspectionHadFailures)
        {
            _hasPaidIds = recovery.Hints.Any(
                loaded =>
                    loaded.Hint.Generation is not null ||
                    loaded.Hint.Conversion is not null);
            _hasImportIds = recovery.Hints.Any(
                loaded => loaded.Hint.Import is not null);
            _inspectionHadFailures = inspectionHadFailures;
            Title = "Acknowledge recovered Tripo IDs";
            MinWidth = 400;
            MinHeight = 300;
            Width = Math.Min(
                620,
                Math.Max(
                    MinWidth,
                    SystemParameters.WorkArea.Width - 48));
            Height = Math.Min(
                520,
                Math.Max(
                    MinHeight,
                    SystemParameters.WorkArea.Height - 48));
            MaxHeight = Math.Max(
                MinHeight,
                SystemParameters.WorkArea.Height - 48);
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.ToolWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Button cancel = ActionButton("Cancel");
            _acknowledge.IsDefault = true;
            _acknowledge.IsEnabled = false;
            cancel.IsCancel = true;
            _paidReviewed.Visibility = VisibleWhen(_hasPaidIds);
            _importsReviewed.Visibility = VisibleWhen(_hasImportIds);
            _failedInspectionsReviewed.Visibility =
                VisibleWhen(_inspectionHadFailures);
            _acknowledge.Click += (_, _) =>
            {
                if (CanAcknowledge())
                {
                    DialogResult = true;
                }
            };
            _paidReviewed.Checked += (_, _) => UpdateAcknowledgeState();
            _paidReviewed.Unchecked += (_, _) => UpdateAcknowledgeState();
            _importsReviewed.Checked += (_, _) => UpdateAcknowledgeState();
            _importsReviewed.Unchecked += (_, _) =>
                UpdateAcknowledgeState();
            _failedInspectionsReviewed.Checked += (_, _) =>
                UpdateAcknowledgeState();
            _failedInspectionsReviewed.Unchecked += (_, _) =>
                UpdateAcknowledgeState();

            TextBox summary = DiagnosticBox();
            summary.Height = 132;
            summary.Text = RecoverySummary(recovery);
            StackPanel content = new()
            {
                Margin = new Thickness(16),
            };
            content.Children.Add(new TextBlock
            {
                Text =
                    "Archiving these hints unlocks new operation UUIDs. It " +
                    "does not cancel remote tasks or change Revit objects.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            content.Children.Add(
                FieldBlock("Recovered IDs in this snapshot", summary));
            content.Children.Add(_paidReviewed);
            content.Children.Add(_importsReviewed);
            content.Children.Add(_failedInspectionsReviewed);
            content.Children.Add(ActionRow(_acknowledge, cancel));
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };
            Loaded += (_, _) =>
            {
                if (_hasPaidIds)
                {
                    Keyboard.Focus(_paidReviewed);
                }
                else if (_hasImportIds)
                {
                    Keyboard.Focus(_importsReviewed);
                }
                else
                {
                    Keyboard.Focus(_acknowledge);
                }
            };
        }

        public bool PaidResultsReviewed =>
            _paidReviewed.IsChecked == true;

        public bool ImportsReviewed =>
            _importsReviewed.IsChecked == true;

        public bool FailedInspectionsReviewed =>
            _failedInspectionsReviewed.IsChecked == true;

        private bool CanAcknowledge() =>
            (!_hasPaidIds || _paidReviewed.IsChecked == true) &&
            (!_hasImportIds || _importsReviewed.IsChecked == true) &&
            (!_inspectionHadFailures ||
             _failedInspectionsReviewed.IsChecked == true);

        private void UpdateAcknowledgeState() =>
            _acknowledge.IsEnabled = CanAcknowledge();

        private static string RecoverySummary(
            Tripo.HostUi.TripoPanelRecoveryLoadResult recovery)
        {
            List<string> lines = [];
            foreach (
                Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded in
                recovery.Hints)
            {
                lines.Add(
                    Tripo.HostUi.TripoPanelPresentation
                        .FormatRecoveryHint(loaded.Hint));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

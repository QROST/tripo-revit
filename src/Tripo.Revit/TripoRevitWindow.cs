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
    private readonly CheckBox _applyMaterials = WrappingCheckBox(
        "Apply baked materials from the OBJ bundle",
        "Requires an MTL material library. The import receipt reports actual " +
        "material and texture counts; review a zero count.");
    private readonly TextBlock _documentStatus = StatusBlock();
    private readonly TextBox _documentSession = OperationStatusBox();
    private readonly TextBlock _credentialStatus = StatusBlock();
    private readonly TextBlock _resultStatus = StatusBlock();
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
    private readonly Expander _recoveryExpander = new();
    private readonly Expander _detailsExpander = new();
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
    private readonly Button _reset = ActionButton("New workflow");
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
    private bool _generationRefreshFailed;
    private bool _conversionRefreshFailed;
    private bool _generationJournalUpdatedOnFailedRefresh;
    private bool _conversionJournalUpdatedOnFailedRefresh;
    private bool _recoveryWasBlocked;
    private bool _allowClose;
    private bool _closing;

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
            600,
            Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        Height = Math.Min(
            760,
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
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildContent(),
        };
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
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        AutomationProperties.SetName(
            this,
            "Tripo 3D generation workflow for Revit");
        AutomationProperties.SetLiveSetting(
            _resultStatus,
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
        StackPanel headerContent = new();
        headerContent.Children.Add(new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Text = "Tripo 3D for Revit",
        });
        headerContent.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 8),
            Text =
                "Generate, convert, and import with durable operation IDs.",
            TextWrapping = TextWrapping.Wrap,
        });
        headerContent.Children.Add(_documentStatus);
        headerContent.Children.Add(_credentialStatus);
        _resultStatus.FontWeight = FontWeights.SemiBold;
        headerContent.Children.Add(_resultStatus);
        headerContent.Children.Add(
            ActionRow(_connect, _apiKey, _clearApiKey));
        panel.Children.Add(new Border
        {
            Background = SystemColors.ControlLightBrush,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Child = headerContent,
        });

        StackPanel recoveryContent = new();
        recoveryContent.Children.Add(
            FieldBlock("Recovered operation details", _recoveryStatus));
        recoveryContent.Children.Add(
            ActionRow(_checkRecovery, _acknowledgeRecovery));
        _recoveryExpander.Header = _recoveryHeader;
        _recoveryExpander.Margin = new Thickness(0, 12, 0, 0);
        _recoveryExpander.Content = recoveryContent;
        panel.Children.Add(_recoveryExpander);

        _generationTask.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(Section(
            "1 · Generate model",
            FieldBlock("Describe the model", _prompt),
            _promptValidation,
            FieldBlock("Face limit (500–200,000)", _faceLimit),
            _faceLimitValidation,
            _withMaterials,
            _generationTask,
            _generationProgress,
            ActionRow(_generate, _refreshGeneration)));

        _conversionTask.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(Section(
            "2 · Convert to OBJ",
            new TextBlock
            {
                Text =
                    "Conversion is a separate Tripo task and may consume credits.",
                TextWrapping = TextWrapping.Wrap,
            },
            _conversionTask,
            _conversionProgress,
            ActionRow(_convert, _refreshConversion)));

        _importStatus.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(Section(
            "3 · Import into Revit",
            FieldBlock("Revit object name", _name),
            _objectNameValidation,
            FieldBlock("Revit import type", _importMode),
            _importModeHelp,
            _applyMaterials,
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
        _detailsExpander.Margin = new Thickness(0, 12, 0, 0);
        _detailsExpander.Content = detailsContent;
        panel.Children.Add(_detailsExpander);

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
        panel.Children.Add(ActionRow(_reset));
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
                    _withMaterials.IsChecked == true);
            UpdateControls(_session.State);
            if (!ConfirmPaidDispatch(
                    retry
                        ? "Retry same generation operation?"
                        : "Create Tripo generation task?",
                    prepared.OperationId,
                    retry,
                    "Prompt: " + BoundSummary(prepared.Prompt) +
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
                    _applyMaterials.IsChecked == true)
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
        string operationId,
        bool retry,
        string requestSummary) =>
        MessageBox.Show(
            this,
            (retry
                ? "This retries the existing durable operation ID; it does not " +
                  "create a replacement ID. The sidecar journal will replay a " +
                  "receipt or fail closed. If no paid request was accepted " +
                  "previously, this retry can consume Tripo credits.\n\n"
                : "This action can consume Tripo credits.\n\n") +
            requestSummary +
            "\n\n" +
            $"Durable operation ID:\n{operationId}\n\n" +
            "Keep this ID if the response is lost.",
            caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

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
                _name.Text,
                "Revit",
                _currentAction,
                _generationRefreshFailed,
                _conversionRefreshFailed,
                _generationJournalUpdatedOnFailedRefresh,
                _conversionJournalUpdatedOnFailedRefresh,
                _recoveryInspectionHadFailures);

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

        SetLiveText(_recoveryHeader, presentation.RecoveryHeader);
        _recoveryStatus.Text = presentation.RecoveryDetails;
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
        _faceLimit.IsEnabled = presentation.FaceLimitEnabled;
        _withMaterials.IsEnabled = presentation.WithMaterialsEnabled;
        _name.IsEnabled = presentation.NameEnabled;
        _importMode.IsEnabled = presentation.ImportModeEnabled;
        _applyMaterials.IsEnabled =
            presentation.ApplyMaterialsEnabled;
        if (state.PreparedConversion?.WithMaterials != true)
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

        args.Cancel = true;
        MessageBox.Show(
            this,
            "The Tripo panel was hidden so its workflow and durable operation " +
            "IDs remain available. Reopen it from Add-Ins > Tripo > Tripo 3D. " +
            "Use New workflow only after unresolved operations are reconciled.",
            "Tripo workflow retained",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

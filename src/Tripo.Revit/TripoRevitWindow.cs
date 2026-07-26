using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tripo.Revit;

internal sealed class TripoRevitWindow : Window
{
    private const string ApiKeyInstructions =
        "Create a Tripo v3 API key at:\n" +
        "https://platform.tripo3d.ai/api-keys\n\n" +
        "The key is sent only to the local Tripo sidecar. It is never saved " +
        "in this .rvt or .rfa document.\n\n" +
        "Replacing the effective key can make same-UUID recovery fail closed " +
        "for unfinished UI or MCP paid operations. Reconcile them first.";

    private readonly Tripo.HostUi.TripoPanelSession _session;
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        Height = 76,
        Text = "A buildable architectural pavilion",
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _faceLimit = new() { Text = "20000" };
    private readonly CheckBox _withMaterials = new()
    {
        Content = "Request generated materials",
    };
    private readonly TextBox _name = new() { Text = "Tripo Model" };
    private readonly ComboBox _importMode = new()
    {
        ItemsSource = new[] { "native", "mesh", "family" },
        SelectedIndex = 0,
    };
    private readonly CheckBox _applyMaterials = new()
    {
        Content = "Apply baked diffuse materials",
    };
    private readonly TextBlock _documentStatus = StatusBlock();
    private readonly TextBlock _credentialStatus = StatusBlock();
    private readonly TextBox _recoveryStatus = OperationStatusBox();
    private readonly TextBox _generationOperation = OperationStatusBox();
    private readonly TextBlock _generationTask = StatusBlock();
    private readonly TextBox _conversionOperation = OperationStatusBox();
    private readonly TextBlock _conversionTask = StatusBlock();
    private readonly TextBox _importOperation = OperationStatusBox();
    private readonly TextBlock _resultStatus = StatusBlock();
    private readonly Button _connect = ActionButton("Connect / Refresh");
    private readonly Button _apiKey = ActionButton("API key…");
    private readonly Button _checkRecovery =
        ActionButton("Check recovered paid operations");
    private readonly Button _acknowledgeRecovery =
        ActionButton("Acknowledge reconciled IDs…");
    private readonly Button _generate = ActionButton("Generate");
    private readonly Button _refreshGeneration =
        ActionButton("Refresh generation");
    private readonly Button _convert = ActionButton("Convert to OBJ");
    private readonly Button _refreshConversion =
        ActionButton("Refresh conversion");
    private readonly Button _import = ActionButton("Import into Revit");
    private readonly Button _reset = ActionButton("New workflow");
    private Task _sessionDisposal = Task.CompletedTask;
    private string? _recoveryInspection;
    private string _displayedRecoveryToken =
        Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty.PresentationToken;
    private bool _allowClose;
    private bool _closing;

    internal Task SessionDisposal => _sessionDisposal;

    public TripoRevitWindow(Tripo.HostUi.TripoPanelSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.StateChanged += OnStateChanged;
        _session.RecoveryChanged += OnRecoveryChanged;
        Title = "Tripo 3D";
        Width = 520;
        Height = 760;
        MinWidth = 440;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildContent(),
        };
        _connect.Click += OnConnect;
        _apiKey.Click += OnApiKey;
        _checkRecovery.Click += OnCheckRecovery;
        _acknowledgeRecovery.Click += OnAcknowledgeRecovery;
        _generate.Click += OnGenerate;
        _refreshGeneration.Click += OnRefreshGeneration;
        _convert.Click += OnConvert;
        _refreshConversion.Click += OnRefreshConversion;
        _import.Click += OnImport;
        _reset.Click += OnReset;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
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
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Text = "Tripo 3D for Revit",
        });
        panel.Children.Add(_documentStatus);
        panel.Children.Add(ButtonRow(_connect, _apiKey));
        panel.Children.Add(_credentialStatus);
        panel.Children.Add(SectionHeading("Crash recovery"));
        panel.Children.Add(_recoveryStatus);
        panel.Children.Add(
            ButtonRow(_checkRecovery, _acknowledgeRecovery));
        panel.Children.Add(SectionHeading("1. Text generation"));
        panel.Children.Add(_prompt);
        panel.Children.Add(FieldRow("Face limit", _faceLimit));
        panel.Children.Add(_withMaterials);
        panel.Children.Add(_generationOperation);
        panel.Children.Add(ButtonRow(_generate, _refreshGeneration));
        panel.Children.Add(_generationTask);
        panel.Children.Add(SectionHeading("2. OBJ conversion"));
        panel.Children.Add(_conversionOperation);
        panel.Children.Add(ButtonRow(_convert, _refreshConversion));
        panel.Children.Add(_conversionTask);
        panel.Children.Add(SectionHeading("3. Import"));
        panel.Children.Add(FieldRow("Object name", _name));
        panel.Children.Add(FieldRow("Import mode", _importMode));
        panel.Children.Add(_applyMaterials);
        panel.Children.Add(_importOperation);
        panel.Children.Add(ButtonRow(_import, _reset));
        panel.Children.Add(_resultStatus);
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
            await _session.ConnectAsync();
            if (_session.State.CredentialStatus?.HasApiKey == false &&
                !_session.Recovery.HasBlock)
            {
                await PromptForApiKeyAsync();
            }
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
                await _session.ConnectAsync();
            }

            await PromptForApiKeyAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task PromptForApiKeyAsync()
    {
        RevitApiKeyDialog dialog = new()
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
            await _session.SetApiKeyAsync(secret, dialog.Persist);
        }
        finally
        {
            dialog.ClearApiKey();
            secret = null;
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
                await _session.ConnectAsync();
            }

            List<string> lines = [];
            foreach (Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded in
                     _session.Recovery.Hints)
            {
                lines.Add(
                    $"Document session: {loaded.Hint.DocumentSessionId}");
                await AppendPaidRecoveryStatusAsync(
                    lines,
                    "Generation",
                    loaded.Hint.Generation);
                await AppendPaidRecoveryStatusAsync(
                    lines,
                    "Conversion",
                    loaded.Hint.Conversion);
                if (loaded.Hint.Import is not null)
                {
                    lines.Add(
                        "Import: manual reconciliation required; UUID " +
                        loaded.Hint.Import.OperationId);
                }
            }

            _recoveryInspection = string.Join(Environment.NewLine, lines);
            UpdateControls(_session.State);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task AppendPaidRecoveryStatusAsync(
        List<string> lines,
        string stage,
        Tripo.HostUi.TripoPanelPaidRecoveryHint? hint)
    {
        if (hint is null)
        {
            return;
        }

        try
        {
            Tripo.Bridge.HostControlOperationStatusReceipt status =
                await _session.InspectPaidRecoveryAsync(hint.OperationId);
            lines.Add(
                $"{stage}: {hint.OperationId} — {status.State}; " +
                $"task: {status.CreatedTaskId ?? "not durable"}; " +
                status.NextAction);
        }
        catch (Exception exception)
        {
            lines.Add(
                $"{stage}: {hint.OperationId} — inspection failed: " +
                BoundMessage(exception.Message));
        }
    }

    private void OnAcknowledgeRecovery(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            RevitRecoveryAcknowledgeDialog dialog = new()
            {
                Owner = this,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _session.AcknowledgeRecoveredOperations(
                dialog.Confirmation,
                _displayedRecoveryToken);
            _recoveryInspection = null;
            UpdateControls(_session.State);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnGenerate(object sender, RoutedEventArgs args)
    {
        try
        {
            bool retry = _session.State.GenerationRetryRequired;
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
                    retry))
            {
                return;
            }

            await _session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshGeneration(object sender, RoutedEventArgs args)
    {
        try
        {
            await _session.RefreshGenerationStatusAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnConvert(object sender, RoutedEventArgs args)
    {
        try
        {
            bool retry = _session.State.ConversionRetryRequired;
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
                    retry))
            {
                return;
            }

            await _session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnRefreshConversion(object sender, RoutedEventArgs args)
    {
        try
        {
            await _session.RefreshConversionStatusAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs args)
    {
        try
        {
            _ = _session.State.PreparedImport ??
                _session.PrepareImport(
                    _name.Text,
                    _importMode.SelectedItem?.ToString() ?? "native",
                    _applyMaterials.IsChecked == true);
            UpdateControls(_session.State);
            await _session.ImportPreparedAsync();
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
            _session.ResetWorkflow();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private bool ConfirmPaidDispatch(
        string caption,
        string operationId,
        bool retry) =>
        MessageBox.Show(
            this,
            (retry
                ? "This retries the existing durable operation ID; it does not " +
                  "create a replacement ID. The sidecar journal will replay a " +
                  "receipt or fail closed. If no paid request was accepted " +
                  "previously, this retry can consume Tripo credits.\n\n"
                : "This action can consume Tripo credits.\n\n") +
            $"Durable operation ID:\n{operationId}\n\n" +
            "Keep this ID if the response is lost.",
            caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

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
        _ = Dispatcher.BeginInvoke(() => UpdateControls(_session.State));
    }

    private void UpdateControls(Tripo.HostUi.TripoPanelState state)
    {
        if (_closing)
        {
            return;
        }

        _documentStatus.Text = state.Context is null
            ? "Document: not connected"
            : $"Document: {state.Context.DocumentTitle}\n" +
              $"Session: {state.Context.DocumentSessionId}";
        _credentialStatus.Text = state.CredentialStatus is null
            ? "API key: unknown"
            : $"API key: {state.CredentialStatus.Source}" +
              (state.CredentialStatus.UsesWeakerFileFallback
                  ? " (private-file fallback)"
                  : string.Empty);
        UpdateRecoveryControls(state);
        _generationOperation.Text =
            "Generation operation: " +
            (state.PreparedGeneration?.OperationId ?? "not prepared");
        string? generationTaskId =
            state.GenerationReceipt?.TaskId ??
            state.GenerationOperationStatus?.CreatedTaskId;
        _generationTask.Text = generationTaskId is null
            ? "Generation task: " +
              (state.GenerationOperationStatus?.State ?? "not created")
            : $"Generation task: {generationTaskId} — " +
              $"{state.GenerationStatus?.Status ?? "refresh status"} " +
              $"{state.GenerationStatus?.Progress ?? 0}%";
        _conversionOperation.Text =
            "Conversion operation: " +
            (state.PreparedConversion?.OperationId ?? "not prepared");
        string? conversionTaskId =
            state.ConversionReceipt?.ConversionTaskId ??
            state.ConversionOperationStatus?.CreatedTaskId;
        _conversionTask.Text = conversionTaskId is null
            ? "Conversion task: " +
              (state.ConversionOperationStatus?.State ?? "not created")
            : $"Conversion task: {conversionTaskId} — " +
              $"{state.ConversionStatus?.Status ?? "refresh status"} " +
              $"{state.ConversionStatus?.Progress ?? 0}%";
        _importOperation.Text =
            "Import operation: " +
            (state.PreparedImport?.OperationId ?? "not prepared");
        _resultStatus.Text = state.ImportReceipt is not null
            ? $"Imported {state.ImportReceipt.HostReceipt.CreatedId} " +
              $"({state.ImportReceipt.HostReceipt.TransactionStatus})."
            : state.LastError ?? (state.Busy ? "Working…" : "Ready.");

        bool ready = state.Connected && !state.Busy;
        bool generationPrepared = state.PreparedGeneration is not null;
        bool generationSucceeded =
            state.GenerationStatus?.Status == "success";
        bool conversionPrepared = state.PreparedConversion is not null;
        bool conversionSucceeded =
            state.ConversionStatus?.Status == "success";
        bool recoveryBlocked = _session.Recovery.HasBlock;
        _connect.IsEnabled = !state.Busy;
        _apiKey.IsEnabled =
            ready &&
            !recoveryBlocked &&
            !state.HasUnresolvedPaidDispatch;
        _generate.IsEnabled =
            ready &&
            !recoveryBlocked &&
            state.CredentialStatus?.HasApiKey == true &&
            (!generationPrepared ||
             (state.CanDispatchPreparedGeneration &&
              (!state.GenerationDispatchAttempted ||
               state.GenerationRetryAllowed)));
        _generate.Content = !generationPrepared
            ? "Generate"
            : state.GenerationRetryAllowed
                ? "Retry generation (same UUID)"
                : state.GenerationRetryRequired
                    ? "Refresh before retry"
                : state.CanDispatchPreparedGeneration
                    ? "Send prepared generation"
                    : "Generation dispatched";
        _refreshGeneration.IsEnabled =
            ready &&
            (state.GenerationReceipt is not null ||
             state.GenerationDispatchAttempted);
        _convert.IsEnabled =
            ready &&
            !recoveryBlocked &&
            generationSucceeded &&
            (!conversionPrepared ||
             (state.CanDispatchPreparedConversion &&
              (!state.ConversionDispatchAttempted ||
               state.ConversionRetryAllowed)));
        _convert.Content = !conversionPrepared
            ? "Convert to OBJ"
            : state.ConversionRetryAllowed
                ? "Retry conversion (same UUID)"
                : state.ConversionRetryRequired
                    ? "Refresh before retry"
                : state.CanDispatchPreparedConversion
                    ? "Send prepared conversion"
                    : "Conversion dispatched";
        _refreshConversion.IsEnabled =
            ready &&
            (state.ConversionReceipt is not null ||
             state.ConversionDispatchAttempted);
        _import.IsEnabled =
            ready &&
            !recoveryBlocked &&
            conversionSucceeded &&
            (state.PreparedImport is null ||
             state.CanDispatchPreparedImport);
        _import.Content = state.PreparedImport is null
            ? "Import into Revit"
            : state.ImportRetryRequired
                ? "Retry import (same UUID)"
                : state.CanDispatchPreparedImport
                    ? "Import prepared object"
                    : "Imported";
        _reset.IsEnabled =
            ready &&
            !recoveryBlocked &&
            !state.HasUnresolvedDispatch;
        _prompt.IsEnabled = ready && !generationPrepared;
        _faceLimit.IsEnabled = ready && !generationPrepared;
        _withMaterials.IsEnabled = ready && !generationPrepared;
        _name.IsEnabled = ready && state.PreparedImport is null;
        _importMode.IsEnabled = ready && state.PreparedImport is null;
        _applyMaterials.IsEnabled =
            ready && state.PreparedImport is null;
    }

    private void UpdateRecoveryControls(Tripo.HostUi.TripoPanelState state)
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            _session.Recovery;
        List<string> lines = [];
        if (!recovery.HasBlock)
        {
            lines.Add("No recovered operation IDs require reconciliation.");
        }
        else
        {
            lines.Add(
                "Recovered IDs block new workflows and API-key changes until " +
                "they are checked and explicitly acknowledged.");
            foreach (Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded in
                     recovery.Hints)
            {
                lines.Add(
                    $"Document session: {loaded.Hint.DocumentSessionId}");
                if (loaded.Hint.Generation is not null)
                {
                    lines.Add(
                        "Generation UUID: " +
                        loaded.Hint.Generation.OperationId);
                }

                if (loaded.Hint.Conversion is not null)
                {
                    lines.Add(
                        "Conversion UUID: " +
                        loaded.Hint.Conversion.OperationId);
                }

                if (loaded.Hint.Import is not null)
                {
                    lines.Add(
                        "Import UUID: " +
                        loaded.Hint.Import.OperationId);
                }
            }

            foreach (Tripo.HostUi.TripoPanelRecoveryIssue issue in
                     recovery.Issues)
            {
                lines.Add(
                    $"Blocked recovery file {issue.FileName}: " +
                    $"{issue.Code}. {issue.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_recoveryInspection))
        {
            lines.Add(_recoveryInspection);
        }

        _recoveryStatus.Text = string.Join(Environment.NewLine, lines);
        _checkRecovery.IsEnabled =
            state.Connected &&
            !state.Busy &&
            recovery.Hints.Count > 0;
        _acknowledgeRecovery.IsEnabled =
            !state.Busy &&
            recovery.Hints.Count > 0 &&
            recovery.Issues.Count == 0;
        _displayedRecoveryToken = recovery.PresentationToken;
    }

    private int ParseFaceLimit()
    {
        if (!int.TryParse(
                _faceLimit.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int faceLimit))
        {
            throw new ArgumentException(
                "Face limit must be a whole number from 500 through 200000.");
        }

        return faceLimit;
    }

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

    private static Button ActionButton(string content) =>
        new()
        {
            Content = content,
            Margin = new Thickness(0, 4, 8, 4),
            Padding = new Thickness(10, 5, 10, 5),
        };

    private static TextBlock StatusBlock() =>
        new()
        {
            Margin = new Thickness(0, 5, 0, 5),
            TextWrapping = TextWrapping.Wrap,
        };

    private static TextBox OperationStatusBox() =>
        new()
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(0, 5, 0, 5),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
        };

    private static TextBlock SectionHeading(string text) =>
        new()
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6),
            Text = text,
        };

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
        };
        foreach (Button button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static Grid FieldRow(string label, Control control)
    {
        Grid grid = new()
        {
            Margin = new Thickness(0, 4, 0, 4),
        };
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock caption = new()
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(control, 1);
        grid.Children.Add(caption);
        grid.Children.Add(control);
        return grid;
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

        public RevitApiKeyDialog()
        {
            Title = "Tripo API key";
            Width = 500;
            Height = 360;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Button save = ActionButton("Save");
            Button cancel = ActionButton("Cancel");
            save.Click += (_, _) => DialogResult = true;
            cancel.Click += (_, _) => DialogResult = false;
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = ApiKeyInstructions,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 12),
                    },
                    _password,
                    _persist,
                    ButtonRow(save, cancel),
                },
            };
        }

        public bool Persist => _persist.IsChecked == true;

        public string TakeAndClearApiKey()
        {
            string value = _password.Password;
            _password.Clear();
            return value;
        }

        public void ClearApiKey() => _password.Clear();
    }

    private sealed class RevitRecoveryAcknowledgeDialog : Window
    {
        private readonly TextBox _confirmation = new();

        public RevitRecoveryAcknowledgeDialog()
        {
            Title = "Acknowledge recovered Tripo IDs";
            Width = 520;
            Height = 260;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Button acknowledge = ActionButton("Acknowledge");
            Button cancel = ActionButton("Cancel");
            acknowledge.Click += (_, _) =>
            {
                if (!string.Equals(
                        _confirmation.Text,
                        "RECONCILED",
                        StringComparison.Ordinal))
                {
                    MessageBox.Show(
                        this,
                        "Type RECONCILED exactly after checking every recovered " +
                        "UUID against the local journal or provider history.",
                        "Recovery not acknowledged",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                DialogResult = true;
            };
            cancel.Click += (_, _) => DialogResult = false;
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text =
                            "Archiving these recovery hints unlocks new " +
                            "operation UUIDs. First check every paid UUID and " +
                            "manually reconcile any import. Then type " +
                            "RECONCILED.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 12),
                    },
                    _confirmation,
                    ButtonRow(acknowledge, cancel),
                },
            };
        }

        public string Confirmation => _confirmation.Text;
    }
}

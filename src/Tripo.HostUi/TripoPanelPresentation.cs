using System.Globalization;

namespace Tripo.HostUi;

public enum TripoPanelNoticeKind
{
    None,
    Busy,
    Success,
    Warning,
    Error,
}

public sealed class TripoPanelPresentation
{
    private enum MaterialEvidenceKind
    {
        None,
        Missing,
        MissingTextures,
        UnavailableOnMeshReplay,
    }

    private const int MaximumPromptLength = 1024;
    private const int MaximumObjectNameLength = 128;
    private const int MinimumFaceLimit = 500;
    private const int MaximumFaceLimit = 200_000;

    private TripoPanelPresentation()
    {
    }

    public string DocumentStatus { get; private init; } = string.Empty;

    public string DocumentSessionId { get; private init; } = string.Empty;

    public string CredentialStatus { get; private init; } = string.Empty;

    public string RecoveryHeader { get; private init; } = string.Empty;

    public string RecoveryDetails { get; private init; } = string.Empty;

    public bool RecoveryHasBlock { get; private init; }

    public bool RecoveryReviewed { get; private init; }

    public string RecoveryToken { get; private init; } = string.Empty;

    public string? LatestPreparedOperationId { get; private init; }

    public string GenerationOperationId { get; private init; } = string.Empty;

    public string GenerationTaskId { get; private init; } = string.Empty;

    public string GenerationStatus { get; private init; } = string.Empty;

    public string GenerationDiagnostic { get; private init; } = string.Empty;

    public bool GenerationDiagnosticVisible { get; private init; }

    public int? GenerationProgress { get; private init; }

    public string ConversionOperationId { get; private init; } = string.Empty;

    public string ConversionTaskId { get; private init; } = string.Empty;

    public string ConversionStatus { get; private init; } = string.Empty;

    public string ConversionDiagnostic { get; private init; } = string.Empty;

    public bool ConversionDiagnosticVisible { get; private init; }

    public int? ConversionProgress { get; private init; }

    public string ImportOperationId { get; private init; } = string.Empty;

    public string ImportStatus { get; private init; } = string.Empty;

    public string ImportReceiptDetails { get; private init; } = string.Empty;

    public bool ImportReceiptDetailsVisible { get; private init; }

    public string ResultStatus { get; private init; } = string.Empty;

    public TripoPanelNoticeKind ResultKind { get; private init; }

    public bool ResultVisible { get; private init; }

    public string PromptValidation { get; private init; } = string.Empty;

    public bool PromptValidationVisible { get; private init; }

    public string FaceLimitValidation { get; private init; } = string.Empty;

    public bool FaceLimitValidationVisible { get; private init; }

    public string ObjectNameValidation { get; private init; } = string.Empty;

    public bool ObjectNameValidationVisible { get; private init; }

    public bool ConnectEnabled { get; private init; }

    public string ConnectHelp { get; private init; } = string.Empty;

    public bool ApiKeyEnabled { get; private init; }

    public string ApiKeyHelp { get; private init; } = string.Empty;

    public bool ClearApiKeyEnabled { get; private init; }

    public string ClearApiKeyHelp { get; private init; } = string.Empty;

    public bool CheckRecoveryEnabled { get; private init; }

    public string CheckRecoveryHelp { get; private init; } = string.Empty;

    public bool AcknowledgeRecoveryEnabled { get; private init; }

    public string AcknowledgeRecoveryHelp { get; private init; } =
        string.Empty;

    public bool GenerateEnabled { get; private init; }

    public string GenerateText { get; private init; } = string.Empty;

    public string GenerateHelp { get; private init; } = string.Empty;

    public bool RefreshGenerationEnabled { get; private init; }

    public string RefreshGenerationHelp { get; private init; } =
        string.Empty;

    public bool ConvertEnabled { get; private init; }

    public string ConvertText { get; private init; } = string.Empty;

    public string ConvertHelp { get; private init; } = string.Empty;

    public bool RefreshConversionEnabled { get; private init; }

    public string RefreshConversionHelp { get; private init; } =
        string.Empty;

    public bool ImportEnabled { get; private init; }

    public string ImportText { get; private init; } = string.Empty;

    public string ImportHelp { get; private init; } = string.Empty;

    public bool ResetEnabled { get; private init; }

    public string ResetHelp { get; private init; } = string.Empty;

    public bool PrimaryActionEnabled { get; private init; }

    public string PrimaryActionText { get; private init; } = string.Empty;

    public string PrimaryActionHelp { get; private init; } = string.Empty;

    public string PipelineStageLabel { get; private init; } = string.Empty;

    public bool PipelineProgressVisible { get; private init; }

    public int? PipelineProgress { get; private init; }

    public bool PromptEnabled { get; private init; }

    public bool FaceLimitEnabled { get; private init; }

    public bool WithMaterialsEnabled { get; private init; }

    public bool NameEnabled { get; private init; }

    public bool ImportModeEnabled { get; private init; }

    public bool ApplyMaterialsEnabled { get; private init; }

    public static TripoPanelPresentation Create(
        TripoPanelState state,
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection,
        bool recoveryReviewed,
        string? prompt,
        string? faceLimitText,
        string? objectName,
        string hostDisplayName,
        string? currentAction,
        bool generationRefreshFailed = false,
        bool conversionRefreshFailed = false,
        bool generationJournalUpdatedOnFailedRefresh = false,
        bool conversionJournalUpdatedOnFailedRefresh = false,
        bool recoveryInspectionHadFailures = false,
        string? pipelineStageLabel = null,
        int? pipelineProgressOverride = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(recovery);
        if (string.IsNullOrWhiteSpace(hostDisplayName))
        {
            throw new ArgumentException(
                "A host display name is required.",
                nameof(hostDisplayName));
        }

        string host = hostDisplayName.Trim();
        bool uiBusy =
            state.Busy ||
            !string.IsNullOrWhiteSpace(currentAction);
        bool ready = state.Connected && !uiBusy;
        bool generationPrepared = state.PreparedGeneration is not null;
        bool generationSucceeded =
            state.HasSuccessfulGeneration;
        bool conversionPrepared = state.PreparedConversion is not null;
        bool conversionSucceeded =
            state.HasSuccessfulConversion;
        bool importReceiptConfirmed = IsConfirmedImport(state);
        bool canPrepareImport =
            state.PreparedImport is null || importReceiptConfirmed;
        bool recoveryBlocked = recovery.HasBlock;
        bool remoteTaskNeedsReview =
            state.HasNonTerminalRemoteTask;
        bool credentialRecoveryRequired =
            state.RequiresCredentialRecovery;
        bool environmentOverridesPanelKey =
            state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            };
        bool reviewedCurrentRecovery =
            recoveryBlocked && recoveryReviewed;
        bool promptValid = IsPromptValid(prompt);
        bool faceLimitValid = TryParseFaceLimit(faceLimitText, out _);
        bool objectNameValid = IsObjectNameValid(objectName);
        string importTarget =
            state.Context?.DocumentTitle ?? host;
        string? generationTaskId =
            state.DurableGenerationTaskId;
        string? conversionTaskId =
            state.DurableConversionTaskId;
        (string resultStatus, TripoPanelNoticeKind resultKind) =
            BuildResult(state, currentAction, host);
        (string pipelineLabel, int? pipelineProgress, bool pipelineVisible) =
            BuildPipelineProgress(
                state,
                currentAction,
                pipelineStageLabel,
                pipelineProgressOverride,
                generationRefreshFailed,
                conversionRefreshFailed);

        // Compute stage enablement first so primary action can reuse it.
        bool generateEnabled =
            ready &&
            !recoveryBlocked &&
            state.CredentialStatus?.HasApiKey == true &&
            faceLimitValid &&
            ((!generationPrepared && promptValid) ||
             (generationPrepared &&
              state.CanDispatchPreparedGeneration &&
              (!state.GenerationDispatchAttempted ||
               state.GenerationRetryAllowed)));
        bool convertEnabled =
            ready &&
            !recoveryBlocked &&
            state.CredentialStatus?.HasApiKey == true &&
            faceLimitValid &&
            generationSucceeded &&
            (!conversionPrepared ||
             (state.CanDispatchPreparedConversion &&
              (!state.ConversionDispatchAttempted ||
               state.ConversionRetryAllowed)));
        bool importEnabled =
            ready &&
            !recoveryBlocked &&
            state.CredentialStatus?.HasApiKey == true &&
            conversionSucceeded &&
            (canPrepareImport
                ? objectNameValid
                : state.CanDispatchPreparedImport);

        return new TripoPanelPresentation
        {
            DocumentStatus = state.Context is null
                ? "Document: not connected"
                : $"Document: {state.Context.DocumentTitle}",
            DocumentSessionId =
                state.Context?.DocumentSessionId ?? "Not connected",
            CredentialStatus = FormatCredentialStatus(state),
            RecoveryHeader = !recoveryBlocked
                ? "Recovery · Clear"
                : recovery.Issues.Count > 0
                    ? "Recovery · Storage issue"
                    : reviewedCurrentRecovery
                        ? recoveryInspectionHadFailures
                            ? "Recovery · Manual review required"
                            : "Recovery · Inspected"
                        : "Recovery · Action required",
            RecoveryDetails = BuildRecoveryDetails(
                recovery,
                recoveryInspection,
                reviewedCurrentRecovery,
                recoveryInspectionHadFailures),
            RecoveryHasBlock = recoveryBlocked,
            RecoveryReviewed =
                reviewedCurrentRecovery &&
                !recoveryInspectionHadFailures,
            RecoveryToken = recovery.PresentationToken,
            LatestPreparedOperationId =
                state.PreparedImport?.OperationId ??
                state.PreparedConversion?.OperationId ??
                state.PreparedGeneration?.OperationId,
            GenerationOperationId =
                state.PreparedGeneration?.OperationId ?? "Not prepared",
            GenerationTaskId = generationTaskId ?? "Not created",
            GenerationStatus = WithRefreshEvidence(
                StageStatus(
                    generationTaskId,
                    state.GenerationOperationStatus,
                    state.GenerationStatus),
                generationRefreshFailed,
                generationJournalUpdatedOnFailedRefresh),
            GenerationDiagnostic = WithRefreshDiagnostic(
                StageDiagnostic(
                    state.GenerationOperationStatus,
                    state.GenerationStatus),
                generationRefreshFailed,
                generationJournalUpdatedOnFailedRefresh),
            GenerationDiagnosticVisible =
                state.GenerationOperationStatus is not null ||
                state.GenerationStatus is not null,
            GenerationProgress = VisibleTaskProgress(
                state.GenerationStatus,
                generationTaskId,
                generationRefreshFailed),
            ConversionOperationId =
                state.PreparedConversion?.OperationId ?? "Not prepared",
            ConversionTaskId = conversionTaskId ?? "Not created",
            ConversionStatus = WithRefreshEvidence(
                StageStatus(
                    conversionTaskId,
                    state.ConversionOperationStatus,
                    state.ConversionStatus),
                conversionRefreshFailed,
                conversionJournalUpdatedOnFailedRefresh),
            ConversionDiagnostic = WithRefreshDiagnostic(
                StageDiagnostic(
                    state.ConversionOperationStatus,
                    state.ConversionStatus),
                conversionRefreshFailed,
                conversionJournalUpdatedOnFailedRefresh),
            ConversionDiagnosticVisible =
                state.ConversionOperationStatus is not null ||
                state.ConversionStatus is not null,
            ConversionProgress = VisibleTaskProgress(
                state.ConversionStatus,
                conversionTaskId,
                conversionRefreshFailed),
            ImportOperationId =
                state.PreparedImport?.OperationId ?? "Not prepared",
            ImportStatus = BuildImportStatus(state, host),
            ImportReceiptDetails = BuildImportReceiptDetails(state),
            ImportReceiptDetailsVisible =
                state.ImportReceipt is not null ||
                state.CompletedImports.Count > 0,
            ResultStatus = WithNoticePrefix(
                resultStatus,
                resultKind),
            ResultKind = resultKind,
            ResultVisible =
                resultKind != TripoPanelNoticeKind.None,
            PromptValidation = BuildPromptValidation(prompt),
            PromptValidationVisible = !promptValid,
            FaceLimitValidation = BuildFaceLimitValidation(faceLimitText),
            FaceLimitValidationVisible = !faceLimitValid,
            ObjectNameValidation = BuildObjectNameValidation(objectName),
            ObjectNameValidationVisible = !objectNameValid,
            ConnectEnabled = !uiBusy,
            ConnectHelp = uiBusy
                ? "Wait for the current panel operation to finish."
                : state.Connected
                    ? $"Refresh the exact active {host} document context."
                    : $"Connect to the exact active {host} document.",
            ApiKeyEnabled =
                ready &&
                !recoveryBlocked &&
                !environmentOverridesPanelKey,
            ApiKeyHelp = BuildApiKeyHelp(
                state,
                uiBusy,
                recoveryBlocked,
                credentialRecoveryRequired,
                clearStoredKey: false),
            ClearApiKeyEnabled =
                ready &&
                !recoveryBlocked &&
                !credentialRecoveryRequired &&
                state.CredentialStatus?.StoredKeyPresenceKnown == true &&
                state.CredentialStatus.StoredKeyPresent &&
                state.CredentialStatus.CanClearStoredKey,
            ClearApiKeyHelp = BuildApiKeyHelp(
                state,
                uiBusy,
                recoveryBlocked,
                credentialRecoveryRequired,
                clearStoredKey: true),
            CheckRecoveryEnabled =
                state.Connected &&
                !uiBusy &&
                recovery.Hints.Count > 0,
            CheckRecoveryHelp = BuildCheckRecoveryHelp(
                state,
                uiBusy,
                recovery),
            AcknowledgeRecoveryEnabled =
                !uiBusy &&
                recovery.Hints.Count > 0 &&
                recovery.Issues.Count == 0 &&
                reviewedCurrentRecovery,
            AcknowledgeRecoveryHelp = BuildAcknowledgeRecoveryHelp(
                state,
                uiBusy,
                recovery,
                reviewedCurrentRecovery,
                recoveryInspectionHadFailures),
            GenerateEnabled = generateEnabled,
            GenerateText = !generationPrepared
                ? "Generate"
                : state.GenerationRetryAllowed
                    ? "Retry same UUID"
                    : state.GenerationRetryRequired
                        ? "Refresh before retry"
                        : state.CanDispatchPreparedGeneration
                            ? "Send prepared"
                            : "Request sent",
            GenerateHelp = BuildGenerateHelp(
                state,
                uiBusy,
                recoveryBlocked,
                promptValid,
                faceLimitValid),
            RefreshGenerationEnabled =
                ready &&
                (state.GenerationReceipt is not null ||
                 state.GenerationDispatchAttempted) &&
                (state.CredentialStatus?.HasApiKey == true ||
                 CanInspectAmbiguousGenerationLocally(state)),
            RefreshGenerationHelp = BuildRefreshHelp(
                state,
                uiBusy,
                state.GenerationReceipt is not null ||
                state.GenerationDispatchAttempted,
                "generation",
                CanInspectAmbiguousGenerationLocally(state)),
            ConvertEnabled = convertEnabled,
            ConvertText = !conversionPrepared
                ? "Convert to OBJ"
                : state.ConversionRetryAllowed
                    ? "Retry same UUID"
                    : state.ConversionRetryRequired
                        ? "Refresh before retry"
                        : state.CanDispatchPreparedConversion
                            ? "Send prepared"
                            : "Request sent",
            ConvertHelp = BuildConvertHelp(
                state,
                uiBusy,
                recoveryBlocked,
                generationSucceeded,
                faceLimitValid),
            RefreshConversionEnabled =
                ready &&
                (state.ConversionReceipt is not null ||
                 state.ConversionDispatchAttempted) &&
                (state.CredentialStatus?.HasApiKey == true ||
                 CanInspectAmbiguousConversionLocally(state)),
            RefreshConversionHelp = BuildRefreshHelp(
                state,
                uiBusy,
                state.ConversionReceipt is not null ||
                state.ConversionDispatchAttempted,
                "conversion",
                CanInspectAmbiguousConversionLocally(state)),
            ImportEnabled = importEnabled,
            ImportText = canPrepareImport
                ? importReceiptConfirmed
                    ? $"Import again into {importTarget}"
                    : $"Import into {importTarget}"
                : state.ImportRetryRequired
                    ? "Retry same UUID"
                    : state.CanDispatchPreparedImport
                        ? "Import prepared"
                        : "Review import receipt",
            ImportHelp = BuildImportHelp(
                state,
                uiBusy,
                recoveryBlocked,
                conversionSucceeded,
                objectNameValid,
                host),
            ResetEnabled =
                ready &&
                !recoveryBlocked &&
                state.HasWorkflowState &&
                !state.HasUnresolvedDispatch &&
                !remoteTaskNeedsReview &&
                (state.ImportReceipt is null ||
                 importReceiptConfirmed),
            ResetHelp = BuildResetHelp(
                state,
                uiBusy,
                recoveryBlocked,
                remoteTaskNeedsReview),
            PrimaryActionEnabled =
                generateEnabled || convertEnabled || importEnabled,
            PrimaryActionText = importReceiptConfirmed
                ? "Create again in Revit"
                : "Create in Revit",
            PrimaryActionHelp = BuildPrimaryActionHelp(
                state,
                uiBusy,
                recoveryBlocked,
                promptValid,
                faceLimitValid,
                objectNameValid,
                generateEnabled,
                convertEnabled,
                importEnabled,
                host),
            PipelineStageLabel = pipelineLabel,
            PipelineProgressVisible = pipelineVisible,
            PipelineProgress = pipelineProgress,
            PromptEnabled = ready && !generationPrepared,
            FaceLimitEnabled = ready && !generationPrepared,
            WithMaterialsEnabled = ready && !generationPrepared,
            NameEnabled =
                ready &&
                conversionSucceeded &&
                canPrepareImport,
            ImportModeEnabled =
                ready &&
                conversionSucceeded &&
                canPrepareImport,
            ApplyMaterialsEnabled =
                ready &&
                conversionSucceeded &&
                canPrepareImport &&
                state.PreparedConversion?.WithMaterials == true,
        };
    }

    public static bool TryParseFaceLimit(
        string? value,
        out int faceLimit) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out faceLimit) &&
        faceLimit is >= MinimumFaceLimit and <= MaximumFaceLimit;

    private static string BuildApiKeyHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool credentialRecoveryRequired,
        bool clearStoredKey)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Revit document first.";
        }

        if (recoveryBlocked)
        {
            return "Reconcile the recovered operation IDs before changing keys.";
        }

        if (!clearStoredKey &&
            state.CredentialStatus is
            {
                HasApiKey: true,
                Source: "environment",
            })
        {
            return "TRIPO_API_KEY from the environment overrides panel keys. " +
                   "Change that environment value outside Revit, then reconnect.";
        }

        if (state.HasUnresolvedDispatch && clearStoredKey)
        {
            return "Resolve every ambiguous dispatch before removing keys.";
        }

        if (state.HasNonTerminalRemoteTask && clearStoredKey)
        {
            return "Refresh every accepted remote task to a terminal status " +
                   "before removing the saved key.";
        }

        if (state.HasCredentialBoundWorkflow && clearStoredKey)
        {
            return "Finish or reconcile this account-bound workflow and " +
                   "explicitly reset it before removing the saved key.";
        }

        if (!clearStoredKey)
        {
            if (credentialRecoveryRequired)
            {
                return state.HasUnresolvedPaidDispatch
                    ? "Recovery override: restore the exact original API key " +
                      "because unresolved paid UUIDs are bound to it. The key " +
                      "remains session-only."
                    : "Recovery override: use a key for the same Tripo account. " +
                      "It remains session-only until the workflow is reconciled " +
                      "and explicitly reset.";
            }

            return "Configure or replace the session or OS-stored Tripo API key.";
        }

        Tripo.Bridge.HostControlCredentialStatusReceipt? credentials =
            state.CredentialStatus;
        if (credentials is null ||
            !credentials.StoredKeyPresenceKnown)
        {
            return "Saved-key presence is unknown; refresh the connection first.";
        }

        if (!credentials.StoredKeyPresent)
        {
            return "No OS-stored Tripo API key is present.";
        }

        if (!credentials.CanClearStoredKey)
        {
            return "The current credential backend cannot clear the saved key.";
        }

        return credentials.Source switch
        {
            "environment" =>
                "Remove the OS-stored key. The environment key remains effective.",
            "session" =>
                "Remove the OS-stored key. The current session key remains effective.",
            _ =>
                "Remove the OS-stored key and refresh the effective credential status.",
        };
    }

    private static string BuildCheckRecoveryHelp(
        TripoPanelState state,
        bool uiBusy,
        TripoPanelRecoveryLoadResult recovery)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the local sidecar before inspecting paid UUIDs.";
        }

        if (recovery.Hints.Count == 0)
        {
            return recovery.Issues.Count > 0
                ? "Invalid recovery files require manual inspection."
                : "No recovered operation IDs need inspection.";
        }

        return "Read local journal state for every recovered paid UUID; this " +
               "sends no paid request and performs no import.";
    }

    private static string BuildAcknowledgeRecoveryHelp(
        TripoPanelState state,
        bool uiBusy,
        TripoPanelRecoveryLoadResult recovery,
        bool reviewedCurrentRecovery,
        bool recoveryInspectionHadFailures)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (recovery.Hints.Count == 0)
        {
            return "No valid recovered hints are available to archive.";
        }

        if (recovery.Issues.Count > 0)
        {
            return "Invalid recovery files must be resolved manually first.";
        }

        if (!reviewedCurrentRecovery)
        {
            return "Inspect the current recovered-ID snapshot first.";
        }

        return recoveryInspectionHadFailures
            ? "Open the checklist and confirm manual reconciliation of every " +
              "failed inspection before archiving."
            : "Open the review checklist before archiving the inspected hints.";
    }

    private static string BuildRefreshHelp(
        TripoPanelState state,
        bool uiBusy,
        bool hasDispatchedOperation,
        string stage,
        bool canInspectAmbiguousJournal)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Revit document first.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return canInspectAmbiguousJournal
                ? $"Inspect the local {stage} journal first. A Tripo API key " +
                  "is required only after a durable remote task ID is recovered."
                : "Configure a Tripo API key before refreshing task status.";
        }

        return hasDispatchedOperation
            ? $"Read the current {stage} task state; this sends no paid request."
            : $"Dispatch the prepared {stage} operation before refreshing it.";
    }

    private static string BuildGenerateHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool promptValid,
        bool faceLimitValid)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Revit document first.";
        }

        if (recoveryBlocked)
        {
            return "Inspect and reconcile the recovered operation IDs first.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return "Configure a Tripo API key first.";
        }

        if (state.PreparedGeneration is null && !promptValid)
        {
            return "Enter a valid model description.";
        }

        if (!faceLimitValid)
        {
            return "Enter a face limit from 500 through 200000.";
        }

        if (state.GenerationRetryRequired &&
            !state.GenerationRetryAllowed)
        {
            return "Refresh the existing generation operation before retrying.";
        }

        if (state.HasDurableGenerationTask)
        {
            return "The generation request is already durable; refresh its status.";
        }

        return "Review the request summary and durable UUID before a request " +
               "that may consume Tripo credits.";
    }

    private static string BuildPrimaryActionHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool promptValid,
        bool faceLimitValid,
        bool objectNameValid,
        bool generateEnabled,
        bool convertEnabled,
        bool importEnabled,
        string host)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return $"Connect to the active {host} document first.";
        }

        if (recoveryBlocked)
        {
            return "Inspect and reconcile the recovered operation IDs first.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return "Configure a Tripo API key in Settings first.";
        }

        if (generateEnabled)
        {
            if (!promptValid)
            {
                return "Enter a valid model description.";
            }

            if (!faceLimitValid)
            {
                return "Enter a face limit from 500 through 200000 in Settings.";
            }

            return "Generate, convert, and import into the active document. " +
                   "This may consume Tripo credits.";
        }

        if (convertEnabled)
        {
            return "Continue the pipeline: convert the generated model to OBJ " +
                   "and import it. This may consume Tripo credits.";
        }

        if (importEnabled)
        {
            if (!objectNameValid)
            {
                return $"Enter a valid {host} object name in Settings.";
            }

            return $"Import the prepared OBJ into the active {host} document.";
        }

        if (state.HasNonTerminalRemoteTask)
        {
            return "A remote task is still running. Wait for Create to finish, " +
                   "or use Advanced refresh.";
        }

        return "Finish or clear the current workflow before creating again.";
    }

    private static (
        string Label,
        int? Progress,
        bool Visible) BuildPipelineProgress(
        TripoPanelState state,
        string? currentAction,
        string? pipelineStageLabel,
        int? pipelineProgressOverride,
        bool generationRefreshFailed,
        bool conversionRefreshFailed)
    {
        if (!string.IsNullOrWhiteSpace(pipelineStageLabel))
        {
            return (
                pipelineStageLabel.Trim(),
                pipelineProgressOverride,
                true);
        }

        if (!string.IsNullOrWhiteSpace(currentAction))
        {
            string action = currentAction.Trim();
            if (action.Contains("Generat", StringComparison.OrdinalIgnoreCase))
            {
                int? mapped = MapVisibleProgress(
                    VisibleTaskProgress(
                        state.GenerationStatus,
                        state.DurableGenerationTaskId,
                        generationRefreshFailed),
                    0,
                    33);
                return ("Generating…", mapped ?? 0, true);
            }

            if (action.Contains("Convert", StringComparison.OrdinalIgnoreCase))
            {
                int? mapped = MapVisibleProgress(
                    VisibleTaskProgress(
                        state.ConversionStatus,
                        state.DurableConversionTaskId,
                        conversionRefreshFailed),
                    34,
                    66);
                return ("Converting…", mapped ?? 34, true);
            }

            if (action.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Place", StringComparison.OrdinalIgnoreCase))
            {
                return (action, 85, true);
            }

            return (action, null, true);
        }

        if (state.HasSuccessfulConversion &&
            state.HasConfirmedImportReceipt)
        {
            return ("Done", 100, false);
        }

        if (state.HasDurableConversionTask &&
            !state.HasSuccessfulConversion &&
            VisibleTaskProgress(
                state.ConversionStatus,
                state.DurableConversionTaskId,
                conversionRefreshFailed) is int convertPct)
        {
            return (
                "Converting…",
                MapVisibleProgress(convertPct, 34, 66),
                true);
        }

        if (state.HasDurableGenerationTask &&
            !state.HasSuccessfulGeneration &&
            VisibleTaskProgress(
                state.GenerationStatus,
                state.DurableGenerationTaskId,
                generationRefreshFailed) is int genPct)
        {
            return (
                "Generating…",
                MapVisibleProgress(genPct, 0, 33),
                true);
        }

        return (string.Empty, null, false);
    }

    private static int? MapVisibleProgress(
        int? providerProgress,
        int stageStart,
        int stageEnd)
    {
        if (providerProgress is null)
        {
            return null;
        }

        int span = Math.Max(1, stageEnd - stageStart);
        return stageStart + (providerProgress.Value * span / 100);
    }

    private static string BuildConvertHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool generationSucceeded,
        bool faceLimitValid)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Revit document first.";
        }

        if (recoveryBlocked)
        {
            return "Inspect and reconcile the recovered operation IDs first.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return "Configure a Tripo API key first.";
        }

        if (!generationSucceeded)
        {
            return "Wait for generation to succeed, then refresh its status.";
        }

        if (!faceLimitValid)
        {
            return "The prepared face limit is invalid.";
        }

        if (state.ConversionRetryRequired &&
            !state.ConversionRetryAllowed)
        {
            return "Refresh the existing conversion operation before retrying.";
        }

        if (state.HasDurableConversionTask)
        {
            return "The conversion request is already durable; refresh its status.";
        }

        return "Review the source task and durable UUID before a request " +
               "that may consume Tripo credits.";
    }

    private static string BuildImportHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool conversionSucceeded,
        bool objectNameValid,
        string host)
    {
        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return $"Connect to the active {host} document first.";
        }

        if (recoveryBlocked)
        {
            return "Inspect and reconcile the recovered operation IDs first.";
        }

        if (state.CredentialStatus?.HasApiKey != true)
        {
            return "Configure a Tripo API key first.";
        }

        if (!conversionSucceeded)
        {
            return "Wait for OBJ conversion to succeed, then refresh its status.";
        }

        if (!objectNameValid)
        {
            return $"Enter a valid {host} object name.";
        }

        if (state.ImportReceipt is not null)
        {
            return state.HasConfirmedImportReceipt
                ? $"Create a fresh import UUID for the active {host} document; " +
                  "the confirmed receipt remains in this workflow's import history."
                : "The observed import receipt does not confirm the prepared " +
                  "operation. Inspect it, then retry the same UUID.";
        }

        return $"Import the prepared OBJ into the active {host} document.";
    }

    private static string BuildResetHelp(
        TripoPanelState state,
        bool uiBusy,
        bool recoveryBlocked,
        bool remoteTaskNeedsReview)
    {
        if (!state.HasWorkflowState)
        {
            return "There is no panel workflow to clear.";
        }

        if (uiBusy)
        {
            return "Wait for the current panel operation to finish.";
        }

        if (!state.Connected)
        {
            return "Connect to the active Revit document first.";
        }

        if (recoveryBlocked)
        {
            return "Inspect and reconcile the recovered operation IDs first.";
        }

        if (state.HasUnresolvedDispatch)
        {
            if (state.ImportReceipt is not null &&
                !state.HasConfirmedImportReceipt)
            {
                return "The observed import receipt requires inspection. " +
                       "Preserve its UUID and retry the same import after review.";
            }

            return "Preserve the current UUID and resolve the ambiguous dispatch first.";
        }

        if (remoteTaskNeedsReview)
        {
            return "Refresh every accepted remote task until it reaches a terminal status.";
        }

        return "Clear this resolved panel workflow. Remote tasks, journal " +
               "evidence, and imported Revit objects remain unchanged.";
    }

    private static bool IsPromptValid(string? prompt) =>
        !string.IsNullOrWhiteSpace(prompt) &&
        prompt.Length <= MaximumPromptLength;

    private static bool IsObjectNameValid(string? objectName) =>
        !string.IsNullOrWhiteSpace(objectName) &&
        objectName.Length <= MaximumObjectNameLength;

    private static string BuildPromptValidation(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Enter a model description before generating.";
        }

        return prompt.Length > MaximumPromptLength
            ? $"Keep the description to {MaximumPromptLength} characters or fewer."
            : string.Empty;
    }

    private static string BuildFaceLimitValidation(string? faceLimitText) =>
        TryParseFaceLimit(faceLimitText, out _)
            ? string.Empty
            : $"Enter a whole number from {MinimumFaceLimit} through " +
              $"{MaximumFaceLimit}.";

    private static string BuildObjectNameValidation(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return "Enter a Revit object name before importing.";
        }

        return objectName.Length > MaximumObjectNameLength
            ? $"Keep the object name to {MaximumObjectNameLength} characters or fewer."
            : string.Empty;
    }

    private static (string Status, TripoPanelNoticeKind Kind) BuildResult(
        TripoPanelState state,
        string? currentAction,
        string host)
    {
        if (!string.IsNullOrWhiteSpace(currentAction))
        {
            return (
                currentAction.Trim(),
                TripoPanelNoticeKind.Busy);
        }

        if (state.Busy)
        {
            return ("Working…", TripoPanelNoticeKind.Busy);
        }

        if (state.LastError is not null)
        {
            return (
                BoundText(state.LastError),
                TripoPanelNoticeKind.Error);
        }

        if (state.ImportReceipt is not null)
        {
            bool confirmed = IsConfirmedImport(state);
            if (!confirmed)
            {
                return (
                    "Import receipt requires inspection · See details",
                    TripoPanelNoticeKind.Warning);
            }

            List<string> warnings = [];
            int rejected =
                state.ImportReceipt.HostReceipt.RejectedTriangleCount;
            if (rejected > 0)
            {
                warnings.Add($"{rejected} triangles rejected");
            }

            MaterialEvidenceKind materialEvidence =
                GetMaterialEvidence(state);
            if (materialEvidence == MaterialEvidenceKind.Missing)
            {
                warnings.Add("no applied materials reported");
            }
            else if (materialEvidence ==
                     MaterialEvidenceKind.MissingTextures)
            {
                warnings.Add("materials applied, but no textures reported");
            }
            else if (materialEvidence ==
                     MaterialEvidenceKind.UnavailableOnMeshReplay)
            {
                warnings.Add("prior mesh material counts unavailable");
            }

            string status = FriendlyImportResult(
                state.ImportReceipt.HostReceipt.TransactionStatus,
                host);
            if (warnings.Count > 0)
            {
                status += " · " + string.Join(" · ", warnings) +
                          " · See receipt";
            }

            return (
                status,
                warnings.Count == 0
                    ? TripoPanelNoticeKind.Success
                    : TripoPanelNoticeKind.Warning);
        }

        return (string.Empty, TripoPanelNoticeKind.None);
    }

    private static string FormatCredentialStatus(TripoPanelState state)
    {
        Tripo.Bridge.HostControlCredentialStatusReceipt? credentials =
            state.CredentialStatus;
        if (credentials is null)
        {
            return "API key: status unknown";
        }

        string source = credentials.Source switch
        {
            "environment" => "environment variable",
            "session" => "Revit session",
            "store" => "OS credential store",
            "none" => "none",
            _ => "unrecognized source",
        };
        string status = credentials.HasApiKey
            ? $"API key: configured · Source: {source}"
            : $"API key: not configured · Source: {source}";
        if (credentials.UsesWeakerFileFallback)
        {
            status += " · Private-file fallback";
        }

        if (!credentials.StoredKeyPresenceKnown)
        {
            status += " · Saved-key presence unknown";
        }

        return status;
    }

    private static string WithNoticePrefix(
        string status,
        TripoPanelNoticeKind kind) =>
        kind switch
        {
            TripoPanelNoticeKind.Busy =>
                "In progress · " + status,
            TripoPanelNoticeKind.Success =>
                "Success · " + status,
            TripoPanelNoticeKind.Warning =>
                "Warning · " + status,
            TripoPanelNoticeKind.Error =>
                "Error · " + status,
            _ => status,
        };

    private static string BuildRecoveryDetails(
        TripoPanelRecoveryLoadResult recovery,
        string? recoveryInspection,
        bool recoveryReviewed,
        bool recoveryInspectionHadFailures)
    {
        List<string> lines = [];
        if (!recovery.HasBlock)
        {
            lines.Add("No recovered operation IDs require reconciliation.");
        }
        else
        {
            lines.Add(
                "Recovered IDs block new workflows and API-key changes.");
            lines.Add(
                recoveryReviewed
                    ? recoveryInspectionHadFailures
                        ? "Automated inspection was incomplete. Manually reconcile " +
                          "every failed paid UUID and import before archiving."
                        : "The current recovery snapshot was inspected. Confirm " +
                          "the review checklist before archiving it."
                    : "Inspect every paid ID before acknowledgement. Manually " +
                      "reconcile imports and any failed inspection.");
            foreach (LoadedTripoPanelRecoveryHint loaded in recovery.Hints)
            {
                lines.Add($"Recovery file: {loaded.FileName}");
                lines.Add(FormatRecoveryHint(loaded.Hint));
            }

            foreach (TripoPanelRecoveryIssue issue in recovery.Issues)
            {
                lines.Add(
                    $"Blocked recovery file {issue.FileName}: " +
                    $"{issue.Code}. {BoundText(issue.Message)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(recoveryInspection))
        {
            lines.Add("Inspection result:");
            lines.Add(recoveryInspection.Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatRecoveryHint(
        TripoPanelRecoveryHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        List<string> lines = [];
        AddPaidRecoveryDetails(lines, "Generation", hint.Generation);
        AddPaidRecoveryDetails(lines, "Conversion", hint.Conversion);
        if (hint.Import is not null)
        {
            TripoPanelImportRecoveryHint import = hint.Import;
            lines.Add(
                "Import target document session: " +
                import.DocumentSessionId);
            lines.Add("Import UUID: " + import.OperationId);
            lines.Add(
                "Import conversion task: " +
                import.ConversionTaskId);
            lines.Add("Import object name: " + import.Name);
            lines.Add(
                "Requested import type: " +
                FormatRecoveredImportMode(
                    import.ImportMode,
                    hint.Host));
            lines.Add(
                "Apply baked materials requested: " +
                (import.ApplyMaterials ? "yes" : "no"));
            lines.Add(
                "Import dispatch attempted: " +
                import.DispatchAttempted
                    .ToString()
                    .ToLowerInvariant());
            lines.Add(
                "Confirmed import receipt known: " +
                import.ReceiptKnown
                    .ToString()
                    .ToLowerInvariant());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddPaidRecoveryDetails(
        List<string> lines,
        string stage,
        TripoPanelPaidRecoveryHint? paid)
    {
        if (paid is null)
        {
            return;
        }

        lines.Add($"{stage} UUID: {paid.OperationId}");
        lines.Add(
            $"{stage} dispatch attempted: " +
            paid.DispatchAttempted.ToString().ToLowerInvariant());
        lines.Add(
            $"{stage} task ID: " +
            (string.IsNullOrWhiteSpace(paid.TaskId)
                ? "not recorded"
                : paid.TaskId));
        lines.Add(
            $"{stage} journal state: " +
            (string.IsNullOrWhiteSpace(paid.JournalState)
                ? "not recorded"
                : paid.JournalState));
        lines.Add(
            $"{stage} task ID durable: " +
            paid.TaskIdDurable.ToString().ToLowerInvariant());
        lines.Add(
            $"{stage} creation can resume: " +
            paid.CanResumeCreation.ToString().ToLowerInvariant());
    }

    private static string FormatRecoveredImportMode(
        string importMode,
        string host) =>
        string.Equals(
            importMode,
            "native",
            StringComparison.Ordinal)
            ? host switch
            {
                "revit" => "native (resolves to Revit family)",
                "rhino" => "native (resolves to block instance)",
                _ => "native (host-resolved)",
            }
            : importMode;

    private static string StageStatus(
        string? taskId,
        Tripo.Bridge.HostControlOperationStatusReceipt? operation,
        Tripo.Bridge.HostControlTaskStatusReceipt? task)
    {
        if (task is not null &&
            !string.Equals(
                task.TaskId,
                taskId,
                StringComparison.Ordinal))
        {
            return "Evidence mismatch · The last task status does not belong " +
                   "to the durable journal task; refresh and inspect details.";
        }

        if (task is not null)
        {
            string status = "Last known · " +
                            FriendlyTaskStatus(task.Status);
            bool active =
                task.Status is "queued" or "running";
            if (active)
            {
                status += task.Progress is >= 0 and <= 100
                    ? $" · {task.Progress}%"
                    : " · Progress value needs inspection";
            }
            else if (task.Progress is < 0 or > 100)
            {
                status += " · Progress value needs inspection";
            }
            else if ((task.Status == "success" &&
                      task.Progress != 100) ||
                     ((task.Status is
                          "failed" or
                          "cancelled" or
                          "banned" or
                          "expired") &&
                      task.Progress == 100))
            {
                status += $" · Provider reported {task.Progress}%; see diagnostic";
            }
            if (task.CreditsConsumed.HasValue)
            {
                status += " · " +
                          task.CreditsConsumed.Value.ToString(
                              "0.###",
                              CultureInfo.InvariantCulture) +
                          " credits";
            }

            return status;
        }

        if (taskId is not null)
        {
            return "Task accepted · Refresh to check status";
        }

        return operation is null
            ? "Not started"
            : FriendlyOperationStatus(operation.State);
    }

    private static string StageDiagnostic(
        Tripo.Bridge.HostControlOperationStatusReceipt? operation,
        Tripo.Bridge.HostControlTaskStatusReceipt? task)
    {
        List<string> lines = [];
        if (operation is not null)
        {
            lines.Add($"Operation kind: {operation.Kind}");
            lines.Add($"Operation state: {operation.State}");
            lines.Add(
                $"Task ID durable: {operation.TaskIdDurable.ToString().ToLowerInvariant()}");
            lines.Add(
                "May have created remote task: " +
                operation.MayHaveCreatedRemoteTask
                    .ToString()
                    .ToLowerInvariant());
            lines.Add($"Next action: {BoundText(operation.NextAction)}");
            lines.Add($"Updated: {FormatTimestamp(operation.UpdatedAtUtc)}");
            if (!string.IsNullOrWhiteSpace(operation.FailureStage))
            {
                lines.Add($"Failure stage: {operation.FailureStage}");
            }

            if (!string.IsNullOrWhiteSpace(operation.FailureCode))
            {
                lines.Add($"Failure code: {operation.FailureCode}");
            }

            if (!string.IsNullOrWhiteSpace(operation.FailureMessage))
            {
                lines.Add(
                    $"Failure message: {BoundText(operation.FailureMessage)}");
            }
        }

        if (task is not null)
        {
            lines.Add($"Task type: {task.Type}");
            lines.Add($"Task status: {task.Status}");
            lines.Add($"Task progress reported: {task.Progress}%");
            if (task.CreditsConsumed.HasValue)
            {
                lines.Add(
                    "Credits consumed: " +
                    task.CreditsConsumed.Value.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture));
            }

            if (task.CreatedAt.HasValue)
            {
                lines.Add($"Created: {FormatTimestamp(task.CreatedAt.Value)}");
            }

            if (task.CompletedAt.HasValue)
            {
                lines.Add(
                    $"Completed: {FormatTimestamp(task.CompletedAt.Value)}");
            }

            if (task.ErrorCode.HasValue)
            {
                lines.Add($"Task error code: {task.ErrorCode.Value}");
            }

            if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                lines.Add(
                    $"Task error: {BoundText(task.ErrorMessage)}");
            }
        }

        return lines.Count == 0
            ? "No diagnostic state available."
            : string.Join(Environment.NewLine, lines);
    }

    private static string WithRefreshEvidence(
        string stageStatus,
        bool refreshFailed,
        bool journalUpdatedOnFailedRefresh) =>
        refreshFailed
            ? journalUpdatedOnFailedRefresh
                ? "Refresh did not complete · Current journal evidence was " +
                  "updated; task evidence may be prior: " + stageStatus
                : "Last refresh failed · Showing prior status: " + stageStatus
            : stageStatus;

    private static string WithRefreshDiagnostic(
        string diagnostic,
        bool refreshFailed,
        bool journalUpdatedOnFailedRefresh) =>
        refreshFailed
            ? journalUpdatedOnFailedRefresh
                ? "This refresh updated the operation journal but did not " +
                  "complete the remote task read. Operation evidence is " +
                  "current; task evidence, if present, is prior." +
                  Environment.NewLine +
                  diagnostic
                : "Last refresh failed; the evidence below is from the prior " +
                  "successful refresh." +
                  Environment.NewLine +
                  diagnostic
            : diagnostic;

    private static int? VisibleTaskProgress(
        Tripo.Bridge.HostControlTaskStatusReceipt? task,
        string? expectedTaskId,
        bool refreshFailed) =>
        !refreshFailed &&
        string.Equals(
            task?.TaskId,
            expectedTaskId,
            StringComparison.Ordinal) &&
        task?.Status is "queued" or "running" &&
        task.Progress is >= 0 and <= 100
            ? task.Progress
            : null;

    private static bool CanInspectAmbiguousGenerationLocally(
        TripoPanelState state) =>
        state.GenerationDispatchAttempted &&
        state.GenerationReceipt is null &&
        state.GenerationOperationStatus?.TaskIdDurable != true;

    private static bool CanInspectAmbiguousConversionLocally(
        TripoPanelState state) =>
        state.ConversionDispatchAttempted &&
        state.ConversionReceipt is null &&
        state.ConversionOperationStatus?.TaskIdDurable != true;

    private static string BuildImportStatus(
        TripoPanelState state,
        string host)
    {
        Tripo.Bridge.HostImportReceipt? receipt =
            state.ImportReceipt?.HostReceipt;
        if (receipt is null)
        {
            if (state.ImportDispatchAttempted)
            {
                return "Import outcome not yet confirmed";
            }

            return state.CompletedImports.Count > 0
                ? $"{state.CompletedImports.Count} completed " +
                  $"{(state.CompletedImports.Count == 1 ? "import" : "imports")} " +
                  $"· Ready for the active {host} document"
                : "Not imported";
        }

        if (!state.HasConfirmedImportReceipt)
        {
            return "Import receipt requires inspection";
        }

        string status =
            FriendlyImportResult(receipt.TransactionStatus, host) +
            $" · {receipt.TriangleCount.ToString("N0", CultureInfo.InvariantCulture)} " +
            "triangles";
        if (receipt.RejectedTriangleCount > 0)
        {
            status +=
                $" · {receipt.RejectedTriangleCount.ToString("N0", CultureInfo.InvariantCulture)} " +
                "rejected";
        }

        return status;
    }

    private static string BuildImportReceiptDetails(TripoPanelState state)
    {
        Tripo.Bridge.HostImportReceipt? receipt =
            state.ImportReceipt?.HostReceipt;
        if (receipt is null)
        {
            if (state.CompletedImports.Count == 0)
            {
                return "No import receipt is available.";
            }

            List<string> history = ["Completed imports:"];
            foreach (CompletedObjImport completed in state.CompletedImports)
            {
                string target = string.IsNullOrWhiteSpace(
                    completed.Prepared.TargetDocumentTitle)
                    ? completed.Prepared.DocumentSessionId
                    : completed.Prepared.TargetDocumentTitle;
                history.Add(
                    $"• {target} · {completed.Receipt.HostReceipt.CreatedId} · " +
                    completed.Prepared.OperationId);
            }

            return string.Join(Environment.NewLine, history);
        }

        string receiptConfirmation = "REQUIRES INSPECTION";
        if (state.PreparedImport is not null &&
            state.ImportReceipt is not null)
        {
            string? issue = TripoPanelState.GetImportReceiptIssue(
                state.PreparedImport,
                state.ImportReceipt);
            receiptConfirmation = issue is null
                ? "confirmed"
                : "REQUIRES INSPECTION · " + BoundText(issue);
        }

        List<string> lines =
        [
            $"Created object ID: {receipt.CreatedId}",
            $"Transaction: {receipt.TransactionStatus}",
            "Requested import type: " +
            FormatPreparedImportMode(state),
            $"Actual import type: {receipt.ImportMode}",
            "Receipt identity: " +
            (ImportReceiptIdentityMatches(state)
                ? "matches prepared operation"
                : "REQUIRES INSPECTION"),
            "Receipt confirmation: " + receiptConfirmation,
            $"Vertices: {receipt.VertexCount}",
            $"Triangles: {receipt.TriangleCount}",
            $"Rejected triangles: {receipt.RejectedTriangleCount}",
            $"Materials: {receipt.MaterialCount}",
            $"Textures: {receipt.TextureCount}",
        ];
        MaterialEvidenceKind materialEvidence =
            GetMaterialEvidence(state);
        if (materialEvidence == MaterialEvidenceKind.Missing)
        {
            lines.Add(
                "Material evidence: requested, but zero applied materials " +
                "were reported");
        }
        else if (materialEvidence ==
                 MaterialEvidenceKind.MissingTextures)
        {
            lines.Add(
                "Material evidence: materials applied, but zero textures " +
                "were reported");
        }
        else if (materialEvidence ==
                 MaterialEvidenceKind.UnavailableOnMeshReplay)
        {
            lines.Add(
                "Material evidence: unavailable for a replayed DirectShape");
        }

        if (!string.IsNullOrWhiteSpace(receipt.SavedFamilyPath))
        {
            lines.Add($"Saved family: {receipt.SavedFamilyPath}");
        }

        if (!string.IsNullOrWhiteSpace(receipt.PlacementNote))
        {
            lines.Add($"Placement: {BoundText(receipt.PlacementNote)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FriendlyOperationStatus(string state) =>
        state switch
        {
            "prepared" => "Prepared · Ready to send",
            "operation_in_progress" or
                "dispatching" or
                "image_upload_dispatching" or
                "image_generation_dispatching" =>
                "Request in progress · Wait, then refresh",
            "image_file_token_persisted" =>
                "Upload saved · Ready to resume",
            "task_id_persisted" =>
                "Task accepted · Refresh status",
            "outcome_unknown" =>
                "Outcome unknown · Do not resend",
            _ => "Status requires inspection",
        };

    private static string FriendlyTaskStatus(string status) =>
        status switch
        {
            "queued" => "Queued",
            "running" => "Running",
            "success" => "Success",
            "failed" => "Failed",
            "cancelled" => "Cancelled",
            "banned" => "Banned",
            "expired" => "Expired",
            _ => "Status requires inspection · Raw: " +
                 BoundText(status),
        };

    private static string FriendlyImportResult(
        string transactionStatus,
        string host) =>
        transactionStatus switch
        {
            "committed" => $"Imported into {host}",
            "already_exists" => $"Already imported in {host}",
            _ => "Import receipt available · See details",
        };

    private static bool IsConfirmedImport(TripoPanelState state) =>
        state.HasConfirmedImportReceipt;

    private static MaterialEvidenceKind GetMaterialEvidence(
        TripoPanelState state)
    {
        if (state.PreparedImport?.ApplyMaterials != true ||
            !state.HasConfirmedImportReceipt ||
            state.ImportReceipt is null)
        {
            return MaterialEvidenceKind.None;
        }

        Tripo.Bridge.HostImportReceipt receipt =
            state.ImportReceipt.HostReceipt;
        if (receipt.TransactionStatus == "already_exists" &&
            receipt.ImportMode == "mesh" &&
            (receipt.MaterialCount == 0 || receipt.TextureCount == 0))
        {
            return MaterialEvidenceKind.UnavailableOnMeshReplay;
        }

        if (receipt.MaterialCount == 0)
        {
            return MaterialEvidenceKind.Missing;
        }

        return receipt.TextureCount == 0
            ? MaterialEvidenceKind.MissingTextures
            : MaterialEvidenceKind.None;
    }

    private static bool ImportReceiptIdentityMatches(
        TripoPanelState state)
    {
        PreparedObjImport? prepared = state.PreparedImport;
        Tripo.Bridge.HostControlObjTaskImportReceipt? receipt =
            state.ImportReceipt;
        if (prepared is null || receipt is null || state.Context is null)
        {
            return false;
        }

        string expectedMode = prepared.ImportMode == "native"
            ? state.Context.Host switch
            {
                "rhino" => "instance",
                "revit" => "family",
                _ => "unsupported-native-mode",
            }
            : prepared.ImportMode;
        return string.Equals(
                   receipt.OperationId,
                   prepared.OperationId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.ConversionTaskId,
                   prepared.ConversionTaskId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.HostReceipt.Host,
                   state.Context.Host,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.HostReceipt.DocumentSessionId,
                   prepared.DocumentSessionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.HostReceipt.IdempotencyKey,
                   prepared.OperationId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   receipt.HostReceipt.ImportMode,
                   expectedMode,
                   StringComparison.Ordinal);
    }

    private static string FormatPreparedImportMode(
        TripoPanelState state)
    {
        string mode = state.PreparedImport?.ImportMode ?? "unknown";
        if (mode != "native")
        {
            return mode;
        }

        return state.Context?.Host switch
        {
            "revit" => "native → family",
            "rhino" => "native → instance",
            _ => "native → unresolved",
        };
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp
            .ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string BoundText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No details were provided.";
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}

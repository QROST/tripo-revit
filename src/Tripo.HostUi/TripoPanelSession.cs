namespace Tripo.HostUi;

public sealed record PreparedTextGeneration(
    string Prompt,
    int FaceLimit,
    bool WithMaterials,
    string OperationId,
    string Model = Tripo.Bridge.TripoModelCatalog.DefaultModelId);

public sealed record PreparedObjConversion(
    string SourceTaskId,
    int FaceLimit,
    bool WithMaterials,
    string OperationId);

public sealed record PreparedObjImport(
    string ConversionTaskId,
    string Name,
    string DocumentSessionId,
    string OperationId,
    string ImportMode,
    bool ApplyMaterials,
    string TargetDocumentTitle = "",
    string TargetHost = "",
    string PlacementMode = "origin",
    bool AssociateLevel = false);

public sealed record TripoPipelineProgress(
    string StageLabel,
    int? ProgressPercent);

public sealed record CompletedObjImport(
    PreparedObjImport Prepared,
    Tripo.Bridge.HostControlObjTaskImportReceipt Receipt);

public sealed record TripoPanelState(
    bool Connected,
    bool Busy,
    Tripo.Bridge.HostContextReceipt? Context,
    Tripo.Bridge.HostControlCredentialStatusReceipt? CredentialStatus,
    PreparedTextGeneration? PreparedGeneration,
    bool GenerationDispatchAttempted,
    Tripo.Bridge.HostControlTextTaskCreationReceipt? GenerationReceipt,
    Tripo.Bridge.HostControlOperationStatusReceipt? GenerationOperationStatus,
    Tripo.Bridge.HostControlTaskStatusReceipt? GenerationStatus,
    PreparedObjConversion? PreparedConversion,
    bool ConversionDispatchAttempted,
    Tripo.Bridge.HostControlObjConversionCreationReceipt? ConversionReceipt,
    Tripo.Bridge.HostControlOperationStatusReceipt? ConversionOperationStatus,
    Tripo.Bridge.HostControlTaskStatusReceipt? ConversionStatus,
    PreparedObjImport? PreparedImport,
    bool ImportDispatchAttempted,
    Tripo.Bridge.HostControlObjTaskImportReceipt? ImportReceipt,
    IReadOnlyList<CompletedObjImport> CompletedImports,
    string? LastError,
    IReadOnlyList<Tripo.Bridge.TripoModelInfo>? AvailableModels = null,
    string? ModelCatalogNote = null)
{
    private const int MaximumImportWarnings = 8;
    private const int MaximumImportWarningLength = 512;

    public static TripoPanelState Initial { get; } = new(
        Connected: false,
        Busy: false,
        Context: null,
        CredentialStatus: null,
        PreparedGeneration: null,
        GenerationDispatchAttempted: false,
        GenerationReceipt: null,
        GenerationOperationStatus: null,
        GenerationStatus: null,
        PreparedConversion: null,
        ConversionDispatchAttempted: false,
        ConversionReceipt: null,
        ConversionOperationStatus: null,
        ConversionStatus: null,
        PreparedImport: null,
        ImportDispatchAttempted: false,
        ImportReceipt: null,
        CompletedImports: [],
        LastError: null);

    public bool HasWorkflowState =>
        PreparedGeneration is not null ||
        GenerationDispatchAttempted ||
        GenerationReceipt is not null ||
        GenerationOperationStatus is not null ||
        GenerationStatus is not null ||
        PreparedConversion is not null ||
        ConversionDispatchAttempted ||
        ConversionReceipt is not null ||
        ConversionOperationStatus is not null ||
        ConversionStatus is not null ||
        PreparedImport is not null ||
        ImportDispatchAttempted ||
        ImportReceipt is not null;

    public bool HasUnresolvedPaidDispatch =>
        (GenerationDispatchAttempted &&
         DurableGenerationTaskId is null &&
         !IsDefinitiveRequestRejection(GenerationOperationStatus)) ||
        (ConversionDispatchAttempted &&
         DurableConversionTaskId is null &&
         !IsDefinitiveRequestRejection(ConversionOperationStatus));

    public bool HasUnresolvedDispatch =>
        HasUnresolvedPaidDispatch ||
        HasUnresolvedImportDispatch;

    public bool HasUnresolvedImportDispatch =>
        ImportDispatchAttempted && !HasConfirmedImportReceipt;

    public bool HasCredentialBoundWorkflow =>
        GenerationDispatchAttempted ||
        GenerationReceipt is not null ||
        GenerationOperationStatus is not null ||
        GenerationStatus is not null ||
        ConversionDispatchAttempted ||
        ConversionReceipt is not null ||
        ConversionOperationStatus is not null ||
        ConversionStatus is not null ||
        ImportDispatchAttempted ||
        ImportReceipt is not null;

    public bool RequiresCredentialRecovery =>
        HasCredentialBoundWorkflow;

    public bool HasConfirmedImportReceipt
    {
        get
        {
            if (PreparedImport is null ||
                ImportReceipt is null)
            {
                return false;
            }

            return GetImportReceiptIssue(
                       PreparedImport,
                       ImportReceipt) is null;
        }
    }

    internal static string? GetImportReceiptIssue(
        PreparedObjImport prepared,
        Tripo.Bridge.HostControlObjTaskImportReceipt receipt)
    {
        Tripo.Bridge.HostImportReceipt hostReceipt = receipt.HostReceipt;
        if (hostReceipt.TransactionStatus is not
                ("committed" or "already_exists"))
        {
            return "did not confirm a committed or existing host object";
        }

        string targetHost = string.IsNullOrWhiteSpace(prepared.TargetHost)
            ? hostReceipt.Host
            : prepared.TargetHost;
        string expectedMode = prepared.ImportMode == "native"
            ? targetHost switch
            {
                "rhino" => "instance",
                "revit" => "family",
                _ => "unsupported-native-mode",
            }
            : prepared.ImportMode;
        if (!string.Equals(
                receipt.OperationId,
                prepared.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ConversionTaskId,
                prepared.ConversionTaskId,
                StringComparison.Ordinal) ||
            !string.Equals(
                hostReceipt.Host,
                targetHost,
                StringComparison.Ordinal) ||
            !string.Equals(
                hostReceipt.DocumentSessionId,
                prepared.DocumentSessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                hostReceipt.IdempotencyKey,
                prepared.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                hostReceipt.ImportMode,
                expectedMode,
                StringComparison.Ordinal))
        {
            return "identity did not match the prepared operation";
        }

        if (string.IsNullOrWhiteSpace(hostReceipt.CreatedId) ||
            hostReceipt.CreatedId.Length > 2048)
        {
            return "reported an invalid created-object ID";
        }

        if (hostReceipt.VertexCount <= 0 ||
            hostReceipt.TriangleCount <= 0 ||
            hostReceipt.RejectedTriangleCount < 0 ||
            hostReceipt.MaterialCount < 0 ||
            hostReceipt.TextureCount < 0 ||
            receipt.ConversionCreditsConsumed is < 0)
        {
            return "reported invalid geometry, material, texture, or credit counts";
        }

        if (receipt.Warnings is null ||
            receipt.Warnings.Count > MaximumImportWarnings ||
            receipt.Warnings.Any(
                warning => string.IsNullOrWhiteSpace(warning) ||
                           warning.Length > MaximumImportWarningLength ||
                           warning.Any(
                               character =>
                                   char.IsControl(character) ||
                                   char.GetUnicodeCategory(character) is
                                       System.Globalization.UnicodeCategory.Format or
                                       System.Globalization.UnicodeCategory.LineSeparator or
                                       System.Globalization.UnicodeCategory.ParagraphSeparator)))
        {
            return "reported invalid request warnings";
        }

        if (expectedMode == "family" &&
            hostReceipt.TransactionStatus == "committed" &&
            string.IsNullOrWhiteSpace(hostReceipt.SavedFamilyPath))
        {
            return "omitted the saved family path for a committed family import";
        }

        if (!string.IsNullOrWhiteSpace(hostReceipt.SavedFamilyPath) &&
            (expectedMode != "family" ||
             hostReceipt.SavedFamilyPath.Length > 4096 ||
             !LooksLikeAbsoluteHostPath(hostReceipt.SavedFamilyPath) ||
             (string.Equals(
                  targetHost,
                  "revit",
                  StringComparison.Ordinal) &&
              (!LooksLikeAbsoluteWindowsPath(hostReceipt.SavedFamilyPath) ||
               !hostReceipt.SavedFamilyPath.EndsWith(
                   ".rfa",
                   StringComparison.OrdinalIgnoreCase)))))
        {
            return "reported a saved-family path inconsistent with the import mode";
        }

        return null;
    }

    private static bool LooksLikeAbsoluteHostPath(string path) =>
        path.StartsWith('/') ||
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 &&
         char.IsLetter(path[0]) &&
         path[1] == ':' &&
         (path[2] == '\\' || path[2] == '/'));

    private static bool LooksLikeAbsoluteWindowsPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 &&
         char.IsLetter(path[0]) &&
         path[1] == ':' &&
         (path[2] == '\\' || path[2] == '/'));

    public string? DurableGenerationTaskId =>
        ResolveConsistentTaskId(
            GenerationReceipt?.TaskId,
            GenerationOperationStatus);

    public string? DurableConversionTaskId =>
        ResolveConsistentTaskId(
            ConversionReceipt?.ConversionTaskId,
            ConversionOperationStatus);

    public bool GenerationStatusMatchesDurableTask =>
        DurableGenerationTaskId is { } taskId &&
        string.Equals(
            GenerationStatus?.TaskId,
            taskId,
            StringComparison.Ordinal);

    public bool ConversionStatusMatchesDurableTask =>
        DurableConversionTaskId is { } taskId &&
        string.Equals(
            ConversionStatus?.TaskId,
            taskId,
            StringComparison.Ordinal);

    public bool HasSuccessfulGeneration =>
        GenerationStatusMatchesDurableTask &&
        string.Equals(
            GenerationStatus?.Status,
            "success",
            StringComparison.Ordinal);

    public bool HasSuccessfulConversion =>
        ConversionStatusMatchesDurableTask &&
        string.Equals(
            ConversionStatus?.Status,
            "success",
            StringComparison.Ordinal);

    public bool HasDurableGenerationTask =>
        DurableGenerationTaskId is not null;

    public bool CanDispatchPreparedGeneration =>
        PreparedGeneration is not null &&
        !HasDurableGenerationTask &&
        !IsDefinitiveRequestRejection(GenerationOperationStatus);

    public bool GenerationRetryRequired =>
        CanDispatchPreparedGeneration &&
        GenerationDispatchAttempted;

    public bool GenerationRetryAllowed =>
        GenerationRetryRequired &&
        GenerationOperationStatus?.CanResumeCreation == true;

    public bool HasDurableConversionTask =>
        DurableConversionTaskId is not null;

    public bool CanDispatchPreparedConversion =>
        PreparedConversion is not null &&
        !HasDurableConversionTask &&
        !IsDefinitiveRequestRejection(ConversionOperationStatus);

    public bool ConversionRetryRequired =>
        CanDispatchPreparedConversion &&
        ConversionDispatchAttempted;

    public bool ConversionRetryAllowed =>
        ConversionRetryRequired &&
        ConversionOperationStatus?.CanResumeCreation == true;

    internal static bool IsDefinitiveRequestRejection(
        Tripo.Bridge.HostControlOperationStatusReceipt? status) =>
        status is
        {
            State: Tripo.Bridge.HostControlConstants.RequestRejectedState,
            CreatedTaskId: null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: false,
            OperationInProgress: false,
            FailureStage: null,
        } &&
        !string.IsNullOrWhiteSpace(status.FailureCode) &&
        !string.IsNullOrWhiteSpace(status.FailureMessage);

    public bool CanDispatchPreparedImport =>
        PreparedImport is not null &&
        !HasConfirmedImportReceipt;

    public bool ImportRetryRequired =>
        CanDispatchPreparedImport &&
        ImportDispatchAttempted;

    public bool HasNonTerminalRemoteTask =>
        (HasDurableGenerationTask &&
         !IsTerminalTaskStatus(
             GenerationStatus,
             DurableGenerationTaskId)) ||
        (HasDurableConversionTask &&
         !IsTerminalTaskStatus(
             ConversionStatus,
             DurableConversionTaskId));

    private static bool IsTerminalTaskStatus(
        Tripo.Bridge.HostControlTaskStatusReceipt? status,
        string? expectedTaskId) =>
        expectedTaskId is not null &&
        string.Equals(
            status?.TaskId,
            expectedTaskId,
            StringComparison.Ordinal) &&
        status?.Status is
            "success" or
            "failed" or
            "cancelled" or
            "banned" or
            "expired";

    private static string? ResolveConsistentTaskId(
        string? creationReceiptTaskId,
        Tripo.Bridge.HostControlOperationStatusReceipt? operationStatus)
    {
        string? journalTaskId =
            operationStatus?.TaskIdDurable == true
                ? operationStatus.CreatedTaskId
                : null;
        if (creationReceiptTaskId is null)
        {
            return journalTaskId;
        }

        if (journalTaskId is null)
        {
            return creationReceiptTaskId;
        }

        return string.Equals(
            creationReceiptTaskId,
            journalTaskId,
            StringComparison.Ordinal)
                ? creationReceiptTaskId
                : null;
    }
}

public sealed class TripoPanelSession : IAsyncDisposable
{
    private const string GenerationOperationKind = "text_task_creation";
    private const string ConversionOperationKind = "obj_conversion_creation";
    private const string GenerationTaskType = "text_to_model";
    private const string ConversionTaskType = "convert_model";
    private static readonly TimeSpan PipelinePollDelay = TimeSpan.FromSeconds(2);

    private readonly Tripo.Bridge.IHostSidecarConnector _connector;
    private readonly TripoPanelRecoveryStore? _recoveryStore;
    private readonly Tripo.Bridge.ICredentialWorkflowExecutionGate?
        _recoveryArchiveGate;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<
        RecoveryInspectionKey,
        RecoveryInspectionAttempt>
        _recoveryInspectionAttempts = [];
    private Tripo.Bridge.IHostControlClient? _client;
    private TripoPanelState _state = TripoPanelState.Initial;
    private TripoPanelRecoveryLoadResult _recovery;
    private long _recoveryRevision;
    private PendingRecoveryReview? _pendingRecoveryReview;
    private Task? _disposeTask;
    private volatile bool _disposed;

    public TripoPanelSession(
        Tripo.Bridge.IHostSidecarConnector connector,
        TripoPanelRecoveryStore? recoveryStore = null,
        Tripo.Bridge.ICredentialWorkflowExecutionGate?
            recoveryArchiveGate = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _recoveryStore = recoveryStore;
        _recoveryArchiveGate = recoveryStore is null
            ? recoveryArchiveGate
            : recoveryArchiveGate ??
              new Tripo.Bridge.CredentialWorkflowExecutionGate(
                  recoveryStore.RootDirectory);
        _recovery =
            recoveryStore?.LoadStale() ??
            TripoPanelRecoveryLoadResult.Empty;
    }

    public event EventHandler<TripoPanelState>? StateChanged;

    public event EventHandler<TripoPanelRecoveryLoadResult>? RecoveryChanged;

    public TripoPanelState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public TripoPanelRecoveryLoadResult Recovery
    {
        get
        {
            lock (_stateGate)
            {
                return _recovery;
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client =
                    await _connector.EnsureConnectedAsync(token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlHealthReceipt health =
                    await client.GetHealthAsync(token).ConfigureAwait(false);
                Tripo.Bridge.HostContextReceipt context =
                    await client.GetHostContextAsync(token).ConfigureAwait(false);
                EnsureHealthMatchesContext(health, context);
                TripoPanelState current = State;
                bool documentChanged =
                    current.Context is not null &&
                    !IsSameDocumentSession(current.Context, context);
                if (documentChanged && current.HasUnresolvedImportDispatch)
                {
                    throw new InvalidOperationException(
                        "The active host document changed while an import outcome is " +
                        "unresolved. Return to the import target and retry the same " +
                        "import UUID before selecting another project.");
                }

                Tripo.Bridge.HostControlCredentialStatusReceipt credentials =
                    await client.GetCredentialStatusAsync(token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlModelCatalogReceipt modelCatalog =
                    await client.ListModelsAsync(token).ConfigureAwait(false);
                _client = client;
                UpdateState(state => state with
                {
                    Connected = true,
                    Context = context,
                    CredentialStatus = credentials,
                    AvailableModels = modelCatalog.Models,
                    ModelCatalogNote = modelCatalog.Note,
                    PreparedImport = documentChanged
                        ? null
                        : state.PreparedImport,
                    ImportDispatchAttempted = documentChanged
                        ? false
                        : state.ImportDispatchAttempted,
                    ImportReceipt = documentChanged
                        ? null
                        : state.ImportReceipt,
                    LastError = null,
                });
            },
            cancellationToken);

    public Task SetApiKeyAsync(
        string apiKey,
        bool persist,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                if (persist && State.RequiresCredentialRecovery)
                {
                    throw new InvalidOperationException(
                        "A recovery API key must remain session-only until " +
                        "the account-bound workflow is reconciled and " +
                        "explicitly reset.");
                }

                EnsureCredentialMutationAllowed(
                    allowCredentialRecovery: true);
                Tripo.Bridge.IHostControlClient client = RequireClient();
                Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
                    await client.SetApiKeyAsync(apiKey, persist, token)
                        .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    CredentialStatus = receipt.Status,
                    LastError = null,
                });
            },
            cancellationToken);

    public Task ClearApiKeyAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                EnsureCredentialMutationAllowed(
                    allowCredentialRecovery: false);
                Tripo.Bridge.IHostControlClient client = RequireClient();
                Tripo.Bridge.HostControlCredentialMutationReceipt receipt =
                    await client.ClearApiKeyAsync(token).ConfigureAwait(false);
                UpdateState(state => state with
                {
                    CredentialStatus = receipt.Status,
                    LastError = null,
                });
            },
            cancellationToken);

    public PreparedTextGeneration PrepareGeneration(
        string prompt,
        int faceLimit,
        bool withMaterials,
        string? model = null)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidatePrompt(prompt);
            ValidateFaceLimit(faceLimit);
            string effectiveModel =
                Tripo.Bridge.TripoModelCatalog.NormalizeOrDefault(model);
            if (State.HasWorkflowState)
            {
                throw new InvalidOperationException(
                    "Use the prepared generation operation or start a new workflow " +
                    "before creating another operation ID.");
            }

            PreparedTextGeneration prepared = new(
                prompt,
                faceLimit,
                withMaterials,
                Guid.NewGuid().ToString("D"),
                effectiveModel);
            UpdateState(state => state with
            {
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = false,
                GenerationReceipt = null,
                GenerationOperationStatus = null,
                GenerationStatus = null,
                PreparedConversion = null,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                CompletedImports = [],
                LastError = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task DispatchPreparedGenerationAsync(
        bool userConfirmedExternalCost,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmedExternalCost)
        {
            throw new InvalidOperationException(
                "Generation was not sent because external cost was not confirmed.");
        }

        return RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedTextGeneration prepared =
                    current.PreparedGeneration ??
                    throw new InvalidOperationException(
                        "Prepare a generation operation before dispatch.");
                EnsurePaidDispatchAllowed(
                    current.CanDispatchPreparedGeneration,
                    current.GenerationRetryRequired,
                    current.GenerationOperationStatus,
                    "generation");
                UpdateState(state => state with
                {
                    GenerationDispatchAttempted = true,
                });
                Tripo.Bridge.HostControlTextTaskCreationReceipt receipt;
                try
                {
                    receipt = await client.CreateTextTaskAsync(
                                new Tripo.Bridge.HostControlCreateTextTaskRequest(
                                    prepared.Prompt,
                                    prepared.FaceLimit,
                                    prepared.WithMaterials,
                                    prepared.OperationId,
                                    ConfirmExternalCost: true,
                                    RequireExistingOperation:
                                        current.GenerationRetryRequired,
                                    Model: prepared.Model),
                                token)
                            .ConfigureAwait(false);
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                        StringComparison.Ordinal) &&
                        !current.GenerationRetryRequired)
                {
                    UpdateState(state => state with
                    {
                        GenerationDispatchAttempted = false,
                        GenerationOperationStatus = null,
                    });
                    throw;
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                        StringComparison.Ordinal))
                {
                    Tripo.Bridge.HostControlOperationStatusReceipt? status =
                        await TryGetDefinitiveRequestRejectionAsync(
                                client,
                                prepared.OperationId,
                                GenerationOperationKind,
                                expectedSourceTaskId: null,
                                "generation",
                                current.GenerationOperationStatus,
                                token)
                            .ConfigureAwait(false);
                    if (status is not null)
                    {
                        ClearRejectedGenerationStage();
                    }

                    throw;
                }

                ValidateGenerationCreationReceipt(prepared, receipt);
                Tripo.Bridge.HostControlOperationStatusReceipt
                    journalStatus =
                        await client.GetOperationStatusAsync(
                                prepared.OperationId,
                                token)
                            .ConfigureAwait(false);
                ValidateOperationStatus(
                    journalStatus,
                    prepared.OperationId,
                    GenerationOperationKind,
                    expectedSourceTaskId: null,
                    "generation",
                    receipt.TaskId);
                ValidateOperationStatusTransition(
                    current.GenerationOperationStatus,
                    journalStatus,
                    "generation");
                UpdateState(state => state with
                {
                    GenerationReceipt = receipt,
                    GenerationOperationStatus = journalStatus,
                    LastError = null,
                });
            },
            cancellationToken);
    }

    public Task RefreshGenerationStatusAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client = RequireClient();
                string taskId =
                    await ResolveGenerationTaskIdAsync(client, token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlTaskStatusReceipt status =
                    await client.GetTaskStatusAsync(taskId, token)
                        .ConfigureAwait(false);
                ValidateTaskStatus(
                    status,
                    taskId,
                    GenerationTaskType,
                    "generation");
                UpdateState(state => state with
                {
                    GenerationStatus = status,
                    LastError = null,
                });
            },
            cancellationToken);

    public PreparedObjConversion PrepareConversion(
        int faceLimit,
        bool withMaterials)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidateFaceLimit(faceLimit);
            TripoPanelState state = State;
            string generationTaskId = RequireSuccessfulTask(
                state.GenerationStatus,
                state.DurableGenerationTaskId,
                "generation");
            if (state.PreparedConversion is not null ||
                state.ConversionDispatchAttempted ||
                state.ConversionReceipt is not null ||
                state.ConversionOperationStatus is not null ||
                state.ConversionStatus is not null)
            {
                throw new InvalidOperationException(
                    "Use the prepared conversion operation or start a new workflow " +
                    "before creating another operation ID.");
            }

            PreparedObjConversion prepared = new(
                generationTaskId,
                faceLimit,
                withMaterials,
                Guid.NewGuid().ToString("D"));
            UpdateState(current => current with
            {
                PreparedConversion = prepared,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                CompletedImports = [],
                LastError = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task DispatchPreparedConversionAsync(
        bool userConfirmedExternalCost,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmedExternalCost)
        {
            throw new InvalidOperationException(
                "Conversion was not sent because external cost was not confirmed.");
        }

        return RunExclusiveAsync(
            async token =>
            {
                using IDisposable? lease =
                    _recoveryStore?.AcquireCredentialWorkflowLease();
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedObjConversion prepared =
                    current.PreparedConversion ??
                    throw new InvalidOperationException(
                        "Prepare a conversion operation before dispatch.");
                EnsurePaidDispatchAllowed(
                    current.CanDispatchPreparedConversion,
                    current.ConversionRetryRequired,
                    current.ConversionOperationStatus,
                    "conversion");
                UpdateState(state => state with
                {
                    ConversionDispatchAttempted = true,
                });
                Tripo.Bridge.HostControlObjConversionCreationReceipt receipt;
                try
                {
                    receipt = await client.CreateObjConversionAsync(
                                new Tripo.Bridge
                                    .HostControlCreateObjConversionRequest(
                                        prepared.SourceTaskId,
                                        prepared.FaceLimit,
                                        prepared.WithMaterials,
                                        prepared.OperationId,
                                        ConfirmExternalCost: true,
                                        RequireExistingOperation:
                                            current.ConversionRetryRequired),
                                token)
                            .ConfigureAwait(false);
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialInvalidError,
                        StringComparison.Ordinal) &&
                        !current.ConversionRetryRequired)
                {
                    UpdateState(state => state with
                    {
                        ConversionDispatchAttempted = false,
                        ConversionOperationStatus = null,
                    });
                    throw;
                }
                catch (Tripo.Bridge.HostControlCallException exception)
                    when (string.Equals(
                        exception.Code,
                        Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                        StringComparison.Ordinal))
                {
                    Tripo.Bridge.HostControlOperationStatusReceipt? status =
                        await TryGetDefinitiveRequestRejectionAsync(
                                client,
                                prepared.OperationId,
                                ConversionOperationKind,
                                prepared.SourceTaskId,
                                "conversion",
                                current.ConversionOperationStatus,
                                token)
                            .ConfigureAwait(false);
                    if (status is not null)
                    {
                        ClearRejectedConversionStage();
                    }

                    throw;
                }

                ValidateConversionCreationReceipt(prepared, receipt);
                Tripo.Bridge.HostControlOperationStatusReceipt
                    journalStatus =
                        await client.GetOperationStatusAsync(
                                prepared.OperationId,
                                token)
                            .ConfigureAwait(false);
                ValidateOperationStatus(
                    journalStatus,
                    prepared.OperationId,
                    ConversionOperationKind,
                    prepared.SourceTaskId,
                    "conversion",
                    receipt.ConversionTaskId);
                ValidateOperationStatusTransition(
                    current.ConversionOperationStatus,
                    journalStatus,
                    "conversion");
                UpdateState(state => state with
                {
                    ConversionReceipt = receipt,
                    ConversionOperationStatus = journalStatus,
                    LastError = null,
                });
            },
            cancellationToken);
    }

    public Task RefreshConversionStatusAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.IHostControlClient client = RequireClient();
                string taskId =
                    await ResolveConversionTaskIdAsync(client, token)
                        .ConfigureAwait(false);
                Tripo.Bridge.HostControlTaskStatusReceipt status =
                    await client.GetTaskStatusAsync(taskId, token)
                        .ConfigureAwait(false);
                ValidateTaskStatus(
                    status,
                    taskId,
                    ConversionTaskType,
                    "conversion");
                UpdateState(state => state with
                {
                    ConversionStatus = status,
                    LastError = null,
                });
            },
            cancellationToken);

    public PreparedObjImport PrepareImport(
        string name,
        string importMode,
        bool applyMaterials,
        string placementMode = "origin",
        bool associateLevel = false)
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            ValidateName(name);
            string normalizedMode = ValidateImportMode(importMode);
            string normalizedPlacement = ValidatePlacementMode(placementMode);

            TripoPanelState state = State;
            string conversionTaskId = RequireSuccessfulTask(
                state.ConversionStatus,
                state.DurableConversionTaskId,
                "conversion");
            if ((state.PreparedImport is not null ||
                 state.ImportDispatchAttempted ||
                 state.ImportReceipt is not null) &&
                !state.HasConfirmedImportReceipt)
            {
                throw new InvalidOperationException(
                    "Use or reconcile the prepared import operation before creating " +
                    "another operation ID.");
            }

            Tripo.Bridge.HostContextReceipt context = RequireContext();
            PreparedObjImport prepared = new(
                conversionTaskId,
                name,
                context.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                normalizedMode,
                applyMaterials,
                context.DocumentTitle,
                context.Host,
                normalizedPlacement,
                associateLevel);
            UpdateState(current => current with
            {
                PreparedImport = prepared,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
            });
            return prepared;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task ImportPreparedAsync(
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                EnsureNoStaleRecovery();
                Tripo.Bridge.IHostControlClient client = RequireClient();
                TripoPanelState current = State;
                PreparedObjImport prepared =
                    current.PreparedImport ??
                    throw new InvalidOperationException(
                        "Prepare an import operation before dispatch.");
                if (!current.CanDispatchPreparedImport)
                {
                    throw new InvalidOperationException(
                        "The prepared import already has a durable receipt.");
                }
                await EnsureSessionStillActiveAsync(
                        client,
                        prepared.DocumentSessionId,
                        token)
                    .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    ImportDispatchAttempted = true,
                });
                Tripo.Bridge.HostControlObjTaskImportReceipt receipt =
                    await client.ImportObjTaskAsync(
                            new Tripo.Bridge.HostControlImportObjTaskRequest(
                                prepared.ConversionTaskId,
                                prepared.Name,
                                prepared.DocumentSessionId,
                                prepared.OperationId,
                                prepared.ImportMode,
                                prepared.ApplyMaterials,
                                prepared.PlacementMode,
                                prepared.AssociateLevel),
                            token)
                        .ConfigureAwait(false);
                UpdateState(state => state with
                {
                    ImportReceipt = receipt,
                });
                ValidateImportReceipt(
                    prepared,
                    receipt);
                UpdateState(state => state with
                {
                    CompletedImports = AppendCompletedImport(
                        state.CompletedImports,
                        prepared,
                        receipt),
                    LastError = null,
                });
            },
            cancellationToken);

    /// <summary>
    /// Runs generate → poll → convert → poll → import as one UI pipeline.
    /// Does not hold the operation gate across stages; each stage uses existing APIs.
    /// </summary>
    public async Task RunCreatePipelineAsync(
        string prompt,
        int faceLimit,
        bool withMaterials,
        string objectName,
        string importMode,
        bool applyMaterials,
        bool userConfirmedExternalCost,
        string placementMode = "origin",
        bool associateLevel = false,
        string? model = null,
        IProgress<TripoPipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!userConfirmedExternalCost)
        {
            throw new InvalidOperationException(
                "Create was not started because external cost was not confirmed.");
        }

        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(objectName);

        static void Report(
            IProgress<TripoPipelineProgress>? sink,
            string label,
            int? percent) =>
            sink?.Report(new TripoPipelineProgress(label, percent));

        TripoPanelState state = State;
        if (!state.HasSuccessfulGeneration)
        {
            if (state.PreparedGeneration is null)
            {
                if (state.HasWorkflowState)
                {
                    throw new InvalidOperationException(
                        "Clear the current workflow with New before starting Create.");
                }

                PrepareGeneration(prompt, faceLimit, withMaterials, model);
            }

            state = State;
            if (state.CanDispatchPreparedGeneration)
            {
                Report(progress, "Generating…", 0);
                await DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await PollGenerationUntilTerminalAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            if (!State.HasSuccessfulGeneration)
            {
                throw new InvalidOperationException(
                    State.GenerationStatus?.ErrorMessage ??
                    "Generation did not succeed.");
            }
        }

        state = State;
        if (!state.HasSuccessfulConversion)
        {
            if (state.PreparedConversion is null)
            {
                bool materials =
                    state.PreparedGeneration?.WithMaterials ?? withMaterials;
                int limit =
                    state.PreparedGeneration?.FaceLimit ?? faceLimit;
                PrepareConversion(limit, materials);
            }

            state = State;
            if (state.CanDispatchPreparedConversion)
            {
                Report(progress, "Converting…", 34);
                await DispatchPreparedConversionAsync(
                        userConfirmedExternalCost: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await PollConversionUntilTerminalAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            if (!State.HasSuccessfulConversion)
            {
                throw new InvalidOperationException(
                    State.ConversionStatus?.ErrorMessage ??
                    "Conversion did not succeed.");
            }
        }

        // Match the Advanced Import path: refresh the active document/session
        // immediately before preparing/dispatching import.
        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        state = State;
        if (state.PreparedImport is null ||
            state.HasConfirmedImportReceipt)
        {
            PrepareImport(
                objectName,
                importMode,
                applyMaterials,
                placementMode,
                associateLevel);
        }

        Report(
            progress,
            string.Equals(placementMode, "pick", StringComparison.Ordinal)
                ? "Importing… (you may be asked to pick a point)"
                : "Importing…",
            70);
        await ImportPreparedAsync(cancellationToken).ConfigureAwait(false);
        Report(progress, "Done", 100);
    }

    private async Task PollGenerationUntilTerminalAsync(
        IProgress<TripoPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshGenerationStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            TripoPanelState state = State;
            Tripo.Bridge.HostControlTaskStatusReceipt? task =
                state.GenerationStatus;
            if (task is not null &&
                state.GenerationStatusMatchesDurableTask)
            {
                if (task.Status is "queued" or "running")
                {
                    int mapped = MapStageProgress(task.Progress, 0, 33);
                    progress?.Report(
                        new TripoPipelineProgress("Generating…", mapped));
                    await Task.Delay(PipelinePollDelay, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return;
            }

            if (state.DurableGenerationTaskId is null &&
                state.GenerationRetryAllowed)
            {
                throw new InvalidOperationException(
                    "Generation dispatch needs a same-UUID retry. Use Advanced.");
            }

            await Task.Delay(PipelinePollDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PollConversionUntilTerminalAsync(
        IProgress<TripoPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshConversionStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            TripoPanelState state = State;
            Tripo.Bridge.HostControlTaskStatusReceipt? task =
                state.ConversionStatus;
            if (task is not null &&
                state.ConversionStatusMatchesDurableTask)
            {
                if (task.Status is "queued" or "running")
                {
                    int mapped = MapStageProgress(task.Progress, 34, 66);
                    progress?.Report(
                        new TripoPipelineProgress("Converting…", mapped));
                    await Task.Delay(PipelinePollDelay, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return;
            }

            if (state.DurableConversionTaskId is null &&
                state.ConversionRetryAllowed)
            {
                throw new InvalidOperationException(
                    "Conversion dispatch needs a same-UUID retry. Use Advanced.");
            }

            await Task.Delay(PipelinePollDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static int MapStageProgress(
        int? providerProgress,
        int stageStart,
        int stageEnd)
    {
        int span = Math.Max(1, stageEnd - stageStart);
        int pct = providerProgress is >= 0 and <= 100
            ? providerProgress.Value
            : 0;
        return stageStart + (pct * span / 100);
    }

    private static string ValidatePlacementMode(string placementMode)
    {
        if (string.IsNullOrWhiteSpace(placementMode))
        {
            return "origin";
        }

        string normalized = placementMode.Trim().ToLowerInvariant();
        return normalized is "origin" or "pick"
            ? normalized
            : throw new ArgumentException(
                "Placement mode must be origin or pick.",
                nameof(placementMode));
    }

    public void ResetWorkflow()
    {
        EnterSynchronousMutation();
        try
        {
            EnsureNoStaleRecovery();
            TripoPanelState current = State;
            if (current.HasUnresolvedDispatch)
            {
                throw new InvalidOperationException(
                    "This panel has an unresolved dispatched operation. Keep its " +
                    "displayed operation ID and use Refresh or retry the same stage; " +
                    "a new workflow cannot discard that recovery identity.");
            }

            if (current.HasNonTerminalRemoteTask)
            {
                throw new InvalidOperationException(
                    "Refresh every accepted remote task until it reaches a " +
                    "terminal status before starting a new workflow.");
            }

            UpdateState(state => state with
            {
                PreparedGeneration = null,
                GenerationDispatchAttempted = false,
                GenerationReceipt = null,
                GenerationOperationStatus = null,
                GenerationStatus = null,
                PreparedConversion = null,
                ConversionDispatchAttempted = false,
                ConversionReceipt = null,
                ConversionOperationStatus = null,
                ConversionStatus = null,
                PreparedImport = null,
                ImportDispatchAttempted = false,
                ImportReceipt = null,
                LastError = null,
            });
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<Tripo.Bridge.HostControlOperationStatusReceipt>
        InspectPaidRecoveryAsync(
            string operationId,
            string stage,
            CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(operationId, "D", out Guid parsed) ||
            !string.Equals(
                parsed.ToString("D"),
                operationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "operationId must be a canonical lowercase D-format UUID.",
                nameof(operationId));
        }

        string expectedKind = stage switch
        {
            "generation" => GenerationOperationKind,
            "conversion" => ConversionOperationKind,
            _ => throw new ArgumentException(
                "stage must be generation or conversion.",
                nameof(stage)),
        };
        string recoveryToken;
        bool belongsToDisplayedRecovery;
        string? expectedSourceTaskId;
        string? expectedCreatedTaskId;
        string? persistedJournalState;
        Tripo.Bridge.HostControlOperationStatusReceipt?
            previouslyInspectedStatus;
        lock (_stateGate)
        {
            recoveryToken = _recovery.PresentationToken;
            DisplayedRecoveryPaidOperation? displayed =
                FindRecoveryPaidOperation(
                    _recovery,
                    operationId,
                    stage);
            belongsToDisplayedRecovery = displayed is not null;
            expectedSourceTaskId = displayed?.ExpectedSourceTaskId;
            expectedCreatedTaskId =
                displayed?.Hint.TaskIdDurable == true
                    ? displayed.Hint.TaskId
                    : null;
            persistedJournalState = displayed?.Hint.JournalState;
            previouslyInspectedStatus =
                _recoveryInspectionAttempts.TryGetValue(
                    new RecoveryInspectionKey(
                        recoveryToken,
                        operationId,
                        stage),
                    out RecoveryInspectionAttempt? previousAttempt)
                    ? previousAttempt.Status
                    : null;
        }

        Tripo.Bridge.HostControlOperationStatusReceipt? receipt = null;
        try
        {
            await RunExclusiveAsync(
                    async token =>
                    {
                        receipt = await RequireClient()
                            .GetOperationStatusAsync(operationId, token)
                            .ConfigureAwait(false);
                        ValidateOperationStatus(
                            receipt,
                            operationId,
                            expectedKind,
                            expectedSourceTaskId,
                            stage,
                            expectedCreatedTaskId);
                        if (belongsToDisplayedRecovery)
                        {
                            if (previouslyInspectedStatus is not null)
                            {
                                ValidateOperationStatusTransition(
                                    previouslyInspectedStatus,
                                    receipt,
                                    stage);
                            }
                            else
                            {
                                ValidateOperationStateTransition(
                                    persistedJournalState,
                                    previousUpdatedAtUtc: null,
                                    receipt,
                                    stage);
                            }
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (belongsToDisplayedRecovery)
            {
                RecordRecoveryInspection(
                    recoveryToken,
                    operationId,
                    stage,
                    succeeded: true,
                    receipt);
            }
        }
        catch
        {
            if (belongsToDisplayedRecovery)
            {
                RecordRecoveryInspection(
                    recoveryToken,
                    operationId,
                    stage,
                    succeeded: false,
                    status: null);
            }

            throw;
        }

        return receipt ??
            throw new InvalidOperationException(
                "The paid-operation inspection returned no receipt.");
    }

    public async Task<string> CreateRecoveryReviewReceiptAsync(
        string displayedRecoveryToken,
        bool paidResultsReviewed,
        bool importsReviewed,
        bool failedInspectionsReviewed,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(displayedRecoveryToken))
        {
            throw new InvalidOperationException(
                "The panel has no rendered recovery snapshot to review.");
        }

        string? reviewReceiptId = null;
        await RunExclusiveAsync(
            async token =>
            {
                List<RecoveryInspectionCheck> successfulChecks = [];
                lock (_stateGate)
                {
                    if (_state.HasWorkflowState)
                    {
                        throw new InvalidOperationException(
                            "Start with an empty panel before completing a " +
                            "recovery review.");
                    }

                    TripoPanelRecoveryLoadResult recovery = _recovery;
                    if (!string.Equals(
                            displayedRecoveryToken,
                            recovery.PresentationToken,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recovered operation list changed. Inspect the " +
                            "refreshed list again.");
                    }

                    bool hasPaid = false;
                    bool hasFailedInspection = false;
                    foreach (
                        LoadedTripoPanelRecoveryHint loaded in
                        recovery.Hints)
                    {
                        AddRecoveryInspectionCheck(
                            successfulChecks,
                            recovery.PresentationToken,
                            loaded.Hint.Generation,
                            "generation",
                            expectedSourceTaskId: null,
                            ref hasPaid,
                            ref hasFailedInspection);
                        AddRecoveryInspectionCheck(
                            successfulChecks,
                            recovery.PresentationToken,
                            loaded.Hint.Conversion,
                            "conversion",
                            loaded.Hint.Generation?.TaskId,
                            ref hasPaid,
                            ref hasFailedInspection);
                    }

                    bool hasImports = recovery.Hints.Any(
                        loaded => loaded.Hint.Import is not null);
                    if (hasPaid && !paidResultsReviewed)
                    {
                        throw new InvalidOperationException(
                            "Confirm that every paid inspection result was reviewed.");
                    }

                    if (hasImports && !importsReviewed)
                    {
                        throw new InvalidOperationException(
                            "Confirm manual reconciliation of every recovered import.");
                    }

                    if (hasFailedInspection &&
                        !failedInspectionsReviewed)
                    {
                        throw new InvalidOperationException(
                            "Confirm manual reconciliation of every failed paid " +
                            "inspection.");
                    }
                }

                Tripo.Bridge.IHostControlClient client = RequireClient();
                foreach (RecoveryInspectionCheck check in successfulChecks)
                {
                    Tripo.Bridge.HostControlOperationStatusReceipt current =
                        await client.GetOperationStatusAsync(
                                check.OperationId,
                                token)
                            .ConfigureAwait(false);
                    ValidateOperationStatus(
                        current,
                        check.OperationId,
                        check.ExpectedKind,
                        check.ExpectedSourceTaskId,
                        check.Stage,
                        check.ExpectedCreatedTaskId);
                    EnsureRecoveryStatusCanBeArchived(
                        current,
                        check.Stage);
                    if (!Equals(current, check.ObservedStatus))
                    {
                        throw new InvalidOperationException(
                            "A paid-operation journal entry changed after " +
                            "inspection. Inspect the refreshed evidence again.");
                    }
                }

                lock (_stateGate)
                {
                    if (!string.Equals(
                            displayedRecoveryToken,
                            _recovery.PresentationToken,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recovered operation list changed. Inspect the " +
                            "refreshed list again.");
                    }

                    reviewReceiptId = Guid.NewGuid().ToString("D");
                    _pendingRecoveryReview = new PendingRecoveryReview(
                        reviewReceiptId,
                        displayedRecoveryToken,
                        successfulChecks.ToArray());
                }
            },
            cancellationToken).ConfigureAwait(false);
        return reviewReceiptId ??
            throw new InvalidOperationException(
                "The recovery review returned no receipt.");
    }

    public Task AcknowledgeRecoveredOperationsAsync(
        string reviewReceiptId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(reviewReceiptId))
        {
            throw new InvalidOperationException(
                "Complete the current recovery review before acknowledgement.");
        }

        TripoPanelRecoveryStore store =
            _recoveryStore ??
            throw new InvalidOperationException(
                "This panel has no recovery store.");

        return RunExclusiveAsync(
            async token =>
            {
                Tripo.Bridge.ICredentialWorkflowExecutionGate executionGate =
                    _recoveryArchiveGate ??
                    throw new InvalidOperationException(
                        "This panel has no recovery archival execution gate.");
                using IDisposable intentLease =
                    store.AcquireCredentialWorkflowLease();
                using IDisposable executionLease = executionGate.Acquire();
                PendingRecoveryReview review;
                lock (_stateGate)
                {
                    if (_state.HasWorkflowState)
                    {
                        throw new InvalidOperationException(
                            "Start with an empty panel before acknowledging " +
                            "recovered operations.");
                    }

                    review =
                        _pendingRecoveryReview ??
                        throw new InvalidOperationException(
                            "Complete the current recovery review before " +
                            "acknowledgement.");
                    _pendingRecoveryReview = null;
                    if (!string.Equals(
                            review.ReceiptId,
                            reviewReceiptId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recovery review receipt is invalid or already used.");
                    }
                }

                TripoPanelRecoveryLoadResult current = store.LoadStale();
                PublishRecovery(current);
                ValidateRecoverySnapshotForArchive(
                    review.RecoveryToken,
                    current);

                await RevalidateRecoveryChecksAsync(
                        RequireClient(),
                        review.SuccessfulChecks,
                        token)
                    .ConfigureAwait(false);

                current = store.LoadStale();
                PublishRecovery(current);
                ValidateRecoverySnapshotForArchive(
                    review.RecoveryToken,
                    current);

                try
                {
                    foreach (LoadedTripoPanelRecoveryHint hint in current.Hints)
                    {
                        store.Archive(hint);
                    }
                }
                finally
                {
                    PublishRecovery(store.LoadStale());
                }
            },
            cancellationToken);
    }

    public TripoPanelRecoveryLoadResult RefreshRecovery()
    {
        ThrowIfDisposed();
        long observedRevision;
        lock (_stateGate)
        {
            observedRevision = _recoveryRevision;
        }

        TripoPanelRecoveryLoadResult next =
            _recoveryStore?.LoadStale() ??
            TripoPanelRecoveryLoadResult.Empty;
        lock (_stateGate)
        {
            if (_recoveryRevision != observedRevision)
            {
                return _recovery;
            }

            _recovery = next;
            _recoveryRevision++;
            _pendingRecoveryReview = null;
        }

        RecoveryChanged?.Invoke(this, next);
        return next;
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_stateGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
        _lifetime.Dispose();
        _operationGate.Dispose();
        _recoveryStore?.Dispose();
        StateChanged = null;
        RecoveryChanged = null;
    }

    private void EnsureNoStaleRecovery()
    {
        TripoPanelRecoveryLoadResult recovery =
            _recoveryStore is null
                ? Recovery
                : RefreshRecovery();

        if (recovery.HasBlock)
        {
            throw new InvalidOperationException(
                "Recovered operation IDs require reconciliation before a new " +
                "workflow or credential change can proceed.");
        }
    }

    private static void EnsurePaidDispatchAllowed(
        bool canDispatch,
        bool retryRequired,
        Tripo.Bridge.HostControlOperationStatusReceipt? operationStatus,
        string stage)
    {
        if (!canDispatch)
        {
            throw new InvalidOperationException(
                $"The prepared {stage} operation already has a durable task ID.");
        }

        if (retryRequired && operationStatus?.CanResumeCreation != true)
        {
            throw new InvalidOperationException(
                $"Refresh the {stage} operation status before retrying the " +
                "same UUID. A retry is enabled only when the journal reports " +
                "that creation can resume.");
        }
    }

    private void EnsureCredentialMutationAllowed(
        bool allowCredentialRecovery)
    {
        TripoPanelState state = State;
        if (state.HasCredentialBoundWorkflow &&
            !allowCredentialRecovery)
        {
            throw new InvalidOperationException(
                "The API key cannot change or be cleared while an " +
                "account-bound workflow remains active. Refresh it to a " +
                "terminal status or reconcile it, then explicitly reset first.");
        }

        TripoPanelRecoveryLoadResult global =
            _recoveryStore?.LoadCredentialMutationBlocks(
                excludeCurrentStoreHint: true) ??
            TripoPanelRecoveryLoadResult.Empty;
        if (global.HasBlock)
        {
            throw new InvalidOperationException(
                "The API key cannot change while another Rhino/Revit panel has " +
                "an unresolved paid recovery hint, an unverifiable live owner, " +
                "or invalid recovery storage. Reconcile it in the owning host " +
                "first.");
        }
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken callerCancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellationToken,
                _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            UpdateState(state => state with
            {
                Busy = true,
                LastError = null,
            });
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (!_disposed &&
                  (exception is not OperationCanceledException ||
                   !callerCancellationToken.IsCancellationRequested))
        {
            UpdateTransientState(state => state with
            {
                LastError = BoundError(exception.Message),
            });
            throw;
        }
        finally
        {
            try
            {
                if (!_disposed)
                {
                    UpdateTransientState(
                        state => state with { Busy = false });
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private void EnterSynchronousMutation()
    {
        ThrowIfDisposed();
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Wait for the current panel operation to finish before " +
                "changing workflow state.");
        }

        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private async Task EnsureSessionStillActiveAsync(
        Tripo.Bridge.IHostControlClient client,
        string expectedSessionId,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.HostContextReceipt current =
            await client.GetHostContextAsync(cancellationToken)
                .ConfigureAwait(false);
        Tripo.Bridge.HostContextReceipt original = RequireContext();
        if (current.ProcessId != original.ProcessId ||
            !string.Equals(
                current.Host,
                original.Host,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.DocumentSessionId,
                expectedSessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active host document changed. The prepared operation was " +
                "not dispatched; refresh the panel and prepare a new operation.");
        }
    }

    private async Task<string> ResolveGenerationTaskIdAsync(
        Tripo.Bridge.IHostControlClient client,
        CancellationToken cancellationToken)
    {
        TripoPanelState state = State;
        if (state.GenerationReceipt is not null)
        {
            return state.GenerationReceipt.TaskId;
        }

        PreparedTextGeneration prepared =
            state.PreparedGeneration ??
            throw new InvalidOperationException(
                "No generation operation is available to refresh.");
        if (!state.GenerationDispatchAttempted)
        {
            throw new InvalidOperationException(
                "Dispatch the prepared generation before refreshing it.");
        }

        Tripo.Bridge.HostControlOperationStatusReceipt operationStatus =
            await client.GetOperationStatusAsync(
                    prepared.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        ValidateOperationStatus(
            operationStatus,
            prepared.OperationId,
            GenerationOperationKind,
            expectedSourceTaskId: null,
            "generation",
            state.DurableGenerationTaskId ??
            state.GenerationStatus?.TaskId);
        ValidateOperationStatusTransition(
            state.GenerationOperationStatus,
            operationStatus,
            "generation");
        if (TripoPanelState.IsDefinitiveRequestRejection(operationStatus))
        {
            ClearRejectedGenerationStage();
            throw new InvalidOperationException(
                "The generation request was definitively rejected. Correct the " +
                "API credential and prepare a new generation operation.");
        }

        UpdateState(current => current with
        {
            GenerationOperationStatus = operationStatus,
        });
        return RequireDurableTaskId(operationStatus, "generation");
    }

    private static async Task<
        Tripo.Bridge.HostControlOperationStatusReceipt?>
        TryGetDefinitiveRequestRejectionAsync(
            Tripo.Bridge.IHostControlClient client,
            string operationId,
            string expectedKind,
            string? expectedSourceTaskId,
            string stage,
            Tripo.Bridge.HostControlOperationStatusReceipt? previousStatus,
            CancellationToken cancellationToken)
    {
        try
        {
            Tripo.Bridge.HostControlOperationStatusReceipt status =
                await client.GetOperationStatusAsync(
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateOperationStatus(
                status,
                operationId,
                expectedKind,
                expectedSourceTaskId,
                stage);
            ValidateOperationStatusTransition(
                previousStatus,
                status,
                stage);
            return TripoPanelState.IsDefinitiveRequestRejection(status)
                ? status
                : null;
        }
        catch (Tripo.Bridge.HostControlCallException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string> ResolveConversionTaskIdAsync(
        Tripo.Bridge.IHostControlClient client,
        CancellationToken cancellationToken)
    {
        TripoPanelState state = State;
        if (state.ConversionReceipt is not null)
        {
            return state.ConversionReceipt.ConversionTaskId;
        }

        PreparedObjConversion prepared =
            state.PreparedConversion ??
            throw new InvalidOperationException(
                "No conversion operation is available to refresh.");
        if (!state.ConversionDispatchAttempted)
        {
            throw new InvalidOperationException(
                "Dispatch the prepared conversion before refreshing it.");
        }

        Tripo.Bridge.HostControlOperationStatusReceipt operationStatus =
            await client.GetOperationStatusAsync(
                    prepared.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        ValidateOperationStatus(
            operationStatus,
            prepared.OperationId,
            ConversionOperationKind,
            prepared.SourceTaskId,
            "conversion",
            state.DurableConversionTaskId ??
            state.ConversionStatus?.TaskId);
        ValidateOperationStatusTransition(
            state.ConversionOperationStatus,
            operationStatus,
            "conversion");
        if (TripoPanelState.IsDefinitiveRequestRejection(operationStatus))
        {
            ClearRejectedConversionStage();
            throw new InvalidOperationException(
                "The conversion request was definitively rejected. Correct the " +
                "API credential and prepare a new conversion operation.");
        }

        UpdateState(current => current with
        {
            ConversionOperationStatus = operationStatus,
        });
        return RequireDurableTaskId(operationStatus, "conversion");
    }

    private void ClearRejectedGenerationStage()
    {
        UpdateState(state => state with
        {
            PreparedGeneration = null,
            GenerationDispatchAttempted = false,
            GenerationReceipt = null,
            GenerationOperationStatus = null,
            GenerationStatus = null,
            PreparedConversion = null,
            ConversionDispatchAttempted = false,
            ConversionReceipt = null,
            ConversionOperationStatus = null,
            ConversionStatus = null,
            PreparedImport = null,
            ImportDispatchAttempted = false,
            ImportReceipt = null,
        });
    }

    private void ClearRejectedConversionStage()
    {
        UpdateState(state => state with
        {
            PreparedConversion = null,
            ConversionDispatchAttempted = false,
            ConversionReceipt = null,
            ConversionOperationStatus = null,
            ConversionStatus = null,
            PreparedImport = null,
            ImportDispatchAttempted = false,
            ImportReceipt = null,
        });
    }

    private static string RequireDurableTaskId(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string stage)
    {
        if (status.TaskIdDurable &&
            !string.IsNullOrWhiteSpace(status.CreatedTaskId))
        {
            return status.CreatedTaskId;
        }

        throw new InvalidOperationException(
            $"The {stage} operation is {status.State}; its task ID is not " +
            $"durable. {status.NextAction}");
    }

    private static void ValidateGenerationCreationReceipt(
        PreparedTextGeneration prepared,
        Tripo.Bridge.HostControlTextTaskCreationReceipt receipt)
    {
        if (!string.Equals(
                receipt.OperationId,
                prepared.OperationId,
                StringComparison.Ordinal) ||
            !IsValidTaskId(receipt.TaskId))
        {
            throw new InvalidOperationException(
                "The generation creation receipt did not match the prepared " +
                "operation or contain a valid Tripo task ID. Preserve the UUID " +
                "and inspect the journal.");
        }
    }

    private static void ValidateConversionCreationReceipt(
        PreparedObjConversion prepared,
        Tripo.Bridge.HostControlObjConversionCreationReceipt receipt)
    {
        if (!string.Equals(
                receipt.OperationId,
                prepared.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.SourceTaskId,
                prepared.SourceTaskId,
                StringComparison.Ordinal) ||
            !IsValidTaskId(receipt.ConversionTaskId) ||
            !string.Equals(receipt.Format, "OBJ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The conversion creation receipt did not match the prepared " +
                "operation, source task, Tripo task-ID format, or OBJ format. " +
                "Preserve the UUID and inspect the journal.");
        }
    }

    private static void ValidateOperationStatus(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string expectedOperationId,
        string expectedKind,
        string? expectedSourceTaskId,
        string stage,
        string? expectedCreatedTaskId = null)
    {
        bool prepared = string.Equals(
            status.State,
            "prepared",
            StringComparison.Ordinal);
        bool operationInProgress = string.Equals(
            status.State,
            "operation_in_progress",
            StringComparison.Ordinal);
        bool dispatching = string.Equals(
            status.State,
            "dispatching",
            StringComparison.Ordinal);
        bool taskIdPersisted = string.Equals(
            status.State,
            "task_id_persisted",
            StringComparison.Ordinal);
        bool requestRejected = string.Equals(
            status.State,
            Tripo.Bridge.HostControlConstants.RequestRejectedState,
            StringComparison.Ordinal);
        bool outcomeUnknown = string.Equals(
            status.State,
            "outcome_unknown",
            StringComparison.Ordinal);
        bool knownState =
            prepared ||
            operationInProgress ||
            dispatching ||
            taskIdPersisted ||
            requestRejected ||
            outcomeUnknown;
        bool stateMayHaveCreatedRemoteTask =
            operationInProgress ||
            dispatching ||
            taskIdPersisted ||
            outcomeUnknown;

        if (!string.Equals(
                status.OperationId,
                expectedOperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                status.Kind,
                expectedKind,
                StringComparison.Ordinal) ||
            (expectedKind == GenerationOperationKind &&
             status.SourceTaskId is not null) ||
            (expectedSourceTaskId is not null &&
             !string.Equals(
                 status.SourceTaskId,
                 expectedSourceTaskId,
                 StringComparison.Ordinal)) ||
            !knownState ||
            status.TaskIdDurable != taskIdPersisted ||
            status.MayHaveCreatedRemoteTask !=
                (stateMayHaveCreatedRemoteTask ||
                 status.OperationInProgress) ||
            status.CanResumeCreation !=
                (prepared && !status.OperationInProgress) ||
            (operationInProgress && !status.OperationInProgress) ||
            (prepared && status.OperationInProgress) ||
            status.TaskIdDurable !=
                !string.IsNullOrWhiteSpace(status.CreatedTaskId) ||
            (requestRejected &&
             (status.CreatedTaskId is not null ||
              status.FailureStage is not null ||
              string.IsNullOrWhiteSpace(status.FailureCode) ||
              string.IsNullOrWhiteSpace(status.FailureMessage))) ||
            (expectedCreatedTaskId is not null &&
             (!status.TaskIdDurable ||
              !string.Equals(
                  status.CreatedTaskId,
                  expectedCreatedTaskId,
                  StringComparison.Ordinal))) ||
            (status.TaskIdDurable &&
             (!IsValidTaskId(status.CreatedTaskId) ||
              !status.MayHaveCreatedRemoteTask)))
        {
            throw new InvalidOperationException(
                $"The {stage} operation-status receipt did not match the " +
                "requested UUID, kind, source task, or journal-state " +
                "semantics. Preserve the UUID and inspect the journal.");
        }
    }

    private static void ValidateOperationStatusTransition(
        Tripo.Bridge.HostControlOperationStatusReceipt? previous,
        Tripo.Bridge.HostControlOperationStatusReceipt current,
        string stage)
    {
        ValidateOperationStateTransition(
            previous?.State,
            previous?.UpdatedAtUtc,
            current,
            stage);
    }

    private static void ValidateOperationStateTransition(
        string? previousState,
        DateTimeOffset? previousUpdatedAtUtc,
        Tripo.Bridge.HostControlOperationStatusReceipt current,
        string stage)
    {
        if (previousState is null)
        {
            return;
        }

        bool allowed = previousState switch
        {
            "prepared" => current.State is
                "prepared" or
                "operation_in_progress" or
                "dispatching" or
                "task_id_persisted" or
                Tripo.Bridge.HostControlConstants.RequestRejectedState or
                "outcome_unknown",
            "operation_in_progress" => current.State is
                "prepared" or
                "operation_in_progress" or
                "dispatching" or
                "task_id_persisted" or
                Tripo.Bridge.HostControlConstants.RequestRejectedState or
                "outcome_unknown",
            "dispatching" => current.State is
                "dispatching" or
                "task_id_persisted" or
                Tripo.Bridge.HostControlConstants.RequestRejectedState or
                "outcome_unknown",
            "task_id_persisted" =>
                current.State == "task_id_persisted",
            Tripo.Bridge.HostControlConstants.RequestRejectedState =>
                current.State ==
                Tripo.Bridge.HostControlConstants.RequestRejectedState,
            "outcome_unknown" =>
                current.State == "outcome_unknown",
            _ => false,
        };
        if (!allowed ||
            (previousUpdatedAtUtc.HasValue &&
             current.UpdatedAtUtc < previousUpdatedAtUtc.Value))
        {
            throw new InvalidOperationException(
                $"The {stage} paid-operation journal regressed from " +
                $"{previousState} to {current.State}. Preserve the UUID and " +
                "do not resend the paid request.");
        }
    }

    private static bool IsValidTaskId(string? taskId) =>
        Tripo.Bridge.TripoTaskId.IsValid(taskId);

    private static void ValidateTaskStatus(
        Tripo.Bridge.HostControlTaskStatusReceipt status,
        string expectedTaskId,
        string expectedType,
        string stage)
    {
        if (!string.Equals(
                status.TaskId,
                expectedTaskId,
                StringComparison.Ordinal) ||
            !string.Equals(
                status.Type,
                expectedType,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {stage} task-status receipt did not match the requested " +
                "task ID or task type. The stage remains unconfirmed.");
        }
    }

    private void RecordRecoveryInspection(
        string recoveryToken,
        string operationId,
        string stage,
        bool succeeded,
        Tripo.Bridge.HostControlOperationStatusReceipt? status)
    {
        lock (_stateGate)
        {
            if (!string.Equals(
                    recoveryToken,
                    _recovery.PresentationToken,
                    StringComparison.Ordinal))
            {
                return;
            }

            _recoveryInspectionAttempts[
                new RecoveryInspectionKey(
                    recoveryToken,
                    operationId,
                    stage)] = new RecoveryInspectionAttempt(
                        succeeded,
                        status);
            _pendingRecoveryReview = null;
        }
    }

    private static async Task RevalidateRecoveryChecksAsync(
        Tripo.Bridge.IHostControlClient client,
        IReadOnlyList<RecoveryInspectionCheck> checks,
        CancellationToken cancellationToken)
    {
        foreach (RecoveryInspectionCheck check in checks)
        {
            Tripo.Bridge.HostControlOperationStatusReceipt current =
                await client.GetOperationStatusAsync(
                        check.OperationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateOperationStatus(
                current,
                check.OperationId,
                check.ExpectedKind,
                check.ExpectedSourceTaskId,
                check.Stage,
                check.ExpectedCreatedTaskId);
            EnsureRecoveryStatusCanBeArchived(
                current,
                check.Stage);
            if (!Equals(current, check.ObservedStatus))
            {
                throw new InvalidOperationException(
                    "A paid-operation journal entry changed after inspection. " +
                    "Inspect the refreshed evidence again.");
            }
        }
    }

    private static void ValidateRecoverySnapshotForArchive(
        string expectedToken,
        TripoPanelRecoveryLoadResult current)
    {
        if (!string.Equals(
                expectedToken,
                current.PresentationToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The recovered operation list changed. Review the refreshed " +
                "list and complete the review again.");
        }

        if (current.Issues.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid recovery files must be inspected and moved aside " +
                "manually; the panel will not overwrite or archive them.");
        }
    }

    private void PublishRecovery(TripoPanelRecoveryLoadResult recovery)
    {
        lock (_stateGate)
        {
            _recovery = recovery;
            _recoveryRevision++;
        }

        RecoveryChanged?.Invoke(this, recovery);
    }

    private void AddRecoveryInspectionCheck(
        List<RecoveryInspectionCheck> successfulChecks,
        string recoveryToken,
        TripoPanelPaidRecoveryHint? hint,
        string stage,
        string? expectedSourceTaskId,
        ref bool hasPaid,
        ref bool hasFailedInspection)
    {
        if (hint is null)
        {
            return;
        }

        hasPaid = true;
        if (!_recoveryInspectionAttempts.TryGetValue(
                new RecoveryInspectionKey(
                    recoveryToken,
                    hint.OperationId,
                    stage),
                out RecoveryInspectionAttempt? attempt))
        {
            throw new InvalidOperationException(
                $"Inspect the recovered {stage} UUID before completing the review.");
        }

        hasFailedInspection |= !attempt.Succeeded;
        if (attempt.Succeeded)
        {
            Tripo.Bridge.HostControlOperationStatusReceipt status =
                attempt.Status ??
                throw new InvalidOperationException(
                    "The paid inspection retained no status receipt.");
            EnsureRecoveryStatusCanBeArchived(
                status,
                stage);
            successfulChecks.Add(
                new RecoveryInspectionCheck(
                    hint.OperationId,
                    stage,
                    stage == "generation"
                        ? GenerationOperationKind
                        : ConversionOperationKind,
                    expectedSourceTaskId,
                    hint.TaskIdDurable
                        ? hint.TaskId
                        : null,
                    status));
        }
    }

    private static void EnsureRecoveryStatusCanBeArchived(
        Tripo.Bridge.HostControlOperationStatusReceipt status,
        string stage)
    {
        if (status.OperationInProgress)
        {
            throw new InvalidOperationException(
                $"The recovered {stage} operation is still in progress. " +
                "Wait for the local execution lease to end, then inspect it " +
                "again before archiving recovery evidence.");
        }
    }

    private static DisplayedRecoveryPaidOperation?
        FindRecoveryPaidOperation(
        TripoPanelRecoveryLoadResult recovery,
        string operationId,
        string stage)
    {
        List<DisplayedRecoveryPaidOperation> matches = [];
        foreach (LoadedTripoPanelRecoveryHint loaded in recovery.Hints)
        {
            TripoPanelPaidRecoveryHint? hint =
                stage == "generation"
                    ? loaded.Hint.Generation
                    : loaded.Hint.Conversion;
            if (hint is not null &&
                string.Equals(
                    hint.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                matches.Add(
                    new DisplayedRecoveryPaidOperation(
                        hint,
                        stage == "conversion"
                            ? loaded.Hint.Generation?.TaskId
                            : null));
            }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "The recovery set contains a duplicate paid-operation UUID. " +
                "Resolve the recovery files manually."),
        };
    }

    private sealed record DisplayedRecoveryPaidOperation(
        TripoPanelPaidRecoveryHint Hint,
        string? ExpectedSourceTaskId);

    private sealed record RecoveryInspectionKey(
        string RecoveryToken,
        string OperationId,
        string Stage);

    private sealed record RecoveryInspectionAttempt(
        bool Succeeded,
        Tripo.Bridge.HostControlOperationStatusReceipt? Status);

    private sealed record RecoveryInspectionCheck(
        string OperationId,
        string Stage,
        string ExpectedKind,
        string? ExpectedSourceTaskId,
        string? ExpectedCreatedTaskId,
        Tripo.Bridge.HostControlOperationStatusReceipt ObservedStatus);

    private sealed record PendingRecoveryReview(
        string ReceiptId,
        string RecoveryToken,
        IReadOnlyList<RecoveryInspectionCheck> SuccessfulChecks);

    private static bool IsSameDocumentSession(
        Tripo.Bridge.HostContextReceipt first,
        Tripo.Bridge.HostContextReceipt second) =>
        first.ProcessId == second.ProcessId &&
        string.Equals(first.Host, second.Host, StringComparison.Ordinal) &&
        string.Equals(
            first.DocumentSessionId,
            second.DocumentSessionId,
            StringComparison.Ordinal);

    private static void EnsureHealthMatchesContext(
        Tripo.Bridge.HostControlHealthReceipt health,
        Tripo.Bridge.HostContextReceipt context)
    {
        if (health.HostProcessId != context.ProcessId ||
            !string.Equals(
                health.Host,
                context.Host,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The sidecar is connected to a different host process.");
        }
    }

    private Tripo.Bridge.IHostControlClient RequireClient() =>
        _client ??
        throw new InvalidOperationException(
            "Connect the panel to its sidecar before continuing.");

    private Tripo.Bridge.HostContextReceipt RequireContext() =>
        State.Context ??
        throw new InvalidOperationException(
            "Refresh the active host document before continuing.");

    private static string RequireSuccessfulTask(
        Tripo.Bridge.HostControlTaskStatusReceipt? status,
        string? expectedTaskId,
        string stage)
    {
        if (expectedTaskId is null ||
            !string.Equals(
                status?.TaskId,
                expectedTaskId,
                StringComparison.Ordinal) ||
            !string.Equals(
                status?.Status,
                "success",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {stage} task identity must match the durable journal and " +
                "report success before continuing.");
        }

        return expectedTaskId;
    }

    private static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1024)
        {
            throw new ArgumentException(
                "The prompt must contain 1 to 1024 characters.",
                nameof(prompt));
        }
    }

    private static void ValidateFaceLimit(int faceLimit)
    {
        if (faceLimit is < 500 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faceLimit),
                "faceLimit must be between 500 and 200000.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException(
                "The imported object name must contain 1 to 128 characters.",
                nameof(name));
        }
    }

    private static string ValidateImportMode(string importMode)
    {
        string normalized = (importMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not "native" and
            not "mesh" and
            not "instance" and
            not "family")
        {
            throw new ArgumentException(
                "Import mode must be native, mesh, instance, or family.",
                nameof(importMode));
        }

        return normalized;
    }

    private static void ValidateImportReceipt(
        PreparedObjImport prepared,
        Tripo.Bridge.HostControlObjTaskImportReceipt receipt)
    {
        string? issue = TripoPanelState.GetImportReceiptIssue(
            prepared,
            receipt);
        if (issue is not null)
        {
            throw new InvalidOperationException(
                "The import receipt " + issue +
                ". Preserve the UUID and inspect the host before retrying.");
        }
    }

    private static IReadOnlyList<CompletedObjImport> AppendCompletedImport(
        IReadOnlyList<CompletedObjImport> completedImports,
        PreparedObjImport prepared,
        Tripo.Bridge.HostControlObjTaskImportReceipt receipt)
    {
        if (completedImports.Any(
                completed => string.Equals(
                    completed.Prepared.OperationId,
                    prepared.OperationId,
                    StringComparison.Ordinal)))
        {
            return completedImports;
        }

        return [.. completedImports, new CompletedObjImport(prepared, receipt)];
    }

    private void UpdateState(Func<TripoPanelState, TripoPanelState> update)
    {
        TripoPanelState next = PersistAndAssignState(update);
        StateChanged?.Invoke(this, next);
    }

    private void UpdateTransientState(
        Func<TripoPanelState, TripoPanelState> update)
    {
        TripoPanelState next;
        try
        {
            next = PersistAndAssignState(update);
        }
        catch (Exception exception)
            when (IsRecoveryPersistenceFailure(exception))
        {
            lock (_stateGate)
            {
                next = update(_state);
                _state = next;
            }
        }

        StateChanged?.Invoke(this, next);
    }

    private TripoPanelState PersistAndAssignState(
        Func<TripoPanelState, TripoPanelState> update)
    {
        lock (_stateGate)
        {
            TripoPanelState next = update(_state);
            _recoveryStore?.Save(next);
            _state = next;
            return next;
        }
    }

    private static bool IsRecoveryPersistenceFailure(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        System.Text.Json.JsonException or
        NotSupportedException;

    private void ThrowIfDisposed()
    {
#pragma warning disable CA1513 // ObjectDisposedException.ThrowIf is unavailable on net7.0.
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TripoPanelSession));
        }
#pragma warning restore CA1513
    }

    private static string BoundError(string? message)
    {
        const string fallback = "The operation failed.";
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}

using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelPresentationTests
{
    private const string DocumentSessionId =
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    [Fact]
    public void InitialStateIsDisconnectedAndDoesNotOfferAReset()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                Tripo.HostUi.TripoPanelState.Initial,
                prompt: "A pavilion",
                faceLimit: "20000",
                objectName: "Tripo Model");

        Assert.Equal("Document: not connected", presentation.DocumentStatus);
        Assert.Equal(
            "API key: status unknown",
            presentation.CredentialStatus);
        Assert.Equal("Not started", presentation.GenerationStatus);
        Assert.Equal("Not started", presentation.ConversionStatus);
        Assert.Equal("Not imported", presentation.ImportStatus);
        Assert.True(presentation.ConnectEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.False(presentation.ResultVisible);
        Assert.Contains("Connect", presentation.GenerateHelp);
        Assert.Contains("no panel workflow", presentation.ResetHelp);
    }

    [Fact]
    public void ValidReadyStateEnablesOnlyTheFirstWorkflowAction()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(ReadyState());

        Assert.Equal(
            "API key: configured · Source: OS credential store",
            presentation.CredentialStatus);
        Assert.True(presentation.ApiKeyEnabled);
        Assert.True(presentation.ClearApiKeyEnabled);
        Assert.DoesNotContain(
            "environment key remains",
            presentation.ClearApiKeyHelp,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(presentation.GenerateEnabled);
        Assert.True(presentation.PrimaryActionEnabled);
        Assert.Equal("Create in Revit", presentation.PrimaryActionText);
        Assert.False(presentation.ConvertEnabled);
        Assert.False(presentation.ImportEnabled);
        Assert.False(presentation.NameEnabled);
        Assert.False(presentation.ImportModeEnabled);
        Assert.False(presentation.ApplyMaterialsEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.False(presentation.PromptValidationVisible);
        Assert.False(presentation.FaceLimitValidationVisible);
        Assert.False(presentation.ObjectNameValidationVisible);
        Assert.False(presentation.PipelineProgressVisible);
    }

    [Fact]
    public void PipelineStageOverrideSurfacesUnifiedProgress()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                currentAction: "Creating…",
                pipelineStageLabel: "Generating…",
                pipelineProgressOverride: 12);

        Assert.Equal("Generating…", presentation.PipelineStageLabel);
        Assert.True(presentation.PipelineProgressVisible);
        Assert.Equal(12, presentation.PipelineProgress);
        Assert.False(presentation.PrimaryActionEnabled);
    }

    [Theory]
    [InlineData("", "20000", "Tripo Model")]
    [InlineData("A pavilion", "499", "Tripo Model")]
    [InlineData("A pavilion", "200001", "Tripo Model")]
    [InlineData("A pavilion", "20,000", "Tripo Model")]
    public void InvalidGenerationInputsStayFailClosed(
        string prompt,
        string faceLimit,
        string objectName)
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                prompt: prompt,
                faceLimit: faceLimit,
                objectName: objectName);

        Assert.False(presentation.GenerateEnabled);
        Assert.True(
            presentation.PromptValidationVisible ||
            presentation.FaceLimitValidationVisible);
    }

    [Fact]
    public void OverlongInputsExposeInlineValidation()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                prompt: new string('p', 1025),
                faceLimit: "20000",
                objectName: new string('n', 129));

        Assert.False(presentation.GenerateEnabled);
        Assert.True(presentation.PromptValidationVisible);
        Assert.Contains("1024", presentation.PromptValidation);
        Assert.True(presentation.ObjectNameValidationVisible);
        Assert.Contains("128", presentation.ObjectNameValidation);
    }

    [Fact]
    public void RecoveryMustBeInspectedBeforeAcknowledgement()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            RecoveryWithGeneration();

        Tripo.HostUi.TripoPanelPresentation unreviewed =
            Present(
                ReadyState(),
                recovery: recovery,
                recoveryReviewed: false);
        Tripo.HostUi.TripoPanelPresentation reviewed =
            Present(
                ReadyState(),
                recovery: recovery,
                recoveryReviewed: true,
                recoveryInspection:
                    "Generation: outcome_unknown; do not resend.");

        Assert.True(unreviewed.RecoveryHasBlock);
        Assert.Equal(
            "Recovery · Action required",
            unreviewed.RecoveryHeader);
        Assert.True(unreviewed.CheckRecoveryEnabled);
        Assert.False(unreviewed.AcknowledgeRecoveryEnabled);
        Assert.False(unreviewed.ApiKeyEnabled);
        Assert.False(unreviewed.GenerateEnabled);
        Assert.Equal("Recovery · Inspected", reviewed.RecoveryHeader);
        Assert.True(reviewed.RecoveryReviewed);
        Assert.True(reviewed.AcknowledgeRecoveryEnabled);
        Assert.Contains(
            "Inspection result:",
            reviewed.RecoveryDetails);
        Assert.Contains(
            "do not resend",
            reviewed.RecoveryDetails,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            recovery.PresentationToken,
            reviewed.RecoveryToken);
    }

    [Fact]
    public void ImageRecoveryFormattingExposesOnlyDurableIdentityEvidence()
    {
        const string operationId =
            "e1e1e1e1-e1e1-41e1-81e1-e1e1e1e1e1e1";
        const string taskId = "task_image_recovery123";
        Tripo.HostUi.TripoPanelRecoveryHint hint = new(
            SchemaVersion:
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
            RecoveryId: "image-recovery-id",
            Host: "revit",
            OwnerProcessId: 123,
            OwnerProcessStartedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            Generation: null,
            Conversion: null,
            Import: null,
            ImageGeneration:
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    operationId,
                    true,
                    taskId,
                    "task_id_persisted",
                    true,
                    false));

        string formatted =
            Tripo.HostUi.TripoPanelPresentation.FormatRecoveryHint(hint);

        Assert.Contains(
            $"Image generation UUID: {operationId}",
            formatted);
        Assert.Contains(
            $"Image generation task ID: {taskId}",
            formatted);
        Assert.Contains(
            "Image generation journal state: task_id_persisted",
            formatted);
        Assert.DoesNotContain("StagedImageTransfer", formatted);
        Assert.DoesNotContain("Transfer ID", formatted);
        Assert.DoesNotContain("SHA-256", formatted);
        Assert.DoesNotContain("Image path", formatted);
        Assert.DoesNotContain("Image URL", formatted);
        Assert.DoesNotContain("https://", formatted);
        Assert.DoesNotContain(@"C:\", formatted);
    }

    [Fact]
    public void InvalidRecoveryFileCannotBeAcknowledgedAfterReview()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            new(
                RecoveryWithGeneration().Hints,
                [
                    new Tripo.HostUi.TripoPanelRecoveryIssue(
                        "broken.json",
                        "invalid_json",
                        "The file is invalid."),
                ]);

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                recovery: recovery,
                recoveryReviewed: true);

        Assert.False(presentation.AcknowledgeRecoveryEnabled);
        Assert.Contains(
            "invalid_json",
            presentation.RecoveryDetails);
    }

    [Fact]
    public void FailedRecoveryInspectionRequiresManualReviewWithoutClaimingSuccess()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                recovery: RecoveryWithGeneration(),
                recoveryReviewed: true,
                recoveryInspection:
                    "Generation: inspection failed: sidecar unavailable",
                recoveryInspectionHadFailures: true);

        Assert.Equal(
            "Recovery · Manual review required",
            presentation.RecoveryHeader);
        Assert.False(presentation.RecoveryReviewed);
        Assert.True(presentation.AcknowledgeRecoveryEnabled);
        Assert.Contains(
            "Manually reconcile",
            presentation.RecoveryDetails);
        Assert.Contains(
            "failed inspection",
            presentation.AcknowledgeRecoveryHelp);
    }

    [Fact]
    public void OutcomeUnknownIsProminentAndRawEvidenceRemainsCopyable()
    {
        const string operationId =
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "A pavilion",
                    20_000,
                    false,
                    operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        operationId,
                        "text_generation",
                        "outcome_unknown",
                        null,
                        null,
                        "lost_response",
                        "The response was lost.",
                        false,
                        true,
                        false,
                        "Do not resend the paid request.",
                        DateTimeOffset.UnixEpoch,
                        "provider_create"),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            "Outcome unknown · Do not resend",
            presentation.GenerationStatus);
        Assert.Contains(
            "Operation state: outcome_unknown",
            presentation.GenerationDiagnostic);
        Assert.Contains(
            "Failure stage: provider_create",
            presentation.GenerationDiagnostic);
        Assert.Contains(
            "Next action: Do not resend",
            presentation.GenerationDiagnostic);
        Assert.False(presentation.GenerateEnabled);
        Assert.True(presentation.RefreshGenerationEnabled);
    }

    [Fact]
    public void OutOfRangeProgressIsNotPresentedAsNormalProgress()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationReceipt = MatchingGenerationReceipt(),
                GenerationStatus = new Tripo.Bridge.HostControlTaskStatusReceipt(
                    "task_source123",
                    "text_to_model",
                    "running",
                    137,
                    null,
                    DateTimeOffset.UnixEpoch,
                    null,
                    null,
                    null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            "Last known · Running · Progress value needs inspection",
            presentation.GenerationStatus);
        Assert.Null(presentation.GenerationProgress);
        Assert.Contains(
            "Task progress reported: 137%",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void TaskStatusShowsCreditsAndFailureEvidence()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationReceipt = MatchingGenerationReceipt(),
                GenerationStatus = new Tripo.Bridge.HostControlTaskStatusReceipt(
                    "task_source123",
                    "text_to_model",
                    "failed",
                    64,
                    12.5m,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddMinutes(1),
                    422,
                    "The model could not be generated."),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            "Last known · Failed · 12.5 credits",
            presentation.GenerationStatus);
        Assert.Null(presentation.GenerationProgress);
        Assert.Contains(
            "Task error code: 422",
            presentation.GenerationDiagnostic);
        Assert.Contains(
            "Credits consumed: 12.5",
            presentation.GenerationDiagnostic);
        Assert.Contains(
            "Completed: 1970-01-01 00:01:00 UTC",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void ImportReceiptSummarizesActualRevitResult()
    {
        Tripo.HostUi.PreparedObjImport prepared = new(
            "task_conversion123",
            "Pavilion",
            DocumentSessionId,
            "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
            "family",
            true);
        Tripo.HostUi.PreparedObjConversion conversion = new(
            "task_source123",
            20_000,
            true,
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedConversion = conversion,
                ConversionReceipt =
                    new Tripo.Bridge
                        .HostControlObjConversionCreationReceipt(
                            conversion.OperationId,
                            conversion.SourceTaskId,
                            prepared.ConversionTaskId,
                            "OBJ"),
                ConversionStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        prepared.ConversionTaskId,
                        "convert_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
                PreparedImport = prepared,
                ImportDispatchAttempted = true,
                ImportReceipt =
                    new Tripo.Bridge.HostControlObjTaskImportReceipt(
                        prepared.OperationId,
                        prepared.ConversionTaskId,
                        3.25m,
                        new Tripo.Bridge.HostImportReceipt(
                            "revit",
                            DocumentSessionId,
                            prepared.OperationId,
                            "family-instance-42",
                            100,
                            98,
                            2,
                            "committed",
                            "family",
                            3,
                            1,
                            @"C:\Tripo\Pavilion.rfa"),
                        [
                            "associateLevel was requested but could not be " +
                            "applied to this import.",
                        ]),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            "Imported into Revit · 98 triangles · 2 rejected",
            presentation.ImportStatus);
        Assert.True(presentation.ImportReceiptDetailsVisible);
        Assert.Contains(
            "Created object ID: family-instance-42",
            presentation.ImportReceiptDetails);
        Assert.Contains(
            "Rejected triangles: 2",
            presentation.ImportReceiptDetails);
        Assert.Contains(
            "Materials: 3",
            presentation.ImportReceiptDetails);
        Assert.Contains(
            @"Saved family: C:\Tripo\Pavilion.rfa",
            presentation.ImportReceiptDetails);
        Assert.Contains(
            "Request warning: associateLevel was requested",
            presentation.ImportReceiptDetails);
        Assert.Contains(
            "2 triangles rejected",
            presentation.ResultStatus);
        Assert.Contains(
            "request adjusted",
            presentation.ResultStatus);
        Assert.True(presentation.ImportEnabled);
        Assert.Equal(
            "Import again into Example Project",
            presentation.ImportText);
        Assert.Contains("fresh import UUID", presentation.ImportHelp);
        Assert.Equal(
            Tripo.HostUi.TripoPanelNoticeKind.Warning,
            presentation.ResultKind);
    }

    [Theory]
    [InlineData(
        "committed",
        "family",
        0,
        0,
        "no applied materials reported",
        "requested, but zero applied materials")]
    [InlineData(
        "already_exists",
        "mesh",
        0,
        0,
        "prior mesh material counts unavailable",
        "unavailable for a replayed DirectShape")]
    [InlineData(
        "committed",
        "family",
        1,
        0,
        "materials applied, but no textures reported",
        "materials applied, but zero textures")]
    public void ZeroMaterialReceiptKeepsMissingAndUnavailableEvidenceDistinct(
        string transactionStatus,
        string importMode,
        int materialCount,
        int textureCount,
        string resultEvidence,
        string receiptEvidence)
    {
        Tripo.HostUi.PreparedObjImport prepared = new(
            "task_conversion123",
            "Pavilion",
            DocumentSessionId,
            "dadadada-dada-4dad-8ada-dadadadadada",
            importMode,
            true);
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedImport = prepared,
                ImportDispatchAttempted = true,
                ImportReceipt =
                    new Tripo.Bridge.HostControlObjTaskImportReceipt(
                        prepared.OperationId,
                        prepared.ConversionTaskId,
                        null,
                        new Tripo.Bridge.HostImportReceipt(
                            "revit",
                            DocumentSessionId,
                            prepared.OperationId,
                            "created-1",
                            3,
                            1,
                            0,
                            transactionStatus,
                            importMode,
                            materialCount,
                            textureCount,
                            importMode == "family" &&
                            transactionStatus == "committed"
                                ? @"C:\Tripo\Pavilion.rfa"
                                : null),
                        []),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            Tripo.HostUi.TripoPanelNoticeKind.Warning,
            presentation.ResultKind);
        Assert.Contains(resultEvidence, presentation.ResultStatus);
        Assert.Contains(receiptEvidence, presentation.ImportReceiptDetails);
    }

    [Fact]
    public void CurrentActionNamesWorkInsteadOfShowingGenericBusyText()
    {
        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState() with
                {
                    LastError = "The prior refresh failed.",
                },
                currentAction: "Refreshing generation status…");

        Assert.Equal(
            "In progress · Refreshing generation status…",
            presentation.ResultStatus);
        Assert.Equal(
            Tripo.HostUi.TripoPanelNoticeKind.Busy,
            presentation.ResultKind);
        Assert.False(presentation.ConnectEnabled);
        Assert.False(presentation.ApiKeyEnabled);
        Assert.False(presentation.GenerateEnabled);
        Assert.False(presentation.RefreshGenerationEnabled);
        Assert.False(presentation.ConvertEnabled);
        Assert.False(presentation.ImportEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.Contains("Wait", presentation.ApiKeyHelp);
        Assert.Contains("Wait", presentation.RefreshGenerationHelp);
        Assert.Contains("Wait", presentation.ImportHelp);
    }

    [Fact]
    public void ResetAppearsOnlyForAResolvedWorkflow()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "A pavilion",
            20_000,
            false,
            "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
        Tripo.HostUi.TripoPanelState resolved =
            ReadyState() with
            {
                PreparedGeneration = prepared,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };
        Tripo.HostUi.TripoPanelState unresolved =
            resolved with
            {
                GenerationReceipt = null,
                GenerationDispatchAttempted = true,
            };
        Tripo.HostUi.TripoPanelState running =
            resolved with
            {
                GenerationStatus =
                    resolved.GenerationStatus! with
                    {
                        Status = "running",
                        Progress = 50,
                        CompletedAt = null,
                    },
            };

        Assert.True(Present(resolved).ResetEnabled);
        Assert.False(Present(unresolved).ResetEnabled);
        Assert.False(Present(running).ResetEnabled);
        Assert.True(Present(running).ApiKeyEnabled);
        Assert.False(Present(running).ClearApiKeyEnabled);
        Assert.Contains(
            "Recovery override",
            Present(running).ApiKeyHelp);
        Assert.Contains(
            "terminal status",
            Present(running).ResetHelp);
    }

    [Fact]
    public void SuccessfulStatusForAnotherTaskCannotUnlockTheNextStage()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "A pavilion",
            20_000,
            false,
            "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = prepared,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_other123",
                        "text_to_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.False(state.HasSuccessfulGeneration);
        Assert.False(presentation.ConvertEnabled);
        Assert.False(presentation.ResetEnabled);
        Assert.Contains(
            "Evidence mismatch",
            presentation.GenerationStatus);
        Assert.Null(presentation.GenerationProgress);
    }

    [Fact]
    public void SameUuidRetryCopyRemainsFailClosed()
    {
        const string operationId =
            "ffffffff-ffff-4fff-8fff-ffffffffffff";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = new Tripo.HostUi.PreparedTextGeneration(
                    "A pavilion",
                    20_000,
                    false,
                    operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus = ResumableOperation(operationId),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.True(presentation.GenerateEnabled);
        Assert.Equal("Retry same UUID", presentation.GenerateText);
        Assert.Equal(operationId, presentation.LatestPreparedOperationId);
    }

    [Fact]
    public void ImageGenerationUsesGenericGenerationEvidenceAndProgress()
    {
        const string operationId =
            "f1f1f1f1-f1f1-41f1-81f1-f1f1f1f1f1f1";
        const string taskId = "task_image123";
        const string imageSha256 =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedImageGeneration =
                    new Tripo.HostUi.PreparedImageGeneration(
                        new Tripo.Bridge.StagedImageTransfer(
                            "12121212-1212-4121-8121-121212121212",
                            imageSha256,
                            1_024,
                            "image/png"),
                        20_000,
                        true,
                        operationId,
                        "v3"),
                ImageGenerationDispatchAttempted = true,
                ImageGenerationReceipt =
                    new Tripo.Bridge.HostControlImageTaskCreationReceipt(
                        operationId,
                        taskId,
                        "v3",
                        imageSha256),
                ImageGenerationOperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        operationId,
                        "image_task_creation",
                        "task_id_persisted",
                        null,
                        taskId,
                        null,
                        null,
                        true,
                        true,
                        false,
                        "Refresh the task status.",
                        DateTimeOffset.UnixEpoch),
                ImageGenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        taskId,
                        "image_to_model",
                        "running",
                        42,
                        null,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation = Present(state);

        Assert.Equal(operationId, presentation.GenerationOperationId);
        Assert.Equal(operationId, presentation.LatestPreparedOperationId);
        Assert.Equal(taskId, presentation.GenerationTaskId);
        Assert.Equal("Last known · Running · 42%", presentation.GenerationStatus);
        Assert.Equal(42, presentation.GenerationProgress);
        Assert.Contains(
            "Operation kind: image_task_creation",
            presentation.GenerationDiagnostic);
        Assert.Contains(
            "Task type: image_to_model",
            presentation.GenerationDiagnostic);
        Assert.True(presentation.RefreshGenerationEnabled);
        Assert.Contains(
            "no paid request",
            presentation.RefreshGenerationHelp);
        Assert.Equal("Request sent", presentation.GenerateText);
        Assert.Equal("Generating…", presentation.PipelineStageLabel);
        Assert.True(presentation.PipelineProgressVisible);
        Assert.Equal(13, presentation.PipelineProgress);
    }

    [Fact]
    public void ImageGenerationRetryUsesThePreparedOperationUuid()
    {
        const string operationId =
            "f2f2f2f2-f2f2-42f2-82f2-f2f2f2f2f2f2";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedImageGeneration =
                    new Tripo.HostUi.PreparedImageGeneration(
                        new Tripo.Bridge.StagedImageTransfer(
                            "23232323-2323-4232-8232-232323232323",
                            new string('b', 64),
                            2_048,
                            "image/webp"),
                        20_000,
                        false,
                        operationId),
                ImageGenerationDispatchAttempted = true,
                ImageGenerationOperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        operationId,
                        "image_task_creation",
                        "image_file_token_persisted",
                        null,
                        null,
                        null,
                        null,
                        false,
                        false,
                        true,
                        "Retry the same operation.",
                        DateTimeOffset.UnixEpoch),
            };

        Tripo.HostUi.TripoPanelPresentation presentation = Present(state);

        Assert.True(presentation.GenerateEnabled);
        Assert.Equal("Retry same UUID", presentation.GenerateText);
        Assert.Equal(operationId, presentation.GenerationOperationId);
        Assert.Equal(operationId, presentation.LatestPreparedOperationId);
        Assert.True(presentation.RefreshGenerationEnabled);
    }

    [Fact]
    public void SavedKeyCanOnlyBeClearedWhenTheSidecarSaysItCan()
    {
        Tripo.HostUi.TripoPanelState unknownPresence =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge.HostControlCredentialStatusReceipt(
                        true,
                        "environment",
                        false,
                        false,
                        "os-store",
                        false,
                        StoredKeyPresenceKnown: false),
            };
        Tripo.HostUi.TripoPanelState noClearCapability =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge.HostControlCredentialStatusReceipt(
                        true,
                        "environment",
                        true,
                        false,
                        "os-store",
                        false),
            };

        Assert.False(Present(unknownPresence).ClearApiKeyEnabled);
        Assert.Contains(
            "Saved-key presence unknown",
            Present(unknownPresence).CredentialStatus);
        Assert.False(Present(noClearCapability).ClearApiKeyEnabled);
        Assert.False(Present(noClearCapability).ApiKeyEnabled);
        Assert.Contains(
            "TRIPO_API_KEY",
            Present(noClearCapability).ApiKeyHelp);
    }

    [Fact]
    public void ImportControlsWaitForConversionAndMaterialsStayTruthful()
    {
        Tripo.HostUi.PreparedObjConversion withoutMaterials = new(
            "task_source123",
            20_000,
            false,
            "99999999-9999-4999-8999-999999999999");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedConversion = withoutMaterials,
                ConversionReceipt = MatchingConversionReceipt(
                    withoutMaterials),
                ConversionStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_conversion123",
                        "convert_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation withoutMaterialsPresentation =
            Present(state);
        Tripo.HostUi.TripoPanelPresentation withMaterialsPresentation =
            Present(
                state with
                {
                    PreparedConversion = withoutMaterials with
                    {
                        WithMaterials = true,
                    },
                });

        Assert.True(withoutMaterialsPresentation.NameEnabled);
        Assert.True(withoutMaterialsPresentation.ImportModeEnabled);
        Assert.False(withoutMaterialsPresentation.ApplyMaterialsEnabled);
        Assert.True(withMaterialsPresentation.ApplyMaterialsEnabled);
    }

    [Fact]
    public void GenerationRefreshRemainsAvailableDuringRecoveryBlock()
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "A pavilion",
            20_000,
            false,
            "11111111-1111-4111-8111-111111111111");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = prepared,
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                state,
                recovery: RecoveryWithGeneration());

        Assert.True(presentation.RecoveryHasBlock);
        Assert.True(presentation.RefreshGenerationEnabled);
        Assert.Contains(
            "no paid request",
            presentation.RefreshGenerationHelp);
        Assert.False(presentation.GenerateEnabled);
    }

    [Fact]
    public void FailedRefreshMarksPriorEvidenceAndHidesNormalProgressBarValue()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationReceipt = MatchingGenerationReceipt(),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        "running",
                        42,
                        null,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                state,
                generationRefreshFailed: true);

        Assert.Contains(
            "Last refresh failed",
            presentation.GenerationStatus);
        Assert.Contains(
            "prior successful refresh",
            presentation.GenerationDiagnostic);
        Assert.Null(presentation.GenerationProgress);
    }

    [Fact]
    public void FailedRemoteReadKeepsNewJournalEvidenceDistinct()
    {
        const string operationId =
            "12121212-1212-4121-8121-121212121212";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "A pavilion",
                        20_000,
                        false,
                        operationId),
                GenerationDispatchAttempted = true,
                GenerationOperationStatus =
                    ResumableOperation(operationId),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                state,
                generationRefreshFailed: true,
                generationJournalUpdatedOnFailedRefresh: true);

        Assert.Contains(
            "Current journal evidence was updated",
            presentation.GenerationStatus);
        Assert.Contains(
            "Operation evidence is current",
            presentation.GenerationDiagnostic);
        Assert.DoesNotContain(
            "prior successful refresh",
            presentation.GenerationDiagnostic);
    }

    [Theory]
    [InlineData("success", 83)]
    [InlineData("failed", 100)]
    public void TerminalTaskProgressNeverRendersAsAnActiveProgressBar(
        string status,
        int progress)
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationReceipt = MatchingGenerationReceipt(),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        status,
                        progress,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Null(presentation.GenerationProgress);
        Assert.Contains(
            $"Provider reported {progress}%",
            presentation.GenerationStatus);
        Assert.Contains(
            $"Task progress reported: {progress}%",
            presentation.GenerationDiagnostic);
    }

    [Fact]
    public void UnknownTaskStatusNeverMasqueradesAsSuccess()
    {
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                GenerationReceipt = MatchingGenerationReceipt(),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        "Success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Contains(
            "Status requires inspection",
            presentation.GenerationStatus);
        Assert.Contains(
            "Raw: Success",
            presentation.GenerationStatus);
        Assert.False(presentation.ConvertEnabled);
    }

    [Theory]
    [InlineData("banned")]
    [InlineData("expired")]
    public void ProviderTerminalFailuresAllowAConfirmedNewWorkflow(
        string terminalStatus)
    {
        Tripo.HostUi.PreparedTextGeneration prepared = new(
            "A pavilion",
            20_000,
            false,
            "22222222-2222-4222-8222-222222222222");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedGeneration = prepared,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        prepared.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        terminalStatus,
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.True(presentation.ResetEnabled);
        Assert.False(state.HasNonTerminalRemoteTask);
    }

    [Fact]
    public void MissingCredentialDisablesEveryRemoteStageButAllowsRecoveryKey()
    {
        Tripo.HostUi.PreparedTextGeneration generation = new(
            "A pavilion",
            20_000,
            true,
            "33333333-3333-4333-8333-333333333333");
        Tripo.HostUi.PreparedObjConversion conversion = new(
            "task_source123",
            20_000,
            true,
            "44444444-4444-4444-8444-444444444444");
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge.HostControlCredentialStatusReceipt(
                        false,
                        "none",
                        false,
                        true,
                        "windows-credential-manager",
                        false),
                PreparedGeneration = generation,
                GenerationDispatchAttempted = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        generation.OperationId,
                        "task_source123",
                        "v3"),
                GenerationStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_source123",
                        "text_to_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
                PreparedConversion = conversion,
                ConversionDispatchAttempted = true,
                ConversionReceipt =
                    new Tripo.Bridge.HostControlObjConversionCreationReceipt(
                        conversion.OperationId,
                        conversion.SourceTaskId,
                        "task_conversion123",
                        "OBJ"),
                ConversionStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_conversion123",
                        "convert_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.True(presentation.ApiKeyEnabled);
        Assert.False(presentation.ConvertEnabled);
        Assert.False(presentation.ImportEnabled);
        Assert.False(presentation.RefreshGenerationEnabled);
        Assert.False(presentation.RefreshConversionEnabled);
        Assert.Contains("API key", presentation.ConvertHelp);
        Assert.Contains("API key", presentation.ImportHelp);
        Assert.Contains("API key", presentation.RefreshGenerationHelp);
    }

    [Fact]
    public void AmbiguousPaidDispatchCanInspectLocalJournalWithoutAKey()
    {
        const string operationId =
            "34343434-3434-4343-8343-343434343434";
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                CredentialStatus =
                    new Tripo.Bridge.HostControlCredentialStatusReceipt(
                        false,
                        "none",
                        false,
                        true,
                        "windows-credential-manager",
                        false),
                PreparedGeneration =
                    new Tripo.HostUi.PreparedTextGeneration(
                        "A pavilion",
                        20_000,
                        false,
                        operationId),
                GenerationDispatchAttempted = true,
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.True(presentation.RefreshGenerationEnabled);
        Assert.Contains(
            "local generation journal",
            presentation.RefreshGenerationHelp);
        Assert.True(presentation.ApiKeyEnabled);
        Assert.Contains(
            "exact original API key",
            presentation.ApiKeyHelp);
        Assert.False(presentation.ClearApiKeyEnabled);
    }

    [Fact]
    public void UnconfirmedImportReceiptIsAWarningNotASuccess()
    {
        Tripo.HostUi.PreparedObjImport prepared = new(
            "task_conversion123",
            "Pavilion",
            DocumentSessionId,
            "55555555-5555-4555-8555-555555555555",
            "family",
            true);
        Tripo.HostUi.TripoPanelState state =
            ReadyState() with
            {
                PreparedConversion =
                    new Tripo.HostUi.PreparedObjConversion(
                        "task_source123",
                        20_000,
                        true,
                        "45454545-4545-4454-8454-454545454545"),
                ConversionReceipt =
                    new Tripo.Bridge
                        .HostControlObjConversionCreationReceipt(
                            "45454545-4545-4454-8454-454545454545",
                            "task_source123",
                            "task_conversion123",
                            "OBJ"),
                ConversionStatus =
                    new Tripo.Bridge.HostControlTaskStatusReceipt(
                        "task_conversion123",
                        "convert_model",
                        "success",
                        100,
                        null,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        null,
                        null),
                PreparedImport = prepared,
                ImportDispatchAttempted = true,
                ImportReceipt =
                    new Tripo.Bridge.HostControlObjTaskImportReceipt(
                        prepared.OperationId,
                        prepared.ConversionTaskId,
                        null,
                        new Tripo.Bridge.HostImportReceipt(
                            "revit",
                            DocumentSessionId,
                            prepared.OperationId,
                            "unknown-object",
                            3,
                            1,
                            0,
                            "rolled_back",
                            "family",
                            0,
                            0,
                            null),
                        []),
            };

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(state);

        Assert.Equal(
            Tripo.HostUi.TripoPanelNoticeKind.Warning,
            presentation.ResultKind);
        Assert.StartsWith("Warning ·", presentation.ResultStatus);
        Assert.Contains(
            "requires inspection",
            presentation.ResultStatus);
        Assert.DoesNotContain(
            "Imported into",
            presentation.ResultStatus);
        Assert.True(presentation.ImportEnabled);
        Assert.Equal("Retry same UUID", presentation.ImportText);
        Assert.False(presentation.ResetEnabled);
        Assert.Contains("same UUID", presentation.ImportHelp);
        Assert.Contains("same Tripo account", presentation.ApiKeyHelp);
    }

    [Fact]
    public void RecoveredImportShowsEveryManualReconciliationIdentity()
    {
        Tripo.HostUi.TripoPanelRecoveryLoadResult recovery =
            new(
                [
                    new Tripo.HostUi.LoadedTripoPanelRecoveryHint(
                        "import-recovery.json",
                        new Tripo.HostUi.TripoPanelRecoveryHint(
                            Tripo.HostUi.TripoPanelRecoveryStore
                                .CurrentSchemaVersion,
                            "recovery-id",
                            "revit",
                            123,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch,
                            null,
                            null,
                            new Tripo.HostUi.TripoPanelImportRecoveryHint(
                                "66666666-6666-4666-8666-666666666666",
                                "task_conversion123",
                                DocumentSessionId,
                                "Recovered Pavilion",
                                "native",
                                true,
                                true,
                                false))),
                ],
                []);

        Tripo.HostUi.TripoPanelPresentation presentation =
            Present(
                ReadyState(),
                recovery: recovery);

        Assert.Contains(
            "task_conversion123",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Recovered Pavilion",
            presentation.RecoveryDetails);
        Assert.Contains(
            "native (resolves to Revit family)",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Apply baked materials requested: yes",
            presentation.RecoveryDetails);
        Assert.Contains(
            "Confirmed import receipt known: false",
            presentation.RecoveryDetails);
    }

    private static Tripo.HostUi.TripoPanelPresentation Present(
        Tripo.HostUi.TripoPanelState state,
        Tripo.HostUi.TripoPanelRecoveryLoadResult? recovery = null,
        bool recoveryReviewed = false,
        string? recoveryInspection = null,
        string prompt = "A buildable architectural pavilion",
        string faceLimit = "20000",
        string objectName = "Tripo Model",
        string? currentAction = null,
        bool generationRefreshFailed = false,
        bool conversionRefreshFailed = false,
        bool generationJournalUpdatedOnFailedRefresh = false,
        bool conversionJournalUpdatedOnFailedRefresh = false,
        bool recoveryInspectionHadFailures = false,
        string? pipelineStageLabel = null,
        int? pipelineProgressOverride = null) =>
        Tripo.HostUi.TripoPanelPresentation.Create(
            state,
            recovery ?? Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
            recoveryInspection,
            recoveryReviewed,
            prompt,
            faceLimit,
            objectName,
            "Revit",
            currentAction,
            generationRefreshFailed,
            conversionRefreshFailed,
            generationJournalUpdatedOnFailedRefresh,
            conversionJournalUpdatedOnFailedRefresh,
            recoveryInspectionHadFailures,
            pipelineStageLabel,
            pipelineProgressOverride);

    private static Tripo.HostUi.TripoPanelState ReadyState() =>
        Tripo.HostUi.TripoPanelState.Initial with
        {
            Connected = true,
            Context = new Tripo.Bridge.HostContextReceipt(
                "revit",
                "2026",
                42,
                DocumentSessionId,
                "Example Project",
                "feet",
                []),
            CredentialStatus =
                new Tripo.Bridge.HostControlCredentialStatusReceipt(
                    true,
                    "store",
                    true,
                    true,
                    "windows-credential-manager",
                    false),
        };

    private static Tripo.HostUi.TripoPanelRecoveryLoadResult
        RecoveryWithGeneration() =>
        new(
            [
                new Tripo.HostUi.LoadedTripoPanelRecoveryHint(
                    "recovery.json",
                    new Tripo.HostUi.TripoPanelRecoveryHint(
                        Tripo.HostUi.TripoPanelRecoveryStore
                            .CurrentSchemaVersion,
                        "recovery-id",
                        "revit",
                        123,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                            true,
                            null,
                            "outcome_unknown",
                            false,
                            false),
                        null,
                        null)),
            ],
            []);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ResumableOperation(string operationId) =>
        new(
            operationId,
            "text_generation",
            "prepared",
            null,
            null,
            null,
            null,
            false,
            false,
            true,
            "Retry the same operation.",
            DateTimeOffset.UnixEpoch);

    private static Tripo.Bridge.HostControlTextTaskCreationReceipt
        MatchingGenerationReceipt() =>
        new(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "task_source123",
            "v3");

    private static Tripo.Bridge.HostControlObjConversionCreationReceipt
        MatchingConversionReceipt(
            Tripo.HostUi.PreparedObjConversion prepared) =>
        new(
            prepared.OperationId,
            prepared.SourceTaskId,
            "task_conversion123",
            "OBJ");
}

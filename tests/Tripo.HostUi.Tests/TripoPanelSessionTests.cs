using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelSessionTests
{
    private static readonly SemaphoreSlim LocalDataEnvironmentGate = new(1, 1);

    [Fact]
    public async Task UnconfirmedGenerationMakesNoPaidCallButDisplaysOperationId()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();

        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a mass timber transit pavilion",
                20_000,
                withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: false));

        Assert.Equal(prepared.OperationId, session.State.PreparedGeneration?.OperationId);
        Assert.Equal(0, client.CreateTextCalls);
    }

    [Fact]
    public async Task UnconfirmedImageGenerationMakesNoPaidCallButKeepsIdentity()
    {
        const string imageUrl = "https://images.example.test/chair.png";
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                imageUrl,
                12_000,
                withMaterials: true,
                model: "v2.5-20250123");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: false));

        Assert.Equal(1, client.StageImageCalls);
        Assert.Equal(imageUrl, client.LastStageImageRequest?.ImageUrl);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedImageGeneration?.OperationId);
        Assert.Equal(client.StagedImage, prepared.Image);
        Assert.Equal(0, client.CreateImageCalls);
        Assert.True(session.State.HasImageGenerationState);
        Assert.False(session.State.ImageGenerationDispatchAttempted);
    }

    [Fact]
    public async Task ImageGenerationDispatchBindsExactRequestReceiptAndStatus()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                "https://images.example.test/pavilion.webp",
                22_000,
                withMaterials: true,
                model: "P1-20260311");

        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Tripo.Bridge.HostControlCreateImageTaskRequest request =
            Assert.IsType<Tripo.Bridge.HostControlCreateImageTaskRequest>(
                client.LastImageRequest);
        Assert.Equal(prepared.OperationId, request.OperationId);
        Assert.Equal(prepared.Image, request.Image);
        Assert.Equal(22_000, request.FaceLimit);
        Assert.True(request.WithMaterials);
        Assert.True(request.ConfirmExternalCost);
        Assert.False(request.RequireExistingOperation);
        Assert.Equal("P1-20260311", request.Model);
        Assert.Equal(
            prepared.OperationId,
            session.State.ImageGenerationReceipt?.OperationId);
        Assert.Equal(
            prepared.Model,
            session.State.ImageGenerationReceipt?.Model);
        Assert.Equal(
            prepared.Image.Sha256,
            session.State.ImageGenerationReceipt?.ImageSha256);
        Assert.Equal(client.ImageTaskId, session.State.DurableGenerationTaskId);
        Assert.Equal(
            "image_to_model",
            session.State.ImageGenerationStatus?.Type);
        Assert.True(session.State.HasSuccessfulGeneration);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("model")]
    [InlineData("sha256")]
    public async Task ImageGenerationRejectsReceiptIdentityMismatch(
        string mismatch)
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                "https://images.example.test/chair.png",
                10_000,
                withMaterials: false,
                model: "v2.5-20250123");
        if (mismatch == "operation")
        {
            client.ImageReceiptOperationIdOverride =
                Guid.NewGuid().ToString("D");
        }
        else if (mismatch == "model")
        {
            client.ImageReceiptModelOverride = "v3.0-20250812";
        }
        else
        {
            client.ImageReceiptSha256Override = new string('b', 64);
        }

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedImageGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("receipt did not match", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedImageGeneration?.OperationId);
        Assert.True(session.State.ImageGenerationDispatchAttempted);
        Assert.Null(session.State.ImageGenerationReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task LostImageResponseResumesFileTokenWithTheSameUuid()
    {
        FakeHostControlClient client = new()
        {
            FailFirstImageResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                "https://images.example.test/chair.jpg",
                10_000,
                withMaterials: false,
                model: "v2.5-20250123");
        client.OperationStatus =
            ResumableImageOperationStatus(prepared.OperationId);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: true));
        InvalidOperationException refresh =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("not durable", refresh.Message);
        Assert.True(session.State.ImageGenerationRetryAllowed);
        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Assert.Equal(2, client.CreateImageCalls);
        Assert.All(
            client.ImageRequests,
            request => Assert.Equal(
                prepared.OperationId,
                request.OperationId));
        Assert.False(client.ImageRequests[0].RequireExistingOperation);
        Assert.True(client.ImageRequests[1].RequireExistingOperation);
        Assert.Equal(
            client.StagedImage.Sha256,
            client.ImageRequests[1].Image.Sha256);
        Assert.Equal(
            "image_to_model",
            session.State.CurrentGenerationStatus?.Type);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("generation")]
    public async Task DefinitiveImageCredentialRejectionClearsImageStage(
        string failureStage)
    {
        FakeHostControlClient client = new()
        {
            FailFirstImageResponse = true,
            FirstImageFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialRejectedError,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                "https://images.example.test/chair.png",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            RejectedOperationStatus(
                prepared.OperationId,
                "image_task_creation") with
            {
                FailureStage = failureStage,
            };

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: true));

        Assert.Null(session.State.PreparedImageGeneration);
        Assert.False(session.State.ImageGenerationDispatchAttempted);
        Assert.Null(session.State.ImageGenerationOperationStatus);
        Assert.False(session.State.HasUnresolvedPaidDispatch);
        Assert.False(session.State.HasWorkflowState);
    }

    [Fact]
    public async Task LocalImagePreparationCleansTransferOnResetAndFailure()
    {
        await LocalDataEnvironmentGate.WaitAsync();
        string root = CreateTemporaryRoot();
        string? previousRoot = Environment.GetEnvironmentVariable(
            Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
                root);
            string imagePath = Path.Combine(root, "input.png");
            await File.WriteAllBytesAsync(
                imagePath,
                [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
            await using Tripo.HostUi.TripoPanelSession session =
                CreateSession(new FakeHostControlClient());
            await session.ConnectAsync();

            Tripo.HostUi.PreparedImageGeneration prepared =
                await session.PrepareLocalImageGenerationAsync(
                    imagePath,
                    10_000,
                    withMaterials: false);
            string transferDirectory =
                Tripo.Bridge.BridgePaths.GetImageTransferDirectory();
            string transferPath = Path.Combine(
                transferDirectory,
                prepared.Image.TransferId + ".image");
            Assert.True(File.Exists(transferPath));

            session.ResetWorkflow();

            Assert.False(File.Exists(transferPath));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => session.PrepareLocalImageGenerationAsync(
                    imagePath,
                    faceLimit: 0,
                    withMaterials: false));
            Assert.Empty(Directory.GetFiles(transferDirectory, "*.image"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Tripo.Bridge.BridgePaths.LocalDataEnvironmentVariable,
                previousRoot);
            Directory.Delete(root, recursive: true);
            LocalDataEnvironmentGate.Release();
        }
    }

    [Fact]
    public async Task LostGenerationResponseRetriesTheSameOperationIdentity()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        InvalidOperationException earlyRetry =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
        Assert.Contains("Refresh", earlyRetry.Message);
        Assert.Equal(1, client.CreateTextCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RefreshGenerationStatusAsync());
        Assert.True(session.State.GenerationRetryAllowed);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        Assert.Equal(2, client.CreateTextCalls);
        Assert.All(
            client.TextRequests,
            request => Assert.Equal(prepared.OperationId, request.OperationId));
        Assert.Equal(
            prepared,
            session.State.PreparedGeneration);
        Assert.Equal(
            "task_source123",
            session.State.GenerationReceipt?.TaskId);
    }

    [Fact]
    public async Task RefreshedGenerationJournalCannotSpliceAnotherTaskId()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        client.OperationStatus =
            DurableOperationStatus(
                prepared.OperationId,
                "task_source123");
        await session.RefreshGenerationStatusAsync();

        client.OperationStatus =
            DurableOperationStatus(
                prepared.OperationId,
                "task_other123");
        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("journal-state semantics", error.Message);
        Assert.Equal(
            "task_source123",
            session.State.GenerationOperationStatus?.CreatedTaskId);
        Assert.Equal(
            "task_source123",
            session.State.GenerationStatus?.TaskId);
        Assert.True(session.State.HasSuccessfulGeneration);
    }

    [Fact]
    public async Task DocumentSwitchDoesNotBlockPaidDispatch()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(
            prepared.OperationId,
            session.State.GenerationReceipt?.OperationId);
    }

    [Fact]
    public async Task ReconnectRetargetsDocumentWithoutDiscardingRemoteWorkflow()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        await session.ConnectAsync();

        Assert.Equal(
            client.CurrentSessionId,
            session.State.Context?.DocumentSessionId);
        Assert.Equal(prepared, session.State.PreparedGeneration);
    }

    [Fact]
    public async Task UnresolvedPaidDispatchCannotBeReset()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        InvalidOperationException resetError =
            Assert.Throws<InvalidOperationException>(session.ResetWorkflow);

        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Contains("unresolved", resetError.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
    }

    [Fact]
    public async Task UnresolvedPaidDispatchOnlyAllowsSessionRecoveryKey()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException persistentSetError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "original-recovery-key",
                    persist: true));
        InvalidOperationException clearError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());
        await session.SetApiKeyAsync(
            "original-recovery-key",
            persist: false);

        Assert.Contains("session-only", persistentSetError.Message);
        Assert.Contains("cannot change", clearError.Message);
        Assert.Equal("original-recovery-key", client.LastApiKey);
        Assert.Equal(0, client.ClearApiKeyCalls);
    }

    [Fact]
    public async Task CurrentRecoveryHintDoesNotBlockItsOwnSessionKeyRestore()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            await session.SetApiKeyAsync(
                "original-recovery-key",
                persist: false);
            InvalidOperationException persistentError =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => session.SetApiKeyAsync(
                        "original-recovery-key",
                        persist: true));

            Assert.Equal("original-recovery-key", client.LastApiKey);
            Assert.Contains("session-only", persistentError.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalCredentialFailureClearsFalsePaidDispatchRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialInvalidError,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.False(session.State.GenerationDispatchAttempted);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));

            await session.SetApiKeyAsync("replacement-key", persist: true);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            Assert.Equal("replacement-key", client.LastApiKey);
            Assert.Equal(2, client.CreateTextCalls);
            Assert.Equal(
                "task_source123",
                session.State.GenerationReceipt?.TaskId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialFailureDuringRetryPreservesExistingRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                ResumableOperationStatus(prepared.OperationId);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());
            client.TextFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialInvalidError;

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.True(session.State.GenerationDispatchAttempted);
            Assert.True(session.State.HasUnresolvedPaidDispatch);
            Assert.True(session.State.GenerationRetryAllowed);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCredentialRejectionStartsANewGenerationIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration rejected =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "text_task_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Null(session.State.PreparedGeneration);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await session.SetApiKeyAsync("replacement-key", persist: true);
            Tripo.HostUi.PreparedTextGeneration replacement =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            Assert.NotEqual(rejected.OperationId, replacement.OperationId);
            Assert.Equal(2, client.CreateTextCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedCredentialRejectionCannotUnlockGeneration(
        bool wrongOperationId)
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                AllowMismatchedOperationStatusReceipt = wrongOperationId,
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    wrongOperationId
                        ? Guid.NewGuid().ToString("D")
                        : prepared.OperationId,
                    wrongOperationId
                        ? "text_task_creation"
                        : "obj_conversion_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.True(session.State.GenerationDispatchAttempted);
            Assert.True(session.State.HasUnresolvedPaidDispatch);
            Assert.Null(session.State.GenerationOperationStatus);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedReceiptWithFailureStageCannotUnlockGeneration()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
            FirstTextFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialRejectedError,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            RejectedOperationStatus(
                prepared.OperationId,
                "text_task_creation") with
            {
                FailureStage = "generation",
            };

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Null(session.State.GenerationOperationStatus);
    }

    [Fact]
    public async Task LostResponseRefreshClearsDurableGenerationRejection()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration rejected =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "text_task_creation");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.Null(session.State.PreparedGeneration);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.False(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await session.SetApiKeyAsync("replacement-key", persist: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderCredentialRejectionClearsOnlyConversionStage()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            Tripo.HostUi.PreparedObjConversion rejected =
                session.PrepareConversion(10_000, withMaterials: false);
            client.FailFirstConversionResponse = true;
            client.FirstConversionFailureCode =
                Tripo.Bridge.HostControlConstants.CredentialRejectedError;
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "obj_conversion_creation",
                    rejected.SourceTaskId);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));

            Assert.NotNull(session.State.GenerationReceipt);
            Assert.NotNull(session.State.GenerationStatus);
            Assert.Null(session.State.PreparedConversion);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.False(
                store.LoadCredentialMutationBlocks(
                    excludeCurrentStoreHint: true).HasBlock);
            string recoveryFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.DoesNotContain(
                rejected.OperationId,
                File.ReadAllText(recoveryFile),
                StringComparison.Ordinal);
            await session.SetApiKeyAsync("replacement-key", persist: false);
            Tripo.HostUi.PreparedObjConversion replacement =
                session.PrepareConversion(10_000, withMaterials: false);
            await session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);

            Assert.NotEqual(rejected.OperationId, replacement.OperationId);
            Assert.Equal(2, client.CreateConversionCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LostConversionResponseRefreshClearsOnlyRejectedConversion()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            Tripo.HostUi.PreparedObjConversion rejected =
                session.PrepareConversion(10_000, withMaterials: false);
            client.FailFirstConversionResponse = true;

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));
            client.OperationStatus =
                RejectedOperationStatus(
                    rejected.OperationId,
                    "obj_conversion_creation",
                    rejected.SourceTaskId);

            InvalidOperationException rejection =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => session.RefreshConversionStatusAsync());

            Assert.Contains("definitively rejected", rejection.Message);
            Assert.NotNull(session.State.GenerationReceipt);
            Assert.NotNull(session.State.GenerationStatus);
            Assert.Null(session.State.PreparedConversion);
            Assert.False(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.False(
                store.LoadCredentialMutationBlocks(
                    excludeCurrentStoreHint: true).HasBlock);
            string recoveryFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.DoesNotContain(
                rejected.OperationId,
                File.ReadAllText(recoveryFile),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWhileVerifyingCredentialRejectionKeepsRecovery()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
                FirstTextFailureCode =
                    Tripo.Bridge.HostControlConstants.CredentialRejectedError,
                OperationStatusEntered =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                ContinueOperationStatus =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            client.OperationStatus =
                RejectedOperationStatus(
                    prepared.OperationId,
                    "text_task_creation");
            using CancellationTokenSource cancellation = new();

            Task dispatch = session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true,
                cancellation.Token);
            await client.OperationStatusEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dispatch);
            Assert.True(session.State.HasUnresolvedPaidDispatch);
            Assert.True(store.LoadCredentialMutationBlocks().HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptedTaskMustReachTerminalStateBeforeKeyChangeOrReset()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        InvalidOperationException resetError =
            Assert.Throws<InvalidOperationException>(session.ResetWorkflow);
        InvalidOperationException clearError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());

        Assert.True(session.State.HasNonTerminalRemoteTask);
        Assert.Contains("terminal status", resetError.Message);
        Assert.Contains("terminal status", clearError.Message);
        Assert.Equal(0, client.ClearApiKeyCalls);

        InvalidOperationException persistentKeyError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "same-account-recovery-key",
                    persist: true));
        Assert.Contains(
            "session-only",
            persistentKeyError.Message);

        await session.SetApiKeyAsync(
            "same-account-recovery-key",
            persist: false);
        Assert.Equal(
            "same-account-recovery-key",
            client.LastApiKey);

        await session.RefreshGenerationStatusAsync();
        Assert.False(session.State.HasNonTerminalRemoteTask);

        session.ResetWorkflow();

        Assert.False(session.State.HasWorkflowState);
    }

    [Fact]
    public async Task SuccessfulGenerationKeepsCredentialsBoundUntilReset()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Assert.True(session.State.HasSuccessfulGeneration);
        Assert.True(session.State.RequiresCredentialRecovery);
        InvalidOperationException persistentError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "same-account-recovery-key",
                    persist: true));
        InvalidOperationException clearError =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());
        await session.SetApiKeyAsync(
            "same-account-recovery-key",
            persist: false);

        Assert.Contains("session-only", persistentError.Message);
        Assert.Contains("account-bound workflow", clearError.Message);
        Assert.Equal(0, client.ClearApiKeyCalls);

        session.ResetWorkflow();
        await session.SetApiKeyAsync(
            "replacement-after-reset",
            persist: true);
        Assert.Equal(
            "replacement-after-reset",
            client.LastApiKey);
    }

    [Fact]
    public async Task RefreshReconcilesDurableTaskIdAfterLostResponse()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        client.OperationStatus =
            DurableOperationStatus(prepared.OperationId, "task_source123");

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        await session.RefreshGenerationStatusAsync();

        Assert.True(session.State.GenerationOperationStatus?.TaskIdDurable);
        Assert.Equal(
            "task_source123",
            session.State.GenerationStatus?.TaskId);
        Assert.False(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task CanonicalTaskUuidsPassReceiptJournalAndStatusValidation()
    {
        const string generationTaskId =
            "ef731ad6-aeb0-4950-9a2e-2298359dfaf8";
        const string conversionTaskId =
            "31c84619-872d-4c3f-95f4-a5f4eb16f75d";
        FakeHostControlClient client = new()
        {
            TextTaskId = generationTaskId,
            ConversionTaskId = conversionTaskId,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);

        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();

        Assert.Equal(
            generationTaskId,
            session.State.GenerationReceipt?.TaskId);
        Assert.Equal(
            generationTaskId,
            session.State.GenerationOperationStatus?.CreatedTaskId);
        Assert.Equal(
            generationTaskId,
            session.State.GenerationStatus?.TaskId);

        session.PrepareConversion(
            10_000,
            withMaterials: false);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();

        Assert.Equal(
            conversionTaskId,
            session.State.ConversionReceipt?.ConversionTaskId);
        Assert.Equal(
            conversionTaskId,
            session.State.ConversionOperationStatus?.CreatedTaskId);
        Assert.Equal(
            conversionTaskId,
            session.State.ConversionStatus?.TaskId);
    }

    [Fact]
    public async Task GenerationCreationReceiptMismatchRemainsUnresolved()
    {
        FakeHostControlClient client = new()
        {
            TextReceiptOperationIdOverride =
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("did not match", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.True(session.State.GenerationDispatchAttempted);
        Assert.Null(session.State.GenerationReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task MalformedGenerationTaskIdRemainsUnresolved()
    {
        FakeHostControlClient client = new()
        {
            TextReceiptTaskIdOverride = "task_bad/identifier",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("valid Tripo task ID", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.True(session.State.GenerationDispatchAttempted);
        Assert.Null(session.State.GenerationReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task GenerationReceiptTaskIdMustMatchDurableJournal()
    {
        FakeHostControlClient client = new()
        {
            TextReceiptTaskIdOverride = "task_other123",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("journal-state semantics", error.Message);
        Assert.Null(session.State.GenerationReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task ConversionCreationReceiptMismatchRemainsUnresolved()
    {
        FakeHostControlClient client = new()
        {
            ConversionReceiptFormatOverride = "GLB",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjConversion prepared =
            session.PrepareConversion(
                10_000,
                withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("OBJ format", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedConversion?.OperationId);
        Assert.True(session.State.ConversionDispatchAttempted);
        Assert.Null(session.State.ConversionReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task MalformedConversionTaskIdRemainsUnresolved()
    {
        FakeHostControlClient client = new()
        {
            ConversionReceiptTaskIdOverride = "x",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjConversion prepared =
            session.PrepareConversion(
                10_000,
                withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("task-ID format", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedConversion?.OperationId);
        Assert.True(session.State.ConversionDispatchAttempted);
        Assert.Null(session.State.ConversionReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
    }

    [Fact]
    public async Task ConversionReceiptTaskIdMustMatchDurableJournal()
    {
        FakeHostControlClient client = new()
        {
            ConversionReceiptTaskIdOverride = "task_other123",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(
            10_000,
            withMaterials: false);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("journal-state semantics", error.Message);
        Assert.Null(session.State.ConversionReceipt);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.Equal(1, client.CreateConversionCalls);
    }

    [Fact]
    public async Task WrongTaskStatusIdentityCannotUnlockTheNextStage()
    {
        FakeHostControlClient client = new()
        {
            TaskStatusTypeOverride = "convert_model",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("task type", error.Message);
        Assert.Null(session.State.GenerationStatus);
        Assert.Throws<InvalidOperationException>(
            () => session.PrepareConversion(
                10_000,
                withMaterials: false));
    }

    [Fact]
    public async Task WrongOperationStatusIdentityIsNotRetained()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
            AllowMismatchedOperationStatusReceipt = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            DurableOperationStatus(
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                "task_source123");
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("operation-status receipt", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.Null(session.State.GenerationOperationStatus);
        Assert.Null(session.State.GenerationStatus);
    }

    [Fact]
    public async Task ContradictoryOperationTaskIdentityIsNotRetained()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId) with
            {
                CreatedTaskId = "task_source123",
            };
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("operation-status receipt", error.Message);
        Assert.Null(session.State.GenerationOperationStatus);
        Assert.Equal("Not created", Tripo.HostUi.TripoPanelPresentation
            .Create(
                session.State with
                {
                    GenerationOperationStatus =
                        ResumableOperationStatus(prepared.OperationId) with
                        {
                            CreatedTaskId = "task_source123",
                        },
                },
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty,
                recoveryInspection: null,
                recoveryReviewed: false,
                prompt: "a chair",
                faceLimitText: "10000",
                objectName: "Chair",
                hostDisplayName: "Revit",
                currentAction: null)
            .GenerationTaskId);
    }

    [Fact]
    public async Task OutcomeUnknownCannotClaimThatPaidCreationMayResume()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId) with
            {
                State = "outcome_unknown",
                MayHaveCreatedRemoteTask = true,
                CanResumeCreation = true,
                NextAction = "Do not resend the paid request.",
            };
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("journal-state semantics", error.Message);
        Assert.Null(session.State.GenerationOperationStatus);
        Assert.False(session.State.GenerationRetryAllowed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task ActiveDispatchStatusIsAcceptedButCannotAuthorizeRetry()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId) with
            {
                State = "dispatching",
                MayHaveCreatedRemoteTask = true,
                CanResumeCreation = false,
                OperationInProgress = true,
                NextAction = "Wait for the active local operation.",
            };
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        InvalidOperationException active =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("task ID is not durable", active.Message);
        Assert.Equal(
            "dispatching",
            session.State.GenerationOperationStatus?.State);
        Assert.True(
            session.State.GenerationOperationStatus?.OperationInProgress);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.False(session.State.GenerationRetryAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task OlderCredentialRejectionCannotUnlockPreparedRetry()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        DateTimeOffset preparedAt = DateTimeOffset.UtcNow;
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId) with
            {
                UpdatedAtUtc = preparedAt,
            };
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RefreshGenerationStatusAsync());
        Assert.True(session.State.GenerationRetryAllowed);

        client.TextFailureCode =
            Tripo.Bridge.HostControlConstants.CredentialRejectedError;
        client.OperationStatus =
            RejectedOperationStatus(
                prepared.OperationId,
                "text_task_creation") with
            {
                UpdatedAtUtc = preparedAt.AddSeconds(-1),
            };

        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));

        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedGeneration?.OperationId);
        Assert.Equal(
            "prepared",
            session.State.GenerationOperationStatus?.State);
        Assert.True(session.State.HasUnresolvedPaidDispatch);
        Assert.True(session.State.GenerationRetryAllowed);
        Assert.Equal(2, client.CreateTextCalls);
    }

    [Fact]
    public async Task OutcomeUnknownJournalCannotRegressToResumable()
    {
        FakeHostControlClient client = new()
        {
            FailFirstTextResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration prepared =
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId) with
            {
                State = "outcome_unknown",
                MayHaveCreatedRemoteTask = true,
                CanResumeCreation = false,
                FailureCode = "sidecar_unavailable",
                FailureMessage = "The provider outcome is unknown.",
                NextAction = "Do not resend the paid request.",
            };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RefreshGenerationStatusAsync());
        Assert.Equal(
            "outcome_unknown",
            session.State.GenerationOperationStatus?.State);

        client.OperationStatus =
            ResumableOperationStatus(prepared.OperationId);
        InvalidOperationException regression =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

        Assert.Contains("regressed", regression.Message);
        Assert.Equal(
            "outcome_unknown",
            session.State.GenerationOperationStatus?.State);
        Assert.False(session.State.GenerationRetryAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true));
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task RecoveryInspectionRejectsWrongOperationIdentity()
    {
        const string operationId =
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        FakeHostControlClient client = new()
        {
            AllowMismatchedOperationStatusReceipt = true,
            OperationStatus = DurableOperationStatus(
                "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                "task_source123"),
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.InspectPaidRecoveryAsync(
                    operationId,
                    "generation"));

        Assert.Contains("operation-status receipt", error.Message);
    }

    [Fact]
    public async Task GenerationAndConversionUseSeparateConfirmationsAndUuids()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedTextGeneration generation =
            session.PrepareGeneration("a chair", 10_000, withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        Tripo.HostUi.PreparedObjConversion conversion =
            session.PrepareConversion(10_000, withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: false));
        Assert.Equal(0, client.CreateConversionCalls);

        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);

        Assert.NotEqual(generation.OperationId, conversion.OperationId);
        Assert.True(client.LastTextRequest?.ConfirmExternalCost);
        Assert.True(client.LastConversionRequest?.ConfirmExternalCost);
        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(1, client.CreateConversionCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true));
        Assert.Equal(1, client.CreateConversionCalls);
    }

    [Fact]
    public async Task CompletedImportCannotEnterTheConnectorAgain()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport("Chair", "native", applyMaterials: true);

        await session.ImportPreparedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ImportPreparedAsync());

        Assert.Equal(1, client.ImportCalls);
    }

    [Fact]
    public async Task CreatePipelineRunsGenerateConvertAndImportWithoutManualRefresh()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        List<string> stages = [];
        Progress<Tripo.HostUi.TripoPipelineProgress> progress = new(
            update => stages.Add(update.StageLabel));

        await session.RunCreatePipelineAsync(
            "a pavilion",
            10_000,
            withMaterials: true,
            "Pavilion",
            "family",
            applyMaterials: true,
            userConfirmedExternalCost: true,
            placementMode: "origin",
            associateLevel: true,
            progress: progress);

        Assert.Equal(1, client.CreateTextCalls);
        Assert.Equal(1, client.CreateConversionCalls);
        Assert.Equal(1, client.ImportCalls);
        Assert.True(session.State.HasSuccessfulGeneration);
        Assert.True(session.State.HasSuccessfulConversion);
        Assert.True(session.State.HasConfirmedImportReceipt);
        Assert.Equal(
            "origin",
            session.State.PreparedImport?.PlacementMode);
        Assert.True(session.State.PreparedImport?.AssociateLevel);
        Assert.Contains(stages, stage => stage.Contains("Generat"));
        Assert.Contains(stages, stage => stage.Contains("Convert"));
        Assert.Contains(stages, stage => stage.Contains("Import") || stage == "Done");
    }

    [Fact]
    public async Task ImagePipelineRunsGenerateConvertAndImportWithoutManualRefresh()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.HostUi.PreparedImageGeneration prepared =
            await session.PrepareUrlImageGenerationAsync(
                "https://images.example.test/pavilion.png",
                18_000,
                withMaterials: true,
                model: "P1-20260311");

        await session.RunPreparedImagePipelineAsync(
            "Image Pavilion",
            "family",
            applyMaterials: true,
            userConfirmedExternalCost: true,
            placementMode: "origin",
            associateLevel: true);

        Assert.Equal(1, client.CreateImageCalls);
        Assert.Equal(0, client.CreateTextCalls);
        Assert.Equal(1, client.CreateConversionCalls);
        Assert.Equal(1, client.ImportCalls);
        Assert.Equal(
            prepared.OperationId,
            client.LastImageRequest?.OperationId);
        Assert.Equal(
            client.ImageTaskId,
            client.LastConversionRequest?.SourceTaskId);
        Assert.True(session.State.HasSuccessfulGeneration);
        Assert.True(session.State.HasSuccessfulConversion);
        Assert.True(session.State.HasConfirmedImportReceipt);
        Assert.Equal(
            "image_to_model",
            session.State.ImageGenerationStatus?.Type);
    }

    [Fact]
    public async Task PrepareImportPreservesAssociateLevelForWorkflowDowngrade()
    {
        const string warning =
            "associateLevel was downgraded for this import.";
        FakeHostControlClient client = new()
        {
            ImportWarningsOverride = [warning],
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a chair",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: false);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();

        Tripo.HostUi.PreparedObjImport prepared =
            session.PrepareImport(
                "Chair",
                "mesh",
                applyMaterials: false,
                associateLevel: true);

        Assert.True(prepared.AssociateLevel);
        await session.ImportPreparedAsync();
        Assert.True(client.LastImportRequest?.AssociateLevel);
        Assert.Equal([warning], session.State.ImportReceipt?.Warnings);
        Assert.Equal(
            [warning],
            Assert.Single(session.State.CompletedImports).Receipt.Warnings);
    }

    [Fact]
    public async Task CompletedConversionImportsIntoTwoDocumentsWithFreshUuids()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            CurrentDocumentTitle = "Project A",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();

        Tripo.HostUi.PreparedObjImport first =
            session.PrepareImport(
                "Pavilion",
                "native",
                applyMaterials: true);
        await session.ImportPreparedAsync();

        client.CurrentSessionId = Guid.NewGuid().ToString("D");
        client.CurrentDocumentTitle = "Project B";
        await session.ConnectAsync();

        Assert.Null(session.State.PreparedImport);
        Assert.Single(session.State.CompletedImports);
        Assert.True(session.State.HasSuccessfulConversion);
        Tripo.HostUi.PreparedObjImport second =
            session.PrepareImport(
                "Pavilion",
                "native",
                applyMaterials: true);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(client.CurrentSessionId, second.DocumentSessionId);
        Assert.Equal("Project B", second.TargetDocumentTitle);

        await session.ImportPreparedAsync();

        Assert.Equal(2, client.ImportCalls);
        Assert.Equal(2, session.State.CompletedImports.Count);
    }

    [Fact]
    public async Task UnresolvedImportDispatchCannotRetargetAnotherDocument()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            FailFirstImportResponse = true,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        string originalSessionId =
            session.State.Context!.DocumentSessionId;
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: false);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport(
            "Pavilion",
            "native",
            applyMaterials: false);
        await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
            () => session.ImportPreparedAsync());
        client.CurrentSessionId = Guid.NewGuid().ToString("D");

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ConnectAsync());

        Assert.Contains("import outcome is unresolved", error.Message);
        Assert.Equal(
            originalSessionId,
            session.State.Context?.DocumentSessionId);
    }

    [Fact]
    public async Task ImportReceiptIdentityMismatchRemainsUnresolved()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            ImportModeOverride = "mesh",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        Tripo.HostUi.PreparedObjImport prepared =
            session.PrepareImport(
                "Pavilion",
                "family",
                applyMaterials: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("identity", error.Message);
        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedImport?.OperationId);
        Assert.True(session.State.ImportDispatchAttempted);
        Assert.NotNull(session.State.ImportReceipt);
        Assert.False(session.State.HasConfirmedImportReceipt);
        Assert.True(session.State.HasUnresolvedDispatch);
        Assert.Throws<InvalidOperationException>(session.ResetWorkflow);
    }

    [Fact]
    public async Task UnconfirmedImportReceiptIsRetainedForInspection()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            ImportTransactionStatusOverride = "rolled_back",
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport(
            "Pavilion",
            "family",
            applyMaterials: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("did not confirm", error.Message);
        Assert.Equal(
            "rolled_back",
            session.State.ImportReceipt?.HostReceipt.TransactionStatus);
        Assert.Equal(
            "created-1",
            session.State.ImportReceipt?.HostReceipt.CreatedId);
        Assert.False(session.State.HasConfirmedImportReceipt);
        Assert.True(session.State.ImportRetryRequired);
        Assert.True(session.State.RequiresCredentialRecovery);
    }

    [Fact]
    public async Task InvalidImportReceiptCountsCannotReportSuccess()
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            ImportTriangleCountOverride = -1,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport(
            "Pavilion",
            "family",
            applyMaterials: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("invalid geometry", error.Message);
        Assert.Equal(
            -1,
            session.State.ImportReceipt?.HostReceipt.TriangleCount);
        Assert.False(session.State.HasConfirmedImportReceipt);
        Assert.True(session.State.ImportRetryRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged\nreceipt line")]
    public async Task MalformedImportWarningsCannotReportSuccess(
        string? warning)
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            ImportWarningsOverride =
                warning is null ? null : [warning],
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport(
            "Pavilion",
            "family",
            applyMaterials: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("invalid request warnings", error.Message);
        Assert.False(session.State.HasConfirmedImportReceipt);
        Assert.True(session.State.ImportRetryRequired);
    }

    [Theory]
    [InlineData("/tmp/pavilion.rfa")]
    [InlineData(@"C:\Tripo\pavilion.obj")]
    public async Task InvalidRevitFamilyPathCannotReportImportSuccess(
        string savedFamilyPath)
    {
        FakeHostControlClient client = new()
        {
            Host = "revit",
            SavedFamilyPathOverride = savedFamilyPath,
        };
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration(
            "a pavilion",
            10_000,
            withMaterials: true);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        await session.RefreshGenerationStatusAsync();
        session.PrepareConversion(10_000, withMaterials: true);
        await session.DispatchPreparedConversionAsync(
            userConfirmedExternalCost: true);
        await session.RefreshConversionStatusAsync();
        session.PrepareImport(
            "Pavilion",
            "family",
            applyMaterials: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ImportPreparedAsync());

        Assert.Contains("saved-family path", error.Message);
        Assert.Equal(
            savedFamilyPath,
            session.State.ImportReceipt?.HostReceipt.SavedFamilyPath);
        Assert.False(session.State.HasConfirmedImportReceipt);
        Assert.True(session.State.ImportRetryRequired);
    }

    [Fact]
    public async Task LostImportResponseBlocksSharedCredentialMutation()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            FakeHostControlClient ownerClient = new()
            {
                Host = "revit",
                FailFirstImportResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a pavilion",
                10_000,
                withMaterials: true);
            await owner.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await owner.RefreshGenerationStatusAsync();
            owner.PrepareConversion(10_000, withMaterials: true);
            await owner.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);
            await owner.RefreshConversionStatusAsync();
            owner.PrepareImport(
                "Pavilion",
                "family",
                applyMaterials: true);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.ImportPreparedAsync());

            Assert.True(owner.State.HasUnresolvedDispatch);
            Assert.True(owner.State.RequiresCredentialRecovery);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.SetApiKeyAsync(
                    "same-account-key",
                    persist: true));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.ClearApiKeyAsync());
            await owner.SetApiKeyAsync(
                "same-account-key",
                persist: false);

            FakeHostControlClient observerClient = new();
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await observer.ConnectAsync();
            InvalidOperationException globalBlock =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => observer.SetApiKeyAsync(
                        "unrelated-key",
                        persist: true));

            Assert.Contains("another Rhino/Revit panel", globalBlock.Message);
            Assert.Null(observerClient.LastApiKey);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialValueNeverEntersPanelState()
    {
        const string secret = "ui-secret-value";
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();

        await session.SetApiKeyAsync(secret, persist: true);
        string stateJson = System.Text.Json.JsonSerializer.Serialize(
            session.State,
            Tripo.Bridge.BridgeJson.Options);

        Assert.Equal(secret, client.LastApiKey);
        Assert.DoesNotContain(secret, stateJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, session.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StageDispatchStateDistinguishesFirstSendRetryAndCompletion()
    {
        const string documentSessionId =
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        Tripo.HostUi.PreparedTextGeneration generation = new(
            "a chair",
            10_000,
            false,
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Tripo.HostUi.TripoPanelState firstSend =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                PreparedGeneration = generation,
            };

        Assert.True(firstSend.CanDispatchPreparedGeneration);
        Assert.False(firstSend.GenerationRetryRequired);
        Assert.False(firstSend.HasDurableGenerationTask);

        Tripo.HostUi.TripoPanelState retry = firstSend with
        {
            GenerationDispatchAttempted = true,
        };
        Assert.True(retry.CanDispatchPreparedGeneration);
        Assert.True(retry.GenerationRetryRequired);
        Assert.False(retry.GenerationRetryAllowed);

        retry = retry with
        {
            GenerationOperationStatus =
                ResumableOperationStatus(generation.OperationId),
        };
        Assert.True(retry.GenerationRetryAllowed);

        Tripo.HostUi.TripoPanelState complete = retry with
        {
            GenerationReceipt =
                new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                    generation.OperationId,
                    "task_source123",
                    "v3.1-20260211"),
        };
        Assert.False(complete.CanDispatchPreparedGeneration);
        Assert.False(complete.GenerationRetryRequired);
        Assert.True(complete.HasDurableGenerationTask);

        Tripo.HostUi.PreparedObjImport import = new(
            "task_conversion123",
            "Chair",
            documentSessionId,
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            "native",
            true);
        Tripo.HostUi.TripoPanelState importRetry = complete with
        {
            Context = new Tripo.Bridge.HostContextReceipt(
                "revit",
                "2026",
                42,
                documentSessionId,
                "Test.rvt",
                "feet",
                []),
            PreparedImport = import,
            ImportDispatchAttempted = true,
        };
        Assert.True(importRetry.CanDispatchPreparedImport);
        Assert.True(importRetry.ImportRetryRequired);

        Tripo.HostUi.TripoPanelState importComplete = importRetry with
        {
            ImportReceipt = new Tripo.Bridge.HostControlObjTaskImportReceipt(
                import.OperationId,
                import.ConversionTaskId,
                null,
                new Tripo.Bridge.HostImportReceipt(
                    "revit",
                    documentSessionId,
                    import.OperationId,
                    "created-1",
                    1,
                    1,
                    0,
                    "committed",
                    "family",
                    1,
                    1,
                    @"C:\Tripo\Chair.rfa"),
                []),
        };
        Assert.False(importComplete.CanDispatchPreparedImport);
        Assert.False(importComplete.ImportRetryRequired);
    }

    [Fact]
    public async Task CompletedGenerationCannotEnterTheConnectorAgain()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

        Assert.Contains("durable task ID", exception.Message);
        Assert.Equal(1, client.CreateTextCalls);
    }

    [Fact]
    public async Task ResetIsRejectedWhilePaidDispatchIsInFlight()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            TaskCompletionSource<bool> createTextEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueCreateText =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.CreateTextEntered = createTextEntered;
            client.ContinueCreateText = continueCreateText;

            Task dispatch = session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await createTextEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                InvalidOperationException blocked =
                    Assert.Throws<InvalidOperationException>(
                        session.ResetWorkflow);
                Assert.Contains("current panel operation", blocked.Message);
            }
            finally
            {
                continueCreateText.TrySetResult(true);
            }

            await dispatch;
            Assert.Equal(
                prepared.OperationId,
                session.State.GenerationReceipt?.OperationId);
            string hint = File.ReadAllText(
                Assert.Single(
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json")));
            Assert.Contains(
                prepared.OperationId,
                hint,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResetIsRejectedWhileStatusRefreshIsInFlight()
    {
        FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session = CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);
        await session.DispatchPreparedGenerationAsync(
            userConfirmedExternalCost: true);
        TaskCompletionSource<bool> taskStatusEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> continueTaskStatus =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TaskStatusEntered = taskStatusEntered;
        client.ContinueTaskStatus = continueTaskStatus;

        Task refresh = session.RefreshGenerationStatusAsync();
        await taskStatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(session.ResetWorkflow);
            Assert.Contains("current panel operation", blocked.Message);
        }
        finally
        {
            continueTaskStatus.TrySetResult(true);
        }

        await refresh;
        Assert.NotNull(session.State.PreparedGeneration);
        Assert.Equal("success", session.State.GenerationStatus?.Status);
    }

    [Fact]
    public async Task RecoveryLockContentionFailsBeforeConnectorAndReleasesGate()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                "rhino",
                ".recovery.lock");

            using (FileStream heldLock = new(
                       lockPath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Tripo.Bridge.BridgePaths.SetPrivateFileMode(lockPath);
                await Assert.ThrowsAsync<IOException>(
                    () => session.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            Assert.Equal(1, client.CreateTextCalls);
            Assert.NotNull(session.State.GenerationReceipt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialMutationLeaseBlocksPaidDispatchUntilMutationCompletes()
    {
        string root = CreateTemporaryRoot();
        try
        {
            TaskCompletionSource<bool> credentialMutationEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueCredentialMutation =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeHostControlClient credentialClient = new()
            {
                Host = "rhino",
                CredentialMutationEntered = credentialMutationEntered,
                ContinueCredentialMutation = continueCredentialMutation,
            };
            FakeHostControlClient dispatchClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession credentialSession =
                new(
                    new FakeConnector(credentialClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await using Tripo.HostUi.TripoPanelSession dispatchSession =
                new(
                    new FakeConnector(dispatchClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await credentialSession.ConnectAsync();
            await dispatchSession.ConnectAsync();
            dispatchSession.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

            Task mutation = credentialSession.SetApiKeyAsync(
                "replacement-key",
                persist: false);
            await credentialMutationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                await Assert.ThrowsAsync<IOException>(
                    () => dispatchSession.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
                Assert.Equal(0, dispatchClient.CreateTextCalls);
                Assert.False(dispatchSession.State.Busy);
            }
            finally
            {
                continueCredentialMutation.TrySetResult(true);
            }

            await mutation;
            await dispatchSession.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            Assert.Equal(1, dispatchClient.CreateTextCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PaidWorkflowBlocksSiblingCredentialMutationUntilCleared()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient dispatchClient = new()
            {
                Host = "rhino",
            };
            FakeHostControlClient credentialClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession dispatchSession =
                new(
                    new FakeConnector(dispatchClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await using Tripo.HostUi.TripoPanelSession credentialSession =
                new(
                    new FakeConnector(credentialClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await dispatchSession.ConnectAsync();
            await credentialSession.ConnectAsync();
            TaskCompletionSource<bool> createTextEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> continueCreateText =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatchClient.CreateTextEntered = createTextEntered;
            dispatchClient.ContinueCreateText = continueCreateText;
            dispatchSession.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);

            Task dispatch = dispatchSession.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await createTextEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            try
            {
                await Assert.ThrowsAsync<IOException>(
                    () => credentialSession.SetApiKeyAsync(
                        "replacement-key",
                        persist: false));
                Assert.Null(credentialClient.LastApiKey);
                Assert.False(credentialSession.State.Busy);
            }
            finally
            {
                continueCreateText.TrySetResult(true);
            }

            await dispatch;
            InvalidOperationException acceptedTaskBlock =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => credentialSession.SetApiKeyAsync(
                        "replacement-key",
                        persist: false));
            Assert.Contains(
                "another Rhino/Revit panel",
                acceptedTaskBlock.Message);
            await dispatchSession.RefreshGenerationStatusAsync();
            await dispatchSession.SetApiKeyAsync(
                "owner-terminal-key",
                persist: false);
            Assert.Equal(
                "owner-terminal-key",
                dispatchClient.LastApiKey);
            dispatchSession.ResetWorkflow();
            await credentialSession.SetApiKeyAsync(
                "replacement-key",
                persist: false);
            Assert.Equal(
                "replacement-key",
                credentialClient.LastApiKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingRecoveryLockSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                "rhino",
                ".recovery.lock");
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));

            Assert.Throws<InvalidOperationException>(
                () => session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.SetApiKeyAsync(
                    "session-key",
                    persist: false));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.ClearApiKeyAsync());
            Assert.Null(client.LastApiKey);
            Assert.Equal(0, client.ClearApiKeyCalls);

            File.Delete(lockPath);
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            File.Delete(lockPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingCredentialWorkflowLockSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new();
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            string lockPath = Path.Combine(
                root,
                "ui-recovery",
                ".credential-workflow.lock");
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "credential-workflow-lock"));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.SetApiKeyAsync(
                    "session-key",
                    persist: false));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            Assert.Null(client.LastApiKey);
            Assert.Equal(0, client.CreateTextCalls);
            Assert.False(session.State.Busy);
            File.Delete(lockPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingHintPersistenceFailureClearsBusyAndPreservesIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            string recoveryDirectory =
                Path.Combine(root, "ui-recovery", "rhino");
            string hintPath = Assert.Single(
                Directory.GetFiles(recoveryDirectory, "*.json"));
            string originalHint = File.ReadAllText(hintPath);
            string lockPath =
                Path.Combine(recoveryDirectory, ".recovery.lock");
            File.Delete(lockPath);
            File.CreateSymbolicLink(
                lockPath,
                Path.Combine(root, "missing", "lock"));
            client.OperationStatus = DurableOperationStatus(
                prepared.OperationId,
                "task_source123");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.False(session.State.Busy);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            Assert.True(session.State.GenerationDispatchAttempted);
            Assert.Equal(originalHint, File.ReadAllText(hintPath));

            File.Delete(lockPath);
            await session.RefreshGenerationStatusAsync();
            Assert.Equal("success", session.State.GenerationStatus?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DanglingRecoveryDestinationSymlinkFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(client),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));
            string hintPath = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            File.Delete(hintPath);
            File.CreateSymbolicLink(
                hintPath,
                Path.Combine(root, "missing", "hint.json"));
            client.OperationStatus = DurableOperationStatus(
                prepared.OperationId,
                "task_source123");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RefreshGenerationStatusAsync());

            Assert.False(session.State.Busy);
            Assert.Equal(
                prepared.OperationId,
                session.State.PreparedGeneration?.OperationId);
            File.Delete(hintPath);
            await session.RefreshGenerationStatusAsync();
            Assert.Equal("success", session.State.GenerationStatus?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryHintExcludesPromptCredentialAndUrlLikeText()
    {
        string root = CreateTemporaryRoot();
        try
        {
            const string secret = "recovery-secret-sentinel";
            const string prompt =
                "Authorization: Bearer prompt-sentinel https://example.invalid";
            FakeHostControlClient client = new()
            {
                FailFirstTextResponse = true,
            };
            string? jsonObservedAtConnectorEntry = null;
            client.BeforeCreateTextCall = _ =>
            {
                string observedFile = Assert.Single(
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json"));
                jsonObservedAtConnectorEntry =
                    File.ReadAllText(observedFile);
            };
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(new FakeConnector(client), store);
            await session.ConnectAsync();
            await session.SetApiKeyAsync(secret, persist: false);
            Tripo.HostUi.PreparedTextGeneration prepared =
                session.PrepareGeneration(
                    prompt,
                    10_000,
                    withMaterials: false);

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => session.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            string json = File.ReadAllText(file);
            string observedAtConnectorEntry =
                Assert.IsType<string>(jsonObservedAtConnectorEntry);
            Assert.Contains(
                prepared.OperationId,
                observedAtConnectorEntry,
                StringComparison.Ordinal);
            Assert.Contains(prepared.OperationId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(prompt, json, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", json, StringComparison.Ordinal);
            Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(file));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageRecoveryHintIsSchemaFourAndUsesImageInspectionStage()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            const string imageUrl =
                "https://private.example.test/source-chair.png";
            FakeHostControlClient ownerClient = new()
            {
                FailFirstImageResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            Tripo.HostUi.PreparedImageGeneration prepared =
                await owner.PrepareUrlImageGenerationAsync(
                    imageUrl,
                    10_000,
                    withMaterials: false,
                    model: "v2.5-20250123");

            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedImageGenerationAsync(
                    userConfirmedExternalCost: true));

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            string json = File.ReadAllText(file);
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    json,
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The image recovery hint could not be read.");
            Assert.Equal(4, hint.SchemaVersion);
            Assert.Null(hint.Generation);
            Assert.Equal(
                prepared.OperationId,
                hint.ImageGeneration?.OperationId);
            Assert.True(hint.ImageGeneration?.DispatchAttempted);
            Assert.Null(hint.ImageGeneration?.TaskId);
            Assert.DoesNotContain(imageUrl, json, StringComparison.Ordinal);
            Assert.DoesNotContain(
                prepared.Image.TransferId,
                json,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                prepared.Image.Sha256,
                json,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "imageUrl",
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "transferId",
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "mediaType",
                json,
                StringComparison.OrdinalIgnoreCase);

            await owner.DisposeAsync();
            owner = null;
            MakeRecoveryOwnerStale(root, "rhino");
            FakeHostControlClient restartedClient = new()
            {
                OperationStatus =
                    ResumableImageOperationStatus(prepared.OperationId),
            };
            await using Tripo.HostUi.TripoPanelSession restarted =
                new(
                    new FakeConnector(restartedClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.Equal(
                prepared.OperationId,
                Assert.Single(restarted.Recovery.Hints)
                    .Hint.ImageGeneration?.OperationId);
            await restarted.ConnectAsync();

            Tripo.Bridge.HostControlOperationStatusReceipt inspected =
                await restarted.InspectPaidRecoveryAsync(
                    prepared.OperationId,
                    "image_generation");

            Assert.Equal("image_task_creation", inspected.Kind);
            Assert.Equal(
                "image_file_token_persisted",
                inspected.State);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartRecoveryBlocksNewPaidWorkUntilExplicitReconciliation()
    {
        string root = CreateTemporaryRoot();
        try
        {
            FakeHostControlClient originalClient = new()
            {
                FailFirstTextResponse = true,
            };
            string operationId;
            string documentSessionId;
            await using (Tripo.HostUi.TripoPanelSession original =
                         new(
                             new FakeConnector(originalClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await original.ConnectAsync();
                documentSessionId =
                    original.State.Context!.DocumentSessionId;
                operationId = original.PrepareGeneration(
                        "a chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => original.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            MakeRecoveryOwnerStale(root, "rhino");
            FakeHostControlClient restartedClient = new()
            {
                CurrentSessionId = documentSessionId,
            };
            await using Tripo.HostUi.TripoPanelSession restarted =
                new(
                    new FakeConnector(restartedClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.True(restarted.Recovery.HasBlock);
            Assert.Equal(
                operationId,
                Assert.Single(restarted.Recovery.Hints)
                    .Hint.Generation?.OperationId);

            await restarted.ConnectAsync();
            Assert.True(restarted.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(
                    () => restarted.PrepareGeneration(
                        "a replacement chair",
                        10_000,
                        withMaterials: false));
            Assert.Contains("reconciliation", blocked.Message);
            Assert.Equal(0, restartedClient.CreateTextCalls);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restarted.AcknowledgeRecoveredOperationsAsync(
                    Guid.NewGuid().ToString("D")));

            Tripo.Bridge.HostControlOperationStatusReceipt inspectedStatus =
                ResumableOperationStatus(operationId);
            restartedClient.OperationStatus = inspectedStatus;
            await restarted.InspectPaidRecoveryAsync(
                operationId,
                "generation");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restarted.CreateRecoveryReviewReceiptAsync(
                    restarted.Recovery.PresentationToken,
                    paidResultsReviewed: false,
                    importsReviewed: false,
                    failedInspectionsReviewed: false));

            restartedClient.OperationStatus = inspectedStatus with
            {
                UpdatedAtUtc = inspectedStatus.UpdatedAtUtc.AddSeconds(1),
            };
            InvalidOperationException changedJournal =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => restarted.CreateRecoveryReviewReceiptAsync(
                        restarted.Recovery.PresentationToken,
                        paidResultsReviewed: true,
                        importsReviewed: false,
                        failedInspectionsReviewed: false));
            Assert.Contains("changed after inspection", changedJournal.Message);
            await restarted.InspectPaidRecoveryAsync(
                operationId,
                "generation");

            string reviewReceiptId =
                await restarted.CreateRecoveryReviewReceiptAsync(
                    restarted.Recovery.PresentationToken,
                    paidResultsReviewed: true,
                    importsReviewed: false,
                    failedInspectionsReviewed: false);
            restartedClient.OperationStatus = inspectedStatus with
            {
                UpdatedAtUtc = inspectedStatus.UpdatedAtUtc.AddSeconds(2),
            };
            InvalidOperationException changedAfterReview =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => restarted.AcknowledgeRecoveredOperationsAsync(
                        reviewReceiptId));
            Assert.Contains(
                "changed after inspection",
                changedAfterReview.Message);

            await restarted.InspectPaidRecoveryAsync(
                operationId,
                "generation");
            string currentReviewReceiptId =
                await restarted.CreateRecoveryReviewReceiptAsync(
                    restarted.Recovery.PresentationToken,
                    paidResultsReviewed: true,
                    importsReviewed: false,
                    failedInspectionsReviewed: false);
            await restarted.AcknowledgeRecoveredOperationsAsync(
                currentReviewReceiptId);
            Assert.False(restarted.Recovery.HasBlock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restarted.AcknowledgeRecoveredOperationsAsync(
                    currentReviewReceiptId));
            string archivedFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive"),
                    "*.json"));
            Assert.Contains(
                operationId,
                File.ReadAllText(archivedFile),
                StringComparison.Ordinal);
            _ = restarted.PrepareGeneration(
                "a replacement chair",
                10_000,
                withMaterials: false);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveRecoveredOperationCannotBeArchived(
        bool activeDuringReview)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string operationId;
            string documentSessionId;
            await using (Tripo.HostUi.TripoPanelSession owner =
                         new(
                             new FakeConnector(
                                 new FakeHostControlClient
                                 {
                                     FailFirstTextResponse = true,
                                 }),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await owner.ConnectAsync();
                documentSessionId =
                    owner.State.Context!.DocumentSessionId;
                operationId = owner.PrepareGeneration(
                        "a chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => owner.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            MakeRecoveryOwnerStale(root, "rhino");
            FakeHostControlClient observerClient = new()
            {
                CurrentSessionId = documentSessionId,
            };
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await observer.ConnectAsync();
            Tripo.Bridge.HostControlOperationStatusReceipt prepared =
                ResumableOperationStatus(operationId);
            Tripo.Bridge.HostControlOperationStatusReceipt active =
                prepared with
                {
                    State = "dispatching",
                    MayHaveCreatedRemoteTask = true,
                    CanResumeCreation = false,
                    OperationInProgress = true,
                    NextAction = "Wait for the active local operation.",
                    UpdatedAtUtc = prepared.UpdatedAtUtc.AddSeconds(1),
                };
            observerClient.OperationStatus =
                activeDuringReview ? active : prepared;
            await observer.InspectPaidRecoveryAsync(
                operationId,
                "generation");

            InvalidOperationException blocked;
            if (activeDuringReview)
            {
                blocked =
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => observer.CreateRecoveryReviewReceiptAsync(
                            observer.Recovery.PresentationToken,
                            paidResultsReviewed: true,
                            importsReviewed: false,
                            failedInspectionsReviewed: false));
            }
            else
            {
                string reviewReceipt =
                    await observer.CreateRecoveryReviewReceiptAsync(
                        observer.Recovery.PresentationToken,
                        paidResultsReviewed: true,
                        importsReviewed: false,
                        failedInspectionsReviewed: false);
                observerClient.OperationStatus = active;
                blocked =
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => observer.AcknowledgeRecoveredOperationsAsync(
                            reviewReceipt));
            }

            Assert.Contains("still in progress", blocked.Message);
            Assert.True(observer.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            string archiveDirectory = Path.Combine(
                root,
                "ui-recovery",
                "rhino",
                "archive");
            Assert.False(
                Directory.Exists(archiveDirectory) &&
                Directory.GetFiles(archiveDirectory, "*.json").Length > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveSidecarExecutionGateBlocksRecoveryArchival()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string operationId;
            string documentSessionId;
            await using (Tripo.HostUi.TripoPanelSession owner =
                         new(
                             new FakeConnector(
                                 new FakeHostControlClient
                                 {
                                     FailFirstTextResponse = true,
                                 }),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await owner.ConnectAsync();
                documentSessionId =
                    owner.State.Context!.DocumentSessionId;
                operationId = owner.PrepareGeneration(
                        "a chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => owner.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            MakeRecoveryOwnerStale(root, "rhino");
            FakeHostControlClient observerClient = new()
            {
                CurrentSessionId = documentSessionId,
                OperationStatus = ResumableOperationStatus(operationId),
            };
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await observer.ConnectAsync();
            await observer.InspectPaidRecoveryAsync(
                operationId,
                "generation");
            string reviewReceipt =
                await observer.CreateRecoveryReviewReceiptAsync(
                    observer.Recovery.PresentationToken,
                    paidResultsReviewed: true,
                    importsReviewed: false,
                    failedInspectionsReviewed: false);
            Tripo.Bridge.CredentialWorkflowExecutionGate activeGate =
                new(root);

            using (activeGate.Acquire())
            {
                Tripo.Bridge.BridgeCallException blocked =
                    await Assert.ThrowsAsync<
                        Tripo.Bridge.BridgeCallException>(
                        () => observer.AcknowledgeRecoveredOperationsAsync(
                            reviewReceipt));
                Assert.Equal(
                    "credential_workflow_unavailable",
                    blocked.Code);
            }

            Assert.True(observer.Recovery.HasBlock);
            Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            await observer.AcknowledgeRecoveredOperationsAsync(reviewReceipt);
            Assert.False(observer.Recovery.HasBlock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConversionRecoveryRejectsWrongSourceTaskIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string conversionOperationId;
            string expectedSourceTaskId;
            await using (Tripo.HostUi.TripoPanelSession original =
                         new(
                             new FakeConnector(new FakeHostControlClient()),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await original.ConnectAsync();
                original.PrepareGeneration(
                    "a chair",
                    10_000,
                    withMaterials: false);
                await original.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true);
                await original.RefreshGenerationStatusAsync();
                expectedSourceTaskId =
                    original.State.GenerationStatus!.TaskId;
                conversionOperationId = original.PrepareConversion(
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await original.DispatchPreparedConversionAsync(
                    userConfirmedExternalCost: true);
            }

            FakeHostControlClient restartedClient = new()
            {
                OperationStatus =
                    new Tripo.Bridge.HostControlOperationStatusReceipt(
                        conversionOperationId,
                        "obj_conversion_creation",
                        "task_id_persisted",
                        "task_wrong_source",
                        "task_conversion123",
                        null,
                        null,
                        TaskIdDurable: true,
                        MayHaveCreatedRemoteTask: true,
                        CanResumeCreation: false,
                        NextAction: "Query the durable task ID.",
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
            };
            await using Tripo.HostUi.TripoPanelSession restarted =
                new(
                    new FakeConnector(restartedClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Tripo.HostUi.TripoPanelRecoveryHint recoveredHint =
                Assert.Single(restarted.Recovery.Hints).Hint;
            Assert.NotNull(recoveredHint.Conversion);
            Assert.Equal(
                expectedSourceTaskId,
                recoveredHint.Generation?.TaskId);
            await restarted.ConnectAsync();

            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => restarted.InspectPaidRecoveryAsync(
                        conversionOperationId,
                        "conversion"));

            Assert.Contains("source task", error.Message);

            restartedClient.OperationStatus =
                restartedClient.OperationStatus! with
                {
                    SourceTaskId = expectedSourceTaskId,
                    CreatedTaskId = "task_wrong_conversion",
                };
            InvalidOperationException wrongDurableTask =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => restarted.InspectPaidRecoveryAsync(
                        conversionOperationId,
                        "conversion"));
            Assert.Contains(
                "journal-state semantics",
                wrongDurableTask.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedRecoverySetMustBeRenderedBeforeAcknowledgement()
    {
        string root = CreateTemporaryRoot();
        try
        {
            await using (Tripo.HostUi.TripoPanelSession owner =
                         new(
                             new FakeConnector(
                                 new FakeHostControlClient
                                 {
                                     FailFirstTextResponse = true,
                                 }),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await owner.ConnectAsync();
                owner.PrepareGeneration(
                    "first chair",
                    10_000,
                    withMaterials: false);
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => owner.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
            }

            FakeHostControlClient observerClient = new();
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.Single(observer.Recovery.Hints);
            await observer.ConnectAsync();
            string initiallyDisplayedToken =
                observer.Recovery.PresentationToken;
            string firstOperationId =
                Assert.Single(observer.Recovery.Hints)
                    .Hint.Generation!.OperationId;
            observerClient.OperationStatus =
                ResumableOperationStatus(firstOperationId);
            await observer.InspectPaidRecoveryAsync(
                firstOperationId,
                "generation");
            string staleReviewReceipt =
                await observer.CreateRecoveryReviewReceiptAsync(
                    initiallyDisplayedToken,
                    paidResultsReviewed: true,
                    importsReviewed: false,
                    failedInspectionsReviewed: false);
            string firstFile = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint firstHint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(firstFile),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The first recovery hint could not be read.");
            Tripo.HostUi.TripoPanelRecoveryHint secondHint =
                firstHint with
                {
                    RecoveryId = Guid.NewGuid().ToString("D"),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Generation = firstHint.Generation! with
                    {
                        OperationId = Guid.NewGuid().ToString("D"),
                    },
                };
            string secondFile = Path.Combine(
                Path.GetDirectoryName(firstFile)!,
                secondHint.RecoveryId + ".json");
            File.WriteAllText(
                secondFile,
                System.Text.Json.JsonSerializer.Serialize(
                    secondHint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(secondFile);
            Tripo.HostUi.TripoPanelRecoveryLoadResult? rendered = null;
            observer.RecoveryChanged += (_, recovery) => rendered = recovery;

            InvalidOperationException changed =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => observer.AcknowledgeRecoveredOperationsAsync(
                        staleReviewReceipt));

            Assert.Contains("list changed", changed.Message);
            Tripo.HostUi.TripoPanelRecoveryLoadResult renderedSnapshot =
                Assert.IsType<
                    Tripo.HostUi.TripoPanelRecoveryLoadResult>(rendered);
            Assert.Equal(2, renderedSnapshot.Hints.Count);
            Assert.Equal(2, observer.Recovery.Hints.Count);
            Assert.Equal(
                2,
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json").Length);

            foreach (
                Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded in
                renderedSnapshot.Hints)
            {
                string currentOperationId =
                    loaded.Hint.Generation!.OperationId;
                observerClient.OperationStatus =
                    ResumableOperationStatus(currentOperationId);
                observerClient.OperationStatuses[currentOperationId] =
                    observerClient.OperationStatus;
                await observer.InspectPaidRecoveryAsync(
                    currentOperationId,
                    "generation");
            }

            string currentReviewReceipt =
                await observer.CreateRecoveryReviewReceiptAsync(
                    renderedSnapshot.PresentationToken,
                    paidResultsReviewed: true,
                    importsReviewed: false,
                    failedInspectionsReviewed: false);
            await observer.AcknowledgeRecoveredOperationsAsync(
                currentReviewReceipt);
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Assert.Equal(
                2,
                Directory.GetFiles(
                    Path.Combine(
                        root,
                        "ui-recovery",
                        "rhino",
                        "archive"),
                    "*.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveSessionOwnsHintUntilItsSessionIsDisposed()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            FakeHostControlClient ownerClient = new()
            {
                FailFirstTextResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            Assert.False(observer.Recovery.HasBlock);

            await owner.DisposeAsync();
            owner = null;
            Assert.True(observer.RefreshRecovery().HasBlock);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CachedEmptyPanelRescansBeforeCreatingANewOperationId()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            FakeHostControlClient observerClient = new();
            await using Tripo.HostUi.TripoPanelSession observer =
                new(
                    new FakeConnector(observerClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await observer.ConnectAsync();
            Assert.False(observer.Recovery.HasBlock);

            FakeHostControlClient ownerClient = new()
            {
                FailFirstTextResponse = true,
            };
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(ownerClient),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            await owner.DisposeAsync();
            owner = null;
            InvalidOperationException blocked =
                Assert.Throws<InvalidOperationException>(
                    () => observer.PrepareGeneration(
                        "a new chair",
                        10_000,
                        withMaterials: false));

            Assert.Contains("reconciliation", blocked.Message);
            Assert.True(observer.Recovery.HasBlock);
            Assert.Equal(0, observerClient.CreateTextCalls);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvedHintGloballyBlocksCredentialMutation()
    {
        string root = CreateTemporaryRoot();
        Tripo.HostUi.TripoPanelSession? owner = null;
        try
        {
            owner = new Tripo.HostUi.TripoPanelSession(
                new FakeConnector(
                    new FakeHostControlClient
                    {
                        FailFirstTextResponse = true,
                    }),
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await owner.ConnectAsync();
            owner.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                () => owner.DispatchPreparedGenerationAsync(
                    userConfirmedExternalCost: true));

            FakeHostControlClient sameHostClient = new();
            await using (Tripo.HostUi.TripoPanelSession sameHost =
                         new(
                             new FakeConnector(sameHostClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await sameHost.ConnectAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => sameHost.SetApiKeyAsync(
                        "replacement-key",
                        persist: true));
                Assert.Null(sameHostClient.LastApiKey);
            }

            await owner.DisposeAsync();
            owner = null;
            FakeHostControlClient otherHostClient = new()
            {
                Host = "revit",
            };
            await using Tripo.HostUi.TripoPanelSession otherHost =
                new(
                    new FakeConnector(otherHostClient),
                    new Tripo.HostUi.TripoPanelRecoveryStore("revit", root));
            await otherHost.ConnectAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => otherHost.ClearApiKeyAsync());
            Assert.Equal(0, otherHostClient.ClearApiKeyCalls);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecoveryStorePersistsCanonicalTaskUuidWithoutRewritingIt()
    {
        const string documentSessionId =
            "11111111-1111-4111-8111-111111111111";
        const string operationId =
            "22222222-2222-4222-8222-222222222222";
        const string taskId =
            "ef731ad6-aeb0-4950-9a2e-2298359dfaf8";
        string root = CreateTemporaryRoot();
        try
        {
            using Tripo.HostUi.TripoPanelRecoveryStore store =
                new("revit", root);
            Tripo.HostUi.TripoPanelState state =
                Tripo.HostUi.TripoPanelState.Initial with
                {
                    Connected = true,
                    Context = new Tripo.Bridge.HostContextReceipt(
                        "revit",
                        "2026-test",
                        123,
                        documentSessionId,
                        "Test.rvt",
                        "Feet",
                        []),
                    PreparedGeneration =
                        new Tripo.HostUi.PreparedTextGeneration(
                            "a chair",
                            10_000,
                            false,
                            operationId),
                    GenerationDispatchAttempted = true,
                    GenerationReceipt =
                        new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                            operationId,
                            taskId,
                            "v3.1-20260211"),
                };

            store.Save(state);

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "revit"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(file),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The recovery hint could not be read.");
            Assert.Equal(taskId, hint.Generation?.TaskId);
            Assert.True(hint.Generation?.TaskIdDurable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentHintExclusionRequiresExactOwnerIdentity()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    store);
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: false);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    File.ReadAllText(file),
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The recovery hint could not be read.");
            hint = hint with
            {
                OwnerProcessId = int.MaxValue,
                OwnerProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
            };
            File.WriteAllText(
                file,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(file);

            Assert.True(
                store.LoadCredentialMutationBlocks(
                    excludeCurrentStoreHint: true).HasBlock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HintFromAnotherLiveProcessIsConservativelyBlocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        using System.Diagnostics.Process ownerProcess =
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { "-c", "sleep 30" },
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })
            ?? throw new InvalidOperationException(
                "The live-owner canary process did not start.");
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                ownerProcess.Id,
                new DateTimeOffset(
                    ownerProcess.StartTime.ToUniversalTime(),
                    TimeSpan.Zero),
                DateTimeOffset.UtcNow,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    DispatchAttempted: true,
                    TaskId: null,
                    JournalState: null,
                    TaskIdDurable: false,
                    CanResumeCreation: false),
                null,
                null);
            string path =
                Path.Combine(directory, hint.RecoveryId + ".json");
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Assert.Empty(result.Hints);
            Assert.Equal(
                "recovery_owner_process_alive",
                Assert.Single(result.Issues).Code);
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                ownerProcess.Kill(entireProcessTree: true);
                ownerProcess.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SameDocumentPanelsUseIndependentRecoveryFiles()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string documentSessionId = Guid.NewGuid().ToString("D");
            FakeHostControlClient firstClient = new()
            {
                CurrentSessionId = documentSessionId,
                FailFirstTextResponse = true,
            };
            FakeHostControlClient secondClient = new()
            {
                CurrentSessionId = documentSessionId,
                FailFirstTextResponse = true,
            };
            string firstOperationId;
            string secondOperationId;
            await using (Tripo.HostUi.TripoPanelSession first =
                         new(
                             new FakeConnector(firstClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            await using (Tripo.HostUi.TripoPanelSession second =
                         new(
                             new FakeConnector(secondClient),
                             new Tripo.HostUi.TripoPanelRecoveryStore(
                                 "rhino",
                                 root)))
            {
                await first.ConnectAsync();
                await second.ConnectAsync();
                firstOperationId = first.PrepareGeneration(
                        "first chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                secondOperationId = second.PrepareGeneration(
                        "second chair",
                        10_000,
                        withMaterials: false)
                    .OperationId;
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => first.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));
                await Assert.ThrowsAsync<Tripo.Bridge.HostControlCallException>(
                    () => second.DispatchPreparedGenerationAsync(
                        userConfirmedExternalCost: true));

                Assert.Equal(
                    2,
                    Directory.GetFiles(
                        Path.Combine(root, "ui-recovery", "rhino"),
                        "*.json").Length);
            }

            Tripo.HostUi.TripoPanelRecoveryLoadResult recovered =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();
            Assert.Equal(2, recovered.Hints.Count);
            string[] operationIds = recovered.Hints
                .Select(item => item.Hint.Generation!.OperationId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[] { firstOperationId, secondOperationId }
                    .OrderBy(value => value, StringComparer.Ordinal),
                operationIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportReceiptHintPersistsUntilExplicitWorkflowReset()
    {
        string root = CreateTemporaryRoot();
        try
        {
            await using Tripo.HostUi.TripoPanelSession session =
                new(
                    new FakeConnector(new FakeHostControlClient()),
                    new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root));
            await session.ConnectAsync();
            session.PrepareGeneration(
                "a chair",
                10_000,
                withMaterials: true);
            await session.DispatchPreparedGenerationAsync(
                userConfirmedExternalCost: true);
            await session.RefreshGenerationStatusAsync();
            session.PrepareConversion(10_000, withMaterials: true);
            await session.DispatchPreparedConversionAsync(
                userConfirmedExternalCost: true);
            await session.RefreshConversionStatusAsync();
            Tripo.HostUi.PreparedObjImport import =
                session.PrepareImport(
                    "Chair",
                    "native",
                    applyMaterials: true);
            await session.ImportPreparedAsync();

            string file = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
            string recoveryJson = File.ReadAllText(file);
            Tripo.HostUi.TripoPanelRecoveryHint hint =
                System.Text.Json.JsonSerializer.Deserialize<
                    Tripo.HostUi.TripoPanelRecoveryHint>(
                    recoveryJson,
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new InvalidOperationException(
                    "The import recovery hint could not be read.");
            Assert.Equal(import.OperationId, hint.Import?.OperationId);
            Assert.True(hint.Import!.ReceiptKnown);
            Assert.Equal(
                "task_source123",
                hint.Generation?.TaskId);
            Assert.Equal(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                hint.SchemaVersion);
            Assert.Equal(import.DocumentSessionId, hint.Import.DocumentSessionId);
            Assert.DoesNotContain(
                "sourceTaskId",
                recoveryJson,
                StringComparison.Ordinal);

            session.ResetWorkflow();
            Assert.Empty(
                Directory.GetFiles(
                    Path.Combine(root, "ui-recovery", "rhino"),
                    "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version3RecoveryMigratesWithNoImageGeneration()
    {
        string root = CreateTemporaryRoot();
        try
        {
            const string recoveryId =
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            const string operationId =
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            System.Text.Json.Nodes.JsonObject legacy = new()
            {
                ["schemaVersion"] = 3,
                ["recoveryId"] = recoveryId,
                ["host"] = "rhino",
                ["ownerProcessId"] = int.MaxValue,
                ["ownerProcessStartedAtUtc"] = DateTimeOffset.UnixEpoch,
                ["updatedAtUtc"] = DateTimeOffset.UtcNow,
                ["generation"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["operationId"] = operationId,
                    ["dispatchAttempted"] = true,
                    ["taskId"] = null,
                    ["journalState"] = "prepared",
                    ["taskIdDurable"] = false,
                    ["canResumeCreation"] = true,
                },
                ["conversion"] = null,
                ["import"] = null,
            };
            string path = Path.Combine(directory, recoveryId + ".json");
            File.WriteAllText(path, legacy.ToJsonString());
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);
            using Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);

            Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded =
                Assert.Single(store.LoadStale().Hints);

            Assert.Equal(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                loaded.Hint.SchemaVersion);
            Assert.Equal(operationId, loaded.Hint.Generation?.OperationId);
            Assert.Null(loaded.Hint.ImageGeneration);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version2RecoveryMovesDocumentSessionToImportAndCanArchive()
    {
        string root = CreateTemporaryRoot();
        try
        {
            const string recoveryId =
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            const string documentSessionId =
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            System.Text.Json.Nodes.JsonObject legacy = new()
            {
                ["schemaVersion"] = 2,
                ["recoveryId"] = recoveryId,
                ["host"] = "rhino",
                ["ownerProcessId"] = int.MaxValue,
                ["ownerProcessStartedAtUtc"] = DateTimeOffset.UnixEpoch,
                ["documentSessionId"] = documentSessionId,
                ["updatedAtUtc"] = DateTimeOffset.UtcNow,
                ["generation"] = null,
                ["conversion"] = null,
                ["import"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["operationId"] =
                        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    ["conversionTaskId"] = "task_conversion123",
                    ["name"] = "Legacy Chair",
                    ["importMode"] = "native",
                    ["applyMaterials"] = true,
                    ["dispatchAttempted"] = true,
                    ["receiptKnown"] = false,
                },
            };
            string path = Path.Combine(directory, recoveryId + ".json");
            File.WriteAllText(path, legacy.ToJsonString());
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);
            using Tripo.HostUi.TripoPanelRecoveryStore store =
                new("rhino", root);

            Tripo.HostUi.LoadedTripoPanelRecoveryHint loaded =
                Assert.Single(store.LoadStale().Hints);

            Assert.Equal(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                loaded.Hint.SchemaVersion);
            Assert.Equal(
                documentSessionId,
                loaded.Hint.Import?.DocumentSessionId);
            store.Archive(loaded);
            Assert.Empty(store.LoadStale().Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidOversizedAndUnknownSchemaRecoveryFilesFailClosed()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            string invalidJsonPath =
                Path.Combine(
                    directory,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa.json");
            File.WriteAllText(
                invalidJsonPath,
                "{not-json");
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(invalidJsonPath);
            string oversizedPath =
                Path.Combine(
                    directory,
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb.json");
            File.WriteAllBytes(
                oversizedPath,
                new byte[
                    Tripo.HostUi.TripoPanelRecoveryStore
                        .MaximumRecoveryFileBytes + 1]);
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(oversizedPath);
            Tripo.HostUi.TripoPanelRecoveryHint wrongSchema = new(
                99,
                "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UtcNow,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    true,
                    null,
                    null,
                    false,
                    false),
                null,
                null);
            string wrongSchemaPath =
                Path.Combine(
                    directory,
                    wrongSchema.RecoveryId + ".json");
            File.WriteAllText(
                wrongSchemaPath,
                System.Text.Json.JsonSerializer.Serialize(
                    wrongSchema,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(wrongSchemaPath);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Assert.True(result.HasBlock);
            Assert.Empty(result.Hints);
            Assert.Equal(3, result.Issues.Count);
            Assert.Equal(
                "recovery_file_invalid_json",
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(invalidJsonPath)).Code);
            Assert.Equal(
                "recovery_file_invalid",
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(oversizedPath)).Code);
            Tripo.HostUi.TripoPanelRecoveryIssue schemaIssue =
                Assert.Single(
                    result.Issues,
                    issue => issue.FileName ==
                        Path.GetFileName(wrongSchemaPath));
            Assert.Equal("recovery_file_invalid", schemaIssue.Code);
            Assert.Contains(
                "unsupported schema",
                schemaIssue.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingRequiredRecoveryPropertyFailsClosed()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UtcNow,
                null,
                null,
                new Tripo.HostUi.TripoPanelImportRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    "task_conversion123",
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    "Chair",
                    "native",
                    ApplyMaterials: true,
                    DispatchAttempted: true,
                    ReceiptKnown: false));
            string json = System.Text.Json.JsonSerializer.Serialize(
                hint,
                CreateStrictRecoveryJsonOptions());
            System.Text.Json.Nodes.JsonObject rootObject =
                System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            rootObject["import"]!.AsObject().Remove("applyMaterials");
            string path = Path.Combine(
                directory,
                hint.RecoveryId + ".json");
            File.WriteAllText(path, rootObject.ToJsonString());
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid_json", issue.Code);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionRecoveryRequiresADurableGenerationSource(
        bool includeNonDurableGeneration)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UtcNow,
                includeNonDurableGeneration
                    ? new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                        "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                        true,
                        null,
                        "prepared",
                        false,
                        true)
                    : null,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    true,
                    null,
                    "prepared",
                    false,
                    true),
                null);
            string path =
                Path.Combine(directory, hint.RecoveryId + ".json");
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid", issue.Code);
            Assert.Contains(
                "durable generation source",
                issue.Message);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecoveryTaskIdAndDurabilityMustAgree()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            Tripo.HostUi.TripoPanelRecoveryHint hint = new(
                Tripo.HostUi.TripoPanelRecoveryStore.CurrentSchemaVersion,
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                "rhino",
                int.MaxValue,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UtcNow,
                new Tripo.HostUi.TripoPanelPaidRecoveryHint(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                    true,
                    "task_source123",
                    "task_id_persisted",
                    TaskIdDurable: false,
                    CanResumeCreation: false),
                null,
                null);
            string path =
                Path.Combine(directory, hint.RecoveryId + ".json");
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    hint,
                    CreateStrictRecoveryJsonOptions()));
            Tripo.Bridge.BridgePaths.SetPrivateFileMode(path);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid", issue.Code);
            Assert.Contains(
                "durability",
                issue.Message);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SymbolicLinkRecoveryDirectoryFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string target = Path.Combine(root, "redirected");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(
                Path.Combine(root, "ui-recovery"),
                target);

            Assert.Throws<InvalidDataException>(
                () => new Tripo.HostUi.TripoPanelRecoveryStore(
                    "rhino",
                    root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SymbolicLinkRecoveryFileFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = CreateTemporaryRoot();
        try
        {
            string directory =
                Path.Combine(root, "ui-recovery", "rhino");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(root, "target.json");
            File.WriteAllText(target, "{}");
            File.CreateSymbolicLink(
                Path.Combine(
                    directory,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa.json"),
                target);

            Tripo.HostUi.TripoPanelRecoveryLoadResult result =
                new Tripo.HostUi.TripoPanelRecoveryStore("rhino", root)
                    .LoadStale();

            Tripo.HostUi.TripoPanelRecoveryIssue issue =
                Assert.Single(result.Issues);
            Assert.Equal("recovery_file_invalid", issue.Code);
            Assert.Empty(result.Hints);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Tripo.HostUi.TripoPanelSession CreateSession(
        FakeHostControlClient client) =>
        new(new FakeConnector(client));

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        DurableOperationStatus(string operationId, string taskId) =>
        new(
            operationId,
            "text_task_creation",
            "task_id_persisted",
            null,
            taskId,
            null,
            null,
            TaskIdDurable: true,
            MayHaveCreatedRemoteTask: true,
            CanResumeCreation: false,
            NextAction: "Query the durable task ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ResumableOperationStatus(string operationId) =>
        new(
            operationId,
            "text_task_creation",
            "prepared",
            null,
            null,
            null,
            null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: true,
            NextAction: "Retry the same operation ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ImageDurableOperationStatus(string operationId, string taskId) =>
        new(
            operationId,
            "image_task_creation",
            "task_id_persisted",
            null,
            taskId,
            null,
            null,
            TaskIdDurable: true,
            MayHaveCreatedRemoteTask: true,
            CanResumeCreation: false,
            NextAction: "Query the durable task ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        ResumableImageOperationStatus(string operationId) =>
        new(
            operationId,
            "image_task_creation",
            "image_file_token_persisted",
            null,
            null,
            null,
            null,
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: true,
            NextAction: "Retry image generation with the same operation ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        RejectedOperationStatus(
            string operationId,
            string kind,
            string? sourceTaskId = null) =>
        new(
            operationId,
            kind,
            Tripo.Bridge.HostControlConstants.RequestRejectedState,
            sourceTaskId,
            null,
            "credential_rejected",
            "The provider rejected the credential.",
            TaskIdDurable: false,
            MayHaveCreatedRemoteTask: false,
            CanResumeCreation: false,
            NextAction: "Correct the credential and use a new operation ID.",
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "tripo-host-ui-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void MakeRecoveryOwnerStale(
        string root,
        string host)
    {
        string file = Assert.Single(
            Directory.GetFiles(
                Path.Combine(root, "ui-recovery", host),
                "*.json"));
        Tripo.HostUi.TripoPanelRecoveryHint hint =
            System.Text.Json.JsonSerializer.Deserialize<
                Tripo.HostUi.TripoPanelRecoveryHint>(
                File.ReadAllText(file),
                Tripo.Bridge.BridgeJson.Options)
            ?? throw new InvalidOperationException(
                "The test recovery hint could not be read.");
        hint = hint with
        {
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartedAtUtc = DateTimeOffset.UnixEpoch,
        };
        File.WriteAllText(
            file,
            System.Text.Json.JsonSerializer.Serialize(
                hint,
                CreateStrictRecoveryJsonOptions()));
        Tripo.Bridge.BridgePaths.SetPrivateFileMode(file);
    }

    private static System.Text.Json.JsonSerializerOptions
        CreateStrictRecoveryJsonOptions() =>
        new(Tripo.Bridge.BridgeJson.Options)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

    private sealed class FakeConnector :
        Tripo.Bridge.IHostSidecarConnector
    {
        private readonly Tripo.Bridge.IHostControlClient _client;

        public FakeConnector(Tripo.Bridge.IHostControlClient client)
        {
            _client = client;
        }

        public Task<Tripo.Bridge.IHostControlClient> EnsureConnectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_client);
    }

    private sealed class FakeHostControlClient :
        Tripo.Bridge.IHostControlClient
    {
        private readonly int _hostProcessId = Environment.ProcessId;

        public FakeHostControlClient()
        {
            CurrentSessionId = Guid.NewGuid().ToString("D");
        }

        public string CurrentSessionId { get; set; }

        public string Host { get; set; } = "rhino";

        public string CurrentDocumentTitle { get; set; } = "Test.3dm";

        public bool FailFirstTextResponse { get; set; }

        public string FirstTextFailureCode { get; set; } =
            "sidecar_unavailable";

        public string? TextFailureCode { get; set; }

        public string TextTaskId { get; set; } = "task_source123";

        public bool FailFirstImageResponse { get; set; }

        public string FirstImageFailureCode { get; set; } =
            "sidecar_unavailable";

        public string? ImageFailureCode { get; set; }

        public string ImageTaskId { get; set; } = "task_image123";

        public string ConversionTaskId { get; set; } =
            "task_conversion123";

        public bool FailFirstConversionResponse { get; set; }

        public string FirstConversionFailureCode { get; set; } =
            "sidecar_unavailable";

        public bool FailFirstImportResponse { get; set; }

        public string? TextReceiptOperationIdOverride { get; set; }

        public string? TextReceiptTaskIdOverride { get; set; }

        public string? TextReceiptModelOverride { get; set; }

        public string? ImageReceiptOperationIdOverride { get; set; }

        public string? ImageReceiptTaskIdOverride { get; set; }

        public string? ImageReceiptModelOverride { get; set; }

        public string? ImageReceiptSha256Override { get; set; }

        public string? ConversionReceiptFormatOverride { get; set; }

        public string? ConversionReceiptTaskIdOverride { get; set; }

        public string? SavedFamilyPathOverride { get; set; }

        public string? TaskStatusTaskIdOverride { get; set; }

        public string? TaskStatusTypeOverride { get; set; }

        public Action<Tripo.Bridge.HostControlCreateTextTaskRequest>?
            BeforeCreateTextCall { get; set; }

        public TaskCompletionSource<bool>? HostContextEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueHostContext { get; set; }

        public TaskCompletionSource<bool>? CreateTextEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueCreateText { get; set; }

        public TaskCompletionSource<bool>? TaskStatusEntered { get; set; }

        public TaskCompletionSource<bool>? ContinueTaskStatus { get; set; }

        public TaskCompletionSource<bool>? OperationStatusEntered
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? ContinueOperationStatus
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? CredentialMutationEntered
        {
            get;
            set;
        }

        public TaskCompletionSource<bool>? ContinueCredentialMutation
        {
            get;
            set;
        }

        public int CreateTextCalls { get; private set; }

        public int CreateImageCalls { get; private set; }

        public int StageImageCalls { get; private set; }

        public int CreateConversionCalls { get; private set; }

        public int ClearApiKeyCalls { get; private set; }

        public string? ImportModeOverride { get; set; }

        public string? ImportTransactionStatusOverride { get; set; }

        public int? ImportTriangleCountOverride { get; set; }

        public IReadOnlyList<string>? ImportWarningsOverride { get; set; } = [];

        public int ImportCalls { get; private set; }

        public string? LastApiKey { get; private set; }

        public Tripo.Bridge.HostControlOperationStatusReceipt? OperationStatus
        {
            get;
            set;
        }

        public Dictionary<
            string,
            Tripo.Bridge.HostControlOperationStatusReceipt> OperationStatuses
        {
            get;
        } = new(StringComparer.Ordinal);

        public bool AllowMismatchedOperationStatusReceipt { get; set; }

        public List<Tripo.Bridge.HostControlCreateTextTaskRequest> TextRequests
        {
            get;
        } = [];

        public Tripo.Bridge.HostControlCreateTextTaskRequest? LastTextRequest =>
            TextRequests.LastOrDefault();

        public List<Tripo.Bridge.HostControlCreateImageTaskRequest> ImageRequests
        {
            get;
        } = [];

        public Tripo.Bridge.HostControlCreateImageTaskRequest?
            LastImageRequest => ImageRequests.LastOrDefault();

        public Tripo.Bridge.HostControlStageImageRequest?
            LastStageImageRequest { get; private set; }

        public Tripo.Bridge.StagedImageTransfer StagedImage { get; set; } =
            new(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                new string('a', 64),
                68,
                "image/png");

        public Tripo.Bridge.HostControlCreateObjConversionRequest?
            LastConversionRequest { get; private set; }

        public Tripo.Bridge.HostControlImportObjTaskRequest?
            LastImportRequest { get; private set; }

        public Task<Tripo.Bridge.HostControlHealthReceipt> GetHealthAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new Tripo.Bridge.HostControlHealthReceipt(
                    Host,
                    _hostProcessId,
                    Environment.ProcessId,
                    Tripo.Bridge.HostControlConstants.WorkflowCapabilities));

        public Task<Tripo.Bridge.HostControlCredentialStatusReceipt>
            GetCredentialStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CredentialStatus(hasKey: false));

        public async Task<Tripo.Bridge.HostControlCredentialMutationReceipt>
            SetApiKeyAsync(
                string apiKey,
                bool persist,
                CancellationToken cancellationToken)
        {
            CredentialMutationEntered?.TrySetResult(true);
            if (ContinueCredentialMutation is not null)
            {
                await ContinueCredentialMutation.Task
                    .WaitAsync(cancellationToken);
            }

            LastApiKey = apiKey;
            return new Tripo.Bridge.HostControlCredentialMutationReceipt(
                CredentialStatus(hasKey: true));
        }

        public Task<Tripo.Bridge.HostControlCredentialMutationReceipt>
            ClearApiKeyAsync(CancellationToken cancellationToken)
        {
            ClearApiKeyCalls++;
            return Task.FromResult(
                new Tripo.Bridge.HostControlCredentialMutationReceipt(
                    CredentialStatus(hasKey: false)));
        }

        public async Task<Tripo.Bridge.HostContextReceipt> GetHostContextAsync(
            CancellationToken cancellationToken)
        {
            HostContextEntered?.TrySetResult(true);
            if (ContinueHostContext is not null)
            {
                await ContinueHostContext.Task
                    .WaitAsync(cancellationToken);
            }

            return new Tripo.Bridge.HostContextReceipt(
                Host,
                "8-test",
                _hostProcessId,
                CurrentSessionId,
                CurrentDocumentTitle,
                "Meters",
                []);
        }

        public async Task<Tripo.Bridge.HostControlTextTaskCreationReceipt>
            CreateTextTaskAsync(
                Tripo.Bridge.HostControlCreateTextTaskRequest request,
                CancellationToken cancellationToken)
        {
            BeforeCreateTextCall?.Invoke(request);
            CreateTextEntered?.TrySetResult(true);
            if (ContinueCreateText is not null)
            {
                await ContinueCreateText.Task.WaitAsync(cancellationToken);
            }

            CreateTextCalls++;
            TextRequests.Add(request);
            if (TextFailureCode is not null ||
                FailFirstTextResponse && CreateTextCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    TextFailureCode ?? FirstTextFailureCode,
                    "The response was lost.");
            }

            OperationStatuses[request.OperationId] =
                DurableOperationStatus(
                    request.OperationId,
                    TextTaskId);
            return new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                TextReceiptOperationIdOverride ??
                request.OperationId,
                TextReceiptTaskIdOverride ?? TextTaskId,
                TextReceiptModelOverride ??
                request.Model ??
                Tripo.Bridge.TripoModelCatalog.DefaultModelId);
        }

        public Task<Tripo.Bridge.StagedImageTransfer> StageImageAsync(
            Tripo.Bridge.HostControlStageImageRequest request,
            CancellationToken cancellationToken)
        {
            StageImageCalls++;
            LastStageImageRequest = request;
            return Task.FromResult(StagedImage);
        }

        public Task<Tripo.Bridge.HostControlImageTaskCreationReceipt>
            CreateImageTaskAsync(
                Tripo.Bridge.HostControlCreateImageTaskRequest request,
                CancellationToken cancellationToken)
        {
            CreateImageCalls++;
            ImageRequests.Add(request);
            if (ImageFailureCode is not null ||
                FailFirstImageResponse && CreateImageCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    ImageFailureCode ?? FirstImageFailureCode,
                    "The image response was lost.");
            }

            OperationStatuses[request.OperationId] =
                ImageDurableOperationStatus(
                    request.OperationId,
                    ImageTaskId);
            return Task.FromResult(
                new Tripo.Bridge.HostControlImageTaskCreationReceipt(
                    ImageReceiptOperationIdOverride ?? request.OperationId,
                    ImageReceiptTaskIdOverride ?? ImageTaskId,
                    ImageReceiptModelOverride ??
                    request.Model ??
                    Tripo.Bridge.TripoModelCatalog.DefaultModelId,
                    ImageReceiptSha256Override ?? request.Image.Sha256));
        }

        public async Task<Tripo.Bridge.HostControlTaskStatusReceipt>
            GetTaskStatusAsync(
                string taskId,
                CancellationToken cancellationToken)
        {
            TaskStatusEntered?.TrySetResult(true);
            if (ContinueTaskStatus is not null)
            {
                await ContinueTaskStatus.Task
                    .WaitAsync(cancellationToken);
            }

            return new Tripo.Bridge.HostControlTaskStatusReceipt(
                TaskStatusTaskIdOverride ?? taskId,
                TaskStatusTypeOverride ??
                (taskId == TextTaskId
                    ? "text_to_model"
                    : taskId == ImageTaskId
                        ? "image_to_model"
                    : "convert_model"),
                "success",
                100,
                null,
                null,
                null,
                null,
                null);
        }

        public async Task<Tripo.Bridge.HostControlOperationStatusReceipt>
            GetOperationStatusAsync(
                string operationId,
                CancellationToken cancellationToken)
        {
            OperationStatusEntered?.TrySetResult(true);
            if (ContinueOperationStatus is not null)
            {
                await ContinueOperationStatus.Task
                    .WaitAsync(cancellationToken);
            }

            if (OperationStatuses.TryGetValue(
                    operationId,
                    out Tripo.Bridge.HostControlOperationStatusReceipt?
                        storedStatus))
            {
                return storedStatus;
            }

            return OperationStatus is not null &&
            (AllowMismatchedOperationStatusReceipt ||
             string.Equals(
                 OperationStatus.OperationId,
                 operationId,
                 StringComparison.Ordinal))
                ? OperationStatus
                : throw new Tripo.Bridge.HostControlCallException(
                    "workflow_error",
                    "No local paid operation was found.");
        }

        public Task<Tripo.Bridge.HostControlObjConversionCreationReceipt>
            CreateObjConversionAsync(
                Tripo.Bridge.HostControlCreateObjConversionRequest request,
                CancellationToken cancellationToken)
        {
            CreateConversionCalls++;
            LastConversionRequest = request;
            if (FailFirstConversionResponse &&
                CreateConversionCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    FirstConversionFailureCode,
                    "The conversion request was rejected.");
            }

            OperationStatuses[request.OperationId] =
                new Tripo.Bridge.HostControlOperationStatusReceipt(
                    request.OperationId,
                    "obj_conversion_creation",
                    "task_id_persisted",
                    request.SourceTaskId,
                    ConversionTaskId,
                    null,
                    null,
                    TaskIdDurable: true,
                    MayHaveCreatedRemoteTask: true,
                    CanResumeCreation: false,
                    NextAction: "Query the durable task ID.",
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
            return Task.FromResult(
                new Tripo.Bridge.HostControlObjConversionCreationReceipt(
                    request.OperationId,
                    request.SourceTaskId,
                    ConversionReceiptTaskIdOverride ??
                    ConversionTaskId,
                    ConversionReceiptFormatOverride ?? "OBJ"));
        }

        public Task<Tripo.Bridge.HostControlObjTaskImportReceipt>
            ImportObjTaskAsync(
                Tripo.Bridge.HostControlImportObjTaskRequest request,
                CancellationToken cancellationToken)
        {
            ImportCalls++;
            LastImportRequest = request;
            if (FailFirstImportResponse && ImportCalls == 1)
            {
                throw new Tripo.Bridge.HostControlCallException(
                    "sidecar_unavailable",
                    "The import response was lost.");
            }

            string resolvedImportMode =
                ImportModeOverride ??
                (request.ImportMode == "native"
                    ? Host == "revit"
                        ? "family"
                        : "instance"
                    : request.ImportMode);
            return Task.FromResult(
                new Tripo.Bridge.HostControlObjTaskImportReceipt(
                    request.OperationId,
                    request.ConversionTaskId,
                    null,
                    new Tripo.Bridge.HostImportReceipt(
                        Host,
                        request.DocumentSessionId,
                        request.OperationId,
                        "created-1",
                        1,
                        ImportTriangleCountOverride ?? 1,
                        0,
                        ImportTransactionStatusOverride ?? "committed",
                        resolvedImportMode,
                        request.ApplyMaterials ? 1 : 0,
                        request.ApplyMaterials ? 1 : 0,
                        resolvedImportMode == "family"
                            ? SavedFamilyPathOverride ??
                              @"C:\Tripo\created.rfa"
                            : null),
                    ImportWarningsOverride!));
        }

        public Task<Tripo.Bridge.HostControlHealthReceipt> ShutdownAsync(
            CancellationToken cancellationToken) =>
            GetHealthAsync(cancellationToken);

        private static Tripo.Bridge.HostControlCredentialStatusReceipt
            CredentialStatus(bool hasKey) =>
            new(
                hasKey,
                hasKey ? "store" : "none",
                StoredKeyPresent: hasKey,
                CanClearStoredKey: hasKey,
                PersistenceBackend: "fake",
                UsesWeakerFileFallback: false);
    }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Tripo.Revit;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class RevitHostCoordinationTests
{
    [Fact]
    public void CopiedDocumentsHaveSeparateSessionsButNativeWrappersShareOne()
    {
        RevitDocumentSessions sessions = new();
        Guid creationGuid = Guid.NewGuid();
        Document original = new(creationGuid, 1);
        Document copiedFile = new(creationGuid, 2);
        Document secondWrapper = new(creationGuid, 1);

        string originalSession = sessions.GetOrCreate(original);

        Assert.NotEqual(originalSession, sessions.GetOrCreate(copiedFile));
        Assert.Equal(originalSession, sessions.GetOrCreate(secondWrapper));
    }

    [Theory]
    [InlineData((int)RevitAPIEventStatus.Cancelled)]
    [InlineData((int)RevitAPIEventStatus.Failed)]
    public void UnsuccessfulClosePreservesTheOpenDocumentSession(int status)
    {
        RevitDocumentSessions sessions = new();
        Document document = new(Guid.NewGuid(), 1);
        string beforeClose = sessions.GetOrCreate(document);
        sessions.OnDocumentClosing(null, new(document, 7));

        sessions.OnDocumentClosed(null, new(7, (RevitAPIEventStatus)status));

        Assert.Equal(beforeClose, sessions.GetOrCreate(document));
    }

    [Fact]
    public void ClosingOneCopyDoesNotInvalidateAnotherOrAccessClosedDocument()
    {
        RevitDocumentSessions sessions = new();
        Guid creationGuid = Guid.NewGuid();
        Document original = new(creationGuid, 1);
        Document copy = new(creationGuid, 2);
        string oldOriginal = sessions.GetOrCreate(original);
        string copySession = sessions.GetOrCreate(copy);
        sessions.OnDocumentClosing(null, new(original, 7));
        original.IsClosed = true;

        sessions.OnDocumentClosed(null, new(7, RevitAPIEventStatus.Succeeded));

        Assert.Equal(copySession, sessions.GetOrCreate(copy));
        Assert.NotEqual(oldOriginal, sessions.GetOrCreate(new(creationGuid, 3)));
    }

    [Fact]
    public void ANewClosingEventReplacesAnUncompletedEventMapping()
    {
        RevitDocumentSessions sessions = new();
        Document original = new(Guid.NewGuid(), 1);
        Document untracked = new(Guid.NewGuid(), 2);
        string originalSession = sessions.GetOrCreate(original);
        sessions.OnDocumentClosing(null, new(original, 7));
        // Model a canceled close without a Closed callback, followed by a new
        // closing event. The old map must not retire the original session.
        sessions.OnDocumentClosing(null, new(untracked, 7));

        sessions.OnDocumentClosed(null, new(7, RevitAPIEventStatus.Succeeded));

        Assert.Equal(originalSession, sessions.GetOrCreate(original));
    }

    [Fact]
    public async Task QueuedCancellationCompletesWithoutRevitExecutionAndReclaimsCapacity()
    {
        using RevitExternalEventPump pump = new();
        pump.Attach(new ExternalEvent());
        using CancellationTokenSource timeout = new();
        int canceledExecutions = 0;
        Task<int>[] canceled = Enumerable.Range(0, 4)
            .Select(_ => pump.EnqueueAsync(_ => ++canceledExecutions, timeout.Token))
            .ToArray();
        Assert.Throws<BridgeCallException>(() =>
        {
            _ = pump.EnqueueAsync(_ => 0, CancellationToken.None);
        });

        timeout.Cancel();

        Assert.All(canceled, task => Assert.True(task.IsCanceled));
        List<int> executionOrder = [];
        Task<int>[] admitted = Enumerable.Range(0, 4)
            .Select(value => pump.EnqueueAsync(_ =>
            {
                executionOrder.Add(value);
                return value;
            }, CancellationToken.None)).ToArray();
        pump.Execute(new UIApplication());
        int[] expected = [0, 1, 2, 3];
        Assert.Equal(expected, await Task.WhenAll(admitted));
        Assert.Equal(expected, executionOrder);
        Assert.Equal(0, canceledExecutions);
    }

    [Fact]
    public async Task CancellationAfterExecutionStartsPreservesTheOperationResult()
    {
        using RevitExternalEventPump pump = new();
        pump.Attach(new ExternalEvent());
        using CancellationTokenSource cancellation = new();
        Task<int> committed = pump.EnqueueAsync(_ =>
        {
            cancellation.Cancel();
            return 42;
        }, cancellation.Token);

        pump.Execute(new UIApplication());

        Assert.Equal(42, await committed);
    }

    [Fact]
    public async Task ShutdownCompletesQueuedRequestsWithoutExecutingThem()
    {
        using RevitExternalEventPump pump = new();
        pump.Attach(new ExternalEvent { RequestResult = ExternalEventRequest.Pending });
        Task<int> queued = pump.EnqueueAsync(_ => 42, CancellationToken.None);

        pump.Dispose();

        BridgeCallException exception = await Assert.ThrowsAsync<BridgeCallException>(
            () => queued);
        Assert.Equal("host_shutdown", exception.Code);
    }
}

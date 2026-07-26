using Autodesk.Revit.UI;

namespace Tripo.Revit;

internal sealed class RevitExternalEventPump : IExternalEventHandler, IDisposable
{
    private const int MaximumQueuedRequests = 4;
    private static readonly TimeSpan RaiseRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly Queue<IWorkItem> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ExternalEvent? _externalEvent;
    private bool _raiseInProgress;
    private bool _retryScheduled;
    private bool _disposed;

    public void Attach(ExternalEvent externalEvent)
    {
        ArgumentNullException.ThrowIfNull(externalEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_externalEvent is not null)
            {
                throw new InvalidOperationException("The Revit external event is already attached.");
            }

            _externalEvent = externalEvent;
        }
    }

    public Task<T> EnqueueAsync<T>(
        Func<UIApplication, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        WorkItem<T> item = new(operation, cancellationToken);
        bool shouldRaise;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_externalEvent is null)
            {
                throw new InvalidOperationException("The Revit external event is not attached.");
            }

            if (_queue.Count >= MaximumQueuedRequests)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_busy",
                    "The Revit API queue is full.");
            }

            _queue.Enqueue(item);
            shouldRaise = !_raiseInProgress;
            if (shouldRaise)
            {
                _raiseInProgress = true;
            }
        }

        if (shouldRaise)
        {
            TryRaise();
        }

        return item.Task;
    }

    public void Execute(UIApplication application)
    {
        while (true)
        {
            IWorkItem? item;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_queue.Count == 0)
                {
                    _raiseInProgress = false;
                    return;
                }

                item = _queue.Dequeue();
            }

            item.Execute(application);
        }
    }

    public string GetName() => "Tripo MCP Revit bridge";

    public void Dispose()
    {
        List<IWorkItem> abandoned;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            abandoned = _queue.ToList();
            _queue.Clear();
            _raiseInProgress = false;
        }

        Tripo.Bridge.BridgeCallException exception = new(
            "host_shutdown",
            "Revit is shutting down.");
        foreach (IWorkItem item in abandoned)
        {
            item.Fail(exception);
        }

        _shutdown.Dispose();
    }

    private void TryRaise()
    {
        ExternalEvent? externalEvent;
        lock (_gate)
        {
            if (_disposed || _queue.Count == 0)
            {
                _raiseInProgress = false;
                return;
            }

            externalEvent = _externalEvent;
        }

        ExternalEventRequest result;
        try
        {
            result = externalEvent!.Raise();
        }
        catch (Exception exception)
        {
            FailAll(
                new Tripo.Bridge.BridgeCallException(
                    "host_unavailable",
                    "The Revit external event could not be raised.",
                    exception));
            return;
        }

        switch (result)
        {
            case ExternalEventRequest.Accepted:
                return;
            case ExternalEventRequest.Pending:
                ScheduleRaiseRetry();
                return;
            case ExternalEventRequest.Denied:
            case ExternalEventRequest.TimedOut:
                FailAll(
                    new Tripo.Bridge.BridgeCallException(
                        "host_busy",
                        $"Revit rejected the external event with status {result}."));
                return;
            default:
                FailAll(
                    new Tripo.Bridge.BridgeCallException(
                        "host_error",
                        "Revit returned an unsupported external-event status."));
                return;
        }
    }

    private void ScheduleRaiseRetry()
    {
        lock (_gate)
        {
            if (_disposed || _retryScheduled)
            {
                return;
            }

            _retryScheduled = true;
        }

        _ = RetryRaiseAsync();
    }

    private async Task RetryRaiseAsync()
    {
        try
        {
            await Task.Delay(RaiseRetryDelay, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            _retryScheduled = false;
            if (_disposed || _queue.Count == 0)
            {
                if (_queue.Count == 0)
                {
                    _raiseInProgress = false;
                }

                return;
            }

            _raiseInProgress = true;
        }

        TryRaise();
    }

    private void FailAll(Exception exception)
    {
        List<IWorkItem> failed;
        lock (_gate)
        {
            failed = _queue.ToList();
            _queue.Clear();
            _raiseInProgress = false;
        }

        foreach (IWorkItem item in failed)
        {
            item.Fail(exception);
        }
    }

    private interface IWorkItem
    {
        void Execute(UIApplication application);

        void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<UIApplication, T> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(
            Func<UIApplication, T> operation,
            CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public void Execute(UIApplication application)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _completion.TrySetResult(_operation(application));
            }
            catch (OperationCanceledException)
                when (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) =>
            _completion.TrySetException(exception);
    }
}

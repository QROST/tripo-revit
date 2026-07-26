using System.Windows.Interop;

namespace Tripo.Revit;

internal sealed class RevitPanelController : IDisposable
{
    private readonly Tripo.Bridge.IHostSidecarConnector _connector;
    private readonly object _cleanupGate = new();
    private readonly HashSet<Task> _sessionDisposals = [];
    private TripoRevitWindow? _window;
    private bool _disposed;

    public RevitPanelController(
        Tripo.Bridge.IHostSidecarConnector connector)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public void ShowOrActivate(IntPtr ownerHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null)
        {
            if (_window.WindowState == System.Windows.WindowState.Minimized)
            {
                _window.WindowState = System.Windows.WindowState.Normal;
            }

            if (!_window.IsVisible)
            {
                _window.Show();
            }

            _window.Activate();
            return;
        }

        TripoRevitWindow window = new(
            new Tripo.HostUi.TripoPanelSession(
                _connector,
                new Tripo.HostUi.TripoPanelRecoveryStore("revit")));
        window.Closed += OnWindowClosed;
        new WindowInteropHelper(window).Owner = ownerHandle;
        _window = window;
        window.Show();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TripoRevitWindow? window = Interlocked.Exchange(ref _window, null);
        if (window is not null)
        {
            window.Closed -= OnWindowClosed;
            window.CloseForApplicationShutdown();
            TrackSessionDisposal(window.SessionDisposal);
        }

        Task[] pending;
        lock (_cleanupGate)
        {
            pending = _sessionDisposals.ToArray();
        }

        try
        {
            Task.WhenAll(pending).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Tripo Revit panel shutdown failed: {0}",
                exception);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is TripoRevitWindow window)
        {
            window.Closed -= OnWindowClosed;
            Interlocked.CompareExchange(ref _window, null, window);
            TrackSessionDisposal(window.SessionDisposal);
        }
    }

    private void TrackSessionDisposal(Task disposal)
    {
        lock (_cleanupGate)
        {
            _sessionDisposals.Add(disposal);
        }

        _ = ObserveSessionDisposalAsync(disposal);
    }

    private async Task ObserveSessionDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Tripo Revit panel cleanup failed: {0}",
                exception);
        }
        finally
        {
            lock (_cleanupGate)
            {
                _sessionDisposals.Remove(disposal);
            }
        }
    }
}

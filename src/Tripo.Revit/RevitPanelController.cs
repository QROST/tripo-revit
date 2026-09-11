using System.Windows.Interop;

namespace Tripo.Revit;

internal sealed class RevitPanelController : IDisposable
{
    private readonly Tripo.Bridge.IHostSidecarConnector _connector;
    private readonly object _cleanupGate = new();
    private readonly HashSet<Task> _sessionDisposals = [];
    private TripoRevitWindow? _window;
    private TripoCatalogWindow? _catalogWindow;
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

    public void ShowCatalog(IntPtr ownerHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalogWindow is not null)
        {
            _catalogWindow.WindowState = System.Windows.WindowState.Normal;
            _catalogWindow.Activate();
            return;
        }
        TripoCatalogWindow window = new(_connector);
        new WindowInteropHelper(window).Owner = ownerHandle;
        window.Closed += (_, _) =>
        {
            Interlocked.CompareExchange(ref _catalogWindow, null, window);
            TrackSessionDisposal(window.Completion);
        };
        _catalogWindow = window;
        window.Show();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TripoCatalogWindow? catalogWindow = Interlocked.Exchange(ref _catalogWindow, null);
        if (catalogWindow is not null)
        {
            catalogWindow.Close();
            TrackSessionDisposal(catalogWindow.Completion);
        }
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

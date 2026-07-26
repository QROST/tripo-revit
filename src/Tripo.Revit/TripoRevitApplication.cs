using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Tripo.Revit;

public sealed class TripoRevitApplication : IExternalApplication, IDisposable
{
    private ExternalEvent? _externalEvent;
    private RevitExternalEventPump? _eventPump;
    private Tripo.Bridge.NamedPipeBridgeServer? _bridgeServer;
    private Tripo.Bridge.HostSidecarProcessManager? _sidecarManager;
    private RevitPanelController? _panelController;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            _eventPump = new RevitExternalEventPump();
            _externalEvent = ExternalEvent.Create(_eventPump);
            _eventPump.Attach(_externalEvent);
            RevitBridgeDispatcher dispatcher = new(
                _eventPump,
                new RevitDocumentSessions());
            _bridgeServer = new Tripo.Bridge.NamedPipeBridgeServer(
                "revit",
                "2026",
                [
                    Tripo.Bridge.BridgeConstants.ContextMethod,
                    Tripo.Bridge.BridgeConstants.ImportMeshMethod,
                ],
                dispatcher);
            using CancellationTokenSource startupTimeout = new(TimeSpan.FromSeconds(10));
            _bridgeServer.StartAsync(startupTimeout.Token).GetAwaiter().GetResult();
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string pluginDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException(
                    "The Revit add-in directory is unavailable.");
            _sidecarManager = new Tripo.Bridge.HostSidecarProcessManager(
                "revit",
                Environment.ProcessId,
                pluginDirectory,
                "Tripo.Revit.Mcp");
            _panelController = new RevitPanelController(_sidecarManager);
            RevitAddinContext.Attach(_panelController);
            RibbonPanel ribbonPanel = application.CreateRibbonPanel("Tripo");
            PushButtonData buttonData = new(
                "Tripo.OpenPanel",
                "Tripo 3D",
                assemblyPath,
                typeof(ShowTripoPanelCommand).FullName!);
            PushButton button = (PushButton)ribbonPanel.AddItem(buttonData);
            button.ToolTip =
                "Generate, convert, and import Tripo models without leaving Revit.";
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            Dispose();
            TaskDialog.Show(
                "Tripo MCP Bridge",
                "The local bridge could not start: " + exception.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Dispose();
        return Result.Succeeded;
    }

    public void Dispose()
    {
        RevitPanelController? panelController =
            Interlocked.Exchange(ref _panelController, null);
        if (panelController is not null)
        {
            RevitAddinContext.Detach(panelController);
            panelController.Dispose();
        }

        Tripo.Bridge.HostSidecarProcessManager? sidecarManager =
            Interlocked.Exchange(ref _sidecarManager, null);
        if (sidecarManager is not null)
        {
            try
            {
                sidecarManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Tripo Revit sidecar shutdown failed: {0}",
                    exception);
            }
        }

        Tripo.Bridge.NamedPipeBridgeServer? bridgeServer =
            Interlocked.Exchange(ref _bridgeServer, null);
        Task? bridgeShutdown = bridgeServer?.DisposeAsync().AsTask();

        RevitExternalEventPump? eventPump =
            Interlocked.Exchange(ref _eventPump, null);
        eventPump?.Dispose();

        if (bridgeShutdown is not null)
        {
            try
            {
                bridgeShutdown.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Tripo Revit bridge shutdown failed: {0}",
                    exception);
            }
        }

        ExternalEvent? externalEvent =
            Interlocked.Exchange(ref _externalEvent, null);
        externalEvent?.Dispose();
        GC.SuppressFinalize(this);
    }
}

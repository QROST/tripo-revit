namespace Tripo.Revit;

internal static class RevitAddinContext
{
    private static RevitPanelController? _panelController;

    public static void Attach(RevitPanelController panelController)
    {
        ArgumentNullException.ThrowIfNull(panelController);
        if (Interlocked.CompareExchange(
                ref _panelController,
                panelController,
                null) is not null)
        {
            throw new InvalidOperationException(
                "The Tripo Revit panel controller is already attached.");
        }
    }

    public static RevitPanelController GetPanelController() =>
        Volatile.Read(ref _panelController) ??
        throw new InvalidOperationException(
            "The Tripo Revit add-in is not available.");

    public static void Detach(RevitPanelController expected)
    {
        _ = Interlocked.CompareExchange(
            ref _panelController,
            null,
            expected);
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Tripo.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ShowTripoPanelCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            RevitAddinContext.GetPanelController().ShowOrActivate(
                commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = BoundMessage(exception.Message);
            return Result.Failed;
        }
    }

    private static string BoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The Tripo panel could not be opened.";
        }

        string trimmed = message.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}

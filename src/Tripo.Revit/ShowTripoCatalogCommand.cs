using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Tripo.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ShowTripoCatalogCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            RevitAddinContext.GetPanelController().ShowCatalog(
                commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.Message.Length <= 512 ? exception.Message : exception.Message[..512];
            return Result.Failed;
        }
    }
}

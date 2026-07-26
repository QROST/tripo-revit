using Autodesk.Revit.DB;

namespace Tripo.Revit;

// Overwrite handler for LoadFamily: the derived family name embeds the
// idempotency short key, so a pre-existing family with the same name is a retry
// of the same import and is safe to overwrite (parameters included).
internal sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(
        Family sharedFamily,
        bool familyInUse,
        out FamilySource source,
        out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}

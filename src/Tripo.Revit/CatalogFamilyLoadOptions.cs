using Autodesk.Revit.DB;

namespace Tripo.Revit;

// Matching catalogue families are explicitly verified and reused before load.
// Never replace an unrelated family or a shared dependency from a template.
internal sealed class CatalogFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = false;
        return false;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
        out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Project;
        overwriteParameterValues = false;
        return false;
    }
}

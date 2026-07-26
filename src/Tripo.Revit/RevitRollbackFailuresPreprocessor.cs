using Autodesk.Revit.DB;

namespace Tripo.Revit;

internal sealed class RevitRollbackFailuresPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(
        FailuresAccessor failuresAccessor)
    {
        bool hasError = false;
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                failuresAccessor.DeleteWarning(failure);
            }
            else
            {
                hasError = true;
            }
        }

        return hasError
            ? FailureProcessingResult.ProceedWithRollBack
            : FailureProcessingResult.Continue;
    }
}

using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace Tripo.Revit;

internal sealed class RevitDocumentSessions
{
    private readonly ConditionalWeakTable<Document, SessionHolder> _sessions = new();

    public string GetOrCreate(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _sessions.GetValue(
            document,
            _ => new SessionHolder(Guid.NewGuid().ToString("D"))).SessionId;
    }

    private sealed record SessionHolder(string SessionId);
}

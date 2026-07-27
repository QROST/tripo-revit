using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Tripo.Revit;

internal sealed class RevitDocumentSessions
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _sessions = [];
    private readonly Dictionary<int, Guid> _closingDocuments = [];

    public string GetOrCreate(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Guid documentIdentity = document.CreationGUID;
        lock (_gate)
        {
            if (_sessions.TryGetValue(documentIdentity, out string? sessionId))
            {
                return sessionId;
            }

            sessionId = Guid.NewGuid().ToString("D");
            _sessions.Add(documentIdentity, sessionId);
            return sessionId;
        }
    }

    public void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Guid documentIdentity = args.Document.CreationGUID;
        lock (_gate)
        {
            _closingDocuments[args.DocumentId] = documentIdentity;
        }
    }

    public void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (_gate)
        {
            if (!_closingDocuments.Remove(
                    args.DocumentId,
                    out Guid documentIdentity) ||
                args.Status != RevitAPIEventStatus.Succeeded)
            {
                return;
            }

            _sessions.Remove(documentIdentity);
        }
    }
}

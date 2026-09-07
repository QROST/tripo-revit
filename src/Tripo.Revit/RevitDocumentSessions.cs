using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Tripo.Revit;

internal sealed class RevitDocumentSessions
{
    private readonly object _gate = new();
    private readonly List<DocumentSession> _sessions = [];
    private readonly Dictionary<int, DocumentSession> _closingDocuments = [];

    public string GetOrCreate(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            // CreationGUID survives file copies. Revit's Document.Equals compares
            // the actual open document, including separate managed wrappers.
            DocumentSession? existing = FindSession(document);
            if (existing is not null)
            {
                return existing.Id;
            }

            string sessionId = Guid.NewGuid().ToString("D");
            _sessions.Add(new DocumentSession(document, sessionId));
            return sessionId;
        }
    }

    public void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (_gate)
        {
            // A canceled closing event need not produce a successful Closed
            // callback. A later close owns a fresh mapping for this event ID.
            _closingDocuments.Remove(args.DocumentId);
            DocumentSession? session = FindSession(args.Document);
            if (session is not null)
            {
                _closingDocuments[args.DocumentId] = session;
            }
        }
    }

    public void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (_gate)
        {
            if (!_closingDocuments.Remove(
                    args.DocumentId,
                    out DocumentSession? session) ||
                args.Status != RevitAPIEventStatus.Succeeded)
            {
                return;
            }

            // Do not call Revit APIs on a document after it has closed.
            _sessions.Remove(session);
        }
    }

    private DocumentSession? FindSession(Document document) =>
        _sessions.Find(session => session.Document.Equals(document));

    private sealed class DocumentSession(Document document, string id)
    {
        public Document Document { get; } = document;

        public string Id { get; } = id;
    }
}

// Minimal test-only doubles for the two linked coordination sources. The real
// Revit host still builds against the pinned Autodesk SDK in its own project.
namespace Autodesk.Revit.DB
{
    internal sealed class Document(Guid creationGuid, int openIdentity)
    {
        public Guid CreationGUID { get; } = creationGuid;

        public bool IsClosed { get; set; }

        public override bool Equals(object? other)
        {
            if (IsClosed)
            {
                throw new InvalidOperationException("Cannot inspect a closed document.");
            }

            return other is Document document && document.OpenIdentity == OpenIdentity;
        }

        public override int GetHashCode() => OpenIdentity;

        private int OpenIdentity { get; } = openIdentity;
    }
}

namespace Autodesk.Revit.DB.Events
{
    internal enum RevitAPIEventStatus { Succeeded, Cancelled, Failed }

    internal sealed class DocumentClosingEventArgs(
        Autodesk.Revit.DB.Document document, int documentId) : EventArgs
    {
        public Autodesk.Revit.DB.Document Document { get; } = document;

        public int DocumentId { get; } = documentId;
    }

    internal sealed class DocumentClosedEventArgs(
        int documentId, RevitAPIEventStatus status) : EventArgs
    {
        public int DocumentId { get; } = documentId;

        public RevitAPIEventStatus Status { get; } = status;
    }
}

namespace Autodesk.Revit.UI
{
    internal sealed class UIApplication;

    internal enum ExternalEventRequest { Accepted, Pending, Denied, TimedOut }

    internal interface IExternalEventHandler
    {
        void Execute(UIApplication application);

        string GetName();
    }

    internal sealed class ExternalEvent
    {
        public ExternalEventRequest RequestResult { get; init; } = ExternalEventRequest.Accepted;

        public ExternalEventRequest Raise() => RequestResult;
    }
}

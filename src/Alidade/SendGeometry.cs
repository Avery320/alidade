using System;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class SendGeometry : AlidadeAsyncComponent
{
    private sealed record Target(ProjectSession Project, string Id);

    private Target? _target;
    private WeakReference<GeometryCollection>? _sent;

    public SendGeometry()
        : base("Send Geometry", "Send Geometry", "Send a Geometry Collection to an Asterism input on demand.", "Exchange") { }

    public override Guid ComponentGuid => new("9d700ca8-b14a-4b59-b587-4d86490a705f");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("SendGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("ID", "ID", "Use the same ID on the Asterism Geometry Input.", GH_ParamAccess.item);
        manager.AddBooleanParameter("Send", "Send", "Connect a Grasshopper Button to send once per press.", GH_ParamAccess.item);
        for (var i = 0; i < 4; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddTextParameter("Status", "Status", "Send status for the current geometry and target.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        var id = ExchangeId(data, 2);
        var collection = SingleInput<AlidadeCollection>(data, 1)?.Value;
        var next = project != null && id != null ? new Target(project, id) : null;
        if (_target != next)
        {
            CancelRequest();
            _target = next;
            _sent = null;
        }

        if (ButtonPressed(data, 3) && !IsPending && collection is { GeometryCount: > 0 }
            && next is { } target && CanUseProject(document, target.Project))
        {
            _sent = null;
            var documentId = document.RuntimeID;
            StartRequest(document, target.Project, async cancellation =>
                {
                    await AlidadeClient.SendGeometryAsync(target.Project, documentId, target.Id, collection, cancellation)
                        .ConfigureAwait(false);
                    return new WeakReference<GeometryCollection>(collection);
                }, sent => _sent = sent);
        }

        var status = project == null ? "Connect Project"
            : id == null ? "Set the Geometry Input ID"
            : collection is not { GeometryCount: > 0 } ? "Connect Collection"
            : IsPending ? "Sending…"
            : _sent?.TryGetTarget(out var sent) == true && ReferenceEquals(sent, collection) ? "Sent" : "Ready to send";
        var error = Failure?.Message;
        ReportStatus(data, 0, error ?? status, error);
    }

    protected override void ResetState()
    {
        _target = null;
        _sent = null;
    }
}

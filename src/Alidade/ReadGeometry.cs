using System;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class ReadGeometry : AlidadeAsyncComponent
{
    private sealed record Input(ProjectSession Project, string Id);

    private Input? _input;
    private GeometryCollection? _result;

    public ReadGeometry()
        : base("Read Geometry", "Read Geometry", "Read a Geometry Collection from an Asterism output on demand.", "Exchange") { }

    public override Guid ComponentGuid => new("aecf9292-0da8-4783-9ca6-2aeedf326acc");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("ReadGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("ID", "ID", "Use the same ID on the Asterism Geometry Output.", GH_ParamAccess.item);
        manager.AddBooleanParameter("Read", "Read", "Connect a Grasshopper Button to read once per press.", GH_ParamAccess.item);
        manager[0].Optional = manager[1].Optional = manager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("Status", "Status", "Latest read status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        var id = ExchangeId(data, 1);
        var next = project != null && id != null ? new Input(project, id) : null;
        if (_input != next)
        {
            CancelRequest();
            _input = next;
            _result = null;
        }
        if (ButtonPressed(data, 2) && !IsPending && next is { } input && CanUseProject(document, input.Project))
        {
            var documentId = document.RuntimeID;
            StartRequest(document, input.Project,
                cancellation => AlidadeClient.ReadGeometryAsync(input.Project, documentId, input.Id, cancellation),
                result => _result = result);
        }
        if (_result is { } result)
            data.SetData(0, new AlidadeCollection(result));

        var status = project == null ? "Connect Project"
            : id == null ? "Set the Geometry Output ID"
            : IsPending ? "Reading…"
            : _result != null ? "Received" : "Ready to read";
        var error = Failure?.Message;
        ReportStatus(data, 1, error ?? status, error);
    }

    protected override void ResetState()
    {
        _input = null;
        _result = null;
    }
}

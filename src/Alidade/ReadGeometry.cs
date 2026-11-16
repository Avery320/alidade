using System;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class ReadGeometry : AlidadeAsyncComponent
{
    private sealed record Input(ProjectSession Project, GeometryPortReference Target);

    private Input? _input;
    private GeometryCollection? _collection;
    private string? _error;

    public ReadGeometry()
        : base("Read Geometry", "Read Geometry", "Read a Geometry Collection from an Asterism output.", "Exchange") { }

    public override Guid ComponentGuid => new("aecf9292-0da8-4783-9ca6-2aeedf326acc");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager[0].Optional = manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Latest read status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        AlidadePortReference? reference = null;
        data.GetData(1, ref reference);
        var next = project != null && Params.Input[1].VolatileDataCount == 1
            && reference?.Value is { Direction: "output" } target && target.Matches(project)
            ? new Input(project, target) : null;
        if (_input != next)
        {
            CancelRequest();
            _input = next;
            _collection = null;
            _error = null;
            Refresh();
        }

        if (_collection is { } current && _input is { } input)
        {
            data.SetData(0, new AlidadeCollection(current));
            data.SetData(1, new AlidadePortReference(input.Target));
        }
        var status = _error ?? (IsPending ? "Reading geometry…" : _input?.Target.Name
            ?? (project == null ? "Connect to Torquetum first." : "Select an Asterism output in the connected project."));
        Message = status;
        data.SetData(2, status);
        if (_error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _error);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        Menu_AppendItem(menu, "Refresh read", (_, _) => Refresh(), !IsPending && CanUse(document)
            && document != null && _input?.Project.IsValidFor(document.RuntimeID) == true);
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (document == null || !CanUse(document) || IsPending || _input is not { } input
            || !input.Project.IsValidFor(document.RuntimeID))
            return;

        _collection = null;
        _error = null;
        StartRequest(document, input.Project,
            cancellation => AlidadeClient.ReadGeometryAsync(input.Project, document.RuntimeID, input.Target, cancellation),
            result => _collection = result, error => _error = error.Message);
    }

    protected override void ResetState()
    {
        _input = null;
        _collection = null;
        _error = null;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class SelectAsterismPort : AlidadeAsyncComponent
{
    private ProjectSession? _project;
    private IReadOnlyList<GeometryPort>? _items;
    private string? _selection;
    private Guid _selectedInstance;
    private string? _error;

    public SelectAsterismPort()
        : base("Select Asterism Port", "Select Port", "Select an Asterism input or output from a Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("8006ed19-e281-404e-9ca2-acd6992c6223");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("SelectPort");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("Port ID", "ID", "Optional Asterism port ID. When omitted, select a port from the component menu.",
            GH_ParamAccess.item);
        manager[0].Optional = manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Selection and readiness.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        if (!ReferenceEquals(project, _project))
        {
            CancelRequest();
            _project = project;
            _items = null;
            _error = null;
            LoadList();
        }

        string status;
        var byId = Params.Input[1].VolatileDataCount != 0 || Params.Input[1].SourceCount != 0;
        string? portId = null;
        if (byId && Params.Input[1].VolatileDataCount == 1)
            data.GetData(1, ref portId);
        else if (!byId && project != null && _selectedInstance == project.Status.InstanceId)
            portId = _selection;

        if (project == null) status = "Connect to Torquetum first.";
        else if (IsPending) status = "Reading Asterism ports…";
        else if (_error != null) status = _error;
        else if (_items == null) status = "Use Refresh list to read Asterism ports.";
        else if (byId && string.IsNullOrWhiteSpace(portId)) status = "Provide one Asterism port ID.";
        else if (portId == null) status = _items.Count == 0 ? "No Asterism exchange ports." : "Choose a port from the component menu.";
        else
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == portId);
            if (item == null) status = AlidadeClient.ErrorMessage("PORT_NOT_FOUND");
            else
            {
                data.SetData(0, new AlidadePortReference(item.Reference(project.Status)));
                status = $"{item.Name} ({item.Direction})";
            }
        }
        Message = status;
        data.SetData(1, status);
        if (_error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _error);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = CanUse(document) && document != null && _project?.IsValidFor(document.RuntimeID) == true && !IsPending;
        Menu_AppendItem(menu, "Refresh list", (_, _) => LoadList(), enabled);
        if (!enabled || _items == null || _project is not { } project
            || Params.Input[1].SourceCount != 0 || Params.Input[1].VolatileDataCount != 0)
            return;

        var list = new ToolStripMenuItem("Asterism ports");
        menu.Items.Add(list);
        foreach (var item in _items)
        {
            var suffix = item.Id.Length > 8 ? item.Id[^8..] : item.Id;
            var chosen = _selectedInstance == project.Status.InstanceId && _selection == item.Id;
            Menu_AppendItem(list.DropDown, $"{item.Name} ({item.Direction}) [{suffix}]", (_, _) => Select(project, item.Id), true, chosen);
        }
    }

    private void Select(ProjectSession project, string id)
    {
        var document = OnPingDocument();
        if (document == null || !CanUse(document) || !ReferenceEquals(_project, project) || !project.IsValidFor(document.RuntimeID))
            return;
        _selection = id;
        _selectedInstance = project.Status.InstanceId;
        RequestSolution(document);
    }

    private void LoadList()
    {
        var document = OnPingDocument();
        if (document == null || !CanUse(document) || IsPending || _project is not { } project
            || !project.IsValidFor(document.RuntimeID))
            return;
        _error = null;
        _items = null;
        StartRequest(document, project, cancellation => AlidadeClient.ListPortsAsync(project, document.RuntimeID, cancellation),
            items => _items = items, error => _error = error.Message);
    }

    protected override void ResetState()
    {
        _project = null;
        _items = null;
        _selectedInstance = Guid.Empty;
        _selection = null;
        _error = null;
    }
}

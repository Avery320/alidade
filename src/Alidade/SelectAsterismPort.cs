using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed class SelectAsterismPort : AlidadeAsyncComponent
{
    private sealed record Binding(string ProjectId, string PortId, string Direction);

    private ProjectSession? _project;
    private IReadOnlyList<GeometryPort>? _items;
    private Binding? _binding;
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

        var byId = Params.Input[1].VolatileDataCount != 0 || Params.Input[1].SourceCount != 0;
        var binding = byId ? null : _binding;
        var portId = byId ? SingleInput<string>(data, 1) : binding?.PortId;

        string status;
        if (project == null) status = "Connect to Torquetum first.";
        else if (binding != null && binding.ProjectId != project.Status.ProjectId)
            status = "This port is saved for another project. Pair with that project or choose a new port.";
        else if (IsPending) status = "Reading Asterism ports…";
        else if (_error != null) status = _error;
        else if (_items == null) status = "Use Refresh list to read Asterism ports.";
        else if (byId && string.IsNullOrWhiteSpace(portId)) status = "Provide one Asterism port ID.";
        else if (portId == null) status = _items.Count == 0 ? "No Asterism exchange ports." : "Choose a port from the component menu.";
        else
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == portId
                && (binding == null || candidate.Direction == binding.Direction));
            if (item == null) status = AlidadeClient.ErrorMessage("PORT_NOT_FOUND");
            else
            {
                var target = item.Reference(project.Status);
                data.SetData(0, new AlidadePortReference(target));
                status = $"{target.DisplayName} ({target.Direction})";
            }
        }
        ReportStatus(data, 1, status, _error);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = !IsPending && CanUseProject(document, _project);
        Menu_AppendItem(menu, "Refresh list", (_, _) => LoadList(), enabled);
        if (!enabled || _items == null || _project is not { } project
            || Params.Input[1].SourceCount != 0 || Params.Input[1].VolatileDataCount != 0)
            return;

        var list = new ToolStripMenuItem("Asterism ports");
        menu.Items.Add(list);
        foreach (var item in _items)
        {
            var binding = new Binding(project.Status.ProjectId, item.Id, item.Direction);
            var name = GeometryPortReference.Display(item.Name, item.Id);
            Menu_AppendItem(list.DropDown, $"{name} ({item.Direction})", (_, _) => Select(project, binding), true, _binding == binding);
        }
    }

    private void Select(ProjectSession project, Binding binding)
    {
        var document = OnPingDocument();
        if (!ReferenceEquals(_project, project) || !CanUseProject(document, project))
            return;
        if (_binding == binding)
            return;
        RecordUndoEvent("Select Asterism port");
        _binding = binding;
        RequestSolution(document);
    }

    private void LoadList()
    {
        var document = OnPingDocument();
        var project = _project;
        if (IsPending || !CanUseProject(document, project))
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
        _error = null;
    }

    public override bool Write(GH_IWriter writer)
    {
        if (_binding is { } binding)
        {
            writer.SetString("AlidadeProjectId", binding.ProjectId);
            writer.SetString("AlidadePortId", binding.PortId);
            writer.SetString("AlidadePortDirection", binding.Direction);
        }
        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        var result = base.Read(reader);
        _binding = reader.ItemExists("AlidadeProjectId") && reader.ItemExists("AlidadePortId")
            && reader.ItemExists("AlidadePortDirection")
            && reader.GetString("AlidadeProjectId") is { Length: > 0 } project
            && reader.GetString("AlidadePortId") is { Length: > 0 } port
            && reader.GetString("AlidadePortDirection") is { } direction && direction is ("input" or "output")
            ? new Binding(project, port, direction) : null;
        return result;
    }
}

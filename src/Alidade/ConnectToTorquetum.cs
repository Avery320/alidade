using System;
using System.Windows.Forms;
using Grasshopper.Kernel;
using EtoClipboard = Eto.Forms.Clipboard;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : AlidadeAsyncComponent
{
    private const string Waiting = "Waiting for pairing";

    private ProjectSession? _session;
    private string _status = Waiting;
    private bool _current;
    private string? _warning;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");

    protected override void RegisterInputParams(GH_InputParamManager manager) { }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("Status", "S", "Alidade connection status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        if (_session is { } session && document != null && !session.IsValidFor(document.RuntimeID))
        {
            ResetState();
            _status = "Pair with the project again.";
        }
        if (_current && _session is { } active)
            data.SetData(0, new AlidadeProject(active));

        Message = _status;
        data.SetData(1, _status);
        if (_warning != null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _warning);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var enabled = CanUse(OnPingDocument());
        Menu_AppendItem(menu, "Pair from clipboard", (_, _) => PairFromClipboard(), enabled);
        Menu_AppendItem(menu, "Refresh status", (_, _) => Refresh(), enabled && _session?.IsValid == true);
        Menu_AppendItem(menu, "Disconnect", (_, _) => Disconnect(), _session != null || IsPending);
    }

    private void PairFromClipboard()
    {
        var document = OnPingDocument();
        if (!CanUse(document) || document == null)
            return;

        var code = EtoClipboard.Instance.Text ?? "";
        ResetState();
        Begin("Pairing with Torquetum…");
        StartRequest(document, null, cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
            session => { _session = session; ShowStatus(session.Status); }, ShowFailure, session => session.Disconnect());
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (!CanUse(document) || document == null || _session is not { } session)
            return;

        Begin("Checking the connection…");
        StartRequest(document, session, cancellation => session.RefreshAsync(document.RuntimeID, cancellation),
            status => { session.Status = status; ShowStatus(status); }, ShowFailure);
    }

    private void Begin(string status)
    {
        _current = false;
        _warning = null;
        _status = status;
    }

    private void ShowStatus(ProjectStatus project)
    {
        _current = true;
        _status = project.CanRead
            ? $"{project.ProjectName} ({(project.CanWrite ? "read/write" : "read-only")})"
            : $"{project.ProjectName} (unavailable)";
        if (!project.CanRead || project.AvailabilityReason == "PROJECT_BUSY")
            _warning = project.AvailabilityReason == null
                ? "The Torquetum project is not ready for reading."
                : AlidadeClient.ErrorMessage(project.AvailabilityReason);
    }

    private void ShowFailure(AlidadeConnectionException error)
    {
        if (error.PairingRequired)
            ResetState();
        _status = _warning = error.Message;
    }

    private void Disconnect()
    {
        ResetState();
        if (OnPingDocument() is { } document)
            RequestSolution(document);
    }

    protected override void ResetState()
    {
        CancelRequest();
        _session?.Disconnect();
        _session = null;
        _current = false;
        _status = Waiting;
        _warning = null;
    }
}

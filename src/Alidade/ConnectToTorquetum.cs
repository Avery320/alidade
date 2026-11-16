using System;
using System.Windows.Forms;
using Grasshopper.Kernel;
using EtoClipboard = Eto.Forms.Clipboard;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : AlidadeAsyncComponent
{
    private const string Waiting = "Waiting for pairing";

    private ProjectSession? _session;
    private (string Message, string? Warning)? _failure;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("Connect");

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
            _failure = ("Pair with the project again.", null);
        }
        var (status, warning) = IsPending
            ? (_session == null ? "Pairing with Torquetum…" : "Checking the connection…", null)
            : _failure ?? (_session == null ? (Waiting, null) : ShowConnection(data, _session));
        ReportStatus(data, 1, status, warning);
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
        if (!CanUse(document))
            return;

        var code = EtoClipboard.Instance.Text ?? "";
        ResetState();
        StartRequest(document, null, cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
            session => _session = session, ShowFailure, session => session.Disconnect());
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (!CanUse(document) || _session is not { } session)
            return;

        _failure = null;
        StartRequest(document, session, cancellation => AlidadeClient.ReadProjectAsync(session, document.RuntimeID, cancellation),
            status => session.Status = status, ShowFailure);
    }

    private static (string, string?) ShowConnection(IGH_DataAccess data, ProjectSession session)
    {
        data.SetData(0, new AlidadeProject(session));
        var project = session.Status;
        var status = project.CanRead
            ? $"{project.ProjectName} ({(project.CanWrite ? "read/write" : "read-only")})"
            : $"{project.ProjectName} (unavailable)";
        var warning = !project.CanRead || project.AvailabilityReason == "PROJECT_BUSY"
            ? (project.AvailabilityReason is { } reason ? AlidadeClient.ErrorMessage(reason) : "The Torquetum project is not ready for reading.")
            : null;
        return (status, warning);
    }

    private void ShowFailure(AlidadeConnectionException error)
    {
        if (error.PairingRequired)
            ResetState();
        _failure = (error.Message, error.Message);
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
        _failure = null;
    }
}

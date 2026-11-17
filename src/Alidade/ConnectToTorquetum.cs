using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : AlidadeAsyncComponent
{
    private string? _attemptedCode;
    private ProjectSession? _session;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");
    protected override Bitmap Icon => ComponentIcons.Load("Connect");
    protected override int OriginalInputCount => 0;

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddTextParameter("Pairing Code", "Pairing Code",
            "Paste a one-time pairing code from Torquetum Project Settings.", GH_ParamAccess.item);
        manager.AddBooleanParameter("Connect", "Connect",
            "Set to True to connect or False to disconnect.", GH_ParamAccess.item, false);
        manager[0].Optional = manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("Status", "Status", "Alidade connection status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        if (!CanUse(document) || SingleInput<GH_Boolean>(data, 1)?.Value != true)
        {
            CancelRequest();
            ResetState();
            ReportStatus(data, 1, "Disconnected", null);
            return;
        }

        var code = SingleInput<string>(data, 0)?.Trim();
        if (!string.IsNullOrEmpty(code) && code != _attemptedCode)
        {
            ResetState();
            _attemptedCode = code;
            StartRequest(document, null, cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
                session => _session = session, abandon: session => session.Disconnect());
        }

        if (IsPending || Failure != null || _session is not { } connected || !connected.IsValidFor(document.RuntimeID))
        {
            var error = Failure?.Message;
            var status = IsPending ? "Connecting…" : _attemptedCode == null ? "Enter a Pairing Code" : "Enter a new Pairing Code";
            ReportStatus(data, 1, error ?? status, error);
            return;
        }

        data.SetData(0, new AlidadeProject(connected));
        var project = connected.Status;
        var access = !project.CanRead ? "unavailable" : project.CanWrite ? "read/write" : "read-only";
        var warning = !project.CanRead || project.AvailabilityReason == "PROJECT_BUSY"
            ? AlidadeClient.ErrorMessage(project.AvailabilityReason ?? "PROJECT_BUSY") : null;
        ReportStatus(data, 1, $"{project.ProjectName} ({access})", warning);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        Menu_AppendItem(menu, "Refresh status", (_, _) => Refresh(), !IsPending && CanUseProject(OnPingDocument(), _session));
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        var session = _session;
        if (IsPending || !CanUseProject(document, session))
            return;

        StartRequest(document, session, cancellation => AlidadeClient.ReadProjectAsync(session, document.RuntimeID, cancellation),
            status => session.Status = status);
    }

    protected override void ResetState()
    {
        _session?.Disconnect();
        _session = null;
    }
}

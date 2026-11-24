using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Torquetum.Nuncius;

public sealed class ConnectToTorquetum : NuncAsyncComponent
{
    private string? _attemptedCode;
    private bool _attempted;
    private ProjectSession? _session;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");
    protected override Bitmap Icon => ComponentIcons.Load("Connect");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddTextParameter("Pairing Code", "Pairing Code",
            "Paste a one-time pairing code to authorize this connector.", GH_ParamAccess.item);
        manager.AddBooleanParameter("Connect", "Connect",
            "Set to True to connect or False to disconnect.", GH_ParamAccess.item, false);
        manager[0].Optional = manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new NuncProjectParameter());
        manager.AddTextParameter("Status", "Status", "Nuncius connection status.", GH_ParamAccess.item);
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
        if (!_attempted || (!string.IsNullOrEmpty(code) && code != _attemptedCode))
        {
            ResetState();
            _attempted = true;
            _attemptedCode = code;
            var connector = InstanceGuid;
            var documentId = document.RuntimeID;
            StartRequest(document, null, cancellation => NuncClient.ConnectAsync(code, connector, documentId, cancellation),
                session =>
                {
                    _session = session;
                    if (session != null) session.Disconnected += RefreshConnectionOutput;
                }, abandon: session => session?.Disconnect());
        }

        if (IsPending || Failure != null || _session is not { } connected || !connected.IsValidFor(document.RuntimeID))
        {
            var error = Failure?.Message;
            var status = IsPending ? "Connecting…" : _attemptedCode == null ? "Enter a Pairing Code" : "Enter a new Pairing Code";
            ReportStatus(data, 1, error ?? status, error);
            return;
        }

        data.SetData(0, new NuncProject(connected));
        var project = connected.Status;
        var access = !project.CanRead ? "unavailable" : project.CanWrite ? "read/write" : "read-only";
        var warning = !project.CanRead || project.AvailabilityReason == "PROJECT_BUSY"
            ? NuncClient.ErrorMessage(project.AvailabilityReason ?? "PROJECT_BUSY") : null;
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

        StartRequest(document, session, cancellation => NuncClient.ReadProjectAsync(session, document.RuntimeID, cancellation),
            status => session.Status = status);
    }

    protected override void ResetState()
    {
        _attemptedCode = null;
        _attempted = false;
        if (_session is { } session)
        {
            session.Disconnected -= RefreshConnectionOutput;
            session.Disconnect();
        }
        _session = null;
    }

    private void RefreshConnectionOutput()
    {
        var document = OnPingDocument();
        if (CanUse(document)) RequestSolution(document);
    }
}

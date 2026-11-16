using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using EtoClipboard = Eto.Forms.Clipboard;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : AlidadeAsyncComponent
{
    private const string Waiting = "Waiting for pairing";
    private const string PairAction = "Pair with Torquetum";

    private ProjectSession? _session;
    private (string Message, string? Warning)? _failure;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("704efa3e-ae66-48c5-89fc-01ebffe84565");
    protected override Bitmap Icon => ComponentIcons.Load("Connect");
    public override void CreateAttributes() => m_attributes = new ConnectAttributes(this);

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
        Menu_AppendItem(menu, PairAction, (_, _) => Pair(), enabled && !IsPending);
        Menu_AppendItem(menu, "Refresh status", (_, _) => Refresh(), enabled && _session?.IsValid == true);
        Menu_AppendItem(menu, "Disconnect", (_, _) => Disconnect(), _session != null || IsPending);
    }

    private void Pair()
    {
        var document = OnPingDocument();
        if (IsPending || !CanUse(document))
            return;

        var code = EtoClipboard.Instance.Text?.Trim() ?? "";
        if (AlidadeClient.HasInvitationPrefix(code))
            BeginPair(document, code, fromClipboard: true);
        else
            PromptForPairingCode(document);
    }

    private void PromptForPairingCode(GH_Document document, string? failure = null)
    {
        var message = "Paste the pairing code from Torquetum Project Settings.";
        if (failure != null) message = failure + "\n\n" + message;
        if (Rhino.UI.Dialogs.ShowEditBox(PairAction, message, "", false, out var code)
            && ReferenceEquals(OnPingDocument(), document) && CanUse(document) && !IsPending)
            BeginPair(document, code, fromClipboard: false);
    }

    private void BeginPair(GH_Document document, string code, bool fromClipboard)
    {
        ResetState();
        StartRequest(document, null, cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
            session => _session = session,
            error =>
            {
                ShowFailure(error);
                if (fromClipboard && error.Code == "INVITE_INVALID")
                    PromptForPairingCode(document, error.Message);
            }, session => session.Disconnect());
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

    private sealed class ConnectAttributes : GH_ComponentAttributes
    {
        private readonly ConnectToTorquetum _component;
        private RectangleF _pairButton;

        internal ConnectAttributes(ConnectToTorquetum component) : base(component) => _component = component;

        protected override void Layout()
        {
            base.Layout();
            const float width = 102;
            const float height = 18;
            _pairButton = new RectangleF(ContentBox.Left + (ContentBox.Width - width) / 2,
                Bounds.Top - height - 3, width, height);
            Bounds = RectangleF.Union(Bounds, _pairButton);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;

            using var button = GH_Capsule.CreateTextCapsule(_pairButton, _pairButton, GH_Palette.Blue, PairAction);
            button.Render(graphics, Selected, _component.IsPending || !_component.CanUse(_component.OnPingDocument()), false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Left || !_pairButton.Contains(e.CanvasLocation))
                return base.RespondToMouseDown(sender, e);

            _component.Pair();
            return GH_ObjectResponse.Handled;
        }
    }
}

using System;
using System.Net;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

public sealed class SendGeometry : AlidadeAsyncComponent
{
    private sealed record Inputs(ProjectSession Project, GeometryCollection Collection, string PortId);

    private sealed class Attempt
    {
        internal required ProjectSession Project { get; init; }
        internal required string PortId { get; init; }
        internal required WeakReference<GeometryCollection> Collection { get; init; }
        internal GeometryWriteRequest? Request { get; set; }
        internal GeometryReceipt? Receipt { get; set; }
        internal bool CanStartAnother { get; set; }
    }

    private Inputs? _inputs;
    private Attempt? _attempt;
    private bool _triggerHigh = true;

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
        if (_attempt is { } old && !ReferenceEquals(old.Project, project))
        {
            CancelRequest();
            _attempt = null;
        }
        var (next, blocked) = Capture(data, project, ExchangeId(data, 2));
        _inputs = next;
        if (Params.Input[3].SourceCount > 0 && SingleInput<GH_Boolean>(data, 3) is { } trigger)
        {
            var pressed = trigger.Value && !_triggerHigh;
            _triggerHigh = trigger.Value;
            if (pressed) Begin();
        }
        else
            _triggerHigh = true;

        string? error = null;
        var status = blocked ?? "Ready";
        if (_attempt is { } attempt && next is { } current)
        {
            if (MatchesCurrent(attempt, current))
            {
                error = Failure?.Message ?? attempt.Receipt?.Error;
                status = IsPending ? "Sending…" : error ?? (attempt.Receipt?.Pending == false ? "Sent" : "Check result");
            }
            else if (IsPending || !attempt.CanStartAnother)
                status = "Check previous send";
        }
        ReportStatus(data, 0, status, error);
    }

    private static bool MatchesCurrent(Attempt attempt, Inputs current) => attempt.Collection.TryGetTarget(out var collection)
        && ReferenceEquals(collection, current.Collection) && attempt.PortId == current.PortId;

    private (Inputs? Inputs, string? Blocked) Capture(IGH_DataAccess data, ProjectSession? project, string? portId)
    {
        if (project == null)
            return (null, "Connect Project");
        if (SingleInput<AlidadeCollection>(data, 1)?.Value is not { GeometryCount: > 0 } collection)
            return (null, "Connect Collection");
        if (portId == null)
            return (null, "Set the Geometry Input ID");
        return (new Inputs(project, collection, portId), null);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        Menu_AppendItem(menu, "Send", (_, _) => Begin(), !IsPending && CanStart() && CanUseProject(document, _inputs?.Project));
        var canContinue = !IsPending && CanContinue() && CanUseProject(document, _attempt?.Project);
        Menu_AppendItem(menu, "Check result", (_, _) => Continue(check: true), canContinue);
        Menu_AppendItem(menu, "Retry same request", (_, _) => Continue(check: false), canContinue);
    }

    private bool CanStart() => _inputs != null && (_attempt == null || _attempt.CanStartAnother);

    private bool CanContinue()
    {
        var retryAfter = Failure is { } failure ? failure.RetryAfter : _attempt?.Receipt?.RetryAfter;
        return _attempt?.Request != null && (retryAfter == null || retryAfter <= DateTimeOffset.UtcNow);
    }

    private void Begin()
    {
        var document = OnPingDocument();
        if (IsPending || !CanStart() || _inputs is not { } input || !CanUseProject(document, input.Project))
            return;

        _attempt = new Attempt
        {
            Project = input.Project, PortId = input.PortId, Collection = new(input.Collection),
        };
        Execute(document, _attempt, check: false, input.Collection);
    }

    private void Continue(bool check)
    {
        var document = OnPingDocument();
        if (IsPending || !CanContinue() || _attempt is not { } attempt
            || !CanUseProject(document, attempt.Project))
            return;

        Execute(document, attempt, check);
    }

    private void Execute(GH_Document document, Attempt attempt, bool check, GeometryCollection? initial = null)
    {
        var project = attempt.Project;
        var documentId = document.RuntimeID;
        if (!check) attempt.CanStartAnother = false; // Retried writes can be admitted even if the original was not.
        StartRequest(document, project, async cancellation =>
            {
                if (check)
                    return await AlidadeClient.CheckReceiptAsync(project, documentId, attempt.Request!, cancellation)
                        .ConfigureAwait(false);
                if (initial != null)
                    attempt.Request = await AlidadeClient.PrepareWriteAsync(project, documentId, attempt.PortId, initial, cancellation)
                        .ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return await AlidadeClient.WriteGeometryAsync(project, documentId, attempt.Request!, cancellation).ConfigureAwait(false);
            },
            receipt =>
            {
                attempt.Receipt = receipt;
                attempt.CanStartAnother = !receipt.Pending;
                if (!receipt.Pending) attempt.Request = null;
            },
            error => RecordFailure(attempt, error, check));
    }

    private static void RecordFailure(Attempt attempt, AlidadeConnectionException error, bool checking)
    {
        if (checking)
        {
            if (error.Code == "REQUEST_UNKNOWN")
                attempt.CanStartAnother = true;
            return;
        }

        if (attempt.Request == null || error.Code is "PROJECT_BUSY" or "CAPACITY_EXCEEDED" or "PROJECT_SYNC_PENDING")
        {
            // The write has not been admitted. Retain any prepared request for a retry.
            attempt.CanStartAnother = true;
        }
        else if (error.Code != "REQUEST_ID_CONFLICT" && error.HttpStatus is { } status
            && status >= HttpStatusCode.BadRequest && (int)status < 500)
        {
            attempt.CanStartAnother = true;
            attempt.Request = null;
        }
    }

    protected override void ResetState()
    {
        _inputs = null;
        _attempt = null;
        _triggerHigh = true;
    }
}

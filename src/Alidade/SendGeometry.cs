using System;
using System.Net;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class SendGeometry : AlidadeAsyncComponent
{
    private sealed record Inputs(ProjectSession Project, GeometryCollection Collection, GeometryPortReference Target);

    private sealed class Attempt
    {
        internal Attempt(Inputs inputs, GeometryWriteRequest request)
        {
            Inputs = inputs;
            Id = request.Id;
            Request = request;
        }

        internal Inputs Inputs { get; }
        internal Guid Id { get; }
        internal GeometryWriteRequest? Request { get; set; }
        internal GeometryReceipt? Receipt { get; set; }
        internal string? Error { get; set; }
        internal DateTimeOffset? RetryAfter { get; set; }
        internal bool CanStartAnother { get; set; }
    }

    private ProjectSession? _project;
    private Inputs? _inputs;
    private Attempt? _attempt;
    private string? _problem;
    private string _ready = "Connect to Torquetum first.";

    public SendGeometry()
        : base("Send Geometry", "Send Geometry", "Send a Geometry Collection to an Asterism input on demand.", "Exchange") { }

    public override Guid ComponentGuid => new("9d700ca8-b14a-4b59-b587-4d86490a705f");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("SendGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadePortReferenceParameter(), "Target", "T", "Asterism input to update.", GH_ParamAccess.item);
        for (var i = 0; i < 3; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeWriteReceiptParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "State of the last write and current inputs.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        _project = ConnectedProject(data, document);
        var switched = _attempt is { } old && _project != null && !ReferenceEquals(old.Inputs.Project, _project);
        if (switched)
        {
            CancelRequest();
            _attempt = null;
        }
        var next = Capture(data, _project);
        if (switched)
            _problem = "Project changed. A previous receipt belongs to its original connection.";
        else if (_inputs != next && next != null)
            _problem = null;
        _inputs = next;

        var attempt = _attempt;
        var error = _problem ?? attempt?.Error;
        string status;
        if (attempt != null && ReferenceEquals(_project, attempt.Inputs.Project))
        {
            status = IsPending ? "Checking the geometry write…" : _problem ?? attempt.Error ?? attempt.Receipt?.Message
                ?? "Result unconfirmed. Use Check result.";
            if (_inputs != attempt.Inputs)
                status += " (previous inputs)";
            if (attempt.Receipt is { } receipt)
            {
                data.SetData(0, new AlidadeWriteReceipt(receipt));
                if (receipt is { State: "committed", ETag: { } tag })
                    data.SetData(1, new AlidadePortReference(attempt.Inputs.Target with { ETag = tag }));
            }
        }
        else
            status = _problem ?? _ready;

        ReportStatus(data, 2, status, error);
    }

    private Inputs? Capture(IGH_DataAccess data, ProjectSession? project)
    {
        if (project == null)
        {
            _ready = "Connect to Torquetum first.";
            return null;
        }
        if (SingleInput<AlidadeCollection>(data, 1)?.Value is not { GeometryCount: > 0 } collection)
        {
            _ready = "Connect one nonempty Geometry Collection.";
            return null;
        }

        if (ConnectedPort(data, 2, project, "input") is not { } reference)
        {
            _ready = "Select an Asterism input in the connected project.";
            return null;
        }
        _ready = Unavailable(project, collection) ?? $"Ready to send to {reference.Name}.";
        return new Inputs(project, collection, reference);
    }

    private static string? Unavailable(ProjectSession project, GeometryCollection collection) =>
        !project.Status.CanWrite ? AlidadeClient.ErrorMessage("READ_ONLY")
        : !project.Status.SupportsCollection(collection.SchemaVersion) ? "The paired project does not offer precise STEP exchange."
        : null;

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = !IsPending && CanUseProject(document, _project);
        Menu_AppendItem(menu, "Send", (_, _) => Begin(), enabled && CanStart());
        Menu_AppendItem(menu, "Check result", (_, _) => Continue(check: true), enabled && CanContinue(check: true));
        Menu_AppendItem(menu, "Retry same request", (_, _) => Continue(check: false), enabled && CanContinue(check: false));
    }

    private bool CanStart() => _inputs is { } input && Unavailable(input.Project, input.Collection) == null
        && (_attempt == null || _attempt.CanStartAnother);

    private bool CanContinue(bool check) =>
        _attempt is { } attempt && ReferenceEquals(_project, attempt.Inputs.Project)
        && (attempt.RetryAfter == null || attempt.RetryAfter <= DateTimeOffset.UtcNow)
        && (check || attempt.Request != null);

    private void Begin()
    {
        var document = OnPingDocument();
        if (IsPending || !CanStart() || _inputs is not { } input || !CanUseProject(document, input.Project))
            return;

        try
        {
            var target = input.Target;
            if (_attempt is { Receipt: { State: "committed", ETag: { } tag } } previous && previous.Inputs.Target == target)
                target = target with { ETag = tag };
            _attempt = new Attempt(input, AlidadeClient.PrepareWrite(input.Collection, target));
            _problem = null;
            Execute(document, _attempt, check: false);
        }
        catch (AlidadeConnectionException error)
        {
            _problem = error.Message;
            RequestSolution(document);
        }
    }

    private void Continue(bool check)
    {
        var document = OnPingDocument();
        if (IsPending || !CanContinue(check) || _attempt is not { } attempt
            || !CanUseProject(document, attempt.Inputs.Project))
            return;

        Execute(document, attempt, check);
    }

    private void Execute(GH_Document document, Attempt attempt, bool check)
    {
        var project = attempt.Inputs.Project;
        var request = attempt.Request;
        attempt.Error = null;
        _problem = null;
        StartRequest(document, project, cancellation => check
                ? AlidadeClient.CheckReceiptAsync(project, document.RuntimeID, attempt.Id, attempt.Inputs.Target.PortId, cancellation)
                : AlidadeClient.WriteGeometryAsync(project, document.RuntimeID, request!, cancellation),
            receipt =>
            {
                attempt.Receipt = receipt;
                attempt.RetryAfter = receipt.RetryAfter;
                attempt.CanStartAnother = receipt.State != "pending";
                if (attempt.CanStartAnother) attempt.Request = null;
            },
            error => RecordFailure(attempt, error, check));
    }

    private static void RecordFailure(Attempt attempt, AlidadeConnectionException error, bool checking)
    {
        attempt.Error = error.Message;
        attempt.RetryAfter = error.RetryAfter;
        if (checking)
        {
            if (error.Code == "REQUEST_UNKNOWN")
                attempt.CanStartAnother = true;
            return;
        }

        if (error.Code is "PROJECT_BUSY" or "CAPACITY_EXCEEDED" or "PROJECT_SYNC_PENDING")
        {
            // These replies are sent before a write is admitted. Its immutable envelope can be retried.
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
        _project = null;
        _inputs = null;
        _attempt = null;
        _problem = null;
        _ready = "Connect to Torquetum first.";
    }
}

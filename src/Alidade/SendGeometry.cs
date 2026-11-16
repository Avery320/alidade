using System;
using System.Net;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class SendGeometry : AlidadeAsyncComponent
{
    private sealed record Inputs(ProjectSession Project, GeometryCollection Collection, GeometryReference? Target, string Name);

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
        : base("Send Geometry", "Send Geometry", "Create or replace Station geometry on demand.", "Exchange") { }

    public override Guid ComponentGuid => new("9d700ca8-b14a-4b59-b587-4d86490a705f");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadeGeometryRefParameter(), "Target", "T", "Geometry to replace.", GH_ParamAccess.item);
        manager.AddTextParameter("Name", "N", "Station name for Create.", GH_ParamAccess.item);
        for (var i = 0; i < 4; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeWriteReceiptParameter());
        manager.AddParameter(new AlidadeGeometryRefParameter());
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
                if (receipt is { State: "committed", EntityId: { } id, ETag: { } tag })
                {
                    var name = attempt.Inputs.Target?.Name ?? attempt.Inputs.Name;
                    data.SetData(1, new AlidadeGeometryRef(new GeometryReference(receipt.ProjectId, receipt.InstanceId,
                        id, tag, name, attempt.Inputs.Project.Status.CanWrite)));
                }
            }
        }
        else
            status = _problem ?? _ready;

        Message = status;
        data.SetData(2, status);
        if (error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, error);
    }

    private Inputs? Capture(IGH_DataAccess data, ProjectSession? project)
    {
        if (project == null)
        {
            _ready = "Connect to Torquetum first.";
            return null;
        }
        AlidadeCollection? wrapped = null;
        if (Params.Input[1].VolatileDataCount != 1 || !data.GetData(1, ref wrapped)
            || wrapped?.Value is not { GeometryCount: > 0 } collection)
        {
            _ready = "Connect one nonempty Geometry Collection.";
            return null;
        }

        if (Params.Input[2].SourceCount != 0 || Params.Input[2].VolatileDataCount != 0)
        {
            AlidadeGeometryRef? target = null;
            if (Params.Input[2].VolatileDataCount != 1 || !data.GetData(2, ref target)
                || target?.Value is not { CanReplace: true } reference || !reference.Matches(project))
            {
                _ready = "Select a replaceable geometry in the connected project.";
                return null;
            }
            _ready = Unavailable(project, collection) ?? "Ready to replace geometry.";
            return new Inputs(project, collection, reference, "");
        }

        string? name = null;
        if (Params.Input[3].VolatileDataCount != 1 || !data.GetData(3, ref name) || string.IsNullOrWhiteSpace(name))
        {
            _ready = "Enter a name for Create.";
            return null;
        }
        _ready = Unavailable(project, collection) ?? "Ready to create geometry.";
        return new Inputs(project, collection, null, name.Trim());
    }

    private static string? Unavailable(ProjectSession project, GeometryCollection collection) =>
        !project.Status.CanWrite ? AlidadeClient.ErrorMessage("READ_ONLY")
        : !project.Status.SupportsCollection(collection.SchemaVersion) ? "The paired project does not offer precise STEP exchange."
        : null;

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = !IsPending && CanUse(document) && document != null && _project?.IsValidFor(document.RuntimeID) == true;
        Menu_AppendItem(menu, "Create", (_, _) => Begin(replace: false), enabled && CanStart(replace: false));
        Menu_AppendItem(menu, "Replace", (_, _) => Begin(replace: true), enabled && CanStart(replace: true));
        Menu_AppendItem(menu, "Check result", (_, _) => Continue(check: true), enabled && CanContinue(check: true));
        Menu_AppendItem(menu, "Retry same request", (_, _) => Continue(check: false), enabled && CanContinue(check: false));
    }

    private bool CanStart(bool replace) => _inputs is { } input && Unavailable(input.Project, input.Collection) == null
        && (input.Target != null) == replace && (_attempt == null || _attempt.CanStartAnother);

    private bool CanContinue(bool check) =>
        _attempt is { } attempt && ReferenceEquals(_project, attempt.Inputs.Project)
        && (attempt.RetryAfter == null || attempt.RetryAfter <= DateTimeOffset.UtcNow)
        && (check || attempt.Request != null);

    private void Begin(bool replace)
    {
        var document = OnPingDocument();
        if (document == null || !CanUse(document) || IsPending || !CanStart(replace) || _inputs is not { } input
            || !input.Project.IsValidFor(document.RuntimeID))
            return;

        try
        {
            _attempt = new Attempt(input, AlidadeClient.PrepareWrite(input.Collection, input.Name, input.Target));
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
        if (document == null || !CanUse(document) || IsPending || !CanContinue(check)
            || _attempt is not { } attempt || !attempt.Inputs.Project.IsValidFor(document.RuntimeID))
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
                ? AlidadeClient.CheckReceiptAsync(project, document.RuntimeID, attempt.Id, cancellation)
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

        if (error.Code is "PROJECT_BUSY" or "CAPACITY_EXCEEDED" or "PROJECT_SYNC_PENDING" or "PROJECT_NOT_READY")
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

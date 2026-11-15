using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using EtoApplication = Eto.Forms.Application;
using EtoClipboard = Eto.Forms.Clipboard;

namespace Torquetum.Alidade;

public sealed class ConnectToTorquetum : GH_Component
{
    private const string Waiting = "Waiting for pairing";

    private ProjectSession? _session;
    private CancellationTokenSource? _pending;
    private string _status = Waiting;
    private bool _current;
    private string? _warning;

    public ConnectToTorquetum()
        : base("Connect to Torquetum", "Connect", "Pair with a running Torquetum project.",
            "Alidade", "Session") { }

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
            ResetSession();
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
        Menu_AppendItem(menu, "Disconnect", (_, _) => Disconnect(), _session != null || _pending != null);
    }

    private void PairFromClipboard()
    {
        var document = OnPingDocument();
        if (!CanUse(document) || document == null)
            return;

        var code = EtoClipboard.Instance.Text ?? "";
        ResetSession();
        Start(document, "Pairing with Torquetum…",
            cancellation => AlidadeClient.PairAsync(code, document.RuntimeID, cancellation),
            session => _session = session);
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (!CanUse(document) || document == null || _session is not { } session)
            return;

        Start(document, "Checking the connection…",
            cancellation => session.RefreshAsync(document.RuntimeID, cancellation),
            status => session.Status = status);
    }

    private bool CanUse(GH_Document? document) => document?.Enabled == true && !Locked;

    private void Start<T>(GH_Document document, string status,
        Func<CancellationToken, Task<T>> request, Action<T> accept)
    {
        CancelRequest();
        var operation = new CancellationTokenSource();
        _pending = operation;
        _current = false;
        _warning = null;
        _status = status;
        RequestSolution(document);
        _ = CompleteAsync(document, operation, request, accept);
    }

    private async Task CompleteAsync<T>(GH_Document document, CancellationTokenSource operation,
        Func<CancellationToken, Task<T>> request, Action<T> accept)
    {
        T result = default!;
        AlidadeConnectionException? error = null;
        try
        {
            result = await request(operation.Token).ConfigureAwait(false);
        }
        catch (AlidadeConnectionException expected)
        {
            error = expected;
        }
        catch (HttpRequestException)
        {
            error = new AlidadeConnectionException("Torquetum is unavailable. Check the project and try again.");
        }
        catch (OperationCanceledException)
        {
            error = new AlidadeConnectionException("Torquetum did not answer in time. Check the project and try again.");
        }

        EtoApplication.Instance.AsyncInvoke(() =>
        {
            operation.Dispose();
            if (!ReferenceEquals(_pending, operation) || !ReferenceEquals(OnPingDocument(), document))
            {
                if (result is ProjectSession detached)
                    detached.Disconnect();
                return;
            }

            _pending = null;
            if (error != null)
            {
                if (error.PairingRequired)
                    ResetSession();
                _status = _warning = error.Message;
            }
            else
            {
                accept(result);
                if (_session is { } session)
                {
                    _current = true;
                    var project = session.Status;
                    _status = project.CanRead
                        ? $"{project.ProjectName} ({(project.CanWrite ? "read/write" : "read-only")})"
                        : $"{project.ProjectName} (unavailable)";
                    if (!project.CanRead || project.AvailabilityReason == "PROJECT_BUSY")
                        _warning = project.AvailabilityReason == null
                            ? "The Torquetum project is not ready for reading."
                            : AlidadeClient.ErrorMessage(project.AvailabilityReason);
                }
            }
            RequestSolution(document);
        });
    }

    private void Disconnect()
    {
        ResetSession();
        if (OnPingDocument() is { } document)
            RequestSolution(document);
    }

    private void ResetSession()
    {
        CancelRequest();
        _session?.Disconnect();
        _session = null;
        _current = false;
        _status = Waiting;
        _warning = null;
    }

    private void CancelRequest()
    {
        var operation = _pending;
        _pending = null;
        operation?.Cancel();
    }

    private void RequestSolution(GH_Document document)
    {
        if (document.SolutionDepth > 0)
        {
            document.SolutionEnd -= OnSolutionEnd;
            document.SolutionEnd += OnSolutionEnd;
            return;
        }
        ExpireSolution(false);
        document.ScheduleSolution(1);
    }

    private void OnSolutionEnd(object sender, GH_SolutionEventArgs args)
    {
        var document = args.Document;
        document.SolutionEnd -= OnSolutionEnd;
        EtoApplication.Instance.AsyncInvoke(() =>
        {
            if (ReferenceEquals(OnPingDocument(), document))
                RequestSolution(document);
        });
    }

    public override bool Read(GH_IReader reader)
    {
        ResetSession();
        return base.Read(reader);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        document.SolutionEnd -= OnSolutionEnd;
        ResetSession();
        base.RemovedFromDocument(document);
    }

    public override void MovedBetweenDocuments(GH_Document oldDocument, GH_Document newDocument)
    {
        oldDocument.SolutionEnd -= OnSolutionEnd;
        ResetSession();
        base.MovedBetweenDocuments(oldDocument, newDocument);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        if (context == GH_DocumentContext.Close)
        {
            document.SolutionEnd -= OnSolutionEnd;
            ResetSession();
        }
        base.DocumentContextChanged(document, context);
    }
}

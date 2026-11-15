using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using EtoApplication = Eto.Forms.Application;

namespace Torquetum.Alidade;

public abstract class AlidadeAsyncComponent : GH_Component
{
    private CancellationTokenSource? _operation;

    protected AlidadeAsyncComponent(string name, string nickname, string description, string category)
        : base(name, nickname, description, "Alidade", category) { }

    protected bool IsPending => _operation != null;
    protected bool CanUse(GH_Document? document) => document?.Enabled == true && !Locked;

    protected ProjectSession? ConnectedProject(IGH_DataAccess data, GH_Document? document)
    {
        AlidadeProject? value = null;
        return document != null && Params.Input[0].VolatileDataCount == 1 && data.GetData(0, ref value)
            && value?.Value is { } session && session.IsValidFor(document.RuntimeID) ? session : null;
    }

    internal void StartRequest<T>(GH_Document document, ProjectSession? project, Func<CancellationToken, Task<T>> request,
        Action<T> accept, Action<AlidadeConnectionException> fail, Action<T>? abandon = null)
    {
        CancelRequest();
        var operation = new CancellationTokenSource();
        _operation = operation;
        RequestSolution(document);
        _ = CompleteAsync(document, project, operation, request, accept, fail, abandon);
    }

    private async Task CompleteAsync<T>(GH_Document document, ProjectSession? project, CancellationTokenSource operation,
        Func<CancellationToken, Task<T>> request, Action<T> accept, Action<AlidadeConnectionException> fail, Action<T>? abandon)
    {
        T result = default!;
        AlidadeConnectionException? error = null;
        try { result = await request(operation.Token).ConfigureAwait(false); }
        catch (AlidadeConnectionException expected) { error = expected; }
        catch (HttpRequestException)
        {
            error = new AlidadeConnectionException("Torquetum is unavailable. Check the running project and try again.");
        }
        catch (OperationCanceledException)
        {
            error = new AlidadeConnectionException("Torquetum did not answer in time. Check the request before trying again.");
        }
        catch (CollectionFormatException invalid)
        {
            var diagnostic = invalid.Diagnostic;
            var branch = diagnostic.Path is { Length: > 0 } path ? $"Branch {{{string.Join(";", path)}}}: " : "";
            error = new AlidadeConnectionException(branch + diagnostic);
        }
        catch (JsonException)
        {
            error = new AlidadeConnectionException("Torquetum returned invalid exchange data.");
        }

        EtoApplication.Instance.AsyncInvoke(() =>
        {
            operation.Dispose();
            if (!ReferenceEquals(_operation, operation) || !ReferenceEquals(OnPingDocument(), document))
            {
                if (error == null) abandon?.Invoke(result);
                return;
            }

            _operation = null;
            if (error == null && project != null && !project.IsValidFor(document.RuntimeID))
                error = new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
            if (error == null)
                accept(result);
            else
            {
                if (error.PairingRequired)
                    project?.Disconnect();
                fail(error);
            }
            RequestSolution(document);
        });
    }

    protected void CancelRequest()
    {
        var operation = _operation;
        _operation = null;
        operation?.Cancel();
    }

    protected void RequestSolution(GH_Document document)
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

    protected abstract void ResetState();

    private void InvalidateDocument(GH_Document? document)
    {
        if (document != null)
            document.SolutionEnd -= OnSolutionEnd;
        CancelRequest();
        ResetState();
    }

    public override bool Read(GH_IReader reader)
    {
        InvalidateDocument(OnPingDocument());
        return base.Read(reader);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        InvalidateDocument(document);
        base.RemovedFromDocument(document);
    }

    public override void MovedBetweenDocuments(GH_Document oldDocument, GH_Document newDocument)
    {
        InvalidateDocument(oldDocument);
        base.MovedBetweenDocuments(oldDocument, newDocument);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        if (context == GH_DocumentContext.Close)
            InvalidateDocument(document);
        base.DocumentContextChanged(document, context);
    }
}

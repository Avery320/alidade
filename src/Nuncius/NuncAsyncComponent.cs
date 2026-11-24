using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;
using EtoApplication = Eto.Forms.Application;

namespace Torquetum.Nuncius;

public abstract class NuncAsyncComponent : GH_Component
{
    private CancellationTokenSource? _operation;
    private bool _triggerHigh = true;

    protected NuncAsyncComponent(string name, string nickname, string description, string category)
        : base(name, nickname, description, NuncAssemblyInfo.PluginName, category) { }

    protected bool IsPending => _operation != null;
    private protected NuncConnectionException? Failure { get; private set; }
    protected bool CanUse([NotNullWhen(true)] GH_Document? document) => document?.Enabled == true && !Locked;

    protected bool CanUseProject([NotNullWhen(true)] GH_Document? document,
        [NotNullWhen(true)] ProjectSession? project) => CanUse(document) && project?.IsValidFor(document.RuntimeID) == true;

    protected T? SingleInput<T>(IGH_DataAccess data, int index) where T : class
    {
        T? value = null;
        return Params.Input[index].VolatileDataCount == 1 && data.GetData(index, ref value) ? value : null;
    }

    protected ProjectSession? ConnectedProject(IGH_DataAccess data, GH_Document? document) =>
        SingleInput<NuncProject>(data, 0)?.Value is { } session && CanUseProject(document, session) ? session : null;

    protected string? ExchangeId(IGH_DataAccess data, int index) =>
        SingleInput<string>(data, index)?.Trim() is { Length: > 0 } id ? id : null;

    private protected bool ButtonPressed(IGH_DataAccess data, int index)
    {
        var trigger = Params.Input[index].SourceCount > 0 ? SingleInput<GH_Boolean>(data, index) : null;
        var pressed = trigger?.Value == true && !_triggerHigh;
        _triggerHigh = trigger?.Value != false;
        return pressed;
    }

    protected void ReportStatus(IGH_DataAccess data, int index, string status, string? warning)
    {
        Message = warning == status ? "" : status;
        data.SetData(index, status);
        if (warning != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
    }

    internal void StartRequest<T>(GH_Document document, ProjectSession? project, Func<CancellationToken, Task<T>> request,
        Action<T> accept, Action<T>? abandon = null)
    {
        CancelRequest();
        var operation = new CancellationTokenSource();
        _operation = operation;
        if (document.SolutionDepth == 0) RequestSolution(document);
        _ = CompleteAsync(document, project, operation, request, accept, abandon);
    }

    private async Task CompleteAsync<T>(GH_Document document, ProjectSession? project, CancellationTokenSource operation,
        Func<CancellationToken, Task<T>> request, Action<T> accept, Action<T>? abandon)
    {
        T result = default!;
        NuncConnectionException? error = null;
        try { result = await request(operation.Token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is NuncConnectionException or HttpRequestException
            or OperationCanceledException or CollectionFormatException or JsonException)
        {
            error = failure as NuncConnectionException ?? new NuncConnectionException(failure switch
            {
                HttpRequestException => "Torquetum is unavailable. Check the running project and try again.",
                OperationCanceledException => "Torquetum did not answer in time.",
                CollectionFormatException invalid => invalid.Message,
                _ => "Torquetum returned invalid exchange data.",
            });
        }

        EtoApplication.Instance.AsyncInvoke(() =>
        {
            var current = ReferenceEquals(_operation, operation);
            if (current) _operation = null;
            operation.Dispose();
            if (!current || !ReferenceEquals(OnPingDocument(), document))
            {
                if (error == null) abandon?.Invoke(result);
                return;
            }

            if (error == null && project != null && !project.IsValidFor(document.RuntimeID))
                error = new NuncConnectionException("Pair with the project again.", pairingRequired: true);
            if (error == null)
                accept(result);
            else
            {
                if (error.PairingRequired)
                    project?.Disconnect();
                Failure = error;
            }
            RequestSolution(document);
        });
    }

    protected void CancelRequest()
    {
        var operation = _operation;
        _operation = null;
        Failure = null;
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
        _triggerHigh = true;
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

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed class ReadGeometry : AlidadeAsyncComponent
{
    private sealed record Input(ProjectSession Project, GeometryPortReference Target, GeometryReceipt? Receipt);

    private Input? _input;
    private GeometryCollection? _collection;
    private string? _error;

    public ReadGeometry()
        : base("Read Geometry", "Read Geometry", "Read a Geometry Collection from an Asterism output.", "Exchange") { }

    public override Guid ComponentGuid => new("aecf9292-0da8-4783-9ca6-2aeedf326acc");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("ReadGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddParameter(new AlidadeWriteReceiptParameter());
        manager[0].Optional = manager[1].Optional = manager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Latest read status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        var (next, unavailable) = Capture(data, project);
        if (_input != next)
        {
            CancelRequest();
            _input = next;
            _collection = null;
            _error = null;
            Refresh();
        }

        if (_collection is { } current && _input is { } input)
        {
            data.SetData(0, new AlidadeCollection(current));
            data.SetData(1, new AlidadePortReference(input.Target));
        }
        var status = _error ?? (IsPending ? "Waiting for Asterism output…" : unavailable);
        if (_collection != null && _input is { } completed)
            status = completed.Receipt?.RequestId is { } id
                ? $"{completed.Target.DisplayName} · write {id.ToString("D")[^8..]}"
                : completed.Target.DisplayName;
        ReportStatus(data, 2, status, _error);
    }

    private (Input?, string) Capture(IGH_DataAccess data, ProjectSession? project)
    {
        if (project == null)
            return (null, "Connect to Torquetum first.");
        if (ConnectedPort(data, 1, project, "output") is not { } target)
            return (null, "Select an Asterism output in the connected project.");
        if (Params.Input.Count <= 2 || Params.Input[2].SourceCount == 0)
            return (new Input(project, target, null), target.DisplayName);
        if (SingleInput<AlidadeWriteReceipt>(data, 2)?.Value is not { } receipt)
            return (null, "Waiting for a Send receipt.");
        if (!receipt.Matches(project))
            return (null, "This receipt belongs to another project connection.");
        return receipt.State switch
        {
            "committed" => (new Input(project, target, receipt), target.DisplayName),
            "pending" => (null, "Waiting for the Input write to finish."),
            _ => (null, receipt.Message),
        };
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        Menu_AppendItem(menu, "Refresh read", (_, _) => Refresh(), !IsPending && CanUseProject(document, _input?.Project));
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        if (IsPending || _input is not { } input || !CanUseProject(document, input.Project))
            return;

        _collection = null;
        _error = null;
        StartRequest(document, input.Project,
            cancellation => ReadOutputAsync(input, document.RuntimeID, cancellation),
            result => _collection = result, error => _error = error.Message);
    }

    private static async Task<GeometryCollection> ReadOutputAsync(Input input, ulong documentId, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await AlidadeClient.ReadGeometryAsync(input.Project, documentId, input.Target, cancellation, input.Receipt)
                    .ConfigureAwait(false);
            }
            catch (AlidadeConnectionException error) when (input.Receipt != null && error.Code == "OUTPUT_NOT_READY"
                && Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
            {
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
        }
    }

    protected override void ResetState()
    {
        _input = null;
        _collection = null;
        _error = null;
    }

    public override bool Read(GH_IReader reader)
    {
        ComponentArchive.RestoreAppendedParameter(reader, Params.Input[2], 2, input: true);
        return base.Read(reader);
    }
}

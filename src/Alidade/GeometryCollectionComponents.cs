using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

internal static class CollectionOutputs
{
    internal static void WriteDiagnostics(GH_Component component, IGH_DataAccess data, int index,
        List<CollectionDiagnostic> diagnostics)
    {
        var result = new GH_Structure<GH_String>();
        foreach (var diagnostic in diagnostics)
            result.Append(new GH_String(diagnostic.ToString()), new GH_Path(diagnostic.Path ?? new[] { 0 }));
        data.BlitData(index, result, overwrite: true);

        if (diagnostics.Count != 0)
        {
            var first = diagnostics.FirstOrDefault(item => !item.Warning) ?? diagnostics[0];
            component.AddRuntimeMessage(first.Warning ? GH_RuntimeMessageLevel.Warning : GH_RuntimeMessageLevel.Error,
                first + (diagnostics.Count > 1 ? " See Diagnostics for all affected items." : ""));
        }
    }
}

public sealed class BuildGeometryCollection : GH_Component
{
    public BuildGeometryCollection()
        : base("Build Geometry Collection", "Build Collection", "Convert Rhino geometry and its tree to an Alidade collection.",
            "Alidade", "Convert") { }

    public override Guid ComponentGuid => new("61799db7-0931-4460-b71c-a22097849a68");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("BuildCollection");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddGenericParameter("Geometry", "G", "Rhino geometry data tree.", GH_ParamAccess.tree);
        manager.AddTextParameter("Item Keys", "K", "Optional keys aligned with the paths and slots of Geometry.", GH_ParamAccess.tree);
        manager.AddTextParameter("Source units", "U", "Rhino unit name or mm, cm, m, in or ft. Uses the Rhino document when omitted.",
            GH_ParamAccess.item);
        for (var i = 0; i < 3; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("Diagnostics", "D", "Conversion messages at the original paths and item indices.", GH_ParamAccess.tree);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        Message = "";
        var diagnostics = new List<CollectionDiagnostic>();
        try
        {
            data.GetDataTree<IGH_Goo>(0, out var geometry);
            if (Params.Input[0].SourceCount == 0 && (geometry == null || geometry.PathCount == 0))
                throw CollectionFormatException.At("GEOMETRY_MISSING", "Connect a Rhino geometry tree.");
            data.GetDataTree<GH_String>(1, out var keys);
            if (keys == null || keys.PathCount == 0)
                keys = Params.Input[1].SourceCount == 0 ? null : new GH_Structure<GH_String>();
            string? units = null;
            data.GetData(2, ref units);
            var document = OnPingDocument()?.RhinoDocument;
            var scale = GeometryCollectionConverter.MetresPerUnit(units, document);
            var collection = GeometryCollectionConverter.Build(geometry ?? new GH_Structure<IGH_Goo>(), keys, scale, diagnostics, document);
            if (collection != null)
            {
                Message = collection.ToString();
                data.SetData(0, new AlidadeCollection(collection));
            }
        }
        catch (CollectionFormatException error)
        {
            diagnostics.Add(error.Diagnostic);
        }
        data.DisableGapLogic();
        CollectionOutputs.WriteDiagnostics(this, data, 1, diagnostics);
    }
}

public sealed class UnpackGeometryCollection : GH_Component
{
    public UnpackGeometryCollection()
        : base("Unpack Geometry Collection", "Unpack Collection", "Convert an Alidade collection back to Rhino geometry and its tree.",
            "Alidade", "Convert") { }

    public override Guid ComponentGuid => new("41b84810-df87-4080-b29b-b731a9d53196");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("UnpackCollection");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("Target units", "U", "Rhino unit name or mm, cm, m, in or ft. Uses the Rhino document when omitted.",
            GH_ParamAccess.item);
        manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddGenericParameter("Geometry", "G", "Rhino geometry with the original branch paths and slots.", GH_ParamAccess.tree);
        manager.AddTextParameter("Item Keys", "K", "Keys aligned with the Geometry tree.", GH_ParamAccess.tree);
        manager.AddTextParameter("Diagnostics", "D", "Conversion messages at the original paths and item indices.", GH_ParamAccess.tree);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        AlidadeCollection? input = null;
        if (!data.GetData(0, ref input) || input?.Value is not { } collection)
            return;

        var diagnostics = new List<CollectionDiagnostic>();
        data.DisableGapLogic();
        try
        {
            string? units = null;
            data.GetData(1, ref units);
            var document = OnPingDocument()?.RhinoDocument;
            var scale = GeometryCollectionConverter.MetresPerUnit(units, document);
            var unpacked = GeometryCollectionConverter.Unpack(collection, 1 / scale, diagnostics, document);
            if (unpacked is { } trees)
            {
                // The parameter types match these structures. Copy them without Grasshopper re-matching empty slots.
                data.BlitData(0, trees.Geometry, overwrite: true);
                data.BlitData(1, trees.Keys, overwrite: true);
            }
        }
        catch (CollectionFormatException error)
        {
            diagnostics.Add(error.Diagnostic);
        }
        CollectionOutputs.WriteDiagnostics(this, data, 2, diagnostics);
    }
}

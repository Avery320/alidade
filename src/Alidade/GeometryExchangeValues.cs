using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed record GeometryReference(string ProjectId, Guid InstanceId, string EntityId, string ETag, string Name, bool CanReplace)
{
    internal bool Matches(ProjectSession session) =>
        InstanceId != Guid.Empty && session.Status.InstanceId == InstanceId && session.Status.ProjectId == ProjectId;
}

public sealed record GeometryReceipt(Guid RequestId, string ProjectId, Guid InstanceId, string State,
    string? EntityId, string? ETag, string Message, DateTimeOffset? RetryAfter)
{
    internal static GeometryReceipt Pending(Guid requestId, ProjectStatus project, DateTimeOffset? retryAfter = null) =>
        new(requestId, project.ProjectId, project.InstanceId, "pending", null, null,
            "The result is pending. Use Check result.", retryAfter);

    public override string ToString() =>
        $"{RequestId:D} · {State} · Project {InstanceId:D}" + (EntityId == null ? "" : $" · {EntityId} · {ETag}");
}

public sealed class AlidadeGeometryRef : GH_Goo<GeometryReference?>
{
    public AlidadeGeometryRef() { }
    internal AlidadeGeometryRef(GeometryReference? target) : base(target) { }

    public override bool IsValid => Value is { InstanceId: var id } && id != Guid.Empty;
    public override string TypeName => "Alidade Geometry Reference";
    public override string TypeDescription => "A Station geometry at an observed revision in a live Torquetum project.";
    public override IGH_Goo Duplicate() => new AlidadeGeometryRef(Value);
    public override string ToString() => Value?.Name ?? "No geometry selected";

    public override bool Write(GH_IWriter writer) => true;
    public override bool Read(GH_IReader reader)
    {
        Value = null;
        return true;
    }
}

public sealed class AlidadeGeometryRefParameter : GH_Param<AlidadeGeometryRef>
{
    public AlidadeGeometryRefParameter()
        : base("Geometry Ref", "G", "A geometry reference in the connected Project.", "Alidade", "Exchange", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("c9074cbf-d3cd-4125-a9b8-2fdcc1095be9");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}

public sealed class AlidadeWriteReceipt : GH_Goo<GeometryReceipt?>
{
    public AlidadeWriteReceipt() { }
    internal AlidadeWriteReceipt(GeometryReceipt? receipt) : base(receipt) { }

    public override bool IsValid => Value != null;
    public override string TypeName => "Alidade Write Receipt";
    public override string TypeDescription => "The outcome of an Alidade Create or Replace request.";
    public override IGH_Goo Duplicate() => new AlidadeWriteReceipt(Value);
    public override string ToString() => Value?.ToString() ?? "No receipt";

    public override bool Write(GH_IWriter writer) => true;
    public override bool Read(GH_IReader reader)
    {
        Value = null;
        return true;
    }
}

public sealed class AlidadeWriteReceiptParameter : GH_Param<AlidadeWriteReceipt>
{
    public AlidadeWriteReceiptParameter()
        : base("Receipt", "R", "Status of the last Alidade geometry write.", "Alidade", "Exchange", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("30636267-9981-4b13-99d5-2966428cc4d1");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}

using System;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed record GeometryPortReference(string ProjectId, Guid InstanceId, string PortId, string Name, string Direction, string? ETag)
{
    internal bool Matches(ProjectSession session) =>
        InstanceId != Guid.Empty && session.Status.InstanceId == InstanceId && session.Status.ProjectId == ProjectId;
}

public sealed record GeometryReceipt(Guid RequestId, string ProjectId, Guid InstanceId, string State,
    string? PortId, string? ETag, string Message, DateTimeOffset? RetryAfter)
{
    internal static GeometryReceipt Pending(Guid requestId, ProjectStatus project, DateTimeOffset? retryAfter = null) =>
        new(requestId, project.ProjectId, project.InstanceId, "pending", null, null,
            "The result is pending. Use Check result.", retryAfter);

    public override string ToString() =>
        $"{RequestId:D} · {State} · Project {InstanceId:D}" + (PortId == null ? "" : $" · {PortId} · {ETag}");
}

public sealed class AlidadePortReference : GH_Goo<GeometryPortReference?>
{
    public AlidadePortReference() { }
    internal AlidadePortReference(GeometryPortReference? target) : base(target) { }

    public override bool IsValid => Value is { InstanceId: var id } && id != Guid.Empty;
    public override string TypeName => "Alidade Port Reference";
    public override string TypeDescription => "An Asterism input or output in a live Torquetum project.";
    public override IGH_Goo Duplicate() => new AlidadePortReference(Value);
    public override string ToString() => Value?.Name ?? "No port selected";

    public override bool Write(GH_IWriter writer) => true;
    public override bool Read(GH_IReader reader)
    {
        Value = null;
        return true;
    }
}

public sealed class AlidadePortReferenceParameter : GH_Param<AlidadePortReference>
{
    public AlidadePortReferenceParameter()
        : base("Port Ref", "Port", "An Asterism exchange port in the connected Project.", "Alidade", "Exchange", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("c9074cbf-d3cd-4125-a9b8-2fdcc1095be9");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}

public sealed class AlidadeWriteReceipt : GH_Goo<GeometryReceipt?>
{
    public AlidadeWriteReceipt() { }
    internal AlidadeWriteReceipt(GeometryReceipt? receipt) : base(receipt) { }

    public override bool IsValid => Value != null;
    public override string TypeName => "Alidade Write Receipt";
    public override string TypeDescription => "The outcome of a send to an Asterism input.";
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

using System;
using System.Text.Json;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace Torquetum.Nuncius;

public sealed class NuncCollection : GH_Goo<GeometryCollection?>
{
    public NuncCollection() { }
    internal NuncCollection(GeometryCollection? collection) : base(collection) { }

    public override bool IsValid => Value != null;
    public override string TypeName => "Nuncius Geometry Collection";
    public override string TypeDescription => "A geometry data tree in Torquetum Project coordinates.";
    public override IGH_Goo Duplicate() => new NuncCollection(Value);
    public override string ToString() => Value?.ToString() ?? "Empty Nuncius collection";

    public override bool Write(GH_IWriter writer)
    {
        if (Value is { } collection)
            writer.SetString("Collection", collection.Payload.GetRawText());
        return true;
    }

    public override bool Read(GH_IReader reader)
    {
        Value = null;
        if (!reader.ItemExists("Collection"))
            return true;
        try
        {
            Value = GeometryCollection.FromOwnedPayload(JsonSerializer.Deserialize<JsonElement>(reader.GetString("Collection")));
            return true;
        }
        catch (Exception error) when (error is JsonException or CollectionFormatException)
        {
            return false;
        }
    }
}

public sealed class NuncCollectionParameter : GH_Param<NuncCollection>
{
    public NuncCollectionParameter()
        : base("Collection", "Collection", "A Geometry Collection for Torquetum exchange.", NuncAssemblyInfo.PluginName, "Convert", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("902dcc31-d44a-4054-b739-35fa6fba2a4b");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--exchange"))
    throw new ArgumentException("Usage: Alidade.Checks <Alidade.gha> <geometry-collection.json> [--exchange]");

var plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var geometryCollection = plugin.GetType("Torquetum.Alidade.GeometryCollection", throwOnError: true)!;
var convert = plugin.GetType("Torquetum.Alidade.GeometryCollectionConverter", throwOnError: true)!;
var diagnosis = plugin.GetType("Torquetum.Alidade.CollectionDiagnostic", throwOnError: true)!;
const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;
var parse = geometryCollection.GetMethod("Parse")!;
var build = convert.GetMethod("Build", InternalStatic)!;
var unpack = convert.GetMethod("Unpack", InternalStatic)!;
var diagnostics = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(diagnosis))!;

var fixture = File.ReadAllText(args[1]);
var collection = Parse(fixture);
var payload = Payload(collection).GetProperty("branches");
var shapes = payload.EnumerateArray().SelectMany(branch => branch.GetProperty("items").EnumerateArray())
    .Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.GetProperty("kind").GetString());
Check(shapes.SequenceEqual(new[] { "point", "line", "polyline", "plane", "box", "triangle-mesh" }), "all v1 kinds");
Check(payload[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null, "null slot in wire asset");
Check(payload[1].GetProperty("items").GetArrayLength() == 0, "empty wire branch");

var unordered = Parse("""{"schemaVersion":1,"units":"m","space":"project","branches":[{"path":[2,10],"items":[null]},{"path":[2,9],"items":[]},{"path":[2],"items":[]}]}""");
var sortedPaths = Payload(unordered).GetProperty("branches").EnumerateArray().Select(branch => branch.GetProperty("path").GetRawText());
Check(sortedPaths.SequenceEqual(new[] { "[2]", "[2,9]", "[2,10]" }), "numeric branch ordering");
Reject(fixture.Replace("[0, 4]", "[0]"), "Duplicate branch path");
Reject(fixture.Replace("[1, 2, 3]", "[1e400, 2, 3]"), "finite number");
Reject(fixture.Replace("\"kind\": \"line\"", "\"kind\": \"line\", \"itemKey\": \"sample-point\""), "unique within a collection");
Reject(fixture.Replace("[0, 5]", "[5, 0]"), "strictly increasing");
Reject(fixture.Replace("\"triangles\": [[0, 1, 2]]", "\"triangles\": [[0, 1, 3]]"), "MESH_INDEX_OUT_OF_RANGE");
Reject(fixture.Replace("\"closed\": true", "\"closed\": 1"), "Boolean closure");

var managed = JsonNode.Parse(fixture)!;
managed["branches"]![2]!["items"] = new JsonArray();
managed["branches"]![0]!["items"]![2]!.AsObject().Remove("domain");
var managedAsset = Parse(managed.ToJsonString());
var trees = ((GH_Structure<IGH_Goo>, GH_Structure<GH_String>))unpack.Invoke(null,
    new object[] { managedAsset, 100d, diagnostics })!;
var (geometry, keys) = trees;
Check(diagnostics.Count == 0, "unpack diagnostics");
Check(geometry.PathCount == 3 && geometry.Branches[0][1] == null && geometry.Branches[1].Count == 0, "native GH null and empty branches");
Check(geometry.Paths[2].Indices.SequenceEqual(new[] { 3, 0, 7 }), "native GH deep path");
Check(geometry.Branches[0][0] is GH_Point point && point.Value == new Point3d(100, 200, 300), "point in target centimetres");
Check(geometry.Branches[0][2] is GH_Line line && line.Value.To == new Point3d(100, 0, 0), "line direction and target centimetres");
Check(keys.Branches[0][0].Value == "sample-point" && keys.Branches[0][1] == null && keys.Branches[1].Count == 0, "native GH key alignment");

var roundTrip = build.Invoke(null, new object[] { geometry, keys, 0.01d, diagnostics });
Check(roundTrip != null && diagnostics.Count == 0, "build diagnostics");
Check(Equal(Payload(managedAsset), Payload(roundTrip!)),
    "native GH centimetre round trip");

keys.Append(new GH_String("unexpected"), new GH_Path(0, 4));
Check(build.Invoke(null, new object[] { geometry, keys, 0.01d, diagnostics }) == null
    && diagnostics.Cast<object>().Any(item => item.ToString()!.Contains("ITEM_KEYS_STRUCTURE", StringComparison.Ordinal)),
    "key structure mismatch rejects the whole collection");
Console.WriteLine("PASS six v1 kinds, malformed wire, and native GH tree geometry/key alignment.");
if (args.Length == 3)
    await GeometryExchangeCheck.Run(plugin, collection, Console.ReadLine() ?? "");


object Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return parse.Invoke(null, new object[] { document.RootElement })!;
}

JsonElement Payload(object value) => (JsonElement)geometryCollection.GetProperty("Payload")!.GetValue(value)!;

void Reject(string json, string expected)
{
    try
    {
        Parse(json);
        throw new InvalidOperationException("Unexpectedly accepted: " + expected);
    }
    catch (TargetInvocationException error) when (error.InnerException is FormatException format
        && format.Message.Contains(expected, StringComparison.Ordinal))
    {
    }
}

static void Check(bool condition, string label)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + label);
}

static bool Equal(JsonElement left, JsonElement right)
{
    if (left.ValueKind != right.ValueKind)
        return false;
    return left.ValueKind switch
    {
        JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
            && left.EnumerateObject().All(property =>
                right.TryGetProperty(property.Name, out var value) && Equal(property.Value, value)),
        JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
            && left.EnumerateArray().Zip(right.EnumerateArray(), Equal).All(equal => equal),
        JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
        _ => left.GetRawText() == right.GetRawText(),
    };
}

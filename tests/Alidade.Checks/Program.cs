using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Torquetum.Alidade;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--exchange"))
    throw new ArgumentException("Usage: Alidade.Checks <Alidade.gha> <geometry-collection-basic.json> [--exchange]");

AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
await AlidadeChecks.Run(args[1], args.Length == 3);

internal static class AlidadeChecks
{
    internal static async Task Run(string fixturePath, bool exchange)
    {
        var basic = File.ReadAllText(fixturePath);
        var cadPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!, "geometry-collection.json");
        var cad = File.ReadAllText(cadPath);
        CheckGeometry(basic, cad);
        if (exchange)
            await CheckExchange(Parse(basic), JsonNode.Parse(cad)!, Console.ReadLine() ?? "");
    }

    static GeometryCollection Parse(string json) =>
        GeometryCollection.FromOwnedPayload(JsonSerializer.Deserialize<JsonElement>(json));

    static void CheckGeometry(string basic, string cad)
    {
        var collection = Parse(basic);
        Check(collection.BranchCount == 3 && collection.SlotCount == 7 && collection.GeometryCount == 6,
            "shared mixed-geometry fixture");
        var tree = JsonNode.Parse(basic)!;
        tree["schemaVersion"] = 2;
        Reject(tree, "schemaVersion");
        tree["schemaVersion"] = "0.0";

        var branch = tree["branches"]![2]!["items"]!;
        var frame = branch[2]!["plane"]!.AsObject();
        frame["extra"] = true;
        Reject(tree, "extra");
        frame.Remove("extra");
        branch[1]!.AsObject().Remove("normal");
        Reject(tree, "normal");

        tree["branches"]![2]!["items"] = new JsonArray();
        tree["branches"]![0]!["items"]![2]!.AsObject().Remove("domain");
        var diagnostics = new List<CollectionDiagnostic>();
        var geometry = GeometryCollectionConverter.Unpack(Parse(tree.ToJsonString()), 100d, diagnostics, null)!;
        Check(geometry.Paths[2].Indices.SequenceEqual(new[] { 3, 0, 7 })
            && geometry.Branches[0][1] == null && geometry.Branches[1].Count == 0, "Grasshopper tree structure");
        Check(geometry.Branches[0][0] is GH_Point point && point.Value == new Point3d(100, 200, 300), "metres to centimetres");
        var roundTrip = GeometryCollectionConverter.Build(geometry, 0.01d, diagnostics, null);
        Check(roundTrip != null && diagnostics.Count == 0
            && roundTrip.Payload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 1,
            "centimetres to metres");

        var cadTree = JsonNode.Parse(cad)!;
        var stepItem = cadTree["branches"]![0]!["items"]![0]!;
        stepItem["shapeType"] = "mesh";
        Reject(cadTree, "shapeType");
        var step = Parse(cad).Payload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("step").GetString()!;
        var sanitized = StepText.SanitizeExport(step.Replace("Open CASCADE Shape Model", "/private/source.stp"));
        Check(!sanitized.Contains("/private/source.stp", StringComparison.Ordinal)
            && sanitized[sanitized.IndexOf("DATA;", StringComparison.Ordinal)..] ==
               step[step.IndexOf("DATA;", StringComparison.Ordinal)..], "STEP export keeps geometry and strips private metadata");
        Console.WriteLine("PASS V0.0 collection, Rhino unit conversion, Grasshopper tree structure and STEP sanitization.");

        static void Reject(JsonNode tree, string field)
        {
            try { Parse(tree.ToJsonString()); }
            catch (CollectionFormatException error) when (error.Diagnostic.Field == field) { return; }
            throw new InvalidOperationException($"The collection with invalid {field} was accepted.");
        }
    }

    static async Task CheckExchange(GeometryCollection basic, JsonNode cad, string invite)
    {
        const ulong documentId = 41;
        const string id = "fixture";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var session = await AlidadeClient.PairAsync(invite, documentId, token);
        Check(!session.IsValidFor(documentId + 1), "pairing is scoped to its Grasshopper document");
        var status = await AlidadeClient.ReadProjectAsync(session, documentId, token);
        Check(status.CanWrite, "the paired Project accepts writes");

        await Send(basic);
        var initial = (await AlidadeClient.ReadGeometryAsync(session, documentId, id, token)).Payload.GetProperty("branches");
        Check(initial[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null
            && initial[1].GetProperty("items").GetArrayLength() == 0, "HTTP read preserves null and empty branches");

        cad["branches"]![2]!["items"]![0]!["position"]![0] = 1.493;
        await Send(Parse(cad.ToJsonString()));
        var final = (await AlidadeClient.ReadGeometryAsync(session, documentId, id, token)).Payload.GetProperty("branches");
        Check(final[2].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 1.493
            && final[0].GetProperty("items")[0].GetProperty("shapeType").GetString() == "solid"
            && final[0].GetProperty("items")[2].GetProperty("shapeType").GetString() == "face"
            && final[2].GetProperty("items")[1].GetProperty("shapeType").GetString() == "shell",
            "same ID reads the updated point and exact STEP shapes");
        Console.WriteLine("PASS live C# pairing, send, receipt and 1493 mm mixed geometry readback.");

        async Task Send(GeometryCollection value)
        {
            var write = await AlidadeClient.PrepareWriteAsync(session, documentId, id, value, token);
            var receipt = await AlidadeClient.WriteGeometryAsync(session, documentId, write, token);
            while (receipt.Pending)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                receipt = await AlidadeClient.CheckReceiptAsync(session, documentId, write, token);
            }
            Check(receipt.Error == null, $"write receipt is committed ({receipt.Error})");
        }
    }

    static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Failed: {label}.");
    }
}

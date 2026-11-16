using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

internal static class GeometryExchangeCheck
{
    internal static async Task Run(Assembly plugin, object collection, object cadCollection, string invite)
    {
        const ulong documentId = 41;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var client = plugin.GetType("Torquetum.Alidade.AlidadeClient", true)!;

        var session = await Invoke("PairAsync", invite, documentId, token);
        try
        {
            await Invoke("ListPortsAsync", session, documentId + 1, token);
            throw new InvalidOperationException("Project session accepted another Grasshopper document.");
        }
        catch (Exception error) when (MemberOrDefault(error, "PairingRequired") is true)
        {
        }

        var status = Member(session, "Status");
        var ports = ((IEnumerable)await Invoke("ListPortsAsync", session, documentId, token)).Cast<object>().ToArray();
        var input = ports.Single(port => Member(port, "Direction").Equals("input"));
        var output = ports.Single(port => Member(port, "Direction").Equals("output"));
        var id = Member(input, "Id");
        var tag = Member(input, "ETag");
        Check(MemberOrDefault(output, "ETag") == null, "only inputs have an ETag");
        var inputRef = Reference(input);
        var outputRef = Reference(output);

        try
        {
            await Invoke("ReadGeometryAsync", session, documentId, outputRef, token);
            throw new InvalidOperationException("A fresh graph reported a completed output before its first input.");
        }
        catch (Exception error) when (MemberOrDefault(error, "Code") as string == "OUTPUT_NOT_READY")
        {
        }

        var send = Call("PrepareWrite", collection, inputRef);
        var committed = await Committed(send);
        Check(Member(committed, "PortId").Equals(id) && !Member(committed, "ETag").Equals(tag),
            "send writes to the selected input and advances its revision");
        var replay = await Committed(send);
        Check(Member(replay, "RequestId").Equals(Member(committed, "RequestId"))
            && Member(replay, "PortId").Equals(id) && Member(replay, "ETag").Equals(Member(committed, "ETag")), "same write replay");

        var original = await ReadOutput(outputRef);
        Check(original.GetProperty("branches")[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null
            && original.GetProperty("branches")[1].GetProperty("items").GetArrayLength() == 0,
            "read preserves null and empty branch");

        var edited = JsonNode.Parse(original.GetRawText())!;
        edited["branches"]![0]!["items"]![0]!["position"]![0] = 4;
        using var json = JsonDocument.Parse(edited.ToJsonString());
        var parsed = collection.GetType().GetMethod("Parse")!.Invoke(null, new object[] { json.RootElement })!;
        var update = Call("PrepareWrite", parsed, await CurrentInput());
        var updated = await Committed(update);
        Check(Member(updated, "PortId").Equals(id) && !Member(updated, "ETag").Equals(Member(committed, "ETag")),
            "update keeps input identity and advances its revision");

        try
        {
            var stale = Call("PrepareWrite", collection, inputRef);
            await Invoke("WriteGeometryAsync", session, documentId, stale, token);
            throw new InvalidOperationException("The previous revision was accepted.");
        }
        catch (Exception error) when (MemberOrDefault(error, "Code") as string == "STALE_REVISION")
        {
        }

        var finalPayload = await ReadOutput(outputRef);
        Check(finalPayload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 4,
            "the same output port returns the graph result after input changes");
        Console.WriteLine("PASS live C# pair, document scoping, Asterism port list, first send, exact replay, output read, update and stale protection.");

        var cadWrite = Call("PrepareWrite", cadCollection, await CurrentInput());
        if (Member(status, "SupportsStep") is true)
        {
            await Committed(cadWrite);
            var cadPayload = await ReadOutput(outputRef);
            var cadBranches = cadPayload.GetProperty("branches");
            Check(cadPayload.GetProperty("schemaVersion").GetInt32() == 2
                && cadBranches[0].GetProperty("items")[0].GetProperty("shapeType").GetString() == "solid"
                && cadBranches[0].GetProperty("items")[2].GetProperty("shapeType").GetString() == "face"
                && cadBranches[2].GetProperty("items")[1].GetProperty("shapeType").GetString() == "shell",
                "read preserves all three exact STEP item types");
            Console.WriteLine("PASS negotiated STEP solid, face and shell through the live Project API.");
        }
        else
        {
            try
            {
                await Invoke("WriteGeometryAsync", session, documentId, cadWrite, token);
                throw new InvalidOperationException("Unnegotiated STEP request was accepted.");
            }
            catch (Exception error) when (error.Message == "The paired project does not offer precise STEP exchange.")
            {
                Console.WriteLine("PASS a v1-only Project rejects STEP before sending.");
            }
        }

        object Reference(object port) => port.GetType().GetMethod("Reference", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(port, new[] { status })!;

        async Task<object> CurrentInput()
        {
            var listing = ((IEnumerable)await Invoke("ListPortsAsync", session, documentId, token)).Cast<object>();
            return Reference(listing.Single(port => Member(port, "Id").Equals(id)));
        }

        async Task<JsonElement> ReadOutput(object reference)
        {
            while (true)
            {
                try
                {
                    var result = await Invoke("ReadGeometryAsync", session, documentId, reference, token);
                    return (JsonElement)Member(result, "Payload");
                }
                catch (Exception error) when (MemberOrDefault(error, "Code") as string == "OUTPUT_NOT_READY")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                }
            }
        }

        object Call(string method, params object?[] args) =>
            client.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, args)!;

        async Task<object> Invoke(string method, params object?[] args)
        {
            var pending = (Task)Call(method, args);
            await pending;
            return Member(pending, "Result");
        }

        async Task<object> Committed(object write)
        {
            var receipt = await Invoke("WriteGeometryAsync", session, documentId, write, token);
            while (Member(receipt, "State") as string == "pending")
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                receipt = await Invoke("CheckReceiptAsync", session, documentId, Member(write, "Id"), id, token);
            }
            Check(Member(receipt, "State") as string == "committed", $"write receipt is committed ({Member(receipt, "Message")})");
            return receipt;
        }
    }

    private static object Member(object source, string property) =>
        MemberOrDefault(source, property) ?? throw new InvalidOperationException("Expected " + property);

    private static object? MemberOrDefault(object source, string property) =>
        source.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + label);
    }
}

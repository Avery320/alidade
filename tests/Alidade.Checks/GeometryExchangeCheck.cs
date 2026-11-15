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
            await Invoke("ListGeometryAsync", session, documentId + 1, token);
            throw new InvalidOperationException("Project session accepted another Grasshopper document.");
        }
        catch (Exception error) when (MemberOrDefault(error, "PairingRequired") is true)
        {
        }

        var create = Call("PrepareWrite", collection, "Alidade managed check " + Guid.NewGuid().ToString("N")[..8], null);
        var committed = await Committed(create);
        var id = (string)Member(committed, "EntityId");
        var tag = (string)Member(committed, "ETag");
        var replay = await Committed(create);
        Check(Member(replay, "RequestId").Equals(Member(committed, "RequestId"))
            && Member(replay, "EntityId").Equals(id) && Member(replay, "ETag").Equals(tag), "same write replay");

        var items = ((IEnumerable)await Invoke("ListGeometryAsync", session, documentId, token)).Cast<object>();
        var item = items.Single(value => Member(value, "EntityId").Equals(id));
        Check(Member(item, "CanRead") is true && Member(item, "CanReplace") is true, "select the committed managed geometry");
        var status = Member(session, "Status");
        var reference = item.GetType().GetMethod("Reference", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(item, new[] { status })!;
        var read = await Invoke("ReadGeometryAsync", session, documentId, reference, token);
        var original = (JsonElement)Member(Member(read, "Collection"), "Payload");
        Check(original.GetProperty("branches")[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null
            && original.GetProperty("branches")[1].GetProperty("items").GetArrayLength() == 0,
            "read preserves null and empty branch");

        var edited = JsonNode.Parse(original.GetRawText())!;
        edited["branches"]![0]!["items"]![0]!["position"]![0] = 4;
        using var json = JsonDocument.Parse(edited.ToJsonString());
        var parsed = collection.GetType().GetMethod("Parse")!.Invoke(null, new object[] { json.RootElement })!;
        var replace = Call("PrepareWrite", parsed, "", Member(read, "Reference"));
        var replaced = await Committed(replace);
        Check(Member(replaced, "EntityId").Equals(id) && !Member(replaced, "ETag").Equals(tag), "replace keeps identity and advances version");

        try
        {
            var stale = Call("PrepareWrite", collection, "", reference);
            await Invoke("WriteGeometryAsync", session, documentId, stale, token);
            throw new InvalidOperationException("The previous revision was accepted.");
        }
        catch (Exception error) when (MemberOrDefault(error, "Code") as string == "STALE_REVISION")
        {
        }

        var final = await Invoke("ReadGeometryAsync", session, documentId, reference, token);
        var finalPayload = (JsonElement)Member(Member(final, "Collection"), "Payload");
        Check(finalPayload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 4,
            "read obtains the replacement even using an older reference");
        Console.WriteLine("PASS live C# pair, project scoping, create, exact replay, select, read, replace and stale protection.");

        var cadWrite = Call("PrepareWrite", cadCollection, "Alidade STEP check " + Guid.NewGuid().ToString("N")[..8], null);
        if (Member(status, "SupportsStep") is true)
        {
            var cadReceipt = await Committed(cadWrite);
            var cadId = Member(cadReceipt, "EntityId");
            var cadItems = ((IEnumerable)await Invoke("ListGeometryAsync", session, documentId, token)).Cast<object>();
            var cadItem = cadItems.Single(value => Member(value, "EntityId").Equals(cadId));
            var cadReference = cadItem.GetType().GetMethod("Reference", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(cadItem, new[] { status })!;
            var cadRead = await Invoke("ReadGeometryAsync", session, documentId, cadReference, token);
            var cadPayload = (JsonElement)Member(Member(cadRead, "Collection"), "Payload");
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
                receipt = await Invoke("CheckReceiptAsync", session, documentId, Member(write, "Id"), token);
            }
            Check(Member(receipt, "State") as string == "committed", "write receipt is committed");
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

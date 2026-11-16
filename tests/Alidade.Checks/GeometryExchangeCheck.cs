using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Torquetum.Alidade;
using static AlidadeChecks;

internal static class GeometryExchangeCheck
{
    internal static async Task Run(GeometryCollection collection, GeometryCollection cadCollection, string invite)
    {
        const ulong documentId = 41;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;

        var session = await AlidadeClient.PairAsync(invite, documentId, token);
        try
        {
            await AlidadeClient.ListPortsAsync(session, documentId + 1, token);
            throw new InvalidOperationException("Project session accepted another Grasshopper document.");
        }
        catch (AlidadeConnectionException error) when (error.PairingRequired)
        {
        }

        var status = session.Status;
        var refreshed = await AlidadeClient.ReadProjectAsync(session, documentId, token);
        Check(refreshed.GeometryNames == status.GeometryNames, "project refresh preserves the negotiated names capability");
        var ports = await AlidadeClient.ListPortsAsync(session, documentId, token);
        var input = ports.Single(port => port.Direction == "input");
        var output = ports.Single(port => port.Direction == "output");
        var id = input.Id;
        Check(input.ETag != null && output.ETag == null, "only inputs have an ETag");
        var inputRef = input.Reference(status);
        var outputRef = output.Reference(status);

        try
        {
            await AlidadeClient.ReadGeometryAsync(session, documentId, outputRef, token);
            throw new InvalidOperationException("A fresh graph reported a completed output before its first input.");
        }
        catch (AlidadeConnectionException error) when (error.Code == "OUTPUT_NOT_READY")
        {
        }

        var named = JsonNode.Parse(collection.Payload.GetRawText())!;
        named["branches"]![0]!["items"]![0]!["name"] = "Fixture point";
        var namedCollection = Parse(named.ToJsonString());
        if (!status.GeometryNames)
        {
            try
            {
                var unsupported = AlidadeClient.PrepareWrite(namedCollection, inputRef);
                await AlidadeClient.WriteGeometryAsync(session, documentId, unsupported, token);
                throw new InvalidOperationException("A named write was sent without negotiated Geometry Names.");
            }
            catch (AlidadeConnectionException error) when (error.Code == "GEOMETRY_NAMES_UNSUPPORTED")
            {
                Console.WriteLine("PASS a host without Geometry Names rejects named writes locally.");
            }
        }

        var send = AlidadeClient.PrepareWrite(status.GeometryNames ? namedCollection : collection, inputRef);
        var committed = await Committed(send);
        Check(committed.PortId == id && committed.ETag != null && committed.ETag != input.ETag,
            "send writes to the selected input and advances its revision");
        var replay = await Committed(send);
        Check(replay.RequestId == committed.RequestId && replay.PortId == id && replay.ETag == committed.ETag, "same write replay");

        var original = await ReadOutput(outputRef, committed);
        Check(original.GetProperty("branches")[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null
            && original.GetProperty("branches")[1].GetProperty("items").GetArrayLength() == 0,
            "read preserves null and empty branch");
        if (status.GeometryNames)
            Check(original.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("name").GetString() == "Fixture point",
                "negotiated Geometry Names survive the Output read");

        try
        {
            await AlidadeClient.ReadGeometryAsync(session, documentId, outputRef, token, committed with { ETag = "\"unverified\"" });
            throw new InvalidOperationException("A successful reply did not verify the receipt's revision.");
        }
        catch (AlidadeConnectionException error) when (error.Code == "OUTPUT_REVISION_UNVERIFIED")
        {
        }

        var edited = JsonNode.Parse(original.GetRawText())!;
        edited["branches"]![0]!["items"]![0]!["position"]![0] = 4;
        var update = AlidadeClient.PrepareWrite(Parse(edited.ToJsonString()), await CurrentInput());
        var updated = await Committed(update);
        Check(updated.PortId == id && updated.ETag != null && updated.ETag != committed.ETag,
            "update keeps input identity and advances its revision");

        try
        {
            await AlidadeClient.ReadGeometryAsync(session, documentId, outputRef, token, committed);
            throw new InvalidOperationException("The previous receipt returned output after its input was replaced.");
        }
        catch (AlidadeConnectionException error) when (error.Code == "INPUT_SUPERSEDED")
        {
        }

        try
        {
            var stale = AlidadeClient.PrepareWrite(collection, inputRef);
            await AlidadeClient.WriteGeometryAsync(session, documentId, stale, token);
            throw new InvalidOperationException("The previous revision was accepted.");
        }
        catch (AlidadeConnectionException error) when (error.Code == "STALE_REVISION")
        {
        }

        var finalPayload = await ReadOutput(outputRef);
        Check(finalPayload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 4,
            "the same output port returns the graph result after input changes");
        Console.WriteLine("PASS live C# pair, document scoping, port list, exact replay, output reads, superseded receipts and stale protection.");

        var cadWrite = AlidadeClient.PrepareWrite(cadCollection, await CurrentInput());
        if (status.SupportsStep)
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
                await AlidadeClient.WriteGeometryAsync(session, documentId, cadWrite, token);
                throw new InvalidOperationException("Unnegotiated STEP request was accepted.");
            }
            catch (AlidadeConnectionException error) when (error.Message == "The paired project does not offer precise STEP exchange.")
            {
                Console.WriteLine("PASS a v1-only Project rejects STEP before sending.");
            }
        }

        async Task<GeometryPortReference> CurrentInput()
        {
            var listing = await AlidadeClient.ListPortsAsync(session, documentId, token);
            return listing.Single(port => port.Id == id).Reference(status);
        }

        async Task<JsonElement> ReadOutput(GeometryPortReference reference, GeometryReceipt? receipt = null)
        {
            while (true)
            {
                try
                {
                    return (await AlidadeClient.ReadGeometryAsync(session, documentId, reference, token, receipt)).Payload;
                }
                catch (AlidadeConnectionException error) when (error.Code == "OUTPUT_NOT_READY")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                }
            }
        }

        async Task<GeometryReceipt> Committed(GeometryWriteRequest write)
        {
            var receipt = await AlidadeClient.WriteGeometryAsync(session, documentId, write, token);
            while (receipt.State == "pending")
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                receipt = await AlidadeClient.CheckReceiptAsync(session, documentId, write.Id, id, token);
            }
            Check(receipt.State == "committed", $"write receipt is committed ({receipt.Message})");
            return receipt;
        }
    }
}

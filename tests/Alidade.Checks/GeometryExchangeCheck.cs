using System;
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
        const string id = "fixture";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;

        var session = await AlidadeClient.PairAsync(invite, documentId, token);
        await ExpectError("PAIRING_REQUIRED", () => AlidadeClient.ReadProjectAsync(session, documentId + 1, token));
        await ExpectError("OUTPUT_UNAVAILABLE", () => ReadOutput());

        var send = await Prepare(collection);
        await Commit(send);
        await Commit(send);
        var original = await ReadOutput();
        Check(original.GetProperty("branches")[0].GetProperty("items")[1].ValueKind == JsonValueKind.Null
            && original.GetProperty("branches")[1].GetProperty("items").GetArrayLength() == 0,
            "read preserves null and empty branch");

        var edited = JsonNode.Parse(original.GetRawText())!;
        edited["branches"]![0]!["items"]![0]!["position"]![0] = 1.493;
        var update = await Prepare(Parse(edited.ToJsonString()));
        Check(update.ETag != send.ETag, "a committed write advances the input revision");
        await Commit(update);
        await ExpectError("STALE_REVISION", () =>
            AlidadeClient.WriteGeometryAsync(session, documentId, send with { Id = Guid.NewGuid() }, token));
        var finalPayload = await ReadOutput();
        Check(finalPayload.GetProperty("branches")[0].GetProperty("items")[0].GetProperty("position")[0].GetDouble() == 1.493,
            "the same output ID returns the result after the input is updated");
        Console.WriteLine("PASS live C# pairing, document scoping, same ID in both directions, exact retry, latest output and stale write protection.");

        await Commit(await Prepare(cadCollection));
        var cadBranches = (await ReadOutput()).GetProperty("branches");
        Check(cadBranches[0].GetProperty("items")[0].GetProperty("shapeType").GetString() == "solid"
            && cadBranches[0].GetProperty("items")[2].GetProperty("shapeType").GetString() == "face"
            && cadBranches[2].GetProperty("items")[0].GetProperty("kind").GetString() == "point"
            && cadBranches[2].GetProperty("items")[1].GetProperty("shapeType").GetString() == "shell"
            && cadBranches[2].GetProperty("items")[2].GetProperty("kind").GetString() == "line",
            "read preserves mixed geometry and all three exact STEP item types");
        Console.WriteLine("PASS points, lines and exact STEP shapes in one round trip through the live Project API.");

        Task<GeometryWriteRequest> Prepare(GeometryCollection value) =>
            AlidadeClient.PrepareWriteAsync(session, documentId, id, value, token);

        async Task<JsonElement> ReadOutput() =>
            (await AlidadeClient.ReadGeometryAsync(session, documentId, id, token)).Payload;

        async Task Commit(GeometryWriteRequest write)
        {
            var receipt = await AlidadeClient.WriteGeometryAsync(session, documentId, write, token);
            while (receipt.Pending)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                receipt = await AlidadeClient.CheckReceiptAsync(session, documentId, write, token);
            }
            Check(receipt.Error == null, $"write receipt is committed ({receipt.Error})");
        }
    }

    private static async Task ExpectError(string code, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AlidadeConnectionException error) when (code == "PAIRING_REQUIRED" ? error.PairingRequired : error.Code == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {code}.");
    }
}

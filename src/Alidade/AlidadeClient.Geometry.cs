using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Alidade;

internal sealed record GeometryWriteRequest(Guid Id, GeometryPortReference Target, int SchemaVersion, bool HasNames, ReadOnlyMemory<byte> Payload);

internal static partial class AlidadeClient
{
    private const string AsterismRoute = "/v1/asterism";

    private sealed record PortDescription(string Id, string Name, string Direction, string? ETag);
    private sealed record PortDirectory(PortDescription[] Items);

    private sealed class ReceiptResponse
    {
        public required Guid RequestId { get; init; }
        public required string State { get; init; }
        public ReceiptResult? Result { get; init; }
    }

    private sealed class ReceiptResult
    {
        public required int HttpStatus { get; init; }
        public string? PortId { get; init; }
        public string? ETag { get; init; }
        public ErrorDetails? Error { get; init; }
    }

    internal static async Task<IReadOnlyList<GeometryPortReference>> ListPortsAsync(
        ProjectSession project, ulong documentId, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, $"{AsterismRoute}/ports");
        var listing = await SendAsync<PortDirectory>(request, cancellation).ConfigureAwait(false);
        if (listing.Items == null)
            throw new JsonException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var status = project.Status;
        var ports = new GeometryPortReference[listing.Items.Length];
        for (var index = 0; index < ports.Length; index++)
        {
            var item = listing.Items[index];
            if (item == null || !ValidPortId(item.Id) || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name)
                || item.Direction is not ("input" or "output")
                || (item.Direction == "input" ? !StrongTag(item.ETag) : item.ETag != null))
                throw new JsonException();
            ports[index] = new(status.ProjectId, status.InstanceId, item.Id, item.Name, item.Direction, item.ETag);
        }
        return ports;
    }

    internal static async Task<GeometryCollection> ReadGeometryAsync(ProjectSession project, ulong documentId,
        GeometryPortReference target, CancellationToken cancellation, GeometryReceipt? receipt = null)
    {
        if (!target.Matches(project, "output"))
            throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"));
        if (receipt != null && (!receipt.Matches(project) || receipt.State != "committed" || !StrongTag(receipt.ETag)))
            throw new AlidadeConnectionException("Connect a committed receipt from this project.");

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await ReadOutputAsync(project, documentId, target.PortId, receipt, cancellation).ConfigureAwait(false);
            }
            catch (AlidadeConnectionException error) when (receipt != null && error.Code == "OUTPUT_NOT_READY"
                && Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
            {
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
        }
    }

    private static async Task<GeometryCollection> ReadOutputAsync(ProjectSession project, ulong documentId,
        string portId, GeometryReceipt? receipt, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, PortRoute("outputs", portId));
        if (receipt != null)
            request.Headers.Add("X-Alidade-Request-Id", receipt.RequestId.ToString("D"));
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation, timeoutSeconds: 60)
            .ConfigureAwait(false);
        if (receipt != null && response.Headers.ETag?.ToString() != receipt.ETag)
            throw new AlidadeConnectionException("Torquetum did not verify this Output against the receipt. Check that Torquetum supports receipt-based reads.",
                code: "OUTPUT_REVISION_UNVERIFIED");
        var payload = await ReadJsonAsync<JsonElement>(response, cancellation).ConfigureAwait(false);
        var collection = GeometryCollection.FromOwnedPayload(payload);
        if (!project.Status.SupportsCollection(collection.SchemaVersion))
            throw new AlidadeConnectionException("The paired project did not negotiate this collection format.");
        return collection;
    }

    internal static GeometryWriteRequest PrepareWrite(GeometryCollection collection, GeometryPortReference target)
    {
        if (collection.GeometryCount == 0)
            throw new AlidadeConnectionException("Connect a collection that contains geometry.");
        if (target.Direction != "input")
            throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"));
        if (!StrongTag(target.ETag))
            throw new AlidadeConnectionException(ErrorMessage("PRECONDITION_REQUIRED"));

        var payload = JsonSerializer.SerializeToUtf8Bytes(collection.Payload, JsonOptions);
        if (payload.Length > GeometryCollection.MaxExchangeBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"));
        return new GeometryWriteRequest(Guid.NewGuid(), target, collection.SchemaVersion, collection.HasNames, payload);
    }

    internal static async Task<GeometryReceipt> WriteGeometryAsync(ProjectSession project, ulong documentId,
        GeometryWriteRequest write, CancellationToken cancellation)
    {
        if (!project.IsValidFor(documentId))
            throw new AlidadeConnectionException(ErrorMessage("PAIRING_REQUIRED"), pairingRequired: true);
        if (!write.Target.Matches(project, "input"))
            throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"));
        if (project.Status.CollectionWriteError(write.SchemaVersion, write.HasNames) is { } unsupported)
            throw new AlidadeConnectionException(ErrorMessage(unsupported), code: unsupported);
        if (write.Payload.Length > project.Status.MaxBodyBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"));
        var receiptPath = ReceiptRoute(write.Id);
        using (var request = project.Request(documentId, HttpMethod.Put, PortRoute("inputs", write.Target.PortId)))
        {
            request.Content = new ReadOnlyMemoryContent(write.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Alidade-Request-Id", write.Id.ToString("D"));
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(write.Target.ETag!));

            using var response = await SendForStatusAsync(request, HttpStatusCode.Accepted, cancellation).ConfigureAwait(false);
            if (response.Headers.Location?.OriginalString != receiptPath)
                throw new AlidadeConnectionException("Torquetum returned an invalid receipt address. Check the request result.");
        }

        var pending = GeometryReceipt.Pending(write.Id, project.Status);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                var receipt = await CheckReceiptAsync(project, documentId, write.Id, write.Target.PortId, wait.Token).ConfigureAwait(false);
                if (receipt.State != "pending")
                    return receipt;
                pending = receipt;
                var retryDelay = (receipt.RetryAfter ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
                if (retryDelay >= TimeSpan.FromSeconds(15))
                    return pending;
                await Task.Delay(retryDelay > TimeSpan.FromSeconds(1) ? retryDelay : TimeSpan.FromSeconds(1), wait.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return pending; }
        catch (Exception error) when (error is HttpRequestException or JsonException) { return pending; }
        catch (AlidadeConnectionException error) when (!error.PairingRequired) { return pending; }
    }

    internal static async Task<GeometryReceipt> CheckReceiptAsync(ProjectSession project, ulong documentId,
        Guid requestId, string portId, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, ReceiptRoute(requestId));
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation).ConfigureAwait(false);
        var receipt = await ReadJsonAsync<ReceiptResponse>(response, cancellation).ConfigureAwait(false);
        if (receipt.RequestId != requestId)
            throw new JsonException();

        var status = project.Status;
        var retry = RetryAfter(response);
        return receipt.State switch
        {
            "pending" when receipt.Result == null => GeometryReceipt.Pending(requestId, status, retry),
            "committed" when receipt.Result is { HttpStatus: 200 } committed
                && committed.PortId == portId && StrongTag(committed.ETag) =>
                new GeometryReceipt(requestId, status.ProjectId, status.InstanceId, "committed",
                    committed.PortId, committed.ETag, "Asterism Input updated.", null),
            "rejected" when receipt.Result is { HttpStatus: >= 400 and < 600 } rejected && rejected.Error != null =>
                new GeometryReceipt(requestId, status.ProjectId, status.InstanceId, "rejected", null, null,
                    ErrorException(rejected.Error, (HttpStatusCode)rejected.HttpStatus).Message, null),
            _ => throw new JsonException(),
        };
    }

    private static string ReceiptRoute(Guid requestId) => $"/v1/requests/{requestId:D}";

    private static bool StrongTag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";

    private static bool ValidPortId(string? value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..");

    private static string PortRoute(string direction, string portId) => ValidPortId(portId)
        ? $"{AsterismRoute}/{direction}/{Uri.EscapeDataString(portId)}"
        : throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"));
}

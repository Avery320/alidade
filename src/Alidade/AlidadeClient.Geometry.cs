using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Alidade;

internal sealed record GeometryWriteRequest(Guid Id, string PortId, string ETag, ReadOnlyMemory<byte> Payload);
internal sealed record GeometryReceipt(bool Pending, string? Error = null, DateTimeOffset? RetryAfter = null);

internal static partial class AlidadeClient
{
    private const string AsterismRoute = "/v1/asterism";

    private sealed record PortDescription(string Id, string Direction, string? ETag);
    private sealed record PortDirectory(PortDescription[] Items);
    private sealed record ReceiptResponse(Guid RequestId, string State, ReceiptResult? Result);
    private sealed record ReceiptResult(int HttpStatus, string? PortId, string? ETag, ErrorDetails? Error);

    internal static async Task<GeometryWriteRequest> PrepareWriteAsync(ProjectSession project, ulong documentId,
        string portId, GeometryCollection collection, CancellationToken cancellation)
    {
        ValidatePortId(portId);
        if (collection.GeometryCount == 0)
            throw new AlidadeConnectionException("Connect a collection that contains geometry.");

        using var request = project.Request(documentId, HttpMethod.Get, $"{AsterismRoute}/ports");
        var listing = await SendAsync<PortDirectory>(request, cancellation).ConfigureAwait(false);
        var target = listing.Items?.FirstOrDefault(port => port?.Id == portId && port.Direction == "input");
        if (target == null)
            throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"), code: "PORT_NOT_FOUND");
        if (!StrongTag(target.ETag))
            throw new AlidadeConnectionException(ErrorMessage("PRECONDITION_REQUIRED"), code: "PRECONDITION_REQUIRED");

        var payload = JsonSerializer.SerializeToUtf8Bytes(collection.Payload, JsonOptions);
        if (payload.Length > project.Status.MaxBodyBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"), code: "CONTENT_TOO_LARGE");
        return new GeometryWriteRequest(Guid.NewGuid(), portId, target.ETag!, payload);
    }

    internal static async Task<GeometryCollection> ReadGeometryAsync(ProjectSession project, ulong documentId,
        string portId, CancellationToken cancellation)
    {
        var route = PortRoute("outputs", portId);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                using var request = project.Request(documentId, HttpMethod.Get, route);
                using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation, timeoutSeconds: 60)
                    .ConfigureAwait(false);
                var payload = await ReadJsonAsync<JsonElement>(response, cancellation).ConfigureAwait(false);
                return GeometryCollection.FromOwnedPayload(payload);
            }
            catch (AlidadeConnectionException error) when (error.Code == "OUTPUT_NOT_READY"
                && Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
            {
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<GeometryReceipt> WriteGeometryAsync(ProjectSession project, ulong documentId,
        GeometryWriteRequest write, CancellationToken cancellation)
    {
        var receiptPath = ReceiptRoute(write.Id);
        using (var request = project.Request(documentId, HttpMethod.Put, PortRoute("inputs", write.PortId)))
        {
            request.Content = new ReadOnlyMemoryContent(write.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Alidade-Request-Id", write.Id.ToString("D"));
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(write.ETag));

            using var response = await SendForStatusAsync(request, HttpStatusCode.Accepted, cancellation).ConfigureAwait(false);
            if (response.Headers.Location?.OriginalString != receiptPath)
                throw new AlidadeConnectionException("Torquetum returned an invalid receipt address. Check the request result.");
        }

        var pending = new GeometryReceipt(Pending: true);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                var receipt = await CheckReceiptAsync(project, documentId, write, wait.Token).ConfigureAwait(false);
                if (!receipt.Pending)
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
        GeometryWriteRequest write, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, ReceiptRoute(write.Id));
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation).ConfigureAwait(false);
        var receipt = await ReadJsonAsync<ReceiptResponse>(response, cancellation).ConfigureAwait(false);
        if (receipt.RequestId != write.Id)
            throw new JsonException();

        return receipt.State switch
        {
            "pending" when receipt.Result == null => new(Pending: true, RetryAfter: RetryAfter(response)),
            "committed" when receipt.Result is { HttpStatus: 200 } committed
                && committed.PortId == write.PortId && StrongTag(committed.ETag) =>
                new(Pending: false),
            "rejected" when receipt.Result is { HttpStatus: >= 400 and < 600 } rejected && rejected.Error != null =>
                new(Pending: false, Error: ErrorException(rejected.Error, (HttpStatusCode)rejected.HttpStatus).Message),
            _ => throw new JsonException(),
        };
    }

    private static string ReceiptRoute(Guid requestId) => $"/v1/requests/{requestId:D}";

    private static bool StrongTag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";

    private static void ValidatePortId(string id)
    {
        if (id.Length == 0 || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new AlidadeConnectionException("ID accepts letters, numbers, hyphens and underscores.");
    }

    private static string PortRoute(string direction, string portId)
    {
        ValidatePortId(portId);
        return $"{AsterismRoute}/{direction}/{portId}";
    }
}

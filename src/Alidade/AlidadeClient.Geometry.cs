using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Alidade;

internal static partial class AlidadeClient
{
    private const string AsterismRoute = ApiPrefix + "/asterism";

    private sealed record PortDescription(string Id, string Direction, string? ETag);
    private sealed record PortDirectory(PortDescription[] Items);
    private sealed record ReceiptResponse(Guid RequestId, string State, ReceiptResult? Result);
    private sealed record ReceiptResult(int HttpStatus, string? Id, string? ETag, ErrorDetails? Error);

    internal static async Task SendGeometryAsync(ProjectSession project, ulong documentId, string portId,
        GeometryCollection collection, CancellationToken cancellation)
    {
        var route = PortRoute("inputs", portId);
        if (collection.GeometryCount == 0)
            throw new AlidadeConnectionException("Connect a collection that contains geometry.");

        using var discovery = project.Request(documentId, HttpMethod.Get, $"{AsterismRoute}/ports");
        var listing = await SendAsync<PortDirectory>(discovery, cancellation).ConfigureAwait(false);
        var target = listing.Items?.FirstOrDefault(port => port?.Id == portId && port.Direction == "input");
        if (target == null)
            throw new AlidadeConnectionException(ErrorMessage("PORT_NOT_FOUND"));
        if (!StrongTag(target.ETag))
            throw new AlidadeConnectionException(ErrorMessage("PRECONDITION_REQUIRED"));

        var payload = JsonSerializer.SerializeToUtf8Bytes(collection.Payload, JsonOptions);
        if (payload.Length > project.Status.MaxBodyBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"));

        var requestId = Guid.NewGuid();
        var receiptPath = $"{ApiPrefix}/requests/{requestId:D}";
        using (var request = project.Request(documentId, HttpMethod.Put, route))
        {
            request.Content = new ReadOnlyMemoryContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Tor-Request-Id", requestId.ToString("D"));
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(target.ETag!));

            using var response = await SendForStatusAsync(request, HttpStatusCode.Accepted, cancellation).ConfigureAwait(false);
            if (response.Headers.Location?.OriginalString != receiptPath)
                throw new AlidadeConnectionException("Torquetum returned an invalid receipt address.");
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                using var request = project.Request(documentId, HttpMethod.Get, receiptPath);
                using var response = await SendForStatusAsync(request, HttpStatusCode.OK, wait.Token).ConfigureAwait(false);
                var receipt = await ReadJsonAsync<ReceiptResponse>(response, wait.Token).ConfigureAwait(false);
                if (receipt.RequestId != requestId) throw new JsonException();
                if (receipt.State == "committed" && receipt.Result is { HttpStatus: 200 } committed
                    && committed.Id == portId && StrongTag(committed.ETag)) return;
                if (receipt.State == "rejected" && receipt.Result is { HttpStatus: >= 400 and < 600 } rejected
                    && rejected.Error != null) throw ErrorException(rejected.Error, (HttpStatusCode)rejected.HttpStatus);
                if (receipt.State != "pending" || receipt.Result != null) throw new JsonException();

                var retryDelay = (RetryAfter(response) ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
                await Task.Delay(retryDelay > TimeSpan.FromSeconds(1) ? retryDelay : TimeSpan.FromSeconds(1), wait.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new AlidadeConnectionException("Send confirmation timed out. Press Send again to publish the current collection.");
        }
    }

    internal static async Task<GeometryCollection> ReadGeometryAsync(ProjectSession project, ulong documentId,
        string portId, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, PortRoute("outputs", portId));
        var payload = await SendAsync<JsonElement>(request, cancellation).ConfigureAwait(false);
        return GeometryCollection.FromOwnedPayload(payload);
    }

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

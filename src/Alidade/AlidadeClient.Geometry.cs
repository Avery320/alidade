using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Alidade;

internal sealed record GeometryListing(string EntityId, string Name, string[] ParentPath, string? ETag,
    bool CanRead, bool CanReplace, string? Reason)
{
    internal GeometryReference Reference(ProjectStatus status) =>
        new(status.ProjectId, status.InstanceId, EntityId, ETag!, Name, CanReplace);
}

internal sealed record GeometrySnapshot(GeometryCollection Collection, GeometryReference Reference);
internal sealed record GeometryWriteRequest(Guid Id, string? EntityId, string? ETag, int SchemaVersion, ReadOnlyMemory<byte> Payload);

internal static partial class AlidadeClient
{
    private const string GeometryRoute = "/v1/geometry";

    private sealed class GeometryIndex
    {
        public required GeometryListing[] Items { get; init; }
    }

    private sealed class ReceiptResponse
    {
        public required Guid RequestId { get; init; }
        public required string State { get; init; }
        public ReceiptResult? Result { get; init; }
    }

    private sealed class ReceiptResult
    {
        public required int HttpStatus { get; init; }
        public string? EntityId { get; init; }
        public string? ETag { get; init; }
        public ErrorDetails? Error { get; init; }
    }

    internal static async Task<IReadOnlyList<GeometryListing>> ListGeometryAsync(
        ProjectSession project, ulong documentId, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, GeometryRoute);
        var listing = await SendAsync<GeometryIndex>(request, cancellation).ConfigureAwait(false);
        if (listing.Items == null)
            throw new JsonException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in listing.Items)
        {
            if (item == null || !ValidEntityId(item.EntityId) || !ids.Add(item.EntityId) || string.IsNullOrWhiteSpace(item.Name)
                || item.ParentPath == null || Array.Exists(item.ParentPath, name => name == null)
                || (item.CanRead && !StrongTag(item.ETag)))
                throw new JsonException();
        }
        return listing.Items;
    }

    internal static async Task<GeometrySnapshot> ReadGeometryAsync(ProjectSession project, ulong documentId,
        GeometryReference target, CancellationToken cancellation)
    {
        using var request = project.Request(documentId, HttpMethod.Get, EntityRoute(target.EntityId));
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation,
            timeoutSeconds: project.Status.SupportsStep ? 60 : 15).ConfigureAwait(false);
        var tag = response.Headers.ETag?.ToString();
        if (!StrongTag(tag))
            throw new JsonException();
        var payload = await ReadJsonAsync<JsonElement>(response, cancellation).ConfigureAwait(false);
        var collection = GeometryCollection.Parse(payload);
        if (!project.Status.SupportsCollection(collection.SchemaVersion))
            throw new AlidadeConnectionException("The paired project did not negotiate this collection format.");
        return new GeometrySnapshot(collection, target with { ETag = tag! });
    }

    internal static GeometryWriteRequest PrepareWrite(GeometryCollection collection, string name, GeometryReference? target)
    {
        if (collection.GeometryCount == 0)
            throw new AlidadeConnectionException("Connect a collection that contains geometry.");
        if (target == null && string.IsNullOrWhiteSpace(name))
            throw new AlidadeConnectionException("Enter a name for the new Station geometry.");
        if (target != null && (!target.CanReplace || !StrongTag(target.ETag)))
            throw new AlidadeConnectionException(ErrorMessage("ENTITY_NOT_WRITABLE"));

        var payload = target == null
            ? JsonSerializer.SerializeToUtf8Bytes(new { name = name.Trim(), collection = collection.Payload }, JsonOptions)
            : JsonSerializer.SerializeToUtf8Bytes(new { collection = collection.Payload }, JsonOptions);
        if (payload.Length > StepText.MaxExchangeBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"));
        return new GeometryWriteRequest(Guid.NewGuid(), target?.EntityId, target?.ETag, collection.SchemaVersion, payload);
    }

    internal static async Task<GeometryReceipt> WriteGeometryAsync(ProjectSession project, ulong documentId,
        GeometryWriteRequest write, CancellationToken cancellation)
    {
        if (!project.IsValidFor(documentId))
            throw new AlidadeConnectionException(ErrorMessage("PAIRING_REQUIRED"), pairingRequired: true);
        if (!project.Status.SupportsCollection(write.SchemaVersion))
            throw new AlidadeConnectionException("The paired project does not offer precise STEP exchange.");
        if (write.Payload.Length > project.Status.MaxBodyBytes)
            throw new AlidadeConnectionException(ErrorMessage("CONTENT_TOO_LARGE"));
        var receiptPath = ReceiptRoute(write.Id);
        var replacing = write.EntityId is { };
        using (var request = project.Request(documentId, replacing ? HttpMethod.Put : HttpMethod.Post,
            replacing ? EntityRoute(write.EntityId!) : GeometryRoute))
        {
            request.Content = new ReadOnlyMemoryContent(write.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Alidade-Request-Id", write.Id.ToString("D"));
            if (write.ETag != null)
                request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(write.ETag));

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
                var receipt = await CheckReceiptAsync(project, documentId, write.Id, wait.Token).ConfigureAwait(false);
                if (receipt.State != "pending")
                {
                    if (replacing && receipt.State == "committed" && receipt.EntityId != write.EntityId)
                        throw new AlidadeConnectionException("Torquetum returned a receipt for a different geometry.");
                    return receipt;
                }
                pending = receipt;
                var retryDelay = (receipt.RetryAfter ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
                if (retryDelay >= TimeSpan.FromSeconds(15))
                    return pending;
                await Task.Delay(retryDelay > TimeSpan.FromSeconds(1) ? retryDelay : TimeSpan.FromSeconds(1), wait.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return pending; }
        catch (HttpRequestException) { return pending; }
        catch (AlidadeConnectionException error) when (!error.PairingRequired) { return pending; }
    }

    internal static async Task<GeometryReceipt> CheckReceiptAsync(ProjectSession project, ulong documentId,
        Guid requestId, CancellationToken cancellation)
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
            "committed" when receipt.Result is { HttpStatus: 200 or 201 } committed
                && ValidEntityId(committed.EntityId) && StrongTag(committed.ETag) =>
                new GeometryReceipt(requestId, status.ProjectId, status.InstanceId, "committed",
                    committed.EntityId, committed.ETag, committed.HttpStatus == 201 ? "Created geometry." : "Replaced geometry.", null),
            "rejected" when receipt.Result is { HttpStatus: >= 400 and < 600 } rejected && rejected.Error != null =>
                new GeometryReceipt(requestId, status.ProjectId, status.InstanceId, "rejected", null, null,
                    ErrorException(rejected.Error, (HttpStatusCode)rejected.HttpStatus).Message, null),
            _ => throw new JsonException(),
        };
    }

    private static string ReceiptRoute(Guid requestId) => $"/v1/requests/{requestId:D}";

    private static bool StrongTag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";

    private static bool ValidEntityId(string? value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..");

    private static string EntityRoute(string entityId) => ValidEntityId(entityId)
        ? $"{GeometryRoute}/{Uri.EscapeDataString(entityId)}"
        : throw new AlidadeConnectionException("Select a valid geometry entity.");
}

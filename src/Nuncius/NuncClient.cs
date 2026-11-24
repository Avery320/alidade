using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Nuncius;

internal static partial class NuncClient
{
    private const string InvitationPrefix = "TORAPI.";
    private const string ApiPrefix = "/v0.0";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = GeometryCollection.MaxExchangeBytes,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ProjectSession> PairAsync(string code, ulong documentId, CancellationToken cancellation)
    {
        var invitation = ReadInvitation(code);
        var access = await RequestAccessAsync(invitation.Endpoint, invitation.InstanceId, invitation.Invite, null, cancellation)
            .ConfigureAwait(false);
        return new ProjectSession(documentId, access);
    }

    internal static async Task<ProjectSession?> ConnectAsync(string? code, Guid connector, ulong documentId, CancellationToken cancellation)
    {
        var saved = RememberedConnection.Load(connector);
        if (saved != null && saved.Matches(code))
        {
            var renewed = await ResumeAccessAsync(saved, saved.Locate(), cancellation).ConfigureAwait(false);
            return new ProjectSession(documentId, renewed, saved);
        }
        if (string.IsNullOrWhiteSpace(code)) return null;

        var invitation = ReadInvitation(code);
        var secret = RandomNumberGenerator.GetBytes(32);
        var access = await RequestAccessAsync(invitation.Endpoint, invitation.InstanceId, invitation.Invite,
            RememberedConnection.Hash(secret), cancellation).ConfigureAwait(false);
        var locator = access.Locator ?? throw new NuncConnectionException("Torquetum did not remember this connection.");
        var remembered = new RememberedConnection(access.Status.ProjectId, locator, EncodeBase64Url(secret),
            RememberedConnection.Hash(Encoding.UTF8.GetBytes(code)));
        cancellation.ThrowIfCancellationRequested();
        remembered.Save(connector);
        return new ProjectSession(documentId, access, remembered);
    }

    internal static async Task<ProjectAccess> ResumeAccessAsync(RememberedConnection saved,
        (Uri Endpoint, Guid InstanceId) target, CancellationToken cancellation)
    {
        var bytes = new byte[48];
        Convert.FromHexString(target.InstanceId.ToString("N")).CopyTo(bytes, 0);
        DecodeBase64Url(saved.Secret).CopyTo(bytes, 16);
        var access = await RequestAccessAsync(target.Endpoint, target.InstanceId, EncodeBase64Url(bytes), null, cancellation)
            .ConfigureAwait(false);
        if (access.Status.ProjectId != saved.ProjectId || access.Locator != saved.Locator)
            throw new NuncConnectionException("The Torquetum Project changed. Pair again.", pairingRequired: true);
        return access;
    }

    private static async Task<ProjectAccess> RequestAccessAsync(Uri endpoint, Guid instanceId, string credential, string? remember,
        CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, ApiPrefix + "/pair"));
            request.Content = JsonContent.Create(new PairRequest(credential, $"{NuncAssemblyInfo.PluginName} for Grasshopper", remember));
            var grant = await SendAsync<Grant>(request, cancellation).ConfigureAwait(false);
            if (grant.InstanceId != instanceId || ReadInstanceId(grant.AccessToken) != instanceId
                || grant.GrantId == Guid.Empty || grant.ExpiresAt <= DateTimeOffset.UtcNow
                || (grant.Locator != null && !RememberedConnection.IsLocator(grant.Locator)))
                throw new NuncConnectionException("Torquetum returned an invalid pairing response.");
            return new ProjectAccess(endpoint, grant.Status(), grant.AccessToken, grant.ExpiresAt, grant.Locator);
        }
        catch (JsonException)
        {
            throw new NuncConnectionException("Torquetum returned an invalid pairing response.");
        }
    }

    internal static async Task<ProjectStatus> ReadProjectAsync(ProjectSession project, ulong documentId, CancellationToken cancellation)
    {
        try
        {
            var access = await project.AccessAsync(documentId, cancellation).ConfigureAwait(false);
            using var request = access.Request(HttpMethod.Get, ApiPrefix + "/project");
            var status = (await SendAsync<ProjectResponse>(request, cancellation).ConfigureAwait(false)).Status();
            if (!access.Status.Matches(status.ProjectId, status.InstanceId))
                throw new NuncConnectionException("The Torquetum project changed. Pair again.", pairingRequired: true);
            return status;
        }
        catch (JsonException)
        {
            throw new NuncConnectionException("Torquetum returned invalid project information.");
        }
    }

    private static async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellation).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        using var payload = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(payload, JsonOptions, cancellation).ConfigureAwait(false)
            ?? throw new JsonException();
    }

    private static async Task<HttpResponseMessage> SendForStatusAsync(
        HttpRequestMessage request, HttpStatusCode expected, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == expected)
            return response;

        using (response)
        {
            ApiError? error;
            try { error = await ReadJsonAsync<ApiError>(response, cancellation).ConfigureAwait(false); }
            catch (JsonException) { error = null; }
            throw ErrorException(error?.Error, response.StatusCode);
        }
    }

    private static DateTimeOffset? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : response.Headers.RetryAfter?.Date;

    private static NuncConnectionException ErrorException(ErrorDetails? error, HttpStatusCode status)
    {
        var message = ErrorMessage(error?.Code, error?.Message);
        if (error?.Path is { Length: > 0 } path)
            message += $" Branch {{{string.Join(";", path)}}}" +
                (error.ItemIndex is { } index ? $", item {index}." : ".");
        return new NuncConnectionException(message, pairingRequired: status == HttpStatusCode.Unauthorized);
    }

    internal static string ErrorMessage(string? code, string? serverMessage = null) => code switch
    {
        "INVITE_INVALID" => "The invitation is unavailable. Copy a new invitation from Torquetum.",
        "PAIRING_REQUIRED" => "Pair with the project again.",
        "PORT_NOT_FOUND" => "Check the ID on the matching Asterism node.",
        "STALE_REVISION" => "The Asterism input has changed. Use Send again for its current version.",
        _ when serverMessage is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(serverMessage) => serverMessage,
        "PROJECT_BUSY" => "The Torquetum project is busy. Try again when it is ready.",
        "READ_ONLY" => "This project currently accepts only read access.",
        "PROJECT_SYNC_PENDING" => "Wait for the Torquetum project to finish synchronizing.",
        "PRECONDITION_REQUIRED" => "The Asterism input has no current version. Check its ID and try Send again.",
        "CONTENT_TOO_LARGE" => "The collection exceeds the Torquetum transfer limit.",
        _ => "Torquetum rejected the request.",
    };

    private static (Uri Endpoint, string Invite, Guid InstanceId) ReadInvitation(string code)
    {
        code = code.Trim();
        if (code.Length <= 4096 && code.StartsWith(InvitationPrefix, StringComparison.Ordinal))
        {
            try
            {
                var input = JsonSerializer.Deserialize<Invitation>(DecodeBase64Url(code[InvitationPrefix.Length..]), JsonOptions);
                if (input != null && Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var endpoint) &&
                    endpoint.Port > 0 && input.Endpoint == $"http://127.0.0.1:{endpoint.Port}" &&
                    ReadInstanceId(input.Invite) is { } instanceId)
                    return (endpoint, input.Invite, instanceId);
            }
            catch (FormatException) { }
            catch (JsonException) { }
        }
        throw new NuncConnectionException("Copy a complete pairing code from Torquetum Project Settings.");
    }

    private static Guid? ReadInstanceId(string? credential)
    {
        if (credential is not { Length: 64 })
            return null;
        try
        {
            var bytes = DecodeBase64Url(credential);
            var instance = Guid.ParseExact(Convert.ToHexString(bytes.AsSpan(0, 16)), "N");
            return instance == Guid.Empty ? null : instance;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        foreach (var character in value)
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                throw new FormatException();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
            .PadRight((value.Length + 3) & ~3, '='));
    }

    private sealed class Invitation
    {
        public required string Endpoint { get; init; }
        public required string Invite { get; init; }
    }

    private sealed record PairRequest(string Credential, string ClientName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Remember);

    private class ProjectResponse
    {
        public required string ProjectId { get; init; }
        public required Guid InstanceId { get; init; }
        public required string ProjectName { get; init; }
        public required bool CanRead { get; init; }
        public required bool CanWrite { get; init; }
        public ProjectLimits? Limits { get; init; }
        public string? AvailabilityReason { get; init; }

        public ProjectStatus Status()
        {
            if (string.IsNullOrWhiteSpace(ProjectId) || InstanceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(ProjectName) || (CanWrite && !CanRead))
                throw new JsonException();
            if (Limits?.MaxBodyBytes is not (> 0 and var advertised))
                throw new JsonException();
            return new ProjectStatus(ProjectId, InstanceId, ProjectName, CanRead, CanWrite, AvailabilityReason,
                Math.Min(advertised, GeometryCollection.MaxExchangeBytes));
        }
    }

    private sealed class ProjectLimits
    {
        public int MaxBodyBytes { get; init; }
    }

    private sealed class Grant : ProjectResponse
    {
        public required Guid GrantId { get; init; }
        public required string AccessToken { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public string? Locator { get; init; }
    }

    private sealed class ApiError
    {
        public ErrorDetails? Error { get; init; }
    }

    private sealed class ErrorDetails
    {
        public string? Code { get; init; }
        public string? Message { get; init; }
        public int[]? Path { get; init; }
        public int? ItemIndex { get; init; }
    }
}

internal sealed class NuncConnectionException : Exception
{
    public NuncConnectionException(string message, bool pairingRequired = false) : base(message)
    {
        PairingRequired = pairingRequired;
    }

    public bool PairingRequired { get; }
}

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

internal sealed record ProjectStatus(
    string ProjectId, Guid InstanceId, string ProjectName, bool CanRead, bool CanWrite,
    string? AvailabilityReason, int MaxBodyBytes)
{
    internal bool Matches(string projectId, Guid instanceId) => ProjectId == projectId && InstanceId == instanceId;
}

internal sealed record ProjectAccess(Uri Endpoint, ProjectStatus Status, string Token, DateTimeOffset ExpiresAt, string? Locator)
{
    internal HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }
}

public sealed class ProjectSession
{
    private readonly ulong _documentId;
    private readonly RememberedConnection? _remembered;
    private readonly SemaphoreSlim _renewal = new(1);
    private ProjectAccess? _access;
    private ProjectStatus _status;

    internal ProjectSession(ulong documentId, ProjectAccess access, RememberedConnection? remembered = null)
    {
        _documentId = documentId;
        _access = access;
        _remembered = remembered;
        _status = access.Status;
    }

    internal ProjectStatus Status
    {
        get => Volatile.Read(ref _status);
        set
        {
            if (Volatile.Read(ref _access)?.Status.Matches(value.ProjectId, value.InstanceId) == true)
                Volatile.Write(ref _status, value);
        }
    }
    internal event Action? Disconnected;

    internal bool IsValid => Volatile.Read(ref _access) is { } access
        && (_remembered != null || DateTimeOffset.UtcNow < access.ExpiresAt);

    internal bool IsValidFor(ulong documentId) => IsValid && documentId == _documentId;

    internal void Disconnect()
    {
        if (Interlocked.Exchange(ref _access, null) != null)
            Disconnected?.Invoke();
    }

    internal async Task<ProjectAccess> AccessAsync(ulong documentId, CancellationToken cancellation)
    {
        if (documentId != _documentId)
            throw new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);

        await _renewal.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var access = Volatile.Read(ref _access)
                ?? throw new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
            if (_remembered == null)
            {
                if (DateTimeOffset.UtcNow >= access.ExpiresAt)
                    throw new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
                return access;
            }

            var target = _remembered.Locate(access.Status.InstanceId);
            if (target.InstanceId == access.Status.InstanceId && target.Endpoint == access.Endpoint
                && DateTimeOffset.UtcNow < access.ExpiresAt)
                return access;

            var next = await AlidadeClient.ResumeAccessAsync(_remembered, target, cancellation).ConfigureAwait(false);
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _access, next, access), access))
                throw new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
            Status = next.Status;
            return next;
        }
        finally { _renewal.Release(); }
    }
}

public sealed class AlidadeProject : GH_Goo<ProjectSession?>
{
    public AlidadeProject() { }
    internal AlidadeProject(ProjectSession? session) : base(session) { }

    public override bool IsValid => Value?.IsValid == true;
    public override string TypeName => "Alidade Project";
    public override string TypeDescription => "A live connection to a Torquetum project.";
    public override IGH_Goo Duplicate() => new AlidadeProject(Value);
    public override object ScriptVariable() => ToString();
    public override string ToString() => IsValid && Value is { } session ? session.Status.ProjectName : "Disconnected Alidade project";
    public override bool Write(GH_IWriter writer) => true;
    public override bool Read(GH_IReader reader)
    {
        Value = null;
        return true;
    }
}

public sealed class AlidadeProjectParameter : GH_Param<AlidadeProject>
{
    public AlidadeProjectParameter()
        : base("Project", "Project", "A live Torquetum project connection.", "Alidade", "Session", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("6300cb1e-3e9b-427e-b3d1-c39fed8ee509");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}

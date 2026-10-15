namespace Stelliberty.Application.Runtime;

public sealed record BackgroundOperationRequest(string Resource, string[] SubscriptionIds, bool IsUpdate = false)
{
    public const string Subscriptions = "subscriptions";
    public const string Delay = "delay";
}

public sealed record BackgroundOperationState(Guid Id, Guid Owner, BackgroundOperationRequest Request);
public sealed record BackgroundOperationRelease(Guid Id);
public sealed record BackgroundOperationGrant(Guid? Id);

public interface IBackgroundOperationClient
{
    Task<IAsyncDisposable?> TryAcquireAsync(BackgroundOperationRequest request, CancellationToken cancellationToken = default);
}

// 仅由后台宿主持有；UI 通过 IPC 使用。测速允许同一个 UI 的单测与批测协作，
// 不同客户端互斥。订阅修改整体串行，避免读改写订阅列表时丢失记录。
public sealed class BackgroundOperationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BackgroundOperationState> _active = [];
    public event EventHandler? Changed;

    public IReadOnlyList<BackgroundOperationState> Snapshot
    {
        get { lock (_gate) return _active.Values.ToArray(); }
    }

    public Guid? TryAcquire(Guid owner, BackgroundOperationRequest request)
    {
        if (request.Resource is not (BackgroundOperationRequest.Subscriptions or BackgroundOperationRequest.Delay))
            throw new ArgumentException("Unknown background operation resource.");
        Guid id;
        lock (_gate)
        {
            if (_active.Values.Any(item => item.Request.Resource == request.Resource
                && (request.Resource != BackgroundOperationRequest.Delay || item.Owner != owner))) return null;
            id = Guid.NewGuid();
            _active.Add(id, new(id, owner, request));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Release(Guid owner, Guid id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var lease) || lease.Owner != owner) return;
            _active.Remove(id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReleaseOwner(Guid owner)
    {
        bool changed;
        lock (_gate)
        {
            var ids = _active.Values.Where(item => item.Owner == owner).Select(item => item.Id).ToArray();
            foreach (var id in ids) _active.Remove(id);
            changed = ids.Length > 0;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}

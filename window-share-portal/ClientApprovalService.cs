using System.Collections.Concurrent;
using System.Net;

internal sealed class ClientApprovalService
{
    private readonly object _sync = new();
    private readonly PortalSettingsStore _settingsStore;
    private readonly ConcurrentDictionary<string, PendingApprovalRequest> _pendingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private List<ApprovedClientEntry> _approvedClients;
    private bool _approvalRequired;

    public ClientApprovalService(PortalSettingsStore settingsStore, bool approvalRequired, ApprovedClientEntry[] approvedClients)
    {
        _settingsStore = settingsStore;
        _approvalRequired = approvalRequired;
        _approvedClients = [.. approvedClients];
    }

    public event Action<PendingApprovalRequest>? ApprovalRequested;

    public bool ApprovalRequired
    {
        get { lock (_sync) return _approvalRequired; }
    }

    public IReadOnlyList<ApprovedClientEntry> ApprovedClients
    {
        get { lock (_sync) return _approvedClients.ToArray(); }
    }

    public bool NeedsApproval(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
            return true;

        lock (_sync)
        {
            if (!_approvalRequired)
                return false;

            if (IPAddress.IsLoopback(remoteAddress))
                return false;

            var normalized = NormalizeIp(remoteAddress);
            return !_approvedClients.Any(c => string.Equals(c.IpAddress, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<PendingApprovalRequest> GetPendingApprovals()
    {
        return _pendingApprovals.Values.Where(r => !r.Completion.Task.IsCompleted).ToArray();
    }

    public async Task<bool> RequestApprovalAsync(IPAddress remoteAddress, string userAgent, CancellationToken cancellationToken)
    {
        var ip = NormalizeIp(remoteAddress);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingApprovalRequest(ip, userAgent, tcs);

        _pendingApprovals.AddOrUpdate(ip, request, (_, old) =>
        {
            old.Completion.TrySetResult(false);
            return request;
        });

        ApprovalRequested?.Invoke(request);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        linkedCts.Token.Register(() => tcs.TrySetResult(false));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pendingApprovals.TryRemove(ip, out _);
        }
    }

    public void ResolvePendingApproval(string ipAddress, bool approved)
    {
        if (!_pendingApprovals.TryGetValue(ipAddress, out var request))
            return;

        if (approved)
        {
            var env = ClientConnectionTracker.DescribeClientEnvironment(request.UserAgent);
            ApproveClient(ipAddress, env);
        }

        request.Completion.TrySetResult(approved);
    }

    public void ApproveClient(string ipAddress, string label)
    {
        lock (_sync)
        {
            if (_approvedClients.Any(c => string.Equals(c.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase)))
                return;

            var entry = new ApprovedClientEntry(ipAddress, label, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
            _approvedClients.Add(entry);
            Persist();
        }
    }

    public void RevokeClient(string ipAddress)
    {
        lock (_sync)
        {
            _approvedClients.RemoveAll(c => string.Equals(c.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
            Persist();
        }
    }

    public void SetApprovalRequired(bool required)
    {
        lock (_sync)
        {
            _approvalRequired = required;
            Persist();
        }
    }

    private void Persist()
    {
        _settingsStore.SaveClientApproval(_approvalRequired, _approvedClients.ToArray());
    }

    private static string NormalizeIp(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return normalized.ToString();
    }
}

internal sealed record ApprovedClientEntry(string IpAddress, string Label, string ApprovedAt);

internal sealed record PendingApprovalRequest(string IpAddress, string UserAgent, TaskCompletionSource<bool> Completion);

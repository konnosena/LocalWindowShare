using System.Collections.Concurrent;

internal sealed class ClientConnectionTracker
{
    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, ClientConnectionRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public void TrackRequest(HttpContext context)
    {
        var clientInfo = GetClientInfo(context);
        var record = _records.GetOrAdd(clientInfo.ClientId, _ => new ClientConnectionRecord(clientInfo.ClientId, clientInfo.RemoteAddress, clientInfo.UserAgent));
        record.Update(
            context.Request.Method,
            context.Request.Path.ToString(),
            context.Response.StatusCode,
            DateTimeOffset.UtcNow,
            clientInfo.SessionId);
    }

    public IDisposable RegisterRealtimeConnection(HttpContext context, Func<CancellationToken, Task> disconnectAsync)
    {
        var clientInfo = GetClientInfo(context);
        var record = _records.GetOrAdd(clientInfo.ClientId, _ => new ClientConnectionRecord(clientInfo.ClientId, clientInfo.RemoteAddress, clientInfo.UserAgent));
        record.RegisterSessionId(clientInfo.SessionId);
        return record.RegisterRealtimeConnection(disconnectAsync);
    }

    public IReadOnlyList<ClientConnectionSnapshot> GetSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - RetentionWindow;

        var staleKeys = _records
            .Where(pair => pair.Value.GetLastSeenUtc() < cutoff)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var staleKey in staleKeys)
        {
            _records.TryRemove(staleKey, out _);
        }

        return _records.Values
            .Select(record => record.ToSnapshot(now, ActiveThreshold))
            .OrderByDescending(snapshot => snapshot.LastSeenUtc)
            .ToArray();
    }

    public ClientDisconnectTarget? TryCreateDisconnectTarget(string clientId)
    {
        if (!_records.TryGetValue(clientId, out var record))
        {
            return null;
        }

        return record.CreateDisconnectTarget();
    }

    private static ClientRequestInfo GetClientInfo(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";
        var userAgent = context.Request.Headers.UserAgent.ToString();
        context.Request.Cookies.TryGetValue(PortalSecurityLimits.SessionCookieName, out var sessionId);
        return new ClientRequestInfo(BuildClientId(remoteAddress, userAgent), remoteAddress, userAgent, sessionId);
    }

    private static string BuildClientId(string remoteAddress, string userAgent)
    {
        return $"{remoteAddress}|{userAgent}";
    }

    private sealed class ClientConnectionRecord
    {
        private readonly string _clientId;
        private readonly string _remoteAddress;
        private readonly string _userAgent;
        private readonly object _sync = new();
        private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Func<CancellationToken, Task>> _disconnectHandlers = [];
        private string _lastMethod = "GET";
        private string _lastPath = "/";
        private int _lastStatusCode = StatusCodes.Status200OK;
        private DateTimeOffset _lastSeenUtc;
        private bool _forceInactive;

        public ClientConnectionRecord(string clientId, string remoteAddress, string userAgent)
        {
            _clientId = clientId;
            _remoteAddress = remoteAddress;
            _userAgent = userAgent;
            _lastSeenUtc = DateTimeOffset.UtcNow;
        }

        public void Update(string method, string path, int statusCode, DateTimeOffset lastSeenUtc, string? sessionId)
        {
            lock (_sync)
            {
                _lastMethod = method;
                _lastPath = path;
                _lastStatusCode = statusCode;
                _lastSeenUtc = lastSeenUtc;
                _forceInactive = false;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    _sessionIds.Add(sessionId);
                }
            }
        }

        public void RegisterSessionId(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (_sync)
            {
                _sessionIds.Add(sessionId);
            }
        }

        public IDisposable RegisterRealtimeConnection(Func<CancellationToken, Task> disconnectAsync)
        {
            var registrationId = Guid.NewGuid();
            lock (_sync)
            {
                _disconnectHandlers[registrationId] = disconnectAsync;
            }

            return new DelegateDisposable(() =>
            {
                lock (_sync)
                {
                    _disconnectHandlers.Remove(registrationId);
                }
            });
        }

        public DateTimeOffset GetLastSeenUtc()
        {
            lock (_sync)
            {
                return _lastSeenUtc;
            }
        }

        public ClientConnectionSnapshot ToSnapshot(DateTimeOffset now, TimeSpan activeThreshold)
        {
            lock (_sync)
            {
                return new ClientConnectionSnapshot(
                    _clientId,
                    _remoteAddress,
                    DescribeClientEnvironment(_userAgent),
                    _userAgent,
                    _lastMethod,
                    _lastPath,
                    _lastStatusCode,
                    _lastSeenUtc,
                    !_forceInactive && now - _lastSeenUtc <= activeThreshold);
            }
        }

        public ClientDisconnectTarget CreateDisconnectTarget()
        {
            string[] sessionIds;
            Func<CancellationToken, Task>[] disconnectHandlers;
            lock (_sync)
            {
                _forceInactive = true;
                _lastMethod = "POST";
                _lastPath = "/api/internal/disconnect";
                _lastStatusCode = StatusCodes.Status401Unauthorized;
                _lastSeenUtc = DateTimeOffset.UtcNow;
                sessionIds = _sessionIds.ToArray();
                disconnectHandlers = _disconnectHandlers.Values.ToArray();
            }

            return new ClientDisconnectTarget(
                _clientId,
                _remoteAddress,
                DescribeClientEnvironment(_userAgent),
                _userAgent,
                sessionIds,
                disconnectHandlers.Length,
                async cancellationToken =>
                {
                    foreach (var disconnectHandler in disconnectHandlers)
                    {
                        try
                        {
                            await disconnectHandler(cancellationToken);
                        }
                        catch
                        {
                        }
                    }
                });
        }
    }

    private static string DescribeClientEnvironment(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown client";
        }

        var normalized = userAgent.ToLowerInvariant();
        var device = normalized.Contains("iphone", StringComparison.Ordinal) ? "iPhone" :
            normalized.Contains("ipad", StringComparison.Ordinal) ? "iPad" :
            normalized.Contains("android", StringComparison.Ordinal) ? "Android" :
            normalized.Contains("windows", StringComparison.Ordinal) ? "Windows" :
            normalized.Contains("mac os", StringComparison.Ordinal) || normalized.Contains("macintosh", StringComparison.Ordinal) ? "macOS" :
            normalized.Contains("linux", StringComparison.Ordinal) ? "Linux" :
            "Other";

        var browser = normalized.Contains("edg/", StringComparison.Ordinal) ? "Edge" :
            normalized.Contains("chrome/", StringComparison.Ordinal) && !normalized.Contains("edg/", StringComparison.Ordinal) ? "Chrome" :
            normalized.Contains("safari/", StringComparison.Ordinal) && !normalized.Contains("chrome/", StringComparison.Ordinal) ? "Safari" :
            normalized.Contains("firefox/", StringComparison.Ordinal) ? "Firefox" :
            normalized.Contains("wv", StringComparison.Ordinal) ? "WebView" :
            "Browser";

        return $"{device} / {browser}";
    }

    private sealed class DelegateDisposable(Action disposeAction) : IDisposable
    {
        private Action? _disposeAction = disposeAction;

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeAction, null)?.Invoke();
        }
    }

    private sealed record ClientRequestInfo(string ClientId, string RemoteAddress, string UserAgent, string? SessionId);
}

internal sealed record ClientConnectionSnapshot(
    string ClientId,
    string RemoteAddress,
    string EnvironmentLabel,
    string UserAgent,
    string LastMethod,
    string LastPath,
    int LastStatusCode,
    DateTimeOffset LastSeenUtc,
    bool IsActive);

internal sealed class ClientDisconnectTarget
{
    private readonly Func<CancellationToken, Task> _disconnectAsync;

    public ClientDisconnectTarget(
        string clientId,
        string remoteAddress,
        string environmentLabel,
        string userAgent,
        IReadOnlyList<string> sessionIds,
        int realtimeConnectionCount,
        Func<CancellationToken, Task> disconnectAsync)
    {
        ClientId = clientId;
        RemoteAddress = remoteAddress;
        EnvironmentLabel = environmentLabel;
        UserAgent = userAgent;
        SessionIds = sessionIds;
        RealtimeConnectionCount = realtimeConnectionCount;
        _disconnectAsync = disconnectAsync;
    }

    public string ClientId { get; }

    public string RemoteAddress { get; }

    public string EnvironmentLabel { get; }

    public string UserAgent { get; }

    public IReadOnlyList<string> SessionIds { get; }

    public int RealtimeConnectionCount { get; }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _disconnectAsync(cancellationToken);
    }
}

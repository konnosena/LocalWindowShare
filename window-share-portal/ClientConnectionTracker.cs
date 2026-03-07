using System.Collections.Concurrent;

internal sealed class ClientConnectionTracker
{
    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, ClientConnectionRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public void TrackRequest(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var key = $"{remoteAddress}|{userAgent}";
        var record = _records.GetOrAdd(key, _ => new ClientConnectionRecord(remoteAddress, userAgent));
        record.Update(
            context.Request.Method,
            context.Request.Path.ToString(),
            context.Response.StatusCode,
            DateTimeOffset.UtcNow);
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

    private sealed class ClientConnectionRecord
    {
        private readonly string _remoteAddress;
        private readonly string _userAgent;
        private readonly object _sync = new();
        private string _lastMethod = "GET";
        private string _lastPath = "/";
        private int _lastStatusCode = StatusCodes.Status200OK;
        private DateTimeOffset _lastSeenUtc;

        public ClientConnectionRecord(string remoteAddress, string userAgent)
        {
            _remoteAddress = remoteAddress;
            _userAgent = userAgent;
            _lastSeenUtc = DateTimeOffset.UtcNow;
        }

        public void Update(string method, string path, int statusCode, DateTimeOffset lastSeenUtc)
        {
            lock (_sync)
            {
                _lastMethod = method;
                _lastPath = path;
                _lastStatusCode = statusCode;
                _lastSeenUtc = lastSeenUtc;
            }
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
                    _remoteAddress,
                    DescribeClientEnvironment(_userAgent),
                    _userAgent,
                    _lastMethod,
                    _lastPath,
                    _lastStatusCode,
                    _lastSeenUtc,
                    now - _lastSeenUtc <= activeThreshold);
            }
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
}

internal sealed record ClientConnectionSnapshot(
    string RemoteAddress,
    string EnvironmentLabel,
    string UserAgent,
    string LastMethod,
    string LastPath,
    int LastStatusCode,
    DateTimeOffset LastSeenUtc,
    bool IsActive);

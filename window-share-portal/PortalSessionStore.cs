using System.Collections.Concurrent;
using System.Security.Cryptography;

internal sealed class PortalSessionStore
{
    private readonly ConcurrentDictionary<string, PortalSession> _sessions = new(StringComparer.Ordinal);

    public string CreateSession(string tokenStamp)
    {
        var now = DateTimeOffset.UtcNow;
        CleanupExpired(now);

        while (true)
        {
            var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var session = new PortalSession(tokenStamp, now, now, now + PortalSecurityLimits.SessionLifetime);
            if (_sessions.TryAdd(sessionId, session))
            {
                return sessionId;
            }
        }
    }

    public bool TryValidateAndTouch(string? sessionId, string expectedTokenStamp)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        CleanupExpired(now);
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        if (session.ExpiresUtc <= now || !string.Equals(session.TokenStamp, expectedTokenStamp, StringComparison.Ordinal))
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        var updated = session with
        {
            LastSeenUtc = now,
            ExpiresUtc = now + PortalSecurityLimits.SessionLifetime,
        };

        _sessions.TryUpdate(sessionId, updated, session);
        return true;
    }

    public void Remove(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresUtc <= now)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record PortalSession(string TokenStamp, DateTimeOffset CreatedUtc, DateTimeOffset LastSeenUtc, DateTimeOffset ExpiresUtc);
}

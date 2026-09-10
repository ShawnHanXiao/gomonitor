using System.Security.Cryptography;

namespace GoMonitor.Services;

public sealed record SessionInfo(string SessionId, int RequestCount, DateTimeOffset LastActiveUtc);

/// <summary>
/// Maps conversation content hashes to stable x-opencode-session ids.
/// Same conversation (stable system + first user message) keeps one session id,
/// so prompt caching hits; entries idle for longer than the configured window expire.
/// </summary>
public sealed class SessionRegistry
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly object _lock = new();
    private readonly TimeSpan _idleWindow;
    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private string? _activeSessionId;

    private sealed class SessionEntry
    {
        public required string SessionId;
        public required int RequestCount;
        public required DateTimeOffset LastActiveUtc;
    }

    public SessionRegistry(TimeSpan idleWindow) => _idleWindow = idleWindow;

    public int ActiveSessionCount
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Resolves the session id for a request. Priority: explicit client id,
    /// then content hash, then the currently active session (creates one if none).
    /// Returns the session id and the 1-based request index within that session.
    /// </summary>
    public (string SessionId, int RequestIndex) Resolve(string? explicitSessionId, string? contentHash)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(explicitSessionId))
            {
                return BumpExplicit(explicitSessionId.Trim(), now);
            }

            string? key = null;
            if (!string.IsNullOrWhiteSpace(contentHash))
            {
                key = contentHash;
                if (_sessions.TryGetValue(key, out var entry)
                    && now - entry.LastActiveUtc < _idleWindow)
                {
                    entry.LastActiveUtc = now;
                    entry.RequestCount++;
                    _activeSessionId = entry.SessionId;
                    return (entry.SessionId, entry.RequestCount);
                }

                // Expired or new: create a fresh session for this conversation.
                var fresh = NewSessionId();
                _sessions[key] = new SessionEntry
                {
                    SessionId = fresh,
                    RequestCount = 1,
                    LastActiveUtc = now,
                };
                _activeSessionId = fresh;
                return (fresh, 1);
            }

            // No hash available: reuse the active session so requests still group.
            if (_activeSessionId is not null)
            {
                foreach (var entry in _sessions.Values)
                {
                    if (entry.SessionId == _activeSessionId)
                    {
                        entry.LastActiveUtc = now;
                        entry.RequestCount++;
                        return (entry.SessionId, entry.RequestCount);
                    }
                }
            }

            var temp = NewSessionId();
            _activeSessionId = temp;
            _sessions[$"temp:{temp}"] = new SessionEntry
            {
                SessionId = temp,
                RequestCount = 1,
                LastActiveUtc = now,
            };
            return (temp, 1);
        }
    }

    /// <summary>Forgets all hash mappings; the next request starts a new session.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sessions.Clear();
            _activeSessionId = null;
        }
    }

    /// <summary>Returns the most recently used session id, if any.</summary>
    public string? CurrentSessionId
    {
        get
        {
            lock (_lock)
            {
                return _activeSessionId;
            }
        }
    }

    private (string, int) BumpExplicit(string sessionId, DateTimeOffset now)
    {
        foreach (var entry in _sessions.Values)
        {
            if (string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            {
                entry.LastActiveUtc = now;
                entry.RequestCount++;
                _activeSessionId = sessionId;
                return (sessionId, entry.RequestCount);
            }
        }

        _activeSessionId = sessionId;
        _sessions[$"explicit:{sessionId}"] = new SessionEntry
        {
            SessionId = sessionId,
            RequestCount = 1,
            LastActiveUtc = now,
        };
        return (sessionId, 1);
    }

    public static string NewSessionId()
    {
        // 8 chars, A-Z a-z 0-9, same style as the official OpenCode client.
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray());
    }
}

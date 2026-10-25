namespace Quattromani.Core;

/// <summary>An agent (MCP client) that talked to the app recently. LastDraw is the time of its last document change (null if none yet).</summary>
public sealed record AgentInfo(string Name, DateTime LastSeen, int Sessions, DateTime? LastDraw, int Commands);

/// <summary>
/// Tracks which agents are connected. MCP over HTTP has no persistent connection to observe, so an agent counts as
/// connected while one of its sessions made a request within <see cref="IdleAfter"/> (or until the session is closed).
/// Thread-safe: requests arrive from server threads.
/// </summary>
public sealed class AgentRegistry(Func<DateTime>? now = null)
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    readonly Dictionary<string, (string Name, DateTime Seen)> _sessions = [];
    readonly Dictionary<string, (DateTime Last, int Count)> _draws = []; // by client name

    /// <summary>Raised (from any thread) when an agent appears or disappears. Expiry by idleness raises nothing: poll <see cref="Active"/>.</summary>
    public event Action? Changed;

    /// <summary>A request arrived on this session. The client name is only known after the handshake, so a null name keeps the previous one.</summary>
    public void Touch(string sessionId, string? name)
    {
        bool changed;
        lock (_sessions)
        {
            var known = _sessions.TryGetValue(sessionId, out var s);
            var newName = string.IsNullOrWhiteSpace(name) ? known ? s.Name : "agent" : name.Trim();
            changed = !known || s.Name != newName || _now() - s.Seen > IdleAfter;
            _sessions[sessionId] = (newName, _now());
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>The agent applied a command to the document (author "llm:&lt;name&gt;").</summary>
    public void Drew(string name)
    {
        lock (_sessions) _draws[name] = (_now(), _draws.TryGetValue(name, out var d) ? d.Count + 1 : 1);
    }

    public void Remove(string sessionId)
    {
        bool removed;
        lock (_sessions) removed = _sessions.Remove(sessionId);
        if (removed) Changed?.Invoke();
    }

    /// <summary>Agents seen recently, one per client name (several sessions of the same client collapse), most recent first.</summary>
    public IReadOnlyList<AgentInfo> Active()
    {
        lock (_sessions)
        {
            var cutoff = _now() - IdleAfter;
            foreach (var id in _sessions.Where(kv => kv.Value.Seen < cutoff).Select(kv => kv.Key).ToList()) _sessions.Remove(id);
            return _sessions.Values.GroupBy(s => s.Name)
                .Select(g => new AgentInfo(g.Key, g.Max(s => s.Seen), g.Count(), _draws.TryGetValue(g.Key, out var d) ? d.Last : null, _draws.TryGetValue(g.Key, out d) ? d.Count : 0))
                .OrderByDescending(a => a.LastSeen).ToList();
        }
    }
}

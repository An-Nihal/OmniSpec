namespace OmniSpec.Core.Alerts;

public enum AlertSeverity { Info, Warning, Critical }

public sealed record AlertEvent(string Key, string Title, string Message, AlertSeverity Severity, DateTimeOffset Timestamp);

/// <summary>A condition observation fed into the alert engine on every evaluation.</summary>
public readonly record struct AlertCondition(string Key, bool IsActive, string Title, string Message, AlertSeverity Severity);

/// <summary>
/// De-duplicating alert engine. A condition must be continuously active for <c>sustain</c> before it fires,
/// fires once, and can only fire again after it has cleared (stayed inactive for the same period).
/// This prevents repeated notifications about the same unresolved problem.
/// </summary>
public sealed class AlertEngine
{
    private sealed class State
    {
        public DateTimeOffset? ActiveSince;
        public DateTimeOffset? InactiveSince;
        public bool Fired;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<AlertEvent> _log = new();

    public TimeSpan Sustain { get; set; } = TimeSpan.FromSeconds(10);

    public IReadOnlyList<AlertEvent> RecentAlerts
    {
        get { lock (_gate) return _log.ToList(); }
    }

    public IReadOnlyList<string> ActiveKeys
    {
        get { lock (_gate) return _states.Where(kv => kv.Value.Fired).Select(kv => kv.Key).ToList(); }
    }

    /// <summary>Evaluates a batch of conditions and returns alerts that should be shown now.</summary>
    public IReadOnlyList<AlertEvent> Evaluate(IEnumerable<AlertCondition> conditions, DateTimeOffset now)
    {
        var toFire = new List<AlertEvent>();
        lock (_gate)
        {
            foreach (var c in conditions)
            {
                if (!_states.TryGetValue(c.Key, out var s)) _states[c.Key] = s = new State();

                if (c.IsActive)
                {
                    s.InactiveSince = null;
                    s.ActiveSince ??= now;
                    if (!s.Fired && now - s.ActiveSince.Value >= Sustain)
                    {
                        s.Fired = true;
                        var evt = new AlertEvent(c.Key, c.Title, c.Message, c.Severity, now);
                        toFire.Add(evt);
                        _log.Add(evt);
                        if (_log.Count > 200) _log.RemoveAt(0);
                    }
                }
                else
                {
                    s.ActiveSince = null;
                    if (s.Fired)
                    {
                        s.InactiveSince ??= now;
                        if (now - s.InactiveSince.Value >= Sustain) { s.Fired = false; s.InactiveSince = null; }
                    }
                }
            }
        }
        return toFire;
    }

    public void Reset()
    {
        lock (_gate) { _states.Clear(); }
    }
}

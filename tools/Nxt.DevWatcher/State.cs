namespace Nxt.DevWatcher;

/// <summary>Shared state between the supervisor and the front-end proxy.</summary>
public sealed class State
{
    private long _epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public int BackendPort { get; init; }

    /// <summary>True when the supervisor has observed at least one successful "Now listening" log line
    /// and no failure has been observed since.</summary>
    public bool Healthy { get; private set; }

    /// <summary>Bumped any time the backend transitions states or restarts. The browser-side script
    /// reloads when it sees a new epoch.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Last error message (if any) — surfaced on the placeholder page.</summary>
    public string? LastError { get; private set; }

    public void MarkHealthy()
    {
        if (!Healthy) BumpEpoch();
        Healthy = true;
        LastError = null;
    }

    public void MarkDown(string? reason)
    {
        if (Healthy) BumpEpoch();
        Healthy = false;
        if (reason is not null) LastError = reason;
    }

    private void BumpEpoch() =>
        Interlocked.Exchange(ref _epoch, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

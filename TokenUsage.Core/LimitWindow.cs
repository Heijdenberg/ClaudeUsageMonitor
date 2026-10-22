namespace TokenUsage.Core;

/// <summary>One rate-limit window (five-hour or seven-day). Either value can be missing.</summary>
public sealed record LimitWindow(double? UsedPercentage, DateTimeOffset? ResetsAt)
{
    public static readonly LimitWindow Empty = new(null, null);

    /// <summary>
    /// Several Claude Code sessions report the same windows, each with its own last-received values.
    /// Never let an idle session's older numbers overwrite newer ones.
    /// </summary>
    public static LimitWindow Merge(LimitWindow incoming, LimitWindow? stored, DateTimeOffset now)
    {
        if (stored is not { UsedPercentage: double storedPct, ResetsAt: DateTimeOffset storedReset } || storedReset <= now)
            return incoming;
        if (incoming is not { UsedPercentage: double newPct, ResetsAt: DateTimeOffset newReset })
            return stored;

        var diff = (newReset - storedReset).TotalSeconds;
        if (diff > 60) return incoming;   // a new window started
        if (diff < -60) return stored;
        // Same window: usage only grows, so the higher value is the fresher one.
        return incoming with { UsedPercentage = Math.Max(newPct, storedPct) };
    }
}

namespace ClaudeUsage.Core;

/// <summary>Token counts by type. Thinking tokens are part of <see cref="Output"/>.</summary>
public sealed record TokenCounts(
    long Responses, long Input, long Output, long Thinking,
    long CacheRead, long CacheWrite5m, long CacheWrite1h, long WebSearches)
{
    public static readonly TokenCounts Zero = new(0, 0, 0, 0, 0, 0, 0, 0);

    public long CacheWrite => CacheWrite5m + CacheWrite1h;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Responses + b.Responses, a.Input + b.Input, a.Output + b.Output, a.Thinking + b.Thinking,
        a.CacheRead + b.CacheRead, a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h,
        a.WebSearches + b.WebSearches);
}

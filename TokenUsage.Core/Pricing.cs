namespace TokenUsage.Core;

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheWrite5m, double CacheWrite1h, double CacheRead);

/// <summary>
/// Anthropic API list prices, used to weigh token types against each other. This is what the
/// tokens would cost on the API, not what a subscription costs.
/// </summary>
public static class Pricing
{
    public const double WebSearchUsd = 0.01;   // $10 per 1,000 searches
    public const int HaikuLongPromptTokens = 100_000;

    private static readonly ModelPrice Fable51 = new(10, 50, 12.5, 20, 0.25);
    private static readonly ModelPrice Fable5 = new(10, 50, 12.5, 20, 1.0);
    private static readonly ModelPrice Opus55 = new(4, 20, 5, 8, 0.20);
    private static readonly ModelPrice Opus5 = new(5, 25, 6.25, 10, 0.50);
    private static readonly ModelPrice Sonnet5 = new(2, 10, 2.5, 4, 0.20);
    private static readonly ModelPrice Sonnet4 = new(3, 15, 3.75, 6, 0.30);
    private static readonly ModelPrice Haiku55 = new(0.10, 0.50, 0.125, 0.20, 0.01);
    // Prompts over 100K tokens. Only input/output are published; the cache rates assume the usual multipliers.
    private static readonly ModelPrice Haiku55Long = new(0.50, 2.50, 0.625, 1.0, 0.05);
    private static readonly ModelPrice Haiku45 = new(1, 5, 1.25, 2, 0.10);

    // Longest prefix first, so "claude-opus-5-5" wins over "claude-opus-5".
    private static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", Fable51), ("claude-mythos-5-1", Fable51),
        ("claude-fable-5", Fable5), ("claude-mythos-5", Fable5),
        ("claude-opus-5-5", Opus55),
        ("claude-opus-5", Opus5), ("claude-opus-4-8", Opus5), ("claude-opus-4-7", Opus5),
        ("claude-opus-4-6", Opus5), ("claude-opus-4-5", Opus5),
        ("claude-sonnet-5", Sonnet5),
        ("claude-sonnet-4", Sonnet4),
        ("claude-haiku-5-5", Haiku55),
        ("claude-haiku-4-5", Haiku45),
    ];

    public static ModelPrice? Find(string model, bool longPrompt)
    {
        foreach (var (prefix, price) in Table)
        {
            if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            return longPrompt && price == Haiku55 ? Haiku55Long : price;
        }
        return null;
    }

    /// <summary>API-equivalent cost in USD, or null for a model without a known price.</summary>
    public static double? Cost(string model, string? speed, bool longPrompt, TokenCounts t)
    {
        if (Find(model, longPrompt) is not ModelPrice p) return null;
        var tokens = t.Input * p.Input + t.Output * p.Output + t.CacheRead * p.CacheRead
            + t.CacheWrite5m * p.CacheWrite5m + t.CacheWrite1h * p.CacheWrite1h;
        var fast = string.Equals(speed, "fast", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return tokens * fast / 1_000_000 + t.WebSearches * WebSearchUsd;
    }
}

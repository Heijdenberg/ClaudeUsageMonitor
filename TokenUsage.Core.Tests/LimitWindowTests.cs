using TokenUsage.Core;

namespace TokenUsage.Core.Tests;

public class LimitWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Reset = Now.AddHours(3);

    [Fact]
    public void Lower_value_in_same_window_keeps_the_higher()
    {
        var merged = LimitWindow.Merge(new(30, Reset), new(42, Reset.AddSeconds(30)), Now);
        Assert.Equal(42, merged.UsedPercentage);
        Assert.Equal(Reset, merged.ResetsAt);
    }

    [Fact]
    public void Missing_values_keep_the_stored_window()
    {
        var stored = new LimitWindow(42, Reset);
        Assert.Same(stored, LimitWindow.Merge(LimitWindow.Empty, stored, Now));
        Assert.Same(stored, LimitWindow.Merge(new(50, null), stored, Now));
    }

    [Fact]
    public void A_later_reset_means_a_new_window()
    {
        var incoming = new LimitWindow(2, Reset.AddHours(5));
        Assert.Same(incoming, LimitWindow.Merge(incoming, new(90, Reset), Now));
    }

    [Fact]
    public void An_idle_session_from_the_previous_window_is_ignored()
    {
        var stored = new LimitWindow(2, Reset.AddHours(5));
        Assert.Same(stored, LimitWindow.Merge(new(90, Reset), stored, Now));
    }

    [Fact]
    public void An_expired_stored_window_is_replaced()
    {
        var incoming = new LimitWindow(5, Reset);
        Assert.Same(incoming, LimitWindow.Merge(incoming, new(90, Now.AddMinutes(-1)), Now));
        Assert.Same(LimitWindow.Empty, LimitWindow.Merge(LimitWindow.Empty, new(90, Now.AddMinutes(-1)), Now));
    }
}

public class StatusLineTextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Formats_both_windows()
    {
        var input = StatusInput.Parse("""
            {"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":1800008100},
                            "seven_day":{"used_percentage":10.6,"resets_at":1800273600}}}
            """);
        Assert.Equal("5h: 42% (återställs om 2h 15m) | 7d: 11% (återställs om 3d 4h)", StatusLineText.Format(input, Now));
    }

    [Fact]
    public void Missing_data_waits()
    {
        Assert.Equal("5h: inväntar data | 7d: inväntar data", StatusLineText.Format(StatusInput.Parse("{}"), Now));
        Assert.Equal("5h: inväntar data | 7d: inväntar data", StatusLineText.Format(StatusInput.Parse("not json"), Now));
    }

    [Fact]
    public void Minutes_round_up_and_missing_reset_is_shown()
    {
        Assert.Equal("5h: 3% (återställs om 1m)", StatusLineText.FormatWindow("5h", new(3, Now.AddSeconds(5)), Now));
        Assert.Equal("5h: 3% (återställning: inväntar data)", StatusLineText.FormatWindow("5h", new(3, null), Now));
    }

    [Fact]
    public void Reads_transcript_path()
    {
        Assert.Equal(@"C:\x\s.jsonl", StatusInput.Parse("""{"transcript_path":"C:\\x\\s.jsonl"}""").TranscriptPath);
    }
}

public class PricingTests
{
    [Fact]
    public void Opus_55_weighs_each_token_type()
    {
        // The response measured in this project: in=2, out=1140, cache read 70613, 1h cache write 1530.
        var cost = Pricing.Cost("claude-opus-5-5", "standard", false, new TokenCounts(1, 2, 1140, 241, 70613, 0, 1530, 0));
        Assert.Equal((2 * 4 + 1140 * 20 + 70613 * 0.20 + 1530 * 8) / 1e6, cost!.Value, 9);
    }

    [Fact]
    public void Prefixes_pick_the_most_specific_model()
    {
        Assert.Equal(4, Pricing.Find("claude-opus-5-5", false)!.Input);
        Assert.Equal(5, Pricing.Find("claude-opus-5", false)!.Input);
        Assert.Equal(0.25, Pricing.Find("claude-fable-5-1", false)!.CacheRead);
        Assert.Equal(1.0, Pricing.Find("claude-fable-5", false)!.CacheRead);
        Assert.Equal(0.50, Pricing.Find("claude-haiku-5-5", true)!.Input);
        Assert.Null(Pricing.Find("gpt-something", false));
    }

    [Fact]
    public void Fast_mode_doubles_and_web_searches_add()
    {
        var t = new TokenCounts(1, 1_000_000, 0, 0, 0, 0, 0, 2);
        Assert.Equal(8.02, Pricing.Cost("claude-opus-5-5", "fast", false, t)!.Value, 9);
    }
}

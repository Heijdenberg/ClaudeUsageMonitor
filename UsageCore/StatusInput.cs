using System.Text.Json;

namespace UsageCore;

/// <summary>The parts of Claude Code's status line JSON (stdin) that we use.</summary>
public sealed record StatusInput(LimitWindow FiveHour, LimitWindow SevenDay, string? TranscriptPath)
{
    public static readonly StatusInput Empty = new(LimitWindow.Empty, LimitWindow.Empty, null);

    public static StatusInput Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var five = LimitWindow.Empty;
            var week = LimitWindow.Empty;
            if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                five = ReadWindow(rl, "five_hour");
                week = ReadWindow(rl, "seven_day");
            }
            return new StatusInput(five, week, Json.String(root, "transcript_path"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static LimitWindow ReadWindow(JsonElement rateLimits, string name)
    {
        if (!rateLimits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return LimitWindow.Empty;
        var pct = Json.Number(w, "used_percentage");
        var reset = Json.Number(w, "resets_at") is double s ? DateTimeOffset.FromUnixTimeSeconds((long)s) : (DateTimeOffset?)null;
        return new LimitWindow(pct, reset);
    }
}

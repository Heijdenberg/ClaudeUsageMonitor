using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsage.Core;

/// <summary>Short labels and origins for prompts, from user records in a session log.</summary>
internal static partial class PromptLabel
{
    public const string Human = "human";
    public const string Task = "task";       // a background agent reporting back
    public const string Compact = "compact"; // conversation summary after compaction

    private const int MaxLength = 120;

    /// <summary>
    /// The label for a user record, and whether it is a message (text or images) rather than tool results.
    /// </summary>
    public static (string Label, bool IsMessage) FromMessage(JsonElement record)
    {
        var message = Json.Object(record, "message");
        if (!message.TryGetProperty("content", out var content)) return ("", false);
        if (content.ValueKind == JsonValueKind.String) return (FromText(content.GetString() ?? ""), true);
        if (content.ValueKind != JsonValueKind.Array) return ("", false);

        string? text = null;
        var images = 0;
        foreach (var block in content.EnumerateArray())
        {
            switch (Json.String(block, "type"))
            {
                case "tool_result": return ("", false);
                case "text": text ??= Json.String(block, "text"); break;
                case "image": images++; break;
            }
        }
        if (text is not null) return (FromText(text), true);
        return (images > 0 ? "[bild]" : "", images > 0);
    }

    public static string Origin(JsonElement record, string label)
    {
        if (Json.True(record, "isCompactSummary")) return Compact;
        var turnOrigin = Json.String(record, "turnOrigin") ?? Json.String(Json.Object(record, "origin"), "kind");
        if (turnOrigin is "task_notification" or "task-notification" || label.StartsWith("[bakgrundsagent", StringComparison.Ordinal))
            return Task;
        return Human;
    }

    internal static string FromText(string text)
    {
        if (CommandName().Match(text) is { Success: true } cmd)
        {
            var args = CommandArgs().Match(text) is { Success: true } a ? " " + a.Groups[1].Value.Trim() : "";
            return Shorten(cmd.Groups[1].Value.Trim() + args);
        }
        if (text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            var summary = Summary().Match(text) is { Success: true } s ? ": " + s.Groups[1].Value.Trim() : "";
            return Shorten("[bakgrundsagent klar]" + summary);
        }
        var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Shorten(firstLine);
    }

    private static string Shorten(string s)
    {
        s = Whitespace().Replace(s, " ");
        return s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
    }

    [GeneratedRegex(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex Summary();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

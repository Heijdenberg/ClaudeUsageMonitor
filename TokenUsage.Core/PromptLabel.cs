using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenUsage.Core;

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

    /// <summary>"/workflow" for a slash command, without its arguments; null for anything else.</summary>
    public static string? CommandName(JsonElement record)
    {
        var content = Json.Object(record, "message").TryGetProperty("content", out var c) ? c : default;
        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => content.EnumerateArray().Where(b => Json.String(b, "type") == "text")
                .Select(b => Json.String(b, "text")).FirstOrDefault(),
            _ => null,
        };
        return text is not null && CommandName().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Reads a message's label from Claude Code's log at the stored position. Null when the log has been
    /// removed or no longer holds that message there.
    /// </summary>
    public static string? ReadFromLog(string path, long offset, string promptId) =>
        ReadRecord(path, offset, promptId, record => FromMessage(record) is (var label, true) ? label : null);

    /// <summary>The whole message as you wrote it, read from the log the same way; "/cmd args" for a slash command.</summary>
    public static string? ReadTextFromLog(string path, long offset, string promptId) =>
        ReadRecord(path, offset, promptId, record => FromMessage(record).IsMessage ? FullText(record) : null);

    private static string? ReadRecord(string path, long offset, string promptId, Func<JsonElement, string?> read)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset >= stream.Length) return null;
            stream.Position = offset;
            var line = new MemoryStream();
            int b;
            while ((b = stream.ReadByte()) is not (-1 or '\n')) line.WriteByte((byte)b);
            using var doc = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
            if (Json.String(doc.RootElement, "promptId") != promptId) return null;
            return read(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string? FullText(JsonElement record)
    {
        var content = Json.Object(record, "message").GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : string.Join("\n\n", content.EnumerateArray().Where(b => Json.String(b, "type") == "text").Select(b => Json.String(b, "text")));
        if (CommandName().Match(text) is { Success: true } cmd)
        {
            var args = CommandArgs().Match(text) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
            return args.Length > 0 ? cmd.Groups[1].Value.Trim() + " " + args : cmd.Groups[1].Value.Trim();
        }
        return text.Trim() is { Length: > 0 } t ? t : null;
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

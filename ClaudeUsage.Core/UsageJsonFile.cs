using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClaudeUsage.Core;

/// <summary>
/// usage.json: timestamp, percentage and reset time for both windows. Kept alongside the database
/// so older readers keep working. Contains no other session data.
/// </summary>
public static class UsageJsonFile
{
    public static void Update(string path, StatusInput input, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (oldFive, oldWeek) = Read(path);
        var five = LimitWindow.Merge(input.FiveHour, oldFive, now);
        var week = LimitWindow.Merge(input.SevenDay, oldWeek, now);

        // Write to a per-process temp file and swap it in, since several sessions write the same file.
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = File.Create(tmp))
            // Relaxed escaping keeps "+02:00" readable instead of "+02:00".
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("timestamp", now.ToLocalTime().ToString("o"));
                WriteWindow(w, "five_hour", five);
                WriteWindow(w, "seven_day", week);
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static (LimitWindow? FiveHour, LimitWindow? SevenDay) Read(string path)
    {
        try
        {
            // Share delete/write so another session can replace the file while we read it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return (ReadWindow(doc.RootElement, "five_hour"), ReadWindow(doc.RootElement, "seven_day"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static LimitWindow? ReadWindow(JsonElement root, string name)
    {
        var w = Json.Object(root, name);
        return w.ValueKind == JsonValueKind.Object ? new LimitWindow(Json.Number(w, "used_percentage"), Json.Date(w, "resets_at")) : null;
    }

    private static void WriteWindow(Utf8JsonWriter w, string name, LimitWindow window)
    {
        w.WriteStartObject(name);
        if (window.UsedPercentage is double pct) w.WriteNumber("used_percentage", pct);
        else w.WriteNull("used_percentage");
        if (window.ResetsAt is DateTimeOffset reset) w.WriteString("resets_at", reset.ToLocalTime().ToString("o"));
        else w.WriteNull("resets_at");
        w.WriteEndObject();
    }
}

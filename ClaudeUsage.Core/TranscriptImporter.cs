using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeUsage.Core;

/// <summary>
/// Reads token usage from Claude Code's session logs into the database:
/// projects\&lt;project&gt;\&lt;session&gt;.jsonl and projects\&lt;project&gt;\&lt;session&gt;\subagents\agent-&lt;id&gt;.jsonl (+ .meta.json).
/// Each log is read from where the previous import stopped, so repeated runs only parse new lines.
/// </summary>
public sealed class TranscriptImporter(UsageDatabase db)
{
    /// <summary>Imports a session's main log and its subagent logs.</summary>
    public void ImportSession(string transcriptPath, long maxBytesPerFile = long.MaxValue)
    {
        ImportFile(transcriptPath, maxBytesPerFile);
        var subagents = Path.Combine(Path.ChangeExtension(transcriptPath, null), "subagents");
        if (!Directory.Exists(subagents)) return;
        foreach (var file in Directory.EnumerateFiles(subagents, "*.jsonl"))
            ImportFile(file, maxBytesPerFile);
    }

    /// <summary>Imports every log under the projects directory that has grown since it was last read.</summary>
    public void ImportAll(string projectsDir, long maxBytesPerFile = long.MaxValue)
    {
        if (!Directory.Exists(projectsDir)) return;
        var offsets = LoadOffsets();
        var files = new DirectoryInfo(projectsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (offsets.TryGetValue(Key(file.FullName), out var offset) && offset == file.Length) continue;
            ImportFile(file.FullName, maxBytesPerFile);
        }
    }

    /// <summary>Runs <see cref="ImportAll"/> at most once per interval across all processes.</summary>
    public void ImportAllIfDue(string projectsDir, TimeSpan interval, DateTimeOffset now, long maxBytesPerFile)
    {
        using (var tx = db.Begin())
        {
            var last = long.TryParse(db.GetMeta("last_full_scan", tx), out var ms) ? ms : 0;
            if (now.ToUnixTimeMilliseconds() - last < interval.TotalMilliseconds) return;
            db.SetMeta("last_full_scan", now.ToUnixTimeMilliseconds().ToString(), tx);
            tx.Commit();
        }
        ImportAll(projectsDir, maxBytesPerFile);
    }

    public void ImportFile(string path, long maxBytes = long.MaxValue)
    {
        var source = LogSource.FromPath(path);
        if (source is null) return;
        var key = Key(path);

        using var tx = db.Begin();
        var (offset, promptId) = ReadOffset(key, tx);

        byte[] chunk;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < offset) (offset, promptId) = (0, null);   // the file was replaced
            if (stream.Length == offset) return;
            stream.Position = offset;
            chunk = new byte[(int)Math.Min(stream.Length - offset, Math.Min(maxBytes, int.MaxValue - 1))];
            stream.ReadExactly(chunk);
            // A single line longer than the limit: read on to its end so the import can't get stuck.
            if (Array.LastIndexOf(chunk, (byte)'\n') < 0 && stream.Position < stream.Length)
            {
                var rest = new byte[stream.Length - stream.Position];
                stream.ReadExactly(rest);
                chunk = [.. chunk, .. rest];
            }
        }

        // Only whole lines; a line still being written is read next time.
        var end = Array.LastIndexOf(chunk, (byte)'\n') + 1;
        if (end == 0) return;

        if (source.AgentId is not null) UpsertAgent(source, path, tx);
        UpsertSession(source, tx);

        var logPath = Path.GetFullPath(path);
        var start = 0;
        while (start < end)
        {
            var nl = Array.IndexOf(chunk, (byte)'\n', start, end - start);
            var line = new ReadOnlyMemory<byte>(chunk, start, nl - start);
            var position = new LogPosition(logPath, offset + start);
            start = nl + 1;
            if (line.Length > 1) promptId = ProcessLine(line, source, position, promptId, tx);
        }

        using (var cmd = db.Command("""
            INSERT INTO log_files (path, offset, prompt_id) VALUES ($p, $o, $prompt)
            ON CONFLICT (path) DO UPDATE SET offset = $o, prompt_id = $prompt
            """, tx))
        {
            cmd.Parameters.AddWithValue("$p", key);
            cmd.Parameters.AddWithValue("$o", offset + end);
            cmd.Parameters.AddWithValue("$prompt", (object?)promptId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Handles one log record and returns the prompt in effect after it.</summary>
    private string? ProcessLine(ReadOnlyMemory<byte> line, LogSource source, LogPosition position, string? promptId, SqliteTransaction tx)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return promptId; }

        using (doc)
        {
            var r = doc.RootElement;
            switch (Json.String(r, "type"))
            {
                case "user":
                    // Every user record carries the id of the message you sent, including tool results
                    // and answers given through AskUserQuestion, and subagent records carry their parent's.
                    if (Json.String(r, "promptId") is string id)
                    {
                        promptId = id;
                        if (source.AgentId is null && !Json.True(r, "isMeta") && PromptLabel.FromMessage(r) is var (label, isText) && isText)
                            InsertPrompt(id, source.SessionId, r, label, position, tx);
                    }
                    break;
                case "assistant":
                    InsertResponse(r, source, promptId, tx);
                    break;
                case "custom-title":
                    SetTitle("title", Json.String(r, "customTitle"), source, tx);
                    break;
                case "ai-title":
                    SetTitle("ai_title", Json.String(r, "aiTitle"), source, tx);
                    break;
            }
        }
        return promptId;
    }

    private void SetTitle(string column, string? title, LogSource source, SqliteTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        using var cmd = db.Command($"UPDATE sessions SET {column} = $t WHERE session_id = $s", tx);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$s", source.SessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a message you sent. The label is only used to classify it here; the database keeps
    /// where the text is in the log, plus the command name for slash commands.
    /// </summary>
    private void InsertPrompt(string promptId, string sessionId, JsonElement r, string label, LogPosition position, SqliteTransaction tx)
    {
        using var cmd = db.Command("""
            INSERT OR IGNORE INTO prompts (prompt_id, session_id, at, origin, command, log_path, log_offset)
            VALUES ($id, $s, $at, $o, $c, $p, $off)
            """, tx);
        cmd.Parameters.AddWithValue("$id", promptId);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$at", (Json.Date(r, "timestamp") ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$o", PromptLabel.Origin(r, label));
        cmd.Parameters.AddWithValue("$c", (object?)PromptLabel.CommandName(r) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", position.Path);
        cmd.Parameters.AddWithValue("$off", position.Offset);
        cmd.ExecuteNonQuery();
    }

    private void InsertResponse(JsonElement r, LogSource source, string? promptId, SqliteTransaction tx)
    {
        var message = Json.Object(r, "message");
        var usage = Json.Object(message, "usage");
        if (usage.ValueKind != JsonValueKind.Object || Json.String(message, "id") is not string id) return;
        if (Json.String(message, "model") is not string model || model.StartsWith('<')) return;   // "<synthetic>"

        // A response with several content blocks is logged as several records with the same id and usage.
        var cacheWrite = Json.Long(usage, "cache_creation_input_tokens");
        var split = Json.Object(usage, "cache_creation");
        var write1h = Json.Long(split, "ephemeral_1h_input_tokens");
        var write5m = split.ValueKind == JsonValueKind.Object ? Json.Long(split, "ephemeral_5m_input_tokens") : cacheWrite;

        using var cmd = db.Command("""
            INSERT INTO responses (message_id, session_id, prompt_id, agent_id, at, model, speed, input_tokens, output_tokens,
                thinking_tokens, cache_read_tokens, cache_write_5m_tokens, cache_write_1h_tokens, web_search_requests)
            VALUES ($id, $s, $prompt, $agent, $at, $model, $speed, $in, $out, $think, $cr, $cw5, $cw1, $ws)
            ON CONFLICT (message_id) DO UPDATE SET
                prompt_id = coalesce(responses.prompt_id, excluded.prompt_id),
                input_tokens = excluded.input_tokens, output_tokens = excluded.output_tokens,
                thinking_tokens = excluded.thinking_tokens, cache_read_tokens = excluded.cache_read_tokens,
                cache_write_5m_tokens = excluded.cache_write_5m_tokens, cache_write_1h_tokens = excluded.cache_write_1h_tokens,
                web_search_requests = excluded.web_search_requests
            WHERE excluded.output_tokens >= responses.output_tokens
            """, tx);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", source.SessionId);
        cmd.Parameters.AddWithValue("$prompt", (object?)promptId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", (object?)source.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (Json.Date(r, "timestamp") ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$speed", (object?)Json.String(usage, "speed") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$in", Json.Long(usage, "input_tokens"));
        cmd.Parameters.AddWithValue("$out", Json.Long(usage, "output_tokens"));
        cmd.Parameters.AddWithValue("$think", Json.Long(Json.Object(usage, "output_tokens_details"), "thinking_tokens"));
        cmd.Parameters.AddWithValue("$cr", Json.Long(usage, "cache_read_input_tokens"));
        cmd.Parameters.AddWithValue("$cw5", write5m);
        cmd.Parameters.AddWithValue("$cw1", write1h);
        cmd.Parameters.AddWithValue("$ws", Json.Long(Json.Object(usage, "server_tool_use"), "web_search_requests"));
        cmd.ExecuteNonQuery();
    }

    private void UpsertSession(LogSource source, SqliteTransaction tx)
    {
        using var cmd = db.Command("INSERT OR IGNORE INTO sessions (session_id, project) VALUES ($s, $p)", tx);
        cmd.Parameters.AddWithValue("$s", source.SessionId);
        cmd.Parameters.AddWithValue("$p", source.Project);
        cmd.ExecuteNonQuery();
    }

    private void UpsertAgent(LogSource source, string path, SqliteTransaction tx)
    {
        string? type = null, description = null, model = null, toolUseId = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(path, ".meta.json")));
            var m = doc.RootElement;
            (type, description, model, toolUseId) =
                (Json.String(m, "agentType"), Json.String(m, "description"), Json.String(m, "model"), Json.String(m, "toolUseId"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        using var cmd = db.Command("""
            INSERT INTO agents (agent_id, session_id, agent_type, description, model, tool_use_id) VALUES ($a, $s, $t, $d, $m, $u)
            ON CONFLICT (agent_id) DO UPDATE SET agent_type = coalesce($t, agent_type), description = coalesce($d, description),
                model = coalesce($m, model), tool_use_id = coalesce($u, tool_use_id)
            """, tx);
        cmd.Parameters.AddWithValue("$a", source.AgentId);
        cmd.Parameters.AddWithValue("$s", source.SessionId);
        cmd.Parameters.AddWithValue("$t", (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", (object?)toolUseId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private (long Offset, string? PromptId) ReadOffset(string key, SqliteTransaction tx)
    {
        using var cmd = db.Command("SELECT offset, prompt_id FROM log_files WHERE path = $p", tx);
        cmd.Parameters.AddWithValue("$p", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)) : (0, null);
    }

    private Dictionary<string, long> LoadOffsets()
    {
        var result = new Dictionary<string, long>();
        using var cmd = db.Command("SELECT path, offset FROM log_files");
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);
        return result;
    }

    private static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();

    internal readonly record struct LogPosition(string Path, long Offset);

    /// <summary>Which session (and subagent) a log file belongs to, from its location.</summary>
    internal sealed record LogSource(string Project, string SessionId, string? AgentId)
    {
        public static LogSource? FromPath(string path)
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full)!;
            var name = Path.GetFileNameWithoutExtension(full);
            if (Path.GetFileName(dir).Equals("subagents", StringComparison.OrdinalIgnoreCase))
            {
                var sessionDir = Path.GetDirectoryName(dir)!;
                if (!name.StartsWith("agent-", StringComparison.Ordinal)) return null;
                return new LogSource(Path.GetFileName(Path.GetDirectoryName(sessionDir)!), Path.GetFileName(sessionDir), name["agent-".Length..]);
            }
            return Guid.TryParse(name, out _) ? new LogSource(Path.GetFileName(dir), name, null) : null;
        }
    }
}

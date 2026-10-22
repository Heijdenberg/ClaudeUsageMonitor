namespace TokenUsage.Core;

/// <summary>Tokens and API-equivalent cost for one agent (or the main session) and model within a run.</summary>
public sealed record AgentUsage(string? AgentId, string Name, string Model, TokenCounts Tokens, double? CostUsd);

/// <summary>
/// One run: a message you sent plus everything it triggered, including subagents, answers given
/// through AskUserQuestion and background agents reporting back, until your next message.
/// </summary>
public sealed record RunSummary(
    string SessionId, string? SessionTitle, string? PromptId, string Label,
    DateTimeOffset Start, DateTimeOffset End, TokenCounts Tokens, double? CostUsd,
    IReadOnlyList<AgentUsage> Agents);

public static class RunReport
{
    private const string MainSession = "Huvudsession";

    /// <summary>Runs with responses since the given time, newest first.</summary>
    public static IReadOnlyList<RunSummary> Load(UsageDatabase db, DateTimeOffset since)
    {
        var groups = LoadGroups(db, since);
        if (groups.Count == 0) return [];

        var sessions = groups.Select(g => g.SessionId).ToHashSet();
        // The prompt that starts each run: the message you sent before any background-agent notifications.
        var headOf = new Dictionary<string, Prompt>();
        foreach (var session in sessions)
        {
            Prompt? head = null;
            foreach (var p in LoadPrompts(db, session))
            {
                if (p.Origin == PromptLabel.Human || head is null) head = p;
                headOf[p.PromptId] = head;
            }
        }

        var titles = LoadSessionTitles(db, sessions);
        var agentNames = LoadAgentNames(db, sessions);

        return groups
            .GroupBy(g => g.PromptId is not null && headOf.TryGetValue(g.PromptId, out var head)
                ? (g.SessionId, RunKey: head.PromptId) : (g.SessionId, RunKey: "?"))
            .Select(run =>
            {
                var head = headOf.GetValueOrDefault(run.Key.RunKey);
                var agents = run
                    .GroupBy(g => (g.AgentId, g.Model))
                    .Select(a => new AgentUsage(
                        a.Key.AgentId,
                        a.Key.AgentId is null ? MainSession : agentNames.GetValueOrDefault(a.Key.AgentId, "Underagent " + a.Key.AgentId),
                        a.Key.Model,
                        a.Aggregate(TokenCounts.Zero, (sum, g) => sum + g.Tokens),
                        SumCost(a)))
                    .OrderBy(a => a.AgentId is null ? 0 : 1).ThenByDescending(a => a.CostUsd ?? 0)
                    .ToList();
                return new RunSummary(
                    run.Key.SessionId, titles.GetValueOrDefault(run.Key.SessionId),
                    head?.PromptId,
                    head is null ? "(utan prompt)" : LabelOf(head),
                    run.Min(g => g.First), run.Max(g => g.Last),
                    run.Aggregate(TokenCounts.Zero, (sum, g) => sum + g.Tokens), SumCost(run), agents);
            })
            .OrderByDescending(r => r.Start)
            .ToList();
    }

    /// <summary>Tokens and API-equivalent cost of all responses since the given time.</summary>
    public static (TokenCounts Tokens, double? CostUsd) Totals(UsageDatabase db, DateTimeOffset since)
    {
        var groups = LoadGroups(db, since);
        return (groups.Aggregate(TokenCounts.Zero, (sum, g) => sum + g.Tokens), SumCost(groups));
    }

    // Unknown models count as zero; the cost is null only when nothing at all could be priced.
    private static double? SumCost(IEnumerable<Group> groups)
    {
        double? total = null;
        foreach (var g in groups)
            if (g.CostUsd is double c) total = (total ?? 0) + c;
        return total;
    }

    private sealed record Group(
        string SessionId, string? PromptId, string? AgentId, string Model,
        TokenCounts Tokens, double? CostUsd, DateTimeOffset First, DateTimeOffset Last);

    private static List<Group> LoadGroups(UsageDatabase db, DateTimeOffset since)
    {
        // Grouped by prompt length too, since Haiku 5.5 has a separate price above 100K tokens.
        using var cmd = db.Command($"""
            SELECT session_id, prompt_id, agent_id, model, speed,
                   (input_tokens + cache_read_tokens + cache_write_5m_tokens + cache_write_1h_tokens) > {Pricing.HaikuLongPromptTokens} AS long_prompt,
                   count(*), sum(input_tokens), sum(output_tokens), sum(thinking_tokens), sum(cache_read_tokens),
                   sum(cache_write_5m_tokens), sum(cache_write_1h_tokens), sum(web_search_requests), min(at), max(at)
            FROM responses WHERE at >= $since
            GROUP BY session_id, prompt_id, agent_id, model, speed, long_prompt
            """);
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var result = new List<Group>();
        while (r.Read())
        {
            var tokens = new TokenCounts(r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
                r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13));
            var model = r.GetString(3);
            var speed = r.IsDBNull(4) ? null : r.GetString(4);
            result.Add(new Group(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), model,
                tokens, Pricing.Cost(model, speed, r.GetBoolean(5), tokens),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(14)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(15))));
        }
        return result;
    }

    private sealed record Prompt(string PromptId, string Origin, string? Command, string? LogPath, long LogOffset);

    private static List<Prompt> LoadPrompts(UsageDatabase db, string sessionId)
    {
        using var cmd = db.Command("SELECT prompt_id, origin, command, log_path, log_offset FROM prompts WHERE session_id = $s ORDER BY at, rowid");
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var r = cmd.ExecuteReader();
        var result = new List<Prompt>();
        while (r.Read())
            result.Add(new Prompt(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt64(4)));
        return result;
    }

    // Labels read from the logs; a message's text never changes, so one read per process is enough.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Labels = new();

    /// <summary>
    /// The first line of the message, read from Claude Code's log (the database holds no text). Once the
    /// log has been cleaned up, only a slash command's name is left.
    /// </summary>
    private static string LabelOf(Prompt p)
    {
        var key = $"{p.LogPath}|{p.LogOffset}|{p.PromptId}";
        if (Labels.TryGetValue(key, out var cached)) return cached;
        if (p.LogPath is not null && PromptLabel.ReadFromLog(p.LogPath, p.LogOffset, p.PromptId) is string label)
            return Labels[key] = label;
        return p.Command is null ? "(texten finns inte längre i loggen)" : $"{p.Command} (texten finns inte längre i loggen)";
    }

    private static Dictionary<string, string> LoadSessionTitles(UsageDatabase db, HashSet<string> sessions)
    {
        var result = new Dictionary<string, string>();
        // Your own name wins over the one Claude Code generates.
        using var cmd = db.Command("SELECT session_id, coalesce(title, ai_title) FROM sessions WHERE coalesce(title, ai_title) IS NOT NULL");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (sessions.Contains(r.GetString(0))) result[r.GetString(0)] = r.GetString(1);
        return result;
    }

    private static Dictionary<string, string> LoadAgentNames(UsageDatabase db, HashSet<string> sessions)
    {
        var result = new Dictionary<string, string>();
        using var cmd = db.Command("SELECT agent_id, session_id, agent_type, description FROM agents");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!sessions.Contains(r.GetString(1))) continue;
            var type = r.IsDBNull(2) ? "underagent" : r.GetString(2);
            result[r.GetString(0)] = r.IsDBNull(3) ? type : $"{type}: {r.GetString(3)}";
        }
        return result;
    }
}

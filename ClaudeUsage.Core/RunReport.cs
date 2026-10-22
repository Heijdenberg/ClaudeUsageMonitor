namespace ClaudeUsage.Core;

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
        var runOfPrompt = new Dictionary<string, (string RunKey, string Label, DateTimeOffset At)>();
        foreach (var session in sessions)
        {
            (string Key, string Label, DateTimeOffset At)? current = null;
            foreach (var p in LoadPrompts(db, session))
            {
                if (p.Origin == PromptLabel.Human || current is null) current = (p.PromptId, p.Label, p.At);
                runOfPrompt[p.PromptId] = current.Value;
            }
        }

        var titles = LoadSessionTitles(db, sessions);
        var agentNames = LoadAgentNames(db, sessions);

        return groups
            .GroupBy(g => g.PromptId is not null && runOfPrompt.TryGetValue(g.PromptId, out var run)
                ? (g.SessionId, run.RunKey) : (g.SessionId, RunKey: "?"))
            .Select(run =>
            {
                var first = run.First();
                runOfPrompt.TryGetValue(run.Key.RunKey, out var head);
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
                    run.Key.RunKey == "?" ? null : run.Key.RunKey,
                    run.Key.RunKey == "?" ? "(utan prompt)" : head.Label,
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

    private sealed record Prompt(string PromptId, string Origin, string Label, DateTimeOffset At);

    private static List<Prompt> LoadPrompts(UsageDatabase db, string sessionId)
    {
        using var cmd = db.Command("SELECT prompt_id, origin, coalesce(label, ''), at FROM prompts WHERE session_id = $s ORDER BY at, rowid");
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var r = cmd.ExecuteReader();
        var result = new List<Prompt>();
        while (r.Read())
            result.Add(new Prompt(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3))));
        return result;
    }

    private static Dictionary<string, string> LoadSessionTitles(UsageDatabase db, HashSet<string> sessions)
    {
        var result = new Dictionary<string, string>();
        using var cmd = db.Command("SELECT session_id, title FROM sessions WHERE title IS NOT NULL");
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

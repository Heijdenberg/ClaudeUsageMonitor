using TokenUsage.Core;

namespace TokenUsage.Core.Tests;

public sealed class ImporterTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects;
    private readonly string _mainLog;
    private readonly string _agentLog;
    private readonly UsageDatabase _db;

    public ImporterTests()
    {
        _projects = Path.Combine(_root, "projects");
        var project = Path.Combine(_projects, "C--code");
        _mainLog = Path.Combine(project, Session + ".jsonl");
        var subagents = Path.Combine(project, Session, "subagents");
        Directory.CreateDirectory(subagents);
        _agentLog = Path.Combine(subagents, "agent-abc123.jsonl");
        File.WriteAllText(Path.Combine(subagents, "agent-abc123.meta.json"),
            """{"agentType":"planner","description":"Plan the change","toolUseId":"toolu_1","model":"opus"}""");
        _db = UsageDatabase.OpenForWriting(Path.Combine(_root, "token-usage.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string User(string promptId, string content, string at, string extra = "") =>
        $$"""{"type":"user","promptId":"{{promptId}}","timestamp":"{{at}}","message":{"role":"user","content":{{content}}}{{extra}}}""";

    private static string Assistant(string id, string model, int output, string at) =>
        """{"type":"assistant","timestamp":"@AT","message":{"id":"@ID","model":"@MODEL","usage":{"input_tokens":2,"output_tokens":@OUT,"cache_read_input_tokens":1000,"cache_creation_input_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":200},"output_tokens_details":{"thinking_tokens":10},"server_tool_use":{"web_search_requests":1}}}}"""
            .Replace("@AT", at).Replace("@ID", id).Replace("@MODEL", model).Replace("@OUT", output.ToString());

    private const string ToolResult = """[{"type":"tool_result","tool_use_id":"toolu_q","content":"Svar: Ja"}]""";

    private void WriteFlow()
    {
        File.WriteAllLines(_mainLog,
        [
            User("P1", "\"<command-message>workflow</command-message>\\n<command-name>/workflow</command-name>\\n<command-args>bygg X</command-args>\"",
                "2026-10-22T08:00:00Z", ""","turnOrigin":"human" """),
            Assistant("m1", "claude-opus-5-5", 100, "2026-10-22T08:00:05Z"),
            Assistant("m1", "claude-opus-5-5", 100, "2026-10-22T08:00:05Z"),   // same response, second content block
            User("P1", ToolResult, "2026-10-22T08:01:00Z"),                       // AskUserQuestion answer
            Assistant("m2", "claude-opus-5-5", 50, "2026-10-22T08:01:05Z"),
            User("P2", "\"<task-notification><summary>Agent klar</summary></task-notification>\"", "2026-10-22T08:05:00Z",
                ""","origin":{"kind":"task-notification"},"turnOrigin":"task_notification" """),
            Assistant("m3", "claude-opus-5-5", 20, "2026-10-22T08:05:05Z"),
            User("P3", "\"Nästa uppgift\\nmed fler rader\"", "2026-10-22T09:00:00Z", ""","turnOrigin":"human" """),
            Assistant("m4", "claude-haiku-5-5", 10, "2026-10-22T09:00:05Z"),
            $$"""{"type":"ai-title","aiTitle":"Automatiskt namn","sessionId":"{{Session}}"}""",
            $$"""{"type":"custom-title","customTitle":"Mitt flöde","sessionId":"{{Session}}"}""",
        ]);
        File.WriteAllLines(_agentLog,
        [
            User("P1", "\"Planera\"", "2026-10-22T08:00:10Z", ""","isSidechain":true,"agentId":"abc123" """),
            Assistant("s1", "claude-opus-5-5", 400, "2026-10-22T08:00:20Z"),
        ]);
    }

    private long Count(string sql)
    {
        using var cmd = _db.Command(sql);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Groups_a_flow_into_runs_with_subagents_answers_and_notifications()
    {
        WriteFlow();
        new TranscriptImporter(_db).ImportAll(_projects);

        Assert.Equal(5, Count("SELECT count(*) FROM responses"));   // m1 counted once
        var runs = RunReport.Load(_db, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, runs.Count);

        var second = runs[0];
        Assert.Equal("Nästa uppgift", second.Label);
        Assert.Equal("Nästa uppgift\nmed fler rader", second.Prompt!.ReadText());
        Assert.Equal(1, second.Tokens.Responses);

        var flow = runs[1];
        Assert.Equal("/workflow bygg X", flow.Label);
        Assert.Equal("/workflow bygg X", flow.Prompt!.ReadText());
        Assert.Equal("Mitt flöde", flow.SessionTitle);
        Assert.Equal(4, flow.Tokens.Responses);                      // m1, m2, m3 and the subagent's s1
        Assert.Equal(100 + 50 + 20 + 400, flow.Tokens.Output);
        Assert.Equal(40, flow.Tokens.Thinking);
        Assert.Equal(4 * 100, flow.Tokens.CacheWrite5m);
        Assert.Equal(4 * 200, flow.Tokens.CacheWrite1h);
        Assert.Equal(4, flow.Tokens.WebSearches);

        var planner = Assert.Single(flow.Agents, a => a.AgentId == "abc123");
        Assert.Equal("planner: Plan the change", planner.Name);
        Assert.Equal(400, planner.Tokens.Output);
        Assert.Equal(170, Assert.Single(flow.Agents, a => a.AgentId is null).Tokens.Output);
        Assert.Equal(flow.Agents.Sum(a => a.CostUsd!.Value), flow.CostUsd!.Value, 9);
    }

    [Fact]
    public void A_run_starts_when_the_message_is_sent()
    {
        WriteFlow();
        new TranscriptImporter(_db).ImportAll(_projects);
        var runs = RunReport.Load(_db, DateTimeOffset.UnixEpoch);

        // One response, 5 s after the message: the run lasted 5 s, not 0.
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T09:00:00Z"), runs[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), runs[0].End - runs[0].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T08:00:00Z"), runs[1].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T08:05:05Z"), runs[1].End);
    }

    [Fact]
    public void A_run_belongs_to_the_period_it_started_in_and_is_shown_whole()
    {
        WriteFlow();
        new TranscriptImporter(_db).ImportAll(_projects);

        // Started at the boundary: included with every response, even the background agent's 5 minutes later.
        var flow = RunReport.Load(_db, DateTimeOffset.Parse("2026-10-22T08:00:00Z"))[1];
        Assert.Equal(4, flow.Tokens.Responses);

        // Started before the period: left out, although all its responses came after the boundary.
        var later = RunReport.Load(_db, DateTimeOffset.Parse("2026-10-22T08:00:03Z"));
        Assert.Equal("Nästa uppgift", Assert.Single(later).Label);
        Assert.Empty(RunReport.Load(_db, DateTimeOffset.Parse("2026-10-22T09:00:01Z")));
    }

    [Fact]
    public void Window_totals_count_only_responses_inside_the_window()
    {
        WriteFlow();
        new TranscriptImporter(_db).ImportAll(_projects);

        // m1, m2, m3 and the subagent's s1 before 09:00; m4 at 09:00:05 belongs to the next window.
        var (tokens, _) = RunReport.Totals(_db, DateTimeOffset.Parse("2026-10-22T08:00:00Z"), DateTimeOffset.Parse("2026-10-22T09:00:00Z"));
        Assert.Equal(4, tokens.Responses);
        Assert.Equal(1, RunReport.Totals(_db, DateTimeOffset.Parse("2026-10-22T09:00:00Z"), DateTimeOffset.Parse("2026-10-22T10:00:00Z")).Tokens.Responses);
    }

    private string AgentName()
    {
        using var cmd = _db.Command("SELECT coalesce(agent_type, '-') || ': ' || coalesce(description, '-') FROM agents WHERE agent_id = 'abc123'");
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Agent_metadata_is_read_when_it_arrives_after_the_last_log_line()
    {
        WriteFlow();
        var metaPath = Path.ChangeExtension(_agentLog, ".meta.json");
        var meta = File.ReadAllText(metaPath);
        File.Delete(metaPath);
        var importer = new TranscriptImporter(_db);
        importer.ImportAll(_projects);
        Assert.Equal("-: -", AgentName());

        // The log doesn't grow; only the metadata appears, then changes.
        File.WriteAllText(metaPath, meta);
        importer.ImportAll(_projects);
        Assert.Equal("planner: Plan the change", AgentName());

        File.WriteAllText(metaPath, meta.Replace("Plan the change", "Plan the whole change"));
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddMinutes(1));
        importer.ImportSession(_mainLog);
        Assert.Equal("planner: Plan the whole change", AgentName());
        Assert.Equal(5, Count("SELECT count(*) FROM responses"));   // nothing read twice
    }

    [Fact]
    public void Upgrading_from_version_3_rereads_agent_metadata()
    {
        WriteFlow();
        var path = Path.Combine(_root, "v3.db");
        using (var v3 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            v3.Open();
            using var cmd = v3.CreateCommand();
            // The agent's log was read in full before its metadata existed.
            cmd.CommandText = """
                CREATE TABLE agents (agent_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, agent_type TEXT,
                    description TEXT, model TEXT, tool_use_id TEXT);
                CREATE TABLE log_files (path TEXT PRIMARY KEY, offset INTEGER NOT NULL, prompt_id TEXT);
                INSERT INTO agents VALUES ('abc123', '@SESSION', NULL, NULL, NULL, NULL);
                INSERT INTO log_files VALUES ('@KEY', @LENGTH, 'P1');
                PRAGMA user_version = 3;
                """.Replace("@SESSION", Session).Replace("@KEY", Path.GetFullPath(_agentLog).ToLowerInvariant())
                    .Replace("@LENGTH", new FileInfo(_agentLog).Length.ToString());
            cmd.ExecuteNonQuery();
        }

        using var db = UsageDatabase.OpenForWriting(path);
        new TranscriptImporter(db).ImportAll(_projects);
        using var check = db.Command("SELECT agent_type || ': ' || description FROM agents WHERE agent_id = 'abc123'");
        Assert.Equal("planner: Plan the change", check.ExecuteScalar());
    }

    [Fact]
    public void Stores_no_prompt_text_only_where_it_is_and_the_command_name()
    {
        WriteFlow();
        new TranscriptImporter(_db).ImportAll(_projects);

        using (var cmd = _db.Command("SELECT group_concat(name) FROM pragma_table_info('prompts')"))
            Assert.Equal("prompt_id,session_id,at,origin,command,log_path,log_offset", cmd.ExecuteScalar());
        using (var cmd = _db.Command("SELECT group_concat(prompt_id || '=' || coalesce(command, '-')) FROM prompts"))
            Assert.Equal("P1=/workflow,P2=-,P3=-", cmd.ExecuteScalar());

        // Once Claude Code has cleaned up the log, only the command name is left.
        File.Delete(_mainLog);
        var runs = RunReport.Load(_db, DateTimeOffset.UnixEpoch);
        Assert.Equal("(texten finns inte längre i loggen)", runs[0].Label);
        Assert.Equal("/workflow (texten finns inte längre i loggen)", runs[1].Label);
        Assert.Null(runs[0].Prompt!.ReadText());
    }

    [Fact]
    public void Upgrading_from_version_2_removes_stored_prompt_texts_from_the_file()
    {
        var path = Path.Combine(_root, "v2.db");
        using (var v2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            v2.Open();
            using var cmd = v2.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (session_id TEXT PRIMARY KEY, project TEXT, title TEXT, ai_title TEXT);
                CREATE TABLE prompts (prompt_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, at INTEGER NOT NULL, origin TEXT NOT NULL, label TEXT);
                CREATE TABLE log_files (path TEXT PRIMARY KEY, offset INTEGER NOT NULL, prompt_id TEXT);
                INSERT INTO prompts VALUES ('P1', 's', 0, 'human', 'min nyckel är HEMLIG-sk-ant-123');
                INSERT INTO log_files VALUES ('x.jsonl', 123, NULL);
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = UsageDatabase.OpenForWriting(path))
        {
            using var cmd = db.Command("SELECT (SELECT count(*) FROM prompts) + (SELECT count(*) FROM log_files)");
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path).Concat(File.Exists(path + "-wal") ? File.ReadAllBytes(path + "-wal") : []).ToArray();
        Assert.DoesNotContain("HEMLIG", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Uses_Claude_Codes_generated_name_when_there_is_no_own_name()
    {
        File.WriteAllLines(_mainLog,
        [
            User("P1", "\"Hej\"", "2026-10-22T08:00:00Z"),
            Assistant("m1", "claude-opus-5-5", 100, "2026-10-22T08:00:05Z"),
            $$"""{"type":"ai-title","aiTitle":"Första namnet","sessionId":"{{Session}}"}""",
            $$"""{"type":"ai-title","aiTitle":"Nästa updatering","sessionId":"{{Session}}"}""",
        ]);
        new TranscriptImporter(_db).ImportAll(_projects);
        Assert.Equal("Nästa updatering", Assert.Single(RunReport.Load(_db, DateTimeOffset.UnixEpoch)).SessionTitle);
    }

    [Fact]
    public void Upgrading_from_version_1_adds_generated_names_and_rereads_the_logs()
    {
        var path = Path.Combine(_root, "v1.db");
        using (var v1 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            v1.Open();
            using var cmd = v1.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (session_id TEXT PRIMARY KEY, project TEXT, title TEXT);
                CREATE TABLE log_files (path TEXT PRIMARY KEY, offset INTEGER NOT NULL, prompt_id TEXT);
                INSERT INTO sessions VALUES ('s', 'p', 'Eget namn');
                INSERT INTO log_files VALUES ('x.jsonl', 123, NULL);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = UsageDatabase.OpenForWriting(path);
        using var check = db.Command("SELECT (SELECT count(*) FROM log_files), (SELECT title || '/' || coalesce(ai_title, '-') FROM sessions)");
        using var r = check.ExecuteReader();
        r.Read();
        Assert.Equal(0, r.GetInt64(0));
        Assert.Equal("Eget namn/-", r.GetString(1));
    }

    [Fact]
    public void Reads_only_new_whole_lines()
    {
        var p1 = User("P1", "\"Hej\"", "2026-10-22T08:00:00Z");
        var m1 = Assistant("m1", "claude-opus-5-5", 100, "2026-10-22T08:00:05Z");
        var m2 = Assistant("m2", "claude-opus-5-5", 7, "2026-10-22T08:00:09Z");
        File.WriteAllText(_mainLog, p1 + "\n" + m1 + "\n" + m2[..40]);   // m2 is still being written

        var importer = new TranscriptImporter(_db);
        importer.ImportFile(_mainLog);
        Assert.Equal(1, Count("SELECT count(*) FROM responses"));

        File.AppendAllText(_mainLog, m2[40..] + "\n");
        importer.ImportFile(_mainLog);
        importer.ImportFile(_mainLog);
        Assert.Equal(2, Count("SELECT count(*) FROM responses"));
        // The prompt in effect carries over between imports.
        Assert.Equal(2, Count("SELECT count(*) FROM responses WHERE prompt_id = 'P1'"));
    }

    [Fact]
    public void A_replaced_file_is_read_again_without_duplicates()
    {
        WriteFlow();
        var importer = new TranscriptImporter(_db);
        importer.ImportAll(_projects);
        File.WriteAllText(_mainLog, Assistant("m1", "claude-opus-5-5", 100, "2026-10-22T08:00:05Z") + "\n");
        importer.ImportAll(_projects);
        Assert.Equal(5, Count("SELECT count(*) FROM responses"));
    }

    [Fact]
    public void Limits_record_history_only_on_change()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var input = new StatusInput(new LimitWindow(10, now.AddHours(2)), new LimitWindow(3, now.AddDays(5)), null);
        _db.UpdateLimits(input, now);
        _db.UpdateLimits(input, now.AddSeconds(5));
        _db.UpdateLimits(input with { FiveHour = new LimitWindow(8, now.AddHours(2)) }, now.AddSeconds(10));   // stale session
        _db.UpdateLimits(input with { FiveHour = new LimitWindow(12, now.AddHours(2)) }, now.AddSeconds(15));

        Assert.Equal(3, Count("SELECT count(*) FROM limit_history"));   // 10 and 3 at start, then 12
        var five = _db.ReadLimit(UsageDatabase.FiveHour)!;
        Assert.Equal(12, five.Window.UsedPercentage);
        Assert.Equal(now.AddSeconds(15), five.ChangedAt);
        Assert.Equal(now.AddSeconds(15), five.CheckedAt);
        Assert.Equal(now, _db.ReadLimit(UsageDatabase.SevenDay)!.ChangedAt);
    }

    [Fact]
    public void Limit_history_is_read_per_window()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var reset = now.AddHours(2);
        var week = new LimitWindow(3, now.AddDays(5));
        _db.UpdateLimits(new StatusInput(new LimitWindow(10, reset), week, null), now);
        _db.UpdateLimits(new StatusInput(new LimitWindow(25, reset.AddSeconds(2)), week, null), now.AddMinutes(30));   // jitter
        _db.UpdateLimits(new StatusInput(new LimitWindow(1, reset.AddHours(5)), week, null), reset.AddMinutes(1));     // next window

        var history = _db.ReadLimitHistory(UsageDatabase.FiveHour, reset);
        Assert.Equal([10.0, 25.0], history.Select(p => p.UsedPercentage));
        Assert.Equal([now, now.AddMinutes(30)], history.Select(p => p.At));
        Assert.Equal(1, Assert.Single(_db.ReadLimitHistory(UsageDatabase.FiveHour, reset.AddHours(5))).UsedPercentage);
        Assert.Equal(3, Assert.Single(_db.ReadLimitHistory(UsageDatabase.SevenDay, now.AddDays(5))).UsedPercentage);
    }
}

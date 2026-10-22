namespace ClaudeUsage.Core;

public static class UsagePaths
{
    /// <summary>Set to a test folder to keep both the database and usage.json away from the real ones.</summary>
    private static string? TestDir =>
        Environment.GetEnvironmentVariable("CLAUDE_USAGE_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>
    /// The database holds prompt labels, so it lives in the user's own profile (local, not roaming:
    /// SQLite's WAL files must not be synced between machines).
    /// </summary>
    public static string DataDir => TestDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsage");

    public static string DatabasePath => Path.Combine(DataDir, "claude-usage.db");
    public static string ErrorLogPath => Path.Combine(DataDir, "claude-usage-error.txt");

    /// <summary>Only percentages and reset times; kept in C:\temp where other tools already read it.</summary>
    public static string JsonPath => Path.Combine(TestDir ?? @"C:\temp", "usage.json");

    /// <summary>Claude Code's session logs: %USERPROFILE%\.claude\projects, or under CLAUDE_CONFIG_DIR when set.</summary>
    public static string ClaudeProjectsDir => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");
}

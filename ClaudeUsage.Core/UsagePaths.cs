namespace ClaudeUsage.Core;

public static class UsagePaths
{
    /// <summary>Set to a test folder to keep the database away from the real one.</summary>
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

    /// <summary>Claude Code's session logs: %USERPROFILE%\.claude\projects, or under CLAUDE_CONFIG_DIR when set.</summary>
    public static string ClaudeProjectsDir => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");
}

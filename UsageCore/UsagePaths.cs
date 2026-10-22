namespace UsageCore;

public static class UsagePaths
{
    /// <summary>Where usage.json and usage.db live. USAGE_DIR is for testing only.</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("USAGE_DIR") is { Length: > 0 } dir ? dir : @"C:\temp";

    public static string JsonPath => Path.Combine(DataDir, "usage.json");
    public static string DatabasePath => Path.Combine(DataDir, "usage.db");
    public static string ErrorLogPath => Path.Combine(DataDir, "usage-statusline-error.txt");

    /// <summary>Claude Code's session logs: %USERPROFILE%\.claude\projects, or under CLAUDE_CONFIG_DIR when set.</summary>
    public static string ClaudeProjectsDir => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");
}

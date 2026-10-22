using System.Text;
using TokenUsage.Core;

// Claude Code's status line command. Reads the session JSON from stdin, prints the status line,
// and records limits and token usage. Run with --import-all to read every session log once.

if (args is ["--import-all"])
{
    using var db = UsageDatabase.OpenForWriting(UsagePaths.DatabasePath);
    new TranscriptImporter(db).ImportAll(UsagePaths.ClaudeProjectsDir);
    Console.WriteLine($"Läste in sessionsloggarna i {UsagePaths.ClaudeProjectsDir} till {UsagePaths.DatabasePath}.");
    return;
}

string raw;
using (var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
    raw = stdin.ReadToEnd();

var input = StatusInput.Parse(raw);
var now = DateTimeOffset.Now;

using (var stdout = Console.OpenStandardOutput())
{
    stdout.Write(Encoding.UTF8.GetBytes(StatusLineText.Format(input, now) + Environment.NewLine));
}

// Recording must never break the status line itself; keep only the last error for troubleshooting.
try
{
    using var db = UsageDatabase.OpenForWriting(UsagePaths.DatabasePath);
    db.UpdateLimits(input, now);
    var importer = new TranscriptImporter(db);
    const long maxBytesPerRun = 16 * 1024 * 1024;   // a long backlog is read over several runs
    if (input.TranscriptPath is { Length: > 0 } transcript && File.Exists(transcript))
        importer.ImportSession(transcript, maxBytesPerRun);
    // Other sessions, including headless `claude -p` runs that have no status line of their own.
    importer.ImportAllIfDue(UsagePaths.ClaudeProjectsDir, TimeSpan.FromSeconds(60), now, maxBytesPerRun);
}
catch (Exception ex)
{
    try { File.WriteAllText(UsagePaths.ErrorLogPath, $"{now:o}{Environment.NewLine}{ex}"); }
    catch (IOException) { }
}

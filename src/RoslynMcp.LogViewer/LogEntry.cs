namespace RoslynMcp.LogViewer;

/// <summary>A single parsed log line.</summary>
/// <param name="Timestamp">UTC timestamp string, e.g. '2026-03-26 14:30:45.123Z'.</param>
/// <param name="Level">Trimmed level: START, STOP, TOOL, ERROR, or OTHER.</param>
/// <param name="Message">Everything after the level bracket.</param>
/// <param name="Raw">The original unparsed line.</param>
record LogEntry(string Timestamp, string Level, string Message, string Raw);

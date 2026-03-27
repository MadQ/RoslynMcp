namespace RoslynMcp.LogViewer;

/// <summary>A single parsed log line.</summary>
/// <param name="Timestamp">UTC timestamp string, e.g. '2026-03-26 14:30:45.123Z'.</param>
/// <param name="Level">Trimmed level: START, STOP, TOOL, ERROR, or OTHER.</param>
/// <param name="Message">Everything after the level bracket (full message string).</param>
/// <param name="Raw">The original unparsed line.</param>
/// <param name="WorkspaceMode">For TOOL entries: '◆' MSBuild, '◇' Adhoc, null otherwise.</param>
/// <param name="ToolName">For TOOL entries: tool name with optional subject, e.g. 'roslyn_list_types(RoslynMcp.Tools)'.</param>
/// <param name="ElapsedMs">For TOOL entries: elapsed milliseconds.</param>
/// <param name="Success">For TOOL entries: true if OK, false if ERROR.</param>
/// <param name="Detail">For TOOL entries: the part after ' — ', e.g. '18/18 member(s)'.</param>
record LogEntry(
    string  Timestamp,
    string  Level,
    string  Message,
    string  Raw,
    string? WorkspaceMode = null,
    string? ToolName      = null,
    long?   ElapsedMs     = null,
    bool?   Success       = null,
    string? Detail        = null);

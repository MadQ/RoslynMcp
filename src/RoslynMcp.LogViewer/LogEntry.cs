namespace RoslynMcp.LogViewer;

/// <summary>A single parsed log line.</summary>
/// <param name="Timestamp">UTC timestamp string, e.g. '2026-03-28 14:30:45.123Z'.</param>
/// <param name="Level">Trimmed level: START, STOP, TOOL, ERROR, INFO, or OTHER.</param>
/// <param name="Message">Everything after the level bracket (full message string).</param>
/// <param name="Raw">The original unparsed line.</param>
/// <param name="WorkspaceMode">For TOOL entries: 'MSB' (MSBuildWorkspace), 'ADH' (AdhocWorkspace), '---' (unknown), null for non-TOOL.</param>
/// <param name="ToolName">For TOOL entries: short tool name (without roslyn_ prefix), e.g. 'list_types'.</param>
/// <param name="ElapsedMs">For TOOL entries: elapsed milliseconds.</param>
/// <param name="Success">For TOOL entries: true if OK, false if ERROR.</param>
/// <param name="Subject">For TOOL entries: the primary argument (e.g. symbol name, file path).</param>
/// <param name="Detail">For TOOL entries: outcome summary, e.g. '18/18 member(s)'.</param>
record LogEntry(
    string  Timestamp,
    string? Pid,
    string  Level,
    string  Message,
    string  Raw,
    string? WorkspaceMode = null,
    string? ToolName      = null,
    long?   ElapsedMs     = null,
    bool?   Success       = null,
    string? Subject       = null,
    string? Detail        = null);

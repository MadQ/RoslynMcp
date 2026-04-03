using System.Text.Json.Serialization;

namespace RoslynMcp;

/// <summary>
///     A single structured log entry. Written as NDJSON by <see cref="FileLogger"/>
///     and deserialized by the LogViewer's LogTailer.
/// </summary>
/// <remarks>
///     <para>
///         This file is shared between <c>RoslynMcp</c> (writer) and
///         <c>RoslynMcp.LogViewer</c> (reader) via a linked-file reference in
///         <c>RoslynMcp.LogViewer.csproj</c> — not a project reference. Both
///         projects compile the same source independently, keeping the log schema
///         as the single source of truth without any assembly dependency.
///     </para>
///     <para>
///         <c>Raw</c> is not serialized; the reader populates it from the
///         original JSON line string after deserialization.
///     </para>
/// </remarks>
record LogEntry
{
    /// <summary>ISO 8601 UTC timestamp, e.g. '2026-04-03T11:48:36.123Z'.</summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>Process ID of the MCP server.</summary>
    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    /// <summary>
    ///     Log level: START, STOP, TOOL, ERROR, or INFO.
    /// </summary>
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    /// <summary>
    ///     Per-process tool-call counter. Increments on each LogTool call.
    ///     Null for non-TOOL entries (START, STOP, ERROR, INFO).
    /// </summary>
    [JsonPropertyName("instance")]
    public int? Instance { get; init; }

    /// <summary>Full message text. Populated for non-TOOL entries (START, STOP, ERROR, INFO).</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    // ── TOOL entry fields ─────────────────────────────────────────────────

    /// <summary>For TOOL entries: 'MSB' (MSBuildWorkspace) or 'ADH' (AdhocWorkspace).</summary>
    [JsonPropertyName("workspace_mode")]
    public string? WorkspaceMode { get; init; }

    /// <summary>For TOOL entries: short tool name without roslyn_ prefix, e.g. 'list_types'.</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    /// <summary>For TOOL entries: elapsed milliseconds.</summary>
    [JsonPropertyName("elapsed_ms")]
    public long? ElapsedMs { get; init; }

    /// <summary>For TOOL entries: true if OK, false if ERROR.</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    /// <summary>For TOOL entries: primary argument, e.g. symbol name or file path.</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    /// <summary>For TOOL entries: outcome summary, e.g. '18/18 member(s)'.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>For TOOL entries: workspace cache result — 'HIT', 'MISS', or null.</summary>
    [JsonPropertyName("cache_tag")]
    public string? CacheTag { get; init; }

    /// <summary>For TOOL entries: estimated response token count.</summary>
    [JsonPropertyName("estimated_tokens")]
    public int? EstimatedTokens { get; init; }

    /// <summary>For TOOL entries: running session token total at time of this call.</summary>
    [JsonPropertyName("session_tokens")]
    public long? SessionTokens { get; init; }

    // ── Reader-only ───────────────────────────────────────────────────────

    /// <summary>
    ///     The original unparsed line. Not serialized — the reader sets this
    ///     to the raw JSON string after deserialization.
    /// </summary>
    [JsonIgnore]
    public string Raw { get; init; } = "";
}

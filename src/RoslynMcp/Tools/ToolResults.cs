using System.Text.Json.Serialization;

namespace RoslynMcp.Tools;

// ── Shared ──────────────────────────────────────────────────────────────────

/// <summary>A single compiler diagnostic item returned by <c>roslyn_get_diagnostics</c> and <c>roslyn_build_project</c>.</summary>
/// <remarks>
/// <c>target_frameworks</c> is populated by <c>roslyn_build_project</c> when MSBuild emits TFM context
/// in the bracket suffix (e.g. <c>[proj::TargetFramework=net10.0]</c>). Always <c>null</c> on the
/// Roslyn fast path — Roslyn diagnostics carry no per-diagnostic TFM information.
/// </remarks>
internal sealed record DiagnosticItem(
	[property: JsonPropertyName("code")]              string   Code,
	[property: JsonPropertyName("severity")]          string   Severity,
	[property: JsonPropertyName("file")]              string?  File,
	[property: JsonPropertyName("line")]              int      Line,
	[property: JsonPropertyName("column")]            int      Column,
	[property: JsonPropertyName("message")]           string   Message,
	[property: JsonPropertyName("target_frameworks")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string[]? TargetFrameworks = null
);

/// <summary>Cached page response from <see cref="RoslynMcpTool.ToolScope.TryServeCachedPage{T}"/>.</summary>
internal sealed record CachedPageResult<T>(
	[property: JsonPropertyName("items")]      T[]     Items,
	[property: JsonPropertyName("total")]      int     Total,
	[property: JsonPropertyName("skip")]       int     Skip,
	[property: JsonPropertyName("take")]       int     Take,
	[property: JsonPropertyName("page_token")] string? PageToken,
	[property: JsonPropertyName("has_more")]   bool    HasMore
) : ToolResult;

// ── Analysis tools ──────────────────────────────────────────────────────────

internal sealed record FileOutlineResult(
	[property: JsonPropertyName("file")]        string   File,
	[property: JsonPropertyName("total_types")] int      TotalTypes,
	[property: JsonPropertyName("skip")]        int      Skip,
	[property: JsonPropertyName("take")]        int      Take,
	[property: JsonPropertyName("types")]       object[] Types,
	[property: JsonPropertyName("page_token")]  string?  PageToken,
	[property: JsonPropertyName("has_more")]    bool     HasMore
) : ToolResult;

internal sealed record FindReferencesResult(
	[property: JsonPropertyName("total_references")] int      TotalReferences,
	[property: JsonPropertyName("symbols_searched")] string[] SymbolsSearched,
	[property: JsonPropertyName("skip")]             int      Skip,
	[property: JsonPropertyName("take")]             int      Take,
	[property: JsonPropertyName("references")]       string[] References,
	[property: JsonPropertyName("page_token")]       string?  PageToken,
	[property: JsonPropertyName("has_more")]         bool     HasMore
) : ToolResult;

internal sealed record UnusedSymbolEntry(
	[property: JsonPropertyName("kind")]          string Kind,
	[property: JsonPropertyName("name")]          string Name,
	[property: JsonPropertyName("accessibility")] string Accessibility,
	[property: JsonPropertyName("confidence")]    string Confidence,
	[property: JsonPropertyName("reason")]        string Reason,
	[property: JsonPropertyName("file")]          string File,
	[property: JsonPropertyName("line")]          int    Line
);

internal sealed record FindUnusedResult(
	[property: JsonPropertyName("total_unused")] int                 TotalUnused,
	[property: JsonPropertyName("skip")]         int                 Skip,
	[property: JsonPropertyName("take")]         int                 Take,
	[property: JsonPropertyName("unused")]       UnusedSymbolEntry[] Unused,
	[property: JsonPropertyName("page_token")]   string?             PageToken,
	[property: JsonPropertyName("has_more")]     bool                HasMore
) : ToolResult;

internal sealed record FindImplementationsResult(
	[property: JsonPropertyName("symbol_type")]           string   SymbolType,
	[property: JsonPropertyName("symbol_name")]           string   SymbolName,
	[property: JsonPropertyName("total_implementations")] int      TotalImplementations,
	[property: JsonPropertyName("skip")]                  int      Skip,
	[property: JsonPropertyName("take")]                  int      Take,
	[property: JsonPropertyName("implementations")]       string[] Implementations,
	[property: JsonPropertyName("page_token")]            string?  PageToken = null,
	[property: JsonPropertyName("has_more")]              bool     HasMore   = false
) : ToolResult;

internal sealed record FindOverridesResult(
	[property: JsonPropertyName("symbol_type")]     string   SymbolType,
	[property: JsonPropertyName("symbol_name")]     string   SymbolName,
	[property: JsonPropertyName("total_overrides")] int      TotalOverrides,
	[property: JsonPropertyName("skip")]            int      Skip,
	[property: JsonPropertyName("take")]            int      Take,
	[property: JsonPropertyName("overrides")]       string[] Overrides,
	[property: JsonPropertyName("page_token")]      string?  PageToken = null,
	[property: JsonPropertyName("has_more")]        bool     HasMore   = false
) : ToolResult;

internal sealed record CallerEntry(
	[property: JsonPropertyName("caller")] string Caller,
	[property: JsonPropertyName("file")]   string File,
	[property: JsonPropertyName("line")]   int    Line
);

internal sealed record FindCallersResult(
	[property: JsonPropertyName("symbol_searched")] string        SymbolSearched,
	[property: JsonPropertyName("total_callers")]   int           TotalCallers,
	[property: JsonPropertyName("skip")]            int           Skip,
	[property: JsonPropertyName("take")]            int           Take,
	[property: JsonPropertyName("callers")]         CallerEntry[] Callers,
	[property: JsonPropertyName("page_token")]      string?       PageToken,
	[property: JsonPropertyName("has_more")]        bool          HasMore
) : ToolResult;

internal sealed record CallSiteEntry(
	[property: JsonPropertyName("callee")] string Callee,
	[property: JsonPropertyName("file")]   string File,
	[property: JsonPropertyName("line")]   int    Line
);

internal sealed record GetCallGraphResult(
	[property: JsonPropertyName("method")]      string          Method,
	[property: JsonPropertyName("total_calls")] int             TotalCalls,
	[property: JsonPropertyName("skip")]        int             Skip,
	[property: JsonPropertyName("take")]        int             Take,
	[property: JsonPropertyName("calls")]       CallSiteEntry[] Calls,
	[property: JsonPropertyName("page_token")]  string?         PageToken,
	[property: JsonPropertyName("has_more")]    bool            HasMore
) : ToolResult;

internal sealed record ListTypesResult(
	[property: JsonPropertyName("total_types")] int      TotalTypes,
	[property: JsonPropertyName("skip")]        int      Skip,
	[property: JsonPropertyName("take")]        int      Take,
	[property: JsonPropertyName("types")]       string[] Types,
	[property: JsonPropertyName("page_token")]  string?  PageToken,
	[property: JsonPropertyName("has_more")]    bool     HasMore
) : ToolResult;

internal sealed record ListTypesEmptyResult(
	[property: JsonPropertyName("message")] string Message
) : ToolResult;

internal sealed record TypeDependencyEntry(
	[property: JsonPropertyName("type_name")]       string  TypeName,
	[property: JsonPropertyName("dependency_kind")] string  DependencyKind,
	[property: JsonPropertyName("member")]          string? Member
);

internal sealed record TypeDependenciesResult(
	[property: JsonPropertyName("type_name")]          string                TypeName,
	[property: JsonPropertyName("type_kind")]          string                TypeKind,
	[property: JsonPropertyName("total_dependencies")] int                   TotalDependencies,
	[property: JsonPropertyName("skip")]               int                   Skip,
	[property: JsonPropertyName("take")]               int                   Take,
	[property: JsonPropertyName("dependencies")]       TypeDependencyEntry[] Dependencies,
	[property: JsonPropertyName("page_token")]         string?               PageToken,
	[property: JsonPropertyName("has_more")]           bool                  HasMore
) : ToolResult;

internal sealed record TypeHierarchyResult(
	[property: JsonPropertyName("type_name")]              string   TypeName,
	[property: JsonPropertyName("type_kind")]              string   TypeKind,
	[property: JsonPropertyName("base_types")]             string[] BaseTypes,
	[property: JsonPropertyName("total_interfaces")]       int      TotalInterfaces,
	[property: JsonPropertyName("total_derived_types")]    int      TotalDerivedTypes,
	[property: JsonPropertyName("skip")]                   int      Skip,
	[property: JsonPropertyName("take")]                   int      Take,
	[property: JsonPropertyName("interfaces_and_derived")] string[] InterfacesAndDerived,
	[property: JsonPropertyName("page_token")]             string?  PageToken,
	[property: JsonPropertyName("has_more")]               bool     HasMore
) : ToolResult;

internal sealed record TypeMembersResult(
	[property: JsonPropertyName("type_name")]     string    TypeName,
	[property: JsonPropertyName("type_kind")]     string    TypeKind,
	[property: JsonPropertyName("total_members")] int       TotalMembers,
	[property: JsonPropertyName("skip")]          int       Skip,
	[property: JsonPropertyName("take")]          int       Take,
	[property: JsonPropertyName("members")]       object?[] Members,
	[property: JsonPropertyName("page_token")]    string?   PageToken,
	[property: JsonPropertyName("has_more")]      bool      HasMore
) : ToolResult;

internal sealed record ReadFileResult(
	[property: JsonPropertyName("file")]        string   File,
	[property: JsonPropertyName("source")]      string   Source,
	[property: JsonPropertyName("total_lines")] int      TotalLines,
	[property: JsonPropertyName("start_line")]  int      StartLine,
	[property: JsonPropertyName("end_line")]    int      EndLine,
	[property: JsonPropertyName("lines")]       string[] Lines
) : ToolResult;

internal sealed record LineCountResult(
	[property: JsonPropertyName("files")] object[] Files
) : ToolResult;

internal sealed record LineCountEntry(
	[property: JsonPropertyName("file")]       string  File,
	[property: JsonPropertyName("line_count")] int?    LineCount,
	[property: JsonPropertyName("error")]      string? Error  // Per-file I/O error, not a tool-level failure.
)
;

internal sealed record MemberBodySingleResult(
	[property: JsonPropertyName("symbol_name")] string SymbolName,
	[property: JsonPropertyName("symbol_kind")] string SymbolKind,
	[property: JsonPropertyName("file")]        string File,
	[property: JsonPropertyName("start_line")]  int    StartLine,
	[property: JsonPropertyName("end_line")]    int    EndLine,
	[property: JsonPropertyName("body")]        string Body
) : ToolResult;

internal sealed record MemberBodyPartialResult(
	[property: JsonPropertyName("symbol_name")] string       SymbolName,
	[property: JsonPropertyName("symbol_kind")] string       SymbolKind,
	[property: JsonPropertyName("parts")]       List<object> Parts,
	[property: JsonPropertyName("note")]        string       Note
) : ToolResult;

internal sealed record MemberBodyPart(
	[property: JsonPropertyName("file")]       string File,
	[property: JsonPropertyName("start_line")] int    StartLine,
	[property: JsonPropertyName("end_line")]   int    EndLine,
	[property: JsonPropertyName("body")]       string Body,
	[property: JsonPropertyName("part_index")] int?   PartIndex
);

internal sealed record MetadataSymbolResult(
	[property: JsonPropertyName("symbol_name")] string SymbolName,
	[property: JsonPropertyName("symbol_kind")] string SymbolKind,
	[property: JsonPropertyName("location")]    string Location
) : ToolResult, IToolError;

internal sealed record SymbolDefinitionResult(
	[property: JsonPropertyName("symbol_name")] string  SymbolName,
	[property: JsonPropertyName("symbol_kind")] string  SymbolKind,
	[property: JsonPropertyName("file")]        string  File,
	[property: JsonPropertyName("line")]        int     Line,
	[property: JsonPropertyName("column")]      int     Column,
	[property: JsonPropertyName("signature")]   string  Signature,
	[property: JsonPropertyName("doc_summary")] string? DocSummary
) : ToolResult;

internal sealed record SymbolDocumentationResult(
	[property: JsonPropertyName("symbol_name")] string  SymbolName,
	[property: JsonPropertyName("symbol_kind")] string  SymbolKind,
	[property: JsonPropertyName("summary")]     string? Summary,
	[property: JsonPropertyName("parameters")]  object? Parameters,
	[property: JsonPropertyName("returns")]     string? Returns,
	[property: JsonPropertyName("remarks")]     string? Remarks,
	[property: JsonPropertyName("example")]     string? Example
) : ToolResult;

internal sealed record SymbolDocumentationEmptyResult(
	[property: JsonPropertyName("symbol_name")]   string  SymbolName,
	[property: JsonPropertyName("symbol_kind")]   string  SymbolKind,
	[property: JsonPropertyName("documentation")] string? Documentation
) : ToolResult, IToolError;

internal sealed record SymbolInfoResult(
	[property: JsonPropertyName("kind")]            string  Kind,
	[property: JsonPropertyName("name")]            string  Name,
	[property: JsonPropertyName("containing_type")] string? ContainingType,
	[property: JsonPropertyName("type_or_return")]  string? TypeOrReturn
) : ToolResult;

internal sealed record SymbolsInScopeResult(
	[property: JsonPropertyName("file")]       string                             File,
	[property: JsonPropertyName("line")]       int                                Line,
	[property: JsonPropertyName("column")]     int                                Column,
	[property: JsonPropertyName("locals")]     GetSymbolsInScopeTool.SymbolInfo[] Locals,
	[property: JsonPropertyName("parameters")] GetSymbolsInScopeTool.SymbolInfo[] Parameters,
	[property: JsonPropertyName("fields")]     GetSymbolsInScopeTool.SymbolInfo[] Fields,
	[property: JsonPropertyName("properties")] GetSymbolsInScopeTool.SymbolInfo[] Properties,
	[property: JsonPropertyName("methods")]    GetSymbolsInScopeTool.SymbolInfo[] Methods,
	[property: JsonPropertyName("types")]      GetSymbolsInScopeTool.SymbolInfo[] Types,
	[property: JsonPropertyName("other")]      GetSymbolsInScopeTool.SymbolInfo[] Other
) : ToolResult;

internal sealed record GetUsingsResult(
	[property: JsonPropertyName("file")]          string                        File,
	[property: JsonPropertyName("usings")]        GetUsingsTool.UsingDirective[] Usings,
	[property: JsonPropertyName("global_usings")] string[]                       GlobalUsings
) : ToolResult;

internal sealed record ProjectInfoResult(
	[property: JsonPropertyName("name")]                 string    Name,
	[property: JsonPropertyName("assembly_name")]        string    AssemblyName,
	[property: JsonPropertyName("file_path")]            string?   FilePath,
	[property: JsonPropertyName("target_framework")]     string?   TargetFramework,
	[property: JsonPropertyName("language_version")]     string    LanguageVersion,
	[property: JsonPropertyName("output_kind")]          string    OutputKind,
	[property: JsonPropertyName("nullable")]             string    Nullable,
	[property: JsonPropertyName("is_msbuild_workspace")] bool      IsMsbuildWorkspace,
	[property: JsonPropertyName("package_references")]   object[]  PackageReferences,
	[property: JsonPropertyName("additional_files")]     string[]  AdditionalFiles,
	[property: JsonPropertyName("version")]              string?   Version,
	[property: JsonPropertyName("root_namespace")]       string?   RootNamespace,
	[property: JsonPropertyName("target_frameworks")]    string[]? TargetFrameworks,
	[property: JsonPropertyName("allow_unsafe_blocks")]  bool?     AllowUnsafeBlocks,
	[property: JsonPropertyName("warnings_as_errors")]   bool?     WarningsAsErrors,
	[property: JsonPropertyName("load_warnings")]        string[]? LoadWarnings
) : ToolResult;

internal sealed record CheckDriftResult(
	[property: JsonPropertyName("drifted")]              bool     Drifted,
	[property: JsonPropertyName("drifted_count")]        int      DriftedCount,
	[property: JsonPropertyName("drifted_files")]        string[] DriftedFiles,
	[property: JsonPropertyName("checked_count")]        int      CheckedCount,
	[property: JsonPropertyName("last_synced_utc")]      string   LastSyncedUtc,
	[property: JsonPropertyName("is_msbuild_workspace")] bool     IsMsbuildWorkspace,
	// Reference health is a second, independent axis: source can be perfectly in sync while the
	// workspace holds no metadata references at all. Reporting only drift gave a false all-clear
	// on exactly that failure (issue #235).
	[property: JsonPropertyName("workspace_healthy")]    bool     WorkspaceHealthy,
	[property: JsonPropertyName("projects_without_references")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string[]?                                            ProjectsWithoutReferences,
	[property: JsonPropertyName("last_unhealthy_load")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string?                                              LastUnhealthyLoad,
	// A third axis, and the only one that can report a file the workspace does not know about
	// yet: drifted_files is built from existing documents, so a brand-new file is invisible to it.
	[property: JsonPropertyName("reload_pending")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	bool                                                 ReloadPending
) : ToolResult;

internal sealed record GetTriviaNoMatchResult(
	[property: JsonPropertyName("message")]       string   Message,
	[property: JsonPropertyName("provided_kind")] string   ProvidedKind,
	[property: JsonPropertyName("common_kinds")]  string[] CommonKinds
) : ToolResult, IToolError;

internal sealed record TriviaNodeSpan(
	[property: JsonPropertyName("start")]      int Start,
	[property: JsonPropertyName("end")]        int End,
	[property: JsonPropertyName("start_line")] int StartLine,
	[property: JsonPropertyName("end_line")]   int EndLine
);

internal sealed record TriviaNodeResult(
	[property: JsonPropertyName("node_kind")]       string         NodeKind,
	[property: JsonPropertyName("node_span")]       TriviaNodeSpan NodeSpan,
	[property: JsonPropertyName("node_text")]       string         NodeText,
	[property: JsonPropertyName("leading_trivia")]  object[]       LeadingTrivia,
	[property: JsonPropertyName("trailing_trivia")] object[]       TrailingTrivia
);

internal sealed record GetTriviaResult(
	[property: JsonPropertyName("file")]           string   File,
	[property: JsonPropertyName("total_nodes")]    int      TotalNodes,
	[property: JsonPropertyName("filtered_nodes")] int      FilteredNodes,
	[property: JsonPropertyName("skip")]           int      Skip,
	[property: JsonPropertyName("take")]           int      Take,
	[property: JsonPropertyName("results")]        object[] Results,
	[property: JsonPropertyName("page_token")]     string?  PageToken,
	[property: JsonPropertyName("has_more")]       bool     HasMore
) : ToolResult;

internal sealed record TriviaEntry(
	[property: JsonPropertyName("kind")] string     Kind,
	[property: JsonPropertyName("text")] string     Text,
	[property: JsonPropertyName("span")] TriviaSpan Span
);

internal sealed record TriviaSpan(
	[property: JsonPropertyName("start")] int Start,
	[property: JsonPropertyName("end")]   int End
);

internal sealed record DiagnosticsResult(
	[property: JsonPropertyName("summary")]       string           Summary,
	[property: JsonPropertyName("source")]        string           Source,
	[property: JsonPropertyName("error_count")]   int              ErrorCount,
	[property: JsonPropertyName("warning_count")] int              WarningCount,
	[property: JsonPropertyName("total")]         int              Total,
	[property: JsonPropertyName("returned")]      int              Returned,
	[property: JsonPropertyName("has_more")]      bool             HasMore,
	[property: JsonPropertyName("page_token")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string?                                       PageToken,
	[property: JsonPropertyName("items")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	DiagnosticItem[]?                             Items,
	[property: JsonPropertyName("possible_workspace_load_issue")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	bool                                         PossibleWorkspaceLoadIssue,
	// Present only when the workspace is genuinely unhealthy — the symptom shows up here, so the
	// evidence has to be here too rather than only on roslyn_get_project_info (issue #235).
	[property: JsonPropertyName("load_warnings")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string[]?                                    LoadWarnings
) : ToolResult;

internal sealed record ListFilesResult(
	[property: JsonPropertyName("files")]      string[] Files,
	[property: JsonPropertyName("count")]      int      Count,
	[property: JsonPropertyName("skip")]       int      Skip,
	[property: JsonPropertyName("take")]       int      Take,
	[property: JsonPropertyName("page_token")] string?  PageToken,
	[property: JsonPropertyName("has_more")]   bool     HasMore
) : ToolResult;

internal sealed record ListFilesEmptyResult(
	[property: JsonPropertyName("files")]         string[]  Files,
	[property: JsonPropertyName("count")]         int       Count,
	[property: JsonPropertyName("close_matches")] string[]? CloseMatches
) : ToolResult;

internal sealed record SearchFilesResult(
	[property: JsonPropertyName("matches")]       object[] Matches,
	[property: JsonPropertyName("total_matches")] int      TotalMatches,
	[property: JsonPropertyName("returned")]      int      Returned,
	[property: JsonPropertyName("page_token")]    string?  PageToken,
	[property: JsonPropertyName("has_more")]      bool     HasMore
) : ToolResult;

internal sealed record SemanticSearchResult(
	[property: JsonPropertyName("matches")]       object[] Matches,
	[property: JsonPropertyName("total_matches")] int      TotalMatches,
	[property: JsonPropertyName("returned")]      int      Returned,
	[property: JsonPropertyName("page_token")]    string?  PageToken,
	[property: JsonPropertyName("has_more")]      bool     HasMore,
	[property: JsonPropertyName("context")]       string?  Context = null
) : ToolResult;

internal sealed record SemanticSearchListResult(
	[property: JsonPropertyName("values")]      string[] Values,
	[property: JsonPropertyName("description")] string   Description
) : ToolResult;

internal sealed record DiscoveryKindsResult(
	[property: JsonPropertyName("mode")]         string   Mode,
	[property: JsonPropertyName("count")]        int      Count,
	[property: JsonPropertyName("common_kinds")] string[] CommonKinds,
	[property: JsonPropertyName("all_kinds")]    string[] AllKinds
) : ToolResult;

internal sealed record DiscoveryValuesResult(
	[property: JsonPropertyName("mode")]  string   Mode,
	[property: JsonPropertyName("kinds")] string[] Kinds
) : ToolResult;

internal sealed record DiscoveryContextsResult(
	[property: JsonPropertyName("mode")]     string   Mode,
	[property: JsonPropertyName("contexts")] string[] Contexts
) : ToolResult;

internal sealed record DiscoveryNoMatchResult(
	[property: JsonPropertyName("message")]       string   Message,
	[property: JsonPropertyName("provided_kind")] string   ProvidedKind,
	[property: JsonPropertyName("valid_kinds")]   string[] ValidKinds
) : ToolResult, IToolError;

internal sealed record DetailedErrorResult(
	[property: JsonPropertyName("details")] string Details
) : ToolResult, IToolError;

// ── Build tools ─────────────────────────────────────────────────────────────

internal sealed record BuildResult(
	[property: JsonPropertyName("succeeded")]     bool             Succeeded,
	[property: JsonPropertyName("errors")]        DiagnosticItem[] Errors,
	[property: JsonPropertyName("warnings")]      DiagnosticItem[] Warnings,
	[property: JsonPropertyName("source")]        string           Source,
	[property: JsonPropertyName("build_skipped")] bool             BuildSkipped,
	[property: JsonPropertyName("skip_reason")]   string?          SkipReason,
	[property: JsonPropertyName("duration_ms")]   long             DurationMs,
	[property: JsonPropertyName("exit_code")]     int?             ExitCode,      // null when build_skipped is true (no build ran)
	[property: JsonPropertyName("error_details")] string?          ErrorDetails = null // non-null when succeeded is false and errors[] is empty
) : ToolResult;

internal sealed record ReplaceInCodeResult(
	[property: JsonPropertyName("applied")]       bool     Applied,
	[property: JsonPropertyName("change_count")]  int      ChangeCount,
	[property: JsonPropertyName("changed_nodes")] object[] ChangedNodes,
	[property: JsonPropertyName("message")]       string?  Message     = null,
	[property: JsonPropertyName("syntax_valid")]  bool     SyntaxValid = true
) : ToolResult;

internal sealed record ReplaceInCodeNodeInfo(
	[property: JsonPropertyName("original_text")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string? OriginalText,
	[property: JsonPropertyName("line")]   int Line,
	[property: JsonPropertyName("column")] int Column
);

internal sealed record ReplaceInCodeSyntaxError(
	[property: JsonPropertyName("details")]
	string    Details,
	[property: JsonPropertyName("changed_nodes")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	object[]? ChangedNodes = null
) : ToolResult, IToolError;

internal sealed record RespawnResult(
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("pid")]     int    Pid,
	[property: JsonPropertyName("tip")]     string Tip
) : ToolResult;

internal sealed record DebugAttachResult(
	[property: JsonPropertyName("attached")] bool   Attached,
	[property: JsonPropertyName("pid")]      int    Pid,
	[property: JsonPropertyName("message")]  string Message
) : ToolResult;

internal sealed record DebugAlreadyAttachedResult(
	[property: JsonPropertyName("already_attached")] bool   AlreadyAttached,
	[property: JsonPropertyName("pid")]              int    Pid,
	[property: JsonPropertyName("message")]          string Message
) : ToolResult;

// ── Base class error shapes ──────────────────────────────────────────────────

internal sealed record PathErrorResult(
	[property: JsonPropertyName("message")]        string    Message,
	[property: JsonPropertyName("provided_path")]  string?   ProvidedPath  = null,
	[property: JsonPropertyName("search_path")]    string?   SearchPath    = null,
	[property: JsonPropertyName("directory")]      string?   Directory     = null,
	[property: JsonPropertyName("file_name")]      string?   FileName      = null,
	[property: JsonPropertyName("found_projects")] string[]? FoundProjects = null,
	[property: JsonPropertyName("found_in")]       string[]? FoundIn       = null
) : ToolResult, IToolError;

internal sealed record UnexpectedErrorResult(
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("type")]    string Type
) : ToolResult, IToolError;

/// <summary>
///     Transient workspace failure — almost always the workspace resolving a project mid-reload:
///     a prior edit triggered an MSBuild reload and this call landed before it settled. The operation
///     is safe to retry once the reload completes (seconds). <see cref="Fault"/> carries the underlying
///     exception type and message for log diagnosis; <see cref="Retryable"/> is always <see langword="true"/>.
///     Returned by the editing tools' guarded workspace-resolution helpers instead of letting the
///     exception propagate unhandled (which the MCP transport reports as an opaque invocation error).
/// </summary>
internal sealed record TransientWorkspaceError(
	[property: JsonPropertyName("fault")]     string Fault,
	[property: JsonPropertyName("retryable")] bool   Retryable = true
) : ToolResult, IToolError;


// ── Editing tools ────────────────────────────────────────────────────────────

internal sealed record ReplaceInFileResult(
	[property: JsonPropertyName("applied")]       bool    Applied,
	[property: JsonPropertyName("match_count")]   int     MatchCount,
	[property: JsonPropertyName("changed_lines")] int[]   ChangedLines,
	[property: JsonPropertyName("message")]       string? Message = null
) : ToolResult;

internal sealed record InsertLinesResult(
	[property: JsonPropertyName("applied")]        bool    Applied,
	[property: JsonPropertyName("inserted_at")]    int     InsertedAt,
	[property: JsonPropertyName("line_count")]     int     LineCount,
	[property: JsonPropertyName("inserted_lines")] int[]   InsertedLines,
	[property: JsonPropertyName("backup_token")]   string? BackupToken = null,
	[property: JsonPropertyName("message")]        string? Message = null
) : ToolResult;

// ── Refactoring tools ────────────────────────────────────────────────────────

internal sealed record ChangeSignatureResult(
	[property: JsonPropertyName("diff")]                string   Diff,
	[property: JsonPropertyName("token")]               string   Token,
	[property: JsonPropertyName("message")]             string   Message,
	[property: JsonPropertyName("parameters_added")]    string[] ParametersAdded,
	[property: JsonPropertyName("deprecation_message")] string?  DeprecationMessage,
	[property: JsonPropertyName("files_affected")]      int      FilesAffected
) : ToolResult;

// ── Syntax check ─────────────────────────────────────────────────────────────

/// <summary>Result returned by <c>roslyn_check_syntax</c>.</summary>
internal sealed record CheckSyntaxResult(
	[property: JsonPropertyName("valid")]         bool             Valid,
	[property: JsonPropertyName("error_count")]   int              ErrorCount,
	[property: JsonPropertyName("warning_count")] int              WarningCount,
	[property: JsonPropertyName("items")]         DiagnosticItem[] Items
) : ToolResult;

// ── Server info ──────────────────────────────────────────────────────────────

internal sealed record InfoResult(
	[property: JsonPropertyName("version")]           string  Version,
	[property: JsonPropertyName("pid")]               int     Pid,
	[property: JsonPropertyName("uptime_seconds")]    long    UptimeSeconds,
	[property: JsonPropertyName("msbuild_discovery")] string  MsbuildDiscovery,
	[property: JsonPropertyName("prune_errors")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	IReadOnlyDictionary<string, DateTimeOffset[]>?    PruneErrors = null,
	[property: JsonPropertyName("marker")]
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	string?                                           Marker = null
) : ToolResult;

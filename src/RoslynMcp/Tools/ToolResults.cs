using System.Text.Json.Serialization;

namespace RoslynMcp.Tools;

// ── Shared ──────────────────────────────────────────────────────────────────

/// <summary>A single compiler diagnostic item returned by <c>roslyn_get_diagnostics</c> and <c>roslyn_build_project</c>.</summary>
internal sealed record DiagnosticItem(
	[property: JsonPropertyName("code")]     string  Code,
	[property: JsonPropertyName("severity")] string  Severity,
	[property: JsonPropertyName("file")]     string? File,
	[property: JsonPropertyName("line")]     int     Line,
	[property: JsonPropertyName("column")]   int     Column,
	[property: JsonPropertyName("message")]  string  Message
);

/// <summary>Cached page response from <see cref="RoslynMcpTool.TryServeCachedPage{T}"/>.</summary>
internal sealed record CachedPageResult<T>(
	[property: JsonPropertyName("items")]      T[]    Items,
	[property: JsonPropertyName("total")]      int    Total,
	[property: JsonPropertyName("skip")]       int    Skip,
	[property: JsonPropertyName("take")]       int    Take,
	[property: JsonPropertyName("page_token")] string PageToken,
	[property: JsonPropertyName("has_more")]   bool   HasMore
);

// ── Analysis tools ──────────────────────────────────────────────────────────

internal sealed record FileOutlineResult(
	[property: JsonPropertyName("file")]        string   File,
	[property: JsonPropertyName("total_types")] int      TotalTypes,
	[property: JsonPropertyName("skip")]        int      Skip,
	[property: JsonPropertyName("take")]        int      Take,
	[property: JsonPropertyName("types")]       object[] Types,
	[property: JsonPropertyName("page_token")]  string   PageToken,
	[property: JsonPropertyName("has_more")]    bool     HasMore,
	[property: JsonPropertyName("_caution")]    string?  Caution
);

internal sealed record FindReferencesResult(
	[property: JsonPropertyName("total_references")]  int      TotalReferences,
	[property: JsonPropertyName("symbols_searched")]  string[] SymbolsSearched,
	[property: JsonPropertyName("skip")]              int      Skip,
	[property: JsonPropertyName("take")]              int      Take,
	[property: JsonPropertyName("references")]        string[] References,
	[property: JsonPropertyName("page_token")]        string   PageToken,
	[property: JsonPropertyName("has_more")]          bool     HasMore,
	[property: JsonPropertyName("_caution")]          string?  Caution
);

internal sealed record FindImplementationsResult(
	[property: JsonPropertyName("symbol_type")]            string   SymbolType,
	[property: JsonPropertyName("symbol_name")]            string   SymbolName,
	[property: JsonPropertyName("total_implementations")]  int      TotalImplementations,
	[property: JsonPropertyName("skip")]                   int      Skip,
	[property: JsonPropertyName("take")]                   int      Take,
	[property: JsonPropertyName("implementations")]        string[] Implementations,
	[property: JsonPropertyName("page_token")]             string?  PageToken    = null,
	[property: JsonPropertyName("has_more")]               bool     HasMore      = false,
	[property: JsonPropertyName("_caution")]               string?  Caution      = null
);

internal sealed record FindOverridesResult(
	[property: JsonPropertyName("symbol_type")]    string   SymbolType,
	[property: JsonPropertyName("symbol_name")]    string   SymbolName,
	[property: JsonPropertyName("total_overrides")] int     TotalOverrides,
	[property: JsonPropertyName("skip")]           int      Skip,
	[property: JsonPropertyName("take")]           int      Take,
	[property: JsonPropertyName("overrides")]      string[] Overrides,
	[property: JsonPropertyName("page_token")]     string?  PageToken    = null,
	[property: JsonPropertyName("has_more")]       bool     HasMore      = false,
	[property: JsonPropertyName("_caution")]       string?  Caution      = null
);

internal sealed record ListTypesResult(
	[property: JsonPropertyName("total_types")] int      TotalTypes,
	[property: JsonPropertyName("skip")]        int      Skip,
	[property: JsonPropertyName("take")]        int      Take,
	[property: JsonPropertyName("types")]       string[] Types,
	[property: JsonPropertyName("page_token")]  string   PageToken,
	[property: JsonPropertyName("has_more")]    bool     HasMore,
	[property: JsonPropertyName("_caution")]    string?  Caution
);

internal sealed record TypeHierarchyResult(
	[property: JsonPropertyName("type_name")]              string   TypeName,
	[property: JsonPropertyName("type_kind")]              string   TypeKind,
	[property: JsonPropertyName("base_types")]             string[] BaseTypes,
	[property: JsonPropertyName("total_interfaces")]       int      TotalInterfaces,
	[property: JsonPropertyName("total_derived_types")]    int      TotalDerivedTypes,
	[property: JsonPropertyName("skip")]                   int      Skip,
	[property: JsonPropertyName("take")]                   int      Take,
	[property: JsonPropertyName("interfaces_and_derived")] string[] InterfacesAndDerived,
	[property: JsonPropertyName("page_token")]             string   PageToken,
	[property: JsonPropertyName("has_more")]               bool     HasMore,
	[property: JsonPropertyName("_caution")]               string?  Caution
);

internal sealed record TypeMembersResult(
	[property: JsonPropertyName("type_name")]     string    TypeName,
	[property: JsonPropertyName("type_kind")]     string    TypeKind,
	[property: JsonPropertyName("total_members")] int       TotalMembers,
	[property: JsonPropertyName("skip")]          int       Skip,
	[property: JsonPropertyName("take")]          int       Take,
	[property: JsonPropertyName("members")]       object?[] Members,
	[property: JsonPropertyName("page_token")]    string    PageToken,
	[property: JsonPropertyName("has_more")]      bool      HasMore,
	[property: JsonPropertyName("_caution")]      string?   Caution
);

internal sealed record ReadFileResult(
	[property: JsonPropertyName("file")]        string   File,
	[property: JsonPropertyName("source")]      string   Source,
	[property: JsonPropertyName("total_lines")] int      TotalLines,
	[property: JsonPropertyName("start_line")]  int      StartLine,
	[property: JsonPropertyName("end_line")]    int      EndLine,
	[property: JsonPropertyName("lines")]       string[] Lines,
	[property: JsonPropertyName("_caution")]    string?  Caution
);

internal sealed record LineCountResult(
	[property: JsonPropertyName("files")]    object[] Files,
	[property: JsonPropertyName("_caution")] string?  Caution
);

internal sealed record LineCountEntry(
	[property: JsonPropertyName("file")]       string  File,
	[property: JsonPropertyName("line_count")] int?    LineCount,
	[property: JsonPropertyName("error")]      string? Error
);

internal sealed record MemberBodySingleResult(
	[property: JsonPropertyName("symbol_name")] string  SymbolName,
	[property: JsonPropertyName("symbol_kind")] string  SymbolKind,
	[property: JsonPropertyName("file")]        string  File,
	[property: JsonPropertyName("start_line")]  int     StartLine,
	[property: JsonPropertyName("end_line")]    int     EndLine,
	[property: JsonPropertyName("body")]        string  Body,
	[property: JsonPropertyName("_caution")]    string? Caution
);

internal sealed record MemberBodyPartialResult(
	[property: JsonPropertyName("symbol_name")] string        SymbolName,
	[property: JsonPropertyName("symbol_kind")] string        SymbolKind,
	[property: JsonPropertyName("parts")]       List<object>  Parts,
	[property: JsonPropertyName("note")]        string        Note,
	[property: JsonPropertyName("_caution")]    string?       Caution
);

internal sealed record MemberBodyPart(
	[property: JsonPropertyName("file")]       string  File,
	[property: JsonPropertyName("start_line")] int     StartLine,
	[property: JsonPropertyName("end_line")]   int     EndLine,
	[property: JsonPropertyName("body")]       string  Body,
	[property: JsonPropertyName("part_index")] int?    PartIndex
);

internal sealed record MetadataSymbolResult(
	[property: JsonPropertyName("symbol_name")] string SymbolName,
	[property: JsonPropertyName("symbol_kind")] string SymbolKind,
	[property: JsonPropertyName("location")]    string Location,
	[property: JsonPropertyName("error")]       string Error
) : ToolErrorResult;

internal sealed record SymbolDefinitionResult(
	[property: JsonPropertyName("symbol_name")] string  SymbolName,
	[property: JsonPropertyName("symbol_kind")] string  SymbolKind,
	[property: JsonPropertyName("file")]        string  File,
	[property: JsonPropertyName("line")]        int     Line,
	[property: JsonPropertyName("column")]      int     Column,
	[property: JsonPropertyName("signature")]   string  Signature,
	[property: JsonPropertyName("doc_summary")] string? DocSummary,
	[property: JsonPropertyName("_caution")]    string? Caution
);

internal sealed record SymbolDocumentationResult(
	[property: JsonPropertyName("symbol_name")] string  SymbolName,
	[property: JsonPropertyName("symbol_kind")] string  SymbolKind,
	[property: JsonPropertyName("summary")]     string? Summary,
	[property: JsonPropertyName("parameters")]  object? Parameters,
	[property: JsonPropertyName("returns")]     string? Returns,
	[property: JsonPropertyName("remarks")]     string? Remarks,
	[property: JsonPropertyName("example")]     string? Example,
	[property: JsonPropertyName("_caution")]    string? Caution
);

internal sealed record SymbolDocumentationEmptyResult(
	[property: JsonPropertyName("symbol_name")]  string  SymbolName,
	[property: JsonPropertyName("symbol_kind")]  string  SymbolKind,
	[property: JsonPropertyName("documentation")] string? Documentation,
	[property: JsonPropertyName("error")]        string  Error
) : ToolErrorResult;

internal sealed record SymbolInfoResult(
	[property: JsonPropertyName("kind")]             string  Kind,
	[property: JsonPropertyName("name")]             string  Name,
	[property: JsonPropertyName("containing_type")]  string? ContainingType,
	[property: JsonPropertyName("type_or_return")]   string? TypeOrReturn,
	[property: JsonPropertyName("_caution")]         string? Caution = null
);

internal sealed record SymbolsInScopeResult(
	[property: JsonPropertyName("file")]       string                              File,
	[property: JsonPropertyName("line")]       int                                 Line,
	[property: JsonPropertyName("column")]     int                                 Column,
	[property: JsonPropertyName("locals")]     GetSymbolsInScopeTool.SymbolInfo[]  Locals,
	[property: JsonPropertyName("parameters")] GetSymbolsInScopeTool.SymbolInfo[]  Parameters,
	[property: JsonPropertyName("fields")]     GetSymbolsInScopeTool.SymbolInfo[]  Fields,
	[property: JsonPropertyName("properties")] GetSymbolsInScopeTool.SymbolInfo[]  Properties,
	[property: JsonPropertyName("methods")]    GetSymbolsInScopeTool.SymbolInfo[]  Methods,
	[property: JsonPropertyName("types")]      GetSymbolsInScopeTool.SymbolInfo[]  Types,
	[property: JsonPropertyName("other")]      GetSymbolsInScopeTool.SymbolInfo[]  Other,
	[property: JsonPropertyName("_caution")]   string?                             Caution
);

internal sealed record GetUsingsResult(
	[property: JsonPropertyName("file")]          string                         File,
	[property: JsonPropertyName("usings")]        GetUsingsTool.UsingDirective[] Usings,
	[property: JsonPropertyName("global_usings")] string[]                       GlobalUsings,
	[property: JsonPropertyName("_caution")]      string?                        Caution
);

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
	[property: JsonPropertyName("version")]               string?   Version,
	[property: JsonPropertyName("root_namespace")]        string?   RootNamespace,
	[property: JsonPropertyName("target_frameworks")]     string[]? TargetFrameworks,
	[property: JsonPropertyName("allow_unsafe_blocks")]   bool?     AllowUnsafeBlocks,
	[property: JsonPropertyName("warnings_as_errors")]    bool?     WarningsAsErrors,
	[property: JsonPropertyName("_caution")]              string?   Caution
);

internal sealed record GetTriviaNoMatchResult(
	[property: JsonPropertyName("error")]        string   Error,
	[property: JsonPropertyName("message")]      string   Message,
	[property: JsonPropertyName("hint")]         string   Hint,
	[property: JsonPropertyName("provided_kind")] string  ProvidedKind,
	[property: JsonPropertyName("common_kinds")] string[] CommonKinds
) : ToolErrorResult;

internal sealed record TriviaNodeSpan(
	[property: JsonPropertyName("start")]      int Start,
	[property: JsonPropertyName("end")]        int End,
	[property: JsonPropertyName("start_line")] int StartLine,
	[property: JsonPropertyName("end_line")]   int EndLine
);

internal sealed record TriviaNodeResult(
	[property: JsonPropertyName("node_kind")]      string         NodeKind,
	[property: JsonPropertyName("node_span")]      TriviaNodeSpan NodeSpan,
	[property: JsonPropertyName("node_text")]      string         NodeText,
	[property: JsonPropertyName("leading_trivia")] object[]       LeadingTrivia,
	[property: JsonPropertyName("trailing_trivia")] object[]      TrailingTrivia
);

internal sealed record GetTriviaResult(
	[property: JsonPropertyName("file")]            string   File,
	[property: JsonPropertyName("total_nodes")]     int      TotalNodes,
	[property: JsonPropertyName("filtered_nodes")]  int      FilteredNodes,
	[property: JsonPropertyName("skip")]            int      Skip,
	[property: JsonPropertyName("take")]            int      Take,
	[property: JsonPropertyName("results")]         object[] Results,
	[property: JsonPropertyName("page_token")]      string   PageToken,
	[property: JsonPropertyName("has_more")]        bool     HasMore
);

internal sealed record TriviaEntry(
	[property: JsonPropertyName("kind")] string    Kind,
	[property: JsonPropertyName("text")] string    Text,
	[property: JsonPropertyName("span")] TriviaSpan Span
);

internal sealed record TriviaSpan(
	[property: JsonPropertyName("start")] int Start,
	[property: JsonPropertyName("end")]   int End
);

internal sealed record ListFilesResult(
	[property: JsonPropertyName("files")]      string[] Files,
	[property: JsonPropertyName("count")]      int      Count,
	[property: JsonPropertyName("skip")]       int      Skip,
	[property: JsonPropertyName("take")]       int      Take,
	[property: JsonPropertyName("page_token")] string   PageToken,
	[property: JsonPropertyName("has_more")]   bool     HasMore,
	[property: JsonPropertyName("_caution")]   string?  Caution
);

internal sealed record ListFilesEmptyResult(
	[property: JsonPropertyName("files")]         string[]  Files,
	[property: JsonPropertyName("count")]         int       Count,
	[property: JsonPropertyName("close_matches")] string[]? CloseMatches,
	[property: JsonPropertyName("_caution")]      string?   Caution
);

internal sealed record SearchFilesResult(
	[property: JsonPropertyName("matches")]       object[] Matches,
	[property: JsonPropertyName("total_matches")] int      TotalMatches,
	[property: JsonPropertyName("returned")]      int      Returned,
	[property: JsonPropertyName("page_token")]    string   PageToken,
	[property: JsonPropertyName("has_more")]      bool     HasMore,
	[property: JsonPropertyName("_caution")]      string?  Caution
);

internal sealed record SemanticSearchResult(
	[property: JsonPropertyName("matches")]       object[] Matches,
	[property: JsonPropertyName("total_matches")] int      TotalMatches,
	[property: JsonPropertyName("returned")]      int      Returned,
	[property: JsonPropertyName("page_token")]    string   PageToken,
	[property: JsonPropertyName("has_more")]      bool     HasMore,
	[property: JsonPropertyName("context")]       string?  Context  = null,
	[property: JsonPropertyName("_caution")]      string?  Caution  = null
);

internal sealed record SemanticSearchListResult(
	[property: JsonPropertyName("values")]      string[] Values,
	[property: JsonPropertyName("description")] string   Description
);

internal sealed record DiscoveryKindsResult(
	[property: JsonPropertyName("mode")]        string   Mode,
	[property: JsonPropertyName("count")]       int      Count,
	[property: JsonPropertyName("common_kinds")] string[] CommonKinds,
	[property: JsonPropertyName("all_kinds")]   string[] AllKinds
);

internal sealed record DiscoveryValuesResult(
	[property: JsonPropertyName("mode")]  string   Mode,
	[property: JsonPropertyName("kinds")] string[] Kinds,
	[property: JsonPropertyName("hint")]  string   Hint
);

internal sealed record DiscoveryContextsResult(
	[property: JsonPropertyName("mode")]     string   Mode,
	[property: JsonPropertyName("contexts")] string[] Contexts,
	[property: JsonPropertyName("hint")]     string   Hint
);

internal sealed record DiscoveryNoMatchResult(
	[property: JsonPropertyName("error")]        string   Error,
	[property: JsonPropertyName("message")]      string   Message,
	[property: JsonPropertyName("hint")]         string   Hint,
	[property: JsonPropertyName("provided_kind")] string  ProvidedKind,
	[property: JsonPropertyName("valid_kinds")]  string[] ValidKinds
);

internal sealed record DetailedErrorResult(
	[property: JsonPropertyName("error")]   string Error,
	[property: JsonPropertyName("details")] string Details
) : ToolErrorResult;

// ── Build tools ─────────────────────────────────────────────────────────────

internal sealed record BuildResult(
	[property: JsonPropertyName("succeeded")]      bool             Succeeded,
	[property: JsonPropertyName("errors")]         DiagnosticItem[] Errors,
	[property: JsonPropertyName("warnings")]       DiagnosticItem[] Warnings,
	[property: JsonPropertyName("source")]         string           Source,
	[property: JsonPropertyName("build_skipped")]  bool             BuildSkipped,
	[property: JsonPropertyName("skip_reason")]    string?          SkipReason,
	[property: JsonPropertyName("duration_ms")]    long             DurationMs,
	[property: JsonPropertyName("exit_code")]      int?             ExitCode,
	[property: JsonPropertyName("error_details")]  string?          ErrorDetails = null
);

internal sealed record ReplaceInCodeResult(
	[property: JsonPropertyName("applied")]       bool     Applied,
	[property: JsonPropertyName("change_count")]  int      ChangeCount,
	[property: JsonPropertyName("changed_nodes")] object[] ChangedNodes,
	[property: JsonPropertyName("message")]       string?  Message     = null,
	[property: JsonPropertyName("syntax_valid")]  bool     SyntaxValid = true
);

internal sealed record ReplaceInCodeNodeInfo(
	[property: JsonPropertyName("original_text")] string OriginalText,
	[property: JsonPropertyName("line")]           int    Line,
	[property: JsonPropertyName("column")]         int    Column
);

internal sealed record ReplaceInCodeSyntaxError(
	[property: JsonPropertyName("error")]        string   Error,
	[property: JsonPropertyName("details")]      string   Details,
	[property: JsonPropertyName("changed_nodes")] object[]? ChangedNodes = null
) : ToolErrorResult;

internal sealed record RespawnResult(
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("pid")]     int    Pid,
	[property: JsonPropertyName("tip")]     string Tip
);

internal sealed record DebugAttachResult(
	[property: JsonPropertyName("attached")] bool   Attached,
	[property: JsonPropertyName("pid")]      int    Pid,
	[property: JsonPropertyName("message")]  string Message
);

internal sealed record DebugAlreadyAttachedResult(
	[property: JsonPropertyName("already_attached")] bool   AlreadyAttached,
	[property: JsonPropertyName("pid")]              int    Pid,
	[property: JsonPropertyName("message")]          string Message
);

// ── Base class error shapes ────────────────────────────────────────────────

internal sealed record PathErrorResult(
	[property: JsonPropertyName("error")]          string    Error,
	[property: JsonPropertyName("message")]        string    Message,
	[property: JsonPropertyName("provided_path")]  string?   ProvidedPath  = null,
	[property: JsonPropertyName("search_path")]    string?   SearchPath    = null,
	[property: JsonPropertyName("directory")]      string?   Directory     = null,
	[property: JsonPropertyName("file_name")]      string?   FileName      = null,
	[property: JsonPropertyName("found_projects")] string[]? FoundProjects = null,
	[property: JsonPropertyName("found_in")]       string[]? FoundIn       = null,
	[property: JsonPropertyName("hint")]           string?   Hint          = null
) : ToolErrorResult;

internal sealed record UnexpectedErrorResult(
	[property: JsonPropertyName("error")]   string Error,
	[property: JsonPropertyName("message")] string Message,
	[property: JsonPropertyName("type")]    string Type
) : ToolErrorResult;

// ── Editing tools ───────────────────────────────────────────────────────────

internal sealed record ReplaceInFileResult(
	[property: JsonPropertyName("applied")]       bool    Applied,
	[property: JsonPropertyName("match_count")]   int     MatchCount,
	[property: JsonPropertyName("changed_lines")] int[]   ChangedLines,
	[property: JsonPropertyName("message")]       string? Message = null
);

internal sealed record InsertLinesResult(
	[property: JsonPropertyName("applied")]        bool    Applied,
	[property: JsonPropertyName("inserted_at")]    int     InsertedAt,
	[property: JsonPropertyName("line_count")]     int     LineCount,
	[property: JsonPropertyName("inserted_lines")] int[]   InsertedLines,
	[property: JsonPropertyName("message")]        string? Message = null
);

// ── Refactoring tools ───────────────────────────────────────────────────────

internal sealed record ChangeSignatureResult(
	[property: JsonPropertyName("diff")]                string   Diff,
	[property: JsonPropertyName("token")]               string   Token,
	[property: JsonPropertyName("message")]             string   Message,
	[property: JsonPropertyName("parameters_added")]    string[] ParametersAdded,
	[property: JsonPropertyName("deprecation_message")] string?  DeprecationMessage,
	[property: JsonPropertyName("files_affected")]      int      FilesAffected,
	[property: JsonPropertyName("_caution")]            string?  Caution
);

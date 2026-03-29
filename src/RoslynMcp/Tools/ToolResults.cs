namespace RoslynMcp.Tools;

// ── Shared ──────────────────────────────────────────────────────────────────

/// <summary>Cached page response from <see cref="RoslynMcpTool.TryServeCachedPage{T}"/>.</summary>
internal sealed record CachedPageResult<T>(
	T[]    Items,
	int    Total,
	int    Skip,
	int    Take,
	string Page_token,
	bool   Has_more
);

// ── Analysis tools ──────────────────────────────────────────────────────────

internal sealed record FileOutlineResult(
	string  File,
	int     Total_types,
	int     Skip,
	int     Take,
	object[] Types,
	string  Page_token,
	bool    Has_more,
	string? _caution
);

internal sealed record FindReferencesResult(
	int      Total_references,
	string[] Symbols_searched,
	int      Skip,
	int      Take,
	string[] References,
	string   Page_token,
	bool     Has_more,
	string?  _caution
);

internal sealed record FindImplementationsResult(
	string   Symbol_type,
	string   Symbol_name,
	int      Total_implementations,
	int      Skip,
	int      Take,
	string[] Implementations,
	string?  Page_token    = null,
	bool     Has_more      = false,
	string?  _caution      = null
);

internal sealed record FindOverridesResult(
	string   Symbol_type,
	string   Symbol_name,
	int      Total_overrides,
	int      Skip,
	int      Take,
	string[] Overrides,
	string?  Page_token    = null,
	bool     Has_more      = false,
	string?  _caution      = null
);

internal sealed record ListTypesResult(
	int      Total_types,
	int      Skip,
	int      Take,
	string[] Types,
	string   Page_token,
	bool     Has_more,
	string?  _caution
);

internal sealed record TypeHierarchyResult(
	string   Type_name,
	string   Type_kind,
	string[] Base_types,
	int      Total_interfaces,
	int      Total_derived_types,
	int      Skip,
	int      Take,
	string[] Interfaces_and_derived,
	string   Page_token,
	bool     Has_more,
	string?  _caution
);

internal sealed record TypeMembersResult(
	string    Type_name,
	string    Type_kind,
	int       Total_members,
	int       Skip,
	int       Take,
	object?[] Members,
	string    Page_token,
	bool      Has_more,
	string?   _caution
);

internal sealed record ReadFileResult(
	string   File,
	string   Source,
	int      Total_lines,
	int      Start_line,
	int      End_line,
	string[] Lines,
	string?  _caution
);

internal sealed record LineCountResult(
	object[] Files,
	string?  _caution
);

internal sealed record LineCountEntry(
	string  File,
	int?    Line_count,
	string? Error
);

internal sealed record MemberBodySingleResult(
	string  Symbol_name,
	string  Symbol_kind,
	string  File,
	int     Start_line,
	int     End_line,
	string  Body,
	string? _caution
);

internal sealed record MemberBodyPartialResult(
	string        Symbol_name,
	string        Symbol_kind,
	List<object>  Parts,
	string        Note,
	string?       _caution
);

internal sealed record MemberBodyPart(
	string  File,
	int     Start_line,
	int     End_line,
	string  Body,
	int?    Part_index
);

internal sealed record MetadataSymbolResult(
	string Symbol_name,
	string Symbol_kind,
	string Location,
	string Message
);

internal sealed record SymbolDefinitionResult(
	string  Symbol_name,
	string  Symbol_kind,
	string  File,
	int     Line,
	int     Column,
	string  Signature,
	string? Doc_summary,
	string? _caution
);

internal sealed record SymbolDocumentationResult(
	string  Symbol_name,
	string  Symbol_kind,
	string? Summary,
	object? Parameters,
	string? Returns,
	string? Remarks,
	string? Example,
	string? _caution
);

internal sealed record SymbolDocumentationEmptyResult(
	string  Symbol_name,
	string  Symbol_kind,
	string? Documentation,
	string  Message
);

internal sealed record SymbolInfoResult(
	string  Kind,
	string  Name,
	string? Containing_type,
	string? Type_or_return,
	string? _caution = null
);

internal sealed record SymbolsInScopeResult(
	string                              File,
	int                                 Line,
	int                                 Column,
	GetSymbolsInScopeTool.SymbolInfo[]  Locals,
	GetSymbolsInScopeTool.SymbolInfo[]  Parameters,
	GetSymbolsInScopeTool.SymbolInfo[]  Fields,
	GetSymbolsInScopeTool.SymbolInfo[]  Properties,
	GetSymbolsInScopeTool.SymbolInfo[]  Methods,
	GetSymbolsInScopeTool.SymbolInfo[]  Types,
	GetSymbolsInScopeTool.SymbolInfo[]  Other,
	string?                             _caution
);

internal sealed record GetUsingsResult(
	string              File,
	GetUsingsTool.UsingDirective[] Usings,
	string[]            Global_usings,
	string?             _caution
);

internal sealed record ProjectInfoResult(
	string   Name,
	string   Assembly_name,
	string?  File_path,
	string?  Target_framework,
	string   Language_version,
	string   Output_kind,
	string   Nullable,
	bool     Is_msbuild_workspace,
	object[] Package_references,
	string[] Additional_files,
	string?  _caution
);

// ── Editing tools ───────────────────────────────────────────────────────────

internal sealed record ReplaceInFileResult(
	bool  Applied,
	int   MatchCount,
	int[] ChangedLines,
	string? Message = null
);

internal sealed record InsertLinesResult(
	bool  Applied,
	int   InsertedAt,
	int   LineCount,
	int[] InsertedLines,
	string? Message = null
);

// ── Refactoring tools ───────────────────────────────────────────────────────

internal sealed record ChangeSignatureResult(
	string   Diff,
	string   Token,
	string   Message,
	string[] Parameters_added,
	string?  Deprecation_message,
	int      Files_affected,
	string?  _caution
);

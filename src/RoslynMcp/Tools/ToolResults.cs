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

using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InsertLinesTool : RoslynMcpTool
{
	readonly BackupStore backups;
	static readonly string[] LineSeparators = ["\r\n", "\n"];
	
	// The answer to a call that names no location, or several; null for exactly one.
	static EditStop? CheckLocation(int? atLine, string? insertAfter, string? insertBefore)
		=> ((atLine.HasValue ? 1 : 0) + (insertAfter is not null ? 1 : 0) + (insertBefore is not null ? 1 : 0)) switch {
			
			0 => new(EditStopKind.Failed, "no location", new ErrorResult("Specify exactly one of: atLine, insertAfter, or insertBefore.")),
			1 => null,
			_ => new(EditStopKind.Failed, "multiple locations", new ErrorResult("Specify only one of: atLine, insertAfter, or insertBefore.")),
		}
	;
	
	/// <summary>
	///     The core of the tool (#475): where <paramref name="text"/> goes in
	///     <paramref name="content"/>, as a line index for the tool itself and as one change to the
	///     text for <c>roslyn_multi_edit</c>, which puts it together with the other edits of a
	///     batch. Nothing is read or written.
	/// </summary>
	internal static InsertPlan Plan(string content, string text, int? atLine, string? insertAfter, string? insertBefore)
	{
		if(CheckLocation(atLine, insertAfter, insertBefore) is { } noLocation)
			
			return new() { Stop = noLocation };
		
		// Detect existing line-ending style before splitting strips them.
		var eol   = content.Contains("\r\n") ? "\r\n" : "\n"
		;
		var lines = content.Split(LineSeparators, StringSplitOptions.None);
		
		// Split produces a trailing empty element when the file ends with a newline — trim it
		// so the insertion index math stays consistent with File.ReadAllLines behavior.
		if(lines.Length > 0 && lines[^1].Length == 0)
			lines = lines[..^1];
		
		// Resolve insertion index (0-based, insert BEFORE this index).
		int insertIndex
		;
		
		if(atLine.HasValue)
			// atLine is 1-based, clamp to valid range [1, lines.Length + 1].
			insertIndex = Math.Clamp(atLine.Value, 1, lines.Length + 1) - 1
			;
		
		else if(insertAfter is not null) {
			
			var matchIndex = Array.FindIndex(lines, l => l.Contains(insertAfter, StringComparison.Ordinal));
			
			if(matchIndex < 0)
				
				return new() { Stop = new(EditStopKind.Failed, "anchor not found", new ErrorResult($"insertAfter pattern not found: {insertAfter}")) };
			
			insertIndex = matchIndex + 1;
		}
		
		else {
			
			var matchIndex = Array.FindIndex(lines, l => l.Contains(insertBefore!, StringComparison.Ordinal));
			
			if(matchIndex < 0)
				
				return new() { Stop = new(EditStopKind.Failed, "anchor not found", new ErrorResult($"insertBefore pattern not found: {insertBefore}")) };
			
			insertIndex = matchIndex;
		}
		
		// Split on both \r\n and \n to avoid trailing \r in lines.
		var newLines = text.Split(LineSeparators, StringSplitOptions.None)
		;
		
		// The same insertion as one change to the text: before the line's first character, or at
		// the end of the text, after a line break of its own where the last line has none.
		var position = 0;
		
		for(var line = 0; line < insertIndex && position < content.Length; line++) {
			
			var lineEnd = content.IndexOf('\n', position);
			
			position = lineEnd < 0 ? content.Length : lineEnd + 1;
		}
		
		var inserted = string.Join(eol, newLines) + eol;
		
		if(insertIndex >= lines.Length && content.Length > 0 && content[^1] != '\n')
			inserted = eol + inserted;
		
		return new() {
			
			Changes     = [new TextChange(new TextSpan(position, 0), inserted)],
			Detail      = $"{newLines.Length} line(s) inserted at line {insertIndex + 1}",
			Lines       = lines,
			NewLines    = newLines,
			InsertIndex = insertIndex,
			Eol         = eol,
		};
	}
	
	public InsertLinesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups)
		: base(workspace, logger, paginationCache) { this.backups = backups; }
	
	[McpServerTool(Name = "roslyn_insert_lines", Destructive = false, Title = "Insert Lines", OpenWorld = false)]
		[Description(
		"Use this to add new lines to a file at a specific location without constructing surrounding-context replacement patterns. " +
		"Specify the insertion point by 1-based line number (atLine), by a literal anchor pattern to insert after " +
		"(insertAfter), or by a literal anchor pattern to insert before (insertBefore) — exactly one specifier is required. " +
		"Lines are inserted exactly as provided; indentation is the caller's responsibility. " +
		"Anchor patterns use case-sensitive, whitespace-exact substring matching against line content — " +
		"tabs and spaces are NOT interchangeable. If an anchor fails, use roslyn_read_file to verify the exact content " +
		"(including indentation characters) before retrying. " +
		"Supports dryRun=true to preview the insertion without writing. Returns inserted_line_ranges as inclusive { start, end } 1-based line ranges; start equals end for a single inserted line. " +
		"For replacing existing C# syntax nodes, prefer roslyn_replace_in_code; " +
		"for text-level find/replace in any file type, use roslyn_replace_in_file."
	)]
	public async Task<object> InsertLines(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description("The text to insert. May contain newlines for multi-line insertion.")                                 ] string  text,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("1-based line number to insert BEFORE. Mutually exclusive with insertAfter/insertBefore.")            ] int?    atLine       = null,
		[Description("Literal string pattern — inserts AFTER the first line that contains it. Returns an error if no match is found. Mutually exclusive with atLine/insertBefore.")                    ] string? insertAfter  = null,
		[Description("Literal string pattern — inserts BEFORE the first line that contains it. Returns an error if no match is found. Mutually exclusive with atLine/insertAfter.")                   ] string? insertBefore = null,
		[Description("Preview the insertion without writing. Returns what would change. Default: false.")                   ] bool    dryRun       = false
	)
	{
		using var scope = BeginTool("roslyn_insert_lines", filePath, new { atLine, insertAfter, insertBefore, dryRun });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		// Before the read, as it always was: a call without a location says so whatever the file is.
		if(CheckLocation(atLine, insertAfter, insertBefore) is { } noLocation)
			
			return scope.Failed(noLocation.Detail, noLocation.Answer);
		
		string rawContent;
		
		try {
			rawContent = FileReader.ReadAllText(fullPath, FileReader.DocumentLimit);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(ReadFailure(ex));
		}
		
		var plan = Plan(rawContent, text, atLine, insertAfter, insertBefore);
		
		if(plan.Stop is { } stop)
			
			return scope.Failed(stop.Detail, stop.Answer);
		
		var (lines, newLines, insertIndex, eol) = (plan.Lines, plan.NewLines, plan.InsertIndex, plan.Eol);
		
		// The whole file is written again from its lines, as this tool always did: the result ends
		// in a line break and has one line-ending style. A batch inserts the plan's change into
		// the text as it is instead.
		var resultLines = new List<string>(lines.Length + newLines.Length);
		
		resultLines.AddRange(lines[..insertIndex]);
		resultLines.AddRange(newLines);
		resultLines.AddRange(lines[insertIndex..]);
		
		
		if(dryRun)
			
			return scope.Outcome("dry run", new InsertLinesResult(false, insertIndex + 1, newLines.Length, Enumerable.Range(insertIndex + 1, newLines.Length),
				message: $"Dry run: {newLines.Length} line(s) would be inserted at line {insertIndex + 1}."));
		
		// Preserve the file's original line-ending style — computed once, used for backup,
		// write, and post-write verification.
		var resultText  = string.Join(eol, resultLines) + eol
		;
		var resultBytes = FileWriter.Utf8NoBom.GetBytes(resultText)
		;
		
		bool    preSaved  = false;
		string? backupToken = null;
		
		try {
			
			backupToken = await backups.SavePreAsync(fullPath, projectPath, "roslyn_insert_lines");
			preSaved    = backupToken is not null;
			await backups.SavePostAsync(fullPath, projectPath, "roslyn_insert_lines", resultBytes);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var hint = preSaved
				? "Resolve the issue and retry. The pre-change snapshot that was saved is not needed since the file was not touched."
				: "Resolve the issue (disk space or permissions) and retry.";
			
			return scope.Error(new ErrorResult(
				$"Write aborted — could not save {(preSaved ? "post" : "pre")}-change backup: {ex.Message}. The file was not modified.",
				hint));
		}
		
		try {
			
			await workspace.ApplyTextChange(projectPath, fullPath, SourceText.From(resultText, FileWriter.Utf8NoBom));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
			return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
		}
		
		// TryRecoverTruncation subsumes CheckForTruncation: it detects the same empty-file outcome
		// and additionally self-heals before reporting. Running both would re-stat the file and, for a
		// result of four bytes or fewer, report a truncation that recovery had already cleared.
		if(await TryRecoverTruncation(filePath, fullPath, projectPath, resultBytes) is { } truncErr)
			
			return scope.Error(truncErr);
		
		return scope.Outcome("inserted", new InsertLinesResult(true, insertIndex + 1, newLines.Length, Enumerable.Range(insertIndex + 1, newLines.Length), backupToken: backupToken));
	}
}

/// <summary>The plan of one <c>roslyn_insert_lines</c> edit: its change, and the lines the tool itself writes the file from.</summary>
internal sealed record InsertPlan : EditPlan
{
	public string[] Lines       { get; init; } = [];
	public string[] NewLines    { get; init; } = [];
	public int      InsertIndex { get; init; }
	public string   Eol         { get; init; } = "\n";
}

using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     <c>roslyn_multi_edit</c> (#475): several edits, of several kinds, in several files, in one
///     call. Nine of ten edits in the server's logs were part of a run of edits made back to back,
///     more than half of the runs crossed files and a quarter mixed tools; an agent that had five
///     small changes to make wrote a script instead of six tool calls.
///     <para>
///         Every edit of a batch is resolved against its file <em>as it was before the batch</em>,
///         the file the agent last read, by the core of the tool it names
///         (<see cref="EditPlan"/>). The changes of a file are then made in one pass from the
///         bottom up, so no edit moves the place another one names: the habit of someone who
///         edits a file by hand and starts at its end. What that costs is that one edit cannot
///         build on another of the same batch, and two that touch the same text are an error.
///     </para>
///     <para>
///         All or none, as far as that is in the server's hands: an edit that cannot be made, an
///         overlap, or a syntax error in a C# file as the batch leaves it, writes nothing. The
///         writes themselves are one per file, each after the backups of all of them; a write that
///         fails after others succeeded is reported file by file, with the backup tokens that
///         undo the ones made.
///     </para>
/// </summary>
[McpServerToolType]
internal sealed class MultiEditTool : RoslynMcpTool
{
	readonly BackupStore backups;
	
	public MultiEditTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups) : base(workspace, logger, paginationCache) { this.backups = backups; }
	
	// More than this in one call is a script, and an answer nobody reads.
	const int MaxEdits = 100;
	
	// One file of a batch: its text as it was, read once, and the changes the edits make to it.
	sealed class BatchFile
	{
		public required string Relative    { get; init; }
		public required string FullPath    { get; init; }
		
		// The file the name leads to: what the file is goes by it, whichever name the batch gave.
		public required string Physical    { get; init; }
		public required string ProjectPath { get; init; }
		public required string Content     { get; init; }
		
		// Of a C# file only.
		public SourceText? Text { get; init; }
		public SyntaxTree? Tree { get; init; }
		
		public List<(int Edit, TextChange Change)> Changes { get; } = [];
		
		// What each edit lays claim to (EditPlan.Claims): at least what it changes.
		public List<(int Edit, TextSpan Span)> Claims { get; } = [];
		
		public string  NewContent  { get; set; } = "";
		public string? BackupToken { get; set; }
	}
	
	[McpServerTool(Name = "roslyn_multi_edit", Destructive = true, Title = "Multi Edit", OpenWorld = false)]
	[Description(
		"Makes several edits in one call — in one file or several, of mixed kinds — and writes all of them or none. " +
		"Use it whenever a change is more than one edit: it saves a call per edit, and line numbers do not go stale between them. " +
		"Each entry of edits names its op ('roslyn_replace_in_file', 'roslyn_insert_lines' or 'roslyn_replace_in_code'), its filePath, and the parameters of that tool, with the meaning they have there. " +
		"Every position in a batch — a line, a column, an anchor, a pattern — refers to the file as it is BEFORE the batch, the file as last read: " +
		"the server resolves all edits first and applies them from the bottom of each file up, so no edit moves the place another one names, and the entries can be given in any order. " +
		"The price: one edit cannot build on another of the same batch (insert a method, then edit it — put the final text in the insert), and two edits that change the same text are an error. " +
		"An insertion at the edge of a replaced span is fine, and two insertions at one place are written in the order given. " +
		"Nothing is written when any edit cannot be made: a pattern or a node that is not found, a literal pattern that matches several times without replaceAll, a replacement that does not parse, an overlap. " +
		"When an edit cannot be made, the answer names each edit's outcome (edits[], by its 1-based position in the call), so the one to correct is known. " +
		"A batch that was written answers edit_count and the files with their changed lines, and names only the edits that changed another number of places than one. " +
		"A C# file is parsed as the batch leaves it: a batch that adds a syntax error is refused, quoting the parser — the check is on the result of all edits together, so two edits that are each half of a change pass. allowSyntaxErrors: true writes anyway. The replacement text of a roslyn_replace_in_code edit must itself parse, whatever the rest of the batch does. " +
		"A project's XML files (.csproj, .props, .targets, .slnx and the like) are checked the same way for being well-formed XML as the batch leaves them. " +
		"A roslyn_replace_in_code edit lays claim to its whole node: another edit inside that node is an overlap, also where the two would not change the same characters. " +
		"On success, files[] lists each file with the line ranges that changed, in the file as it is now, and the backup token that restores it with roslyn_local_history. " +
		"Supports dryRun=true: everything is resolved and checked, nothing is written. " +
		"For a single edit, the tool of that kind is simpler; for a whole new file, roslyn_write_file."
	)]
	public async Task<object> MultiEdit(
		[Description("The edits, at most 100. Each names an op, a filePath and that op's parameters; positions refer to the file as it is before the batch.")] MultiEditEntry[] edits,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Resolve and check every edit without writing. Returns what would change. Default: false.")] bool dryRun = false,
		[Description("Write even when the batch adds syntax errors to a C# file; they are then named in _caution. Default: false — such a batch is refused.")] bool allowSyntaxErrors = false
	)
	{
		using var scope = BeginTool("roslyn_multi_edit", $"{edits?.Length ?? 0} edit(s)", new { edits = edits?.Select(edit => new { edit?.Op, edit?.FilePath }), dryRun, allowSyntaxErrors });
		
		if(edits is null or { Length: 0 })
			
			return scope.Error(new ErrorResult("No edits given.", "Pass edits: an array of { op, filePath, ... } entries."));
		
		if(edits.Length > MaxEdits)
			
			return scope.Error(new ErrorResult($"{edits.Length} edits in one call; the most is {MaxEdits}.", "Split the batch by file."));
		
		var outcomes = new MultiEditOutcome[edits.Length];
		var places   = new int[edits.Length];
		var files    = new Dictionary<string, BatchFile>(SecurityBoundary.PathComparer);
		var cautions = new List<string>();
		
		// Every edit is resolved, also after one failed: an answer that names all the edits to
		// correct saves a round for each.
		for(var index = 0; index < edits.Length; index++) {
			
			var edit = edits[index];
			var op   = NormalizeOp(edit?.Op);
			
			MultiEditOutcome Failed(string detail, object? error) => new(index + 1, op ?? edit?.Op ?? "", edit?.FilePath ?? "", false, detail) { Error = error };
			
			if(edit is null || op is null) {
				
				outcomes[index] = Failed($"Unknown op '{edit?.Op}'. An op is one of: roslyn_replace_in_file, roslyn_insert_lines, roslyn_replace_in_code.", null);
				continue;
			}
			
			if(string.IsNullOrWhiteSpace(edit.FilePath)) {
				
				outcomes[index] = Failed("filePath is required.", null);
				continue;
			}
			
			if(!TryResolveProjectArg(projectPath, edit.FilePath, out var fileProject, out var projectError)) {
				
				outcomes[index] = Failed(projectError.Error ?? "The project of the file could not be resolved.", projectError);
				continue;
			}
			
			if(!TryResolveFileContext(fileProject, out var rootPath, out var boundary, out var resolveError)) {
				
				outcomes[index] = Failed(resolveError.Error ?? "The workspace of the file could not be resolved.", resolveError);
				continue;
			}
			
			var fullPath = ResolveFilePath(edit.FilePath, rootPath, boundary, out var ambiguousFile);
			
			if(fullPath is null) {
				
				outcomes[index] = ambiguousFile is not null
					? Failed(ambiguousFile.Message ?? "ambiguous_file", ambiguousFile)
					: Failed($"File not found: {edit.FilePath}", null);
				continue;
			}
			
			// By the file a name leads to, not by the name: a file and a link to it are one file,
			// and as two they were each written whole, the second over the first's edits.
			var physical = boundary.ResolveAllowed(fullPath) ?? fullPath;
			
			// One read per file, whatever the number of its edits: it is the text every one of
			// them is resolved against.
			if(!files.TryGetValue(physical, out var file)) {
				
				string content;
				
				try {
					content = FileReader.ReadAllText(fullPath, FileReader.DocumentLimit);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					
					var failure = ReadFailure(ex);
					
					outcomes[index] = Failed(failure.Error ?? "The file could not be read.", failure);
					continue;
				}
				
				// What the file is goes by the file, as its identity does: a link named 'notes.txt'
				// to a C# file is a C# file, whichever name the batch gives first.
				var text = IsCSharpSourcePath(physical) ? SourceText.From(content) : null;
				
				files[physical] = file = new() {
					
					Relative    = edit.FilePath,
					FullPath    = fullPath,
					Physical    = physical,
					ProjectPath = fileProject,
					Content     = content,
					Text        = text,
					Tree        = text is null ? null : CSharpSyntaxTree.ParseText(text, ParseOptionsOf(fileProject, fullPath, physical), fullPath),
				};
			}
			
			var plan = op switch {
				
				"roslyn_replace_in_file" => PlanTextReplace(edit, file),
				"roslyn_insert_lines"    => PlanInsert(edit, file),
				_                 => PlanCodeReplace(edit, file),
			};
			
			foreach(var note in plan.Notes)
				scope.Record($"edit {index + 1}: {note}");
			
			if(plan.Stop is { } stop) {
				
				// The tool's own answer goes along whole: it is what says which matches there
				// were, or which nodes the file holds instead.
				outcomes[index] = Failed(stop.Answer.Error ?? (stop.Answer as ReplaceInFileResult)?.Message ?? (stop.Answer as ReplaceInCodeResult)?.Message ?? stop.Detail, stop.Answer);
				continue;
			}
			
			foreach(var change in plan.Changes)
				file.Changes.Add((index, change));
			
			foreach(var claim in plan.Claims)
				file.Claims.Add((index, claim));
			
			outcomes[index] = new(index + 1, op, edit.FilePath, true, plan.Detail);
			places[index]   = plan.Places;
			
			if(plan.Caution is not null)
				cautions.Add($"Edit {index + 1}: {plan.Caution}");
		}
		
		// Two edits that change the same text have no order that is right: each was written
		// against the text as it was.
		foreach(var file in files.Values) {
			
			foreach(var (first, second) in EditPlan.Overlaps([..file.Claims.Select(claim => claim.Span)])) {
				
				var (firstEdit, secondEdit) = (file.Claims[first].Edit, file.Claims[second].Edit);
				var line                    = LineOf(file.Content, file.Claims[second].Span.Start);
				
				// The first pair an edit is in is the one it is told about.
				foreach(var (edit, other) in new[] { (firstEdit, secondEdit), (secondEdit, firstEdit) })
					if(outcomes[edit].Ok)
						outcomes[edit] = outcomes[edit] with {
							
							Ok     = false,
							Detail = $"Changes the same text as edit {other + 1}, at line {line} of {file.Relative}. Positions refer to the file as it is before the batch: make the two changes in one edit.",
						};
			}
		}
		
		// What a batch is made of, in every entry: the next look at the logs asks whether batches
		// mix kinds and cross files as the runs of single edits did.
		scope.Record($"{string.Join(", ", outcomes.GroupBy(outcome => outcome.Op).Select(group => $"{group.Count()} {group.Key}"))}; {files.Count} file(s)");
		
		var failed = outcomes.Count(outcome => !outcome.Ok);
		
		if(failed > 0)
			
			return scope.Failed($"{failed} of {edits.Length} edit(s) cannot be made", new MultiEditResult(false, edits.Length, outcomes, null,
				$"Nothing was written: {failed} of {edits.Length} edit(s) cannot be made. Correct the ones with ok: false and send the batch again."));
		
		// The syntax check is on each file as the whole batch leaves it, which is what lets two
		// edits that are each half of a change go in together.
		var broken = new List<MultiEditFile>();
		
		foreach(var file in files.Values) {
			
			file.NewContent = EditPlan.Apply(file.Content, file.Changes.Select(change => change.Change));
			
			if(EditChecks.AddedErrors(file.Physical, file.Tree, file.Content, file.NewContent) is not { Length: > 0 } added)
				continue;
			
			var quoted = $"{added.Length} syntax error(s) in {file.Relative} as the batch leaves it: {ReplaceInFileTool.Quoted(added)}.";
			
			if(allowSyntaxErrors)
				cautions.Add($"The batch added {quoted}");
			else
				broken.Add(new(file.Relative, ChangedLines(file)) { State = "not_written", Error = $"The batch would add {quoted}" });
		}
		
		if(broken.Count > 0)
			
			return scope.Failed("would add syntax errors", new MultiEditResult(false, edits.Length, outcomes, [..broken],
				$"Nothing was written: the batch would add syntax errors to {broken.Count} file(s), named in files[]. The line numbers there are those of the file as the batch would leave it.")
			{
				Hint = "Correct the edits of those files. When the batch is one half of a change that a later one completes, pass allowSyntaxErrors: true.",
			});
		
		var caution = cautions.Count > 0 ? string.Join(" ", cautions) : null;
		
		// Every edit can be made, so the answer names only the ones that changed another number of
		// places than one (a regex, replaceAll, several nodes): the rest it would repeat from the
		// call, at thirty tokens an edit (#490). A caution names its edit by number itself.
		MultiEditOutcome[] telling = [..outcomes.Where((_, index) => places[index] != 1)];
		var named = telling.Length > 0 ? telling : null;
		
		if(dryRun)
			
			return scope.Outcome("dry run", new MultiEditResult(false, edits.Length, named, [..files.Values.Select(file => new MultiEditFile(file.Relative, ChangedLines(file)))],
				$"Dry run: {edits.Length} edit(s) in {files.Count} file(s) would be made.") { Caution = caution });
		
		// Every backup before the first write: a batch whose backups cannot all be saved has
		// changed nothing.
		foreach(var file in files.Values) {
			
			var (token, backupError) = await SaveBackupsAsync(backups, file.FullPath, file.ProjectPath, "roslyn_multi_edit", FileWriter.Utf8NoBom.GetBytes(file.NewContent));
			
			if(backupError is not null)
				
				return scope.Error(backupError);
			
			file.BackupToken = token;
		}
		
		var written = new List<MultiEditFile>(files.Count);
		string? writeError = null;
		
		foreach(var file in files.Values) {
			
			// After a write failed, the rest is left alone: half a batch is bad enough.
			if(writeError is not null) {
				
				written.Add(new(file.Relative, []) { State = "not_written" });
				continue;
			}
			
			try {
				
				await workspace.ApplyTextChange(file.ProjectPath, file.FullPath, SourceText.From(file.NewContent, FileWriter.Utf8NoBom));
				
				if(await TryRecoverTruncation(file.Relative, file.FullPath, file.ProjectPath, FileWriter.Utf8NoBom.GetBytes(file.NewContent)) is { } truncated)
					writeError = truncated.Error ?? "The file was truncated by the write.";
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
				writeError = $"Write failed — '{file.Relative}' may be in an inconsistent state: {ex.Message}.";
			}
			
			written.Add(writeError is null
				? new(file.Relative, ChangedLines(file)) { BackupToken = file.BackupToken }
				: new(file.Relative, []) { BackupToken = file.BackupToken, State = "failed", Error = writeError });
		}
		
		if(writeError is not null)
			
			return scope.Failed("write failed", new MultiEditResult(false, edits.Length, outcomes, [..written],
				$"The batch was written in part: {writeError} files[] says which files were written, which failed and which were left alone.")
			{
				Hint = "Undo a written file with roslyn_local_history (action: apply) and its backup_token, or send the edits of the other files again.",
			});
		
		return scope.Outcome($"{edits.Length} edit(s) in {files.Count} file(s)", new MultiEditResult(true, edits.Length, named, [..written], null) { Caution = caution });
	}
	
	// An op is the name of the tool it stands for, and is reported under that name. The name
	// without the tools' prefix is taken too, since a batch written that way says what it means;
	// it is named nowhere, because a short name an agent has read is one it will call (#326).
	static string? NormalizeOp(string? op)
	{
		var name = op?.Trim().ToLowerInvariant() ?? "";
		
		if(!name.StartsWith(ToolPrefix, StringComparison.Ordinal))
			name = ToolPrefix + name;
		
		return name is "roslyn_replace_in_file" or "roslyn_insert_lines" or "roslyn_replace_in_code" ? name : null;
	}
	
	const string ToolPrefix = "roslyn_";
	
	// An edit that cannot be planned for a reason of the batch's own: an argument its op needs.
	sealed record NoPlan : EditPlan;
	
	static EditPlan Refused(string error, string? hint)
		=> new NoPlan { Stop = new(EditStopKind.Error, error, new ErrorResult(error, hint)) }
	;
	
	static EditPlan PlanTextReplace(MultiEditEntry edit, BatchFile file)
	{
		if(edit.Pattern is null || edit.Replacement is null)
			
			return Refused("roslyn_replace_in_file needs pattern and replacement.", null);
		
		if(!TryResolveMatchMode(edit.Mode, false, "useRegex", MatchMode.Regex, MatchMode.Literal, out var matchMode, out _, out var modeError))
			
			return Refused(modeError, null);
		
		Regex regex;
		
		try {
			regex = BuildContentRegex(edit.Pattern, matchMode, edit.CaseSensitive ?? true);
		}
		catch(ArgumentException ex) {
			return Refused($"Invalid regex pattern: {ex.Message}", null);
		}
		
		// Never as a dry run: the cores word their answers for it, and a batch's dry run must
		// refuse what its write refuses.
		return ReplaceInFileTool.Plan(file.Relative, file.Content, file.Tree, file.Text, regex, matchMode, edit.Replacement, true, string.IsNullOrWhiteSpace(edit.InMember) ? null : edit.InMember, edit.ReplaceAll, false, null);
	}
	
	static EditPlan PlanInsert(MultiEditEntry edit, BatchFile file)
		=> edit.Text is null
			? Refused("roslyn_insert_lines needs text.", null)
			: InsertLinesTool.Plan(file.Content, edit.Text, edit.AtLine, edit.InsertAfter, edit.InsertBefore)
	;
	
	static EditPlan PlanCodeReplace(MultiEditEntry edit, BatchFile file)
	{
		if(file.Tree is null || file.Text is null)
			
			return Refused("roslyn_replace_in_code needs a C# source file (.cs).", "Use op roslyn_replace_in_file for this file.");
		
		if(edit.NodeKind is null || !ReplaceInCodeTool.TryParseSyntaxKind(edit.NodeKind, out var kind))
			
			return Refused($"Unknown node kind: {edit.NodeKind}.", "Examples: MethodDeclaration, ConstructorDeclaration, FieldDeclaration, IdentifierName.");
		
		if(edit.Line < 0 || edit.Column < 0 || (edit.Column > 0 && edit.Line == 0))
			
			return Refused($"Line {edit.Line}, column {edit.Column} is not a valid position.", "Both are 1-based, and column requires line.");
		
		return ReplaceInCodeTool.Plan(file.Tree.GetRoot(), file.Text, file.FullPath, kind, edit.TextPattern, edit.Replacement ?? "", false, edit.Force, false, true, edit.Line, edit.Column, validateFile: false);
	}
	
	static int LineOf(string content, int position)
		=> 1 + content.AsSpan(0, Math.Clamp(position, 0, content.Length)).Count('\n')
	;
	
	// The lines of the new text the changes of a file wrote, or, for a deletion, the line it was
	// made on. Each change sits in the new text where it sat in the old one, moved by what the
	// changes before it added or took away.
	static LineRange[] ChangedLines(BatchFile file)
	{
		var newText = SourceText.From(file.NewContent);
		var lines   = new SortedSet<int>();
		var shift   = 0;
		
		foreach(var (_, change) in file.Changes.OrderBy(change => change.Change.Span.Start).ThenBy(change => change.Change.Span.Length)) {
			
			var length = change.NewText?.Length ?? 0;
			var start  = Math.Clamp(change.Span.Start + shift, 0, newText.Length);
			
			// An insertion behind a last line that had no line break begins with one: it has
			// written the lines after that break, not the line the break now ends. Only there: a
			// text that opens with a blank line anywhere else has written that blank line.
			var behindLastLine = change.Span.Start == file.Content.Length && file.Content.Length > 0 && file.Content[^1] != '\n';
			
			var lead = !behindLastLine || change.Span.Length > 0 || change.NewText is not { Length: > 1 } written ? 0
				: written.StartsWith("\r\n", StringComparison.Ordinal) && written.Length > 2 ? 2
				: written[0] == '\n' ? 1
				: 0
			;
			
			start = Math.Min(start + lead, newText.Length);
			
			var end = Math.Clamp(start + Math.Max(0, length - lead - 1), 0, newText.Length);
			
			// A change that ends in a line break has written the line before the break, not the
			// one after it.
			if(end > start && end < file.NewContent.Length && file.NewContent[end] == '\n')
				end--;
			
			for(var line = newText.Lines.GetLinePosition(start).Line; line <= newText.Lines.GetLinePosition(end).Line; line++)
				lines.Add(line + 1);
			
			shift += length - change.Span.Length;
		}
		
		return MultiEditResult.Ranges(lines);
	}
}

/// <summary>
///     One edit of a <c>roslyn_multi_edit</c> batch: the tool it stands for, the file, and that
///     tool's parameters. Only the ones of its <see cref="Op"/> are read.
/// </summary>
internal sealed class MultiEditEntry
{
	[Description("The kind of edit: 'roslyn_replace_in_file' (pattern, replacement; optional mode, caseSensitive, inMember, replaceAll), 'roslyn_insert_lines' (text, and one of atLine, insertAfter, insertBefore) or 'roslyn_replace_in_code' (nodeKind; optional textPattern, replacement, line, column, force). Each takes the parameters of the tool of that name.")]
	public string Op { get; set; } = "";
	
	[Description("Relative path to the file from the workspace root.")]
	public string FilePath { get; set; } = "";
	
	[Description("roslyn_replace_in_file: the pattern to find; literal unless mode says otherwise. A literal pattern must match once, or pass replaceAll.")]
	public string? Pattern { get; set; }
	
	[Description("roslyn_replace_in_file: the replacement text. roslyn_replace_in_code: the new text of the node; empty or omitted deletes the node.")]
	public string? Replacement { get; set; }
	
	[Description("roslyn_replace_in_file: 'literal' (default), 'regex' or 'glob'.")]
	public string? Mode { get; set; }
	
	[Description("roslyn_replace_in_file: case-sensitive matching. Default: true.")]
	public bool? CaseSensitive { get; set; }
	
	[Description("roslyn_replace_in_file, C# files: count only the matches inside this declaration, e.g. 'Foo' or 'Type.Foo'.")]
	public string? InMember { get; set; }
	
	[Description("roslyn_replace_in_file: replace every match of a literal pattern. Default: false.")]
	public bool ReplaceAll { get; set; }
	
	[Description("roslyn_insert_lines: the text to insert; may hold several lines.")]
	public string? Text { get; set; }
	
	[Description("roslyn_insert_lines: the 1-based line to insert before, in the file as it is before the batch.")]
	public int? AtLine { get; set; }
	
	[Description("roslyn_insert_lines: insert after the first line that contains this literal text.")]
	public string? InsertAfter { get; set; }
	
	[Description("roslyn_insert_lines: insert before the first line that contains this literal text.")]
	public string? InsertBefore { get; set; }
	
	[Description("roslyn_replace_in_code: the syntax node kind, e.g. 'method', 'property', 'class', 'IfStatement'.")]
	public string? NodeKind { get; set; }
	
	[Description("roslyn_replace_in_code: the declared name of the node (for other nodes, text it contains).")]
	public string? TextPattern { get; set; }
	
	[Description("roslyn_replace_in_code: the 1-based line the node starts on, in the file as it is before the batch. Default: 0 — any line.")]
	public int Line { get; set; }
	
	[Description("roslyn_replace_in_code: the 1-based column the node starts at. Requires line. Default: 0 — any column.")]
	public int Column { get; set; }
	
	[Description("roslyn_replace_in_code: replace every matching node. Default: false.")]
	public bool Force { get; set; }
}

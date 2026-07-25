using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewCodeFixTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	readonly CodeFixHost   codeFixHost;
	
	public PreviewCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, CodeFixHost codeFixHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals   = approvals;
		this.codeFixHost = codeFixHost;
	}
	
	[McpServerTool(Name = "roslyn_preview_code_fix", ReadOnly = true, Title = "Preview Code Fix", OpenWorld = false)]
	[Description(
		"Preview actions from code-fix providers bundled with RoslynMcp for a single diagnostic at a file location. " +
		"Returns available action choices when multiple fixes exist, or returns a unified diff plus token " +
		"for the selected fix. This is step 1 of a two-step workflow; no files are written until " +
		"roslyn_apply_code_fix is called. Phase 1 supports targeted modifications to existing in-workspace .cs files only; " +
		"file creation, deletion, external linked files, project-system changes, and FixAll are rejected.")]
	public async Task<object> PreviewCodeFix(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Relative or absolute path to the C# file containing the diagnostic.")] string filePath,
		[Description("1-based line number near or inside the diagnostic span.")] int line,
		[Description("1-based column number near or inside the diagnostic span.")] int column,
		CancellationToken cancellationToken,
		[Description("Optional diagnostic ID to disambiguate when multiple diagnostics are on the same line, e.g. 'RMCP001'.")] string? diagnosticId = null,
		[Description("Optional zero-based code action index. Required when multiple fixes are available.")] int? actionIndex = null)
	{
		using var scope = BeginTool("roslyn_preview_code_fix", filePath, new { line, column, diagnosticId, actionIndex });
		
		if(!TryGetProject(projectPath, out var project, out var error))
			
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var fullPath = ResolveFilePath(filePath, rootPath);
		
		if(fullPath is null)
			return scope.Failed("file not found", new PreviewCodeFixResult(
				null, null, null, [],
				$"File '{filePath}' not found under project root.",
				"file not found"));
		
		var document = project.Documents.FirstOrDefault(d =>
			string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
		
		if(document is null)
			return scope.Failed("document not found", new PreviewCodeFixResult(
				null, null, null, [],
				$"File '{filePath}' is not a C# document in the resolved project.",
				"document not found"));
		
		var text = await document.GetTextAsync(cancellationToken);
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			return scope.Failed("invalid location", new PreviewCodeFixResult(
				null, null, null, [],
				$"Line {line}, column {column} is outside '{filePath}'.",
				"invalid location"));
		
		var diagnostics = await GetDocumentDiagnosticsAsync(project, document, cancellationToken);
		var diagnostic = SelectDiagnostic(diagnostics, fullPath, text, position, line, diagnosticId, out var matches);
		
		if(diagnostic is null) {
			
			var actions = matches
				.Select((d, i) => new CodeFixActionChoice(i, d.Id, d.GetMessage(), null, null, null))
				.ToArray()
			;
			
			var message = matches.Length > 1
				? "Multiple diagnostics matched this location. Re-run with diagnosticId."
				: "No diagnostic was found at the requested location."
			;
			
			return scope.Failed(matches.Length > 1 ? "diagnostic ambiguous" : "diagnostic not found", new PreviewCodeFixResult(
				null, null, null, actions, message,
				matches.Length > 1 ? "diagnostic ambiguous" : "diagnostic not found"));
		}
		
		ImmutableArray<AvailableCodeFix> fixes;
		
		try {
			
			fixes = await codeFixHost.GetFixesAsync(document, diagnostic, cancellationToken);
		}
		catch(CodeFixProviderException ex) {
			
			return scope.Failed("code fix provider failed", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				ex.Message,
				"code fix provider failed"));
		}
		
		var choices = fixes
			.Select((f, i) => new CodeFixActionChoice(i, diagnostic.Id, diagnostic.GetMessage(), f.Title, f.ProviderName, f.EquivalenceKey))
			.ToArray()
		;
		
		if(fixes.Length == 0)
			return scope.Failed("no fixes", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				$"Diagnostic '{diagnostic.Id}' was found, but no code-fix provider bundled with RoslynMcp supports it.",
				"no fixes"));
		
		if(fixes.Length > 1 && actionIndex is null)
			return scope.Outcome($"{fixes.Length} action(s)", new PreviewCodeFixResult(
				null, diagnostic.Id, null, choices,
				"Multiple fixes are available. Re-run with actionIndex to preview one.",
				null));
		
		var selectedIndex = actionIndex ?? 0;
		
		if(selectedIndex < 0 || selectedIndex >= fixes.Length)
			return scope.Failed("invalid action index", new PreviewCodeFixResult(
				null, diagnostic.Id, null, choices,
				$"actionIndex must be between 0 and {fixes.Length - 1}.",
				"invalid action index"));
		
		var selected = fixes[selectedIndex];
		Solution newSolution;
		
		try {
			
			newSolution = await GetChangedSolutionAsync(selected.Action, cancellationToken);
		}
		catch(UnsupportedCodeActionOperationException ex) {
			
			return scope.Failed("unsupported operation shape", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				ex.Message,
				"unsupported operation shape"));
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			return scope.Failed("code action calculation failed", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				$"Selected code fix failed while calculating its operations: {ex.Message}",
				"code action calculation failed"));
		}
		
		try {
			
			await ValidateSupportedChangesAsync(
				document.Project.Solution,
				newSolution,
				rootPath,
				cancellationToken);
		}
		catch(UnsupportedCodeFixChangeException ex) {
			return scope.Failed("unsupported code-fix change", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				ex.Message,
				"unsupported code-fix change"));
		}
		
		var diff = await SolutionDiff.BuildAsync(document.Project.Solution, newSolution, cancellationToken);
		
		if(diff == "(no changes)")
			return scope.Failed("no changes", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, choices,
				"Selected fix completed but produced no file changes.",
				"no changes"));
		
		var operationKey = $"codefix:{diagnostic.Id}:{selected.ProviderName}:{selected.EquivalenceKey ?? selected.Title}";
		IReadOnlyDictionary<string, PreviewFileState> fileStates;
		
		try {
			
			fileStates = await BuildPreviewFileStatesAsync(
				document.Project.Solution,
				newSolution,
				cancellationToken);
		}
		catch(Exception ex) when(ex is UnsupportedFileEncodingException or DecoderFallbackException or EncoderFallbackException) {
			
			return scope.Failed("unsupported file encoding", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"{ex.Message} No approval token was created.",
				"unsupported file encoding"));
		}

		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return scope.Failed("preview file state changed", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"Could not capture a safe file state for this preview: {ex.Message} Re-run roslyn_preview_code_fix.",
				"preview file state changed"));
		}
		
		var (workspaceRoot, isMSBuild, csprojPath) = workspace.GetWorkspaceInfo(projectPath);
		var workspaceBinding = WorkspaceBinding.Create(workspaceRoot, isMSBuild, csprojPath);
		var token = approvals.Register(document.Project.Solution, newSolution, diff, operationKey, null, fileStates, workspaceBinding);
		var selectedAction = choices[selectedIndex];
		
		return scope.Outcome("preview ready", new PreviewCodeFixResult(
			token,
			diagnostic.Id,
			diff,
			[selectedAction],
			$"Review the diff, then call roslyn_apply_code_fix with token '{token}' and approval 'y' or 'n'.",
			null));
	}
	
	private static async Task<Solution> GetChangedSolutionAsync(CodeAction action, CancellationToken cancellationToken)
	{
		var operations = await action.GetOperationsAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		if(operations.Length != 1 || operations[0] is not ApplyChangesOperation applyChanges) {
			
			var returnedShape = operations.Length == 0
				? "no operations"
				: string.Join(", ", operations.Select(operation => operation.GetType().Name))
			;
			
			throw new UnsupportedCodeActionOperationException(
				$"Selected code fix returned an unsupported operation shape: {returnedShape}. " +
				"Exactly one ApplyChangesOperation is required.");
		}
		
		return applyChanges.ChangedSolution;
	}
	
	private static async Task ValidateSupportedChangesAsync(
		Solution baseSolution,
		Solution newSolution,
		string workspaceRoot,
		CancellationToken cancellationToken)
	{
		var changes = newSolution.GetChanges(baseSolution);
		
		if(changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
			throw new UnsupportedCodeFixChangeException("Phase 1 code fixes cannot add or remove projects.");
		
		var canonicalRoot = ResolveCanonicalPath(workspaceRoot);
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var changedDocumentCount = 0;
		
		foreach(var projectChange in changes.GetProjectChanges()) {
			var oldProject = projectChange.OldProject;
			var newProject = projectChange.NewProject;
			
			if(projectChange.GetAddedDocuments().Any() || projectChange.GetRemovedDocuments().Any())
				throw new UnsupportedCodeFixChangeException(
					"Phase 1 code fixes can modify existing C# files only; creating or deleting source files is not supported.");
			
			if(projectChange.GetAddedAdditionalDocuments().Any()
				|| projectChange.GetRemovedAdditionalDocuments().Any()
				|| projectChange.GetChangedAdditionalDocuments().Any()
				|| projectChange.GetAddedAnalyzerConfigDocuments().Any()
				|| projectChange.GetRemovedAnalyzerConfigDocuments().Any()
				|| projectChange.GetChangedAnalyzerConfigDocuments().Any())
				throw new UnsupportedCodeFixChangeException(
					"Phase 1 code fixes cannot change additional documents or analyzer configuration files.");
			
			if(projectChange.GetAddedProjectReferences().Any()
				|| projectChange.GetRemovedProjectReferences().Any()
				|| projectChange.GetAddedMetadataReferences().Any()
				|| projectChange.GetRemovedMetadataReferences().Any()
				|| projectChange.GetAddedAnalyzerReferences().Any()
				|| projectChange.GetRemovedAnalyzerReferences().Any())
				throw new UnsupportedCodeFixChangeException(
					"Phase 1 code fixes cannot change project, metadata, or analyzer references.");
			
			if(!string.Equals(oldProject.Name, newProject.Name, StringComparison.Ordinal)
				|| !string.Equals(oldProject.AssemblyName, newProject.AssemblyName, StringComparison.Ordinal)
				|| !string.Equals(oldProject.FilePath, newProject.FilePath, comparison)
				|| !Equals(oldProject.CompilationOptions, newProject.CompilationOptions)
				|| !Equals(oldProject.ParseOptions, newProject.ParseOptions))
				throw new UnsupportedCodeFixChangeException(
					"Phase 1 code fixes cannot change project identity, compilation options, or parse options.");
			
			foreach(var documentId in projectChange.GetChangedDocuments()) {
				var oldDocument = baseSolution.GetDocument(documentId);
				var newDocument = newSolution.GetDocument(documentId);
				
				if(oldDocument?.FilePath is null || newDocument?.FilePath is null)
					throw new UnsupportedCodeFixChangeException(
						"Phase 1 code fixes require every changed document to have a physical file path.");
				
				if(!string.Equals(oldDocument.FilePath, newDocument.FilePath, comparison))
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes cannot move or rename source file '{oldDocument.FilePath}'.");
				
				if(!string.Equals(Path.GetExtension(oldDocument.FilePath), ".cs", StringComparison.OrdinalIgnoreCase))
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes can modify C# files only; '{oldDocument.FilePath}' is not a .cs file.");
				
				if(!File.Exists(oldDocument.FilePath))
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes can modify existing C# files only; '{oldDocument.FilePath}' does not exist.");
				
				if(baseSolution.GetDocumentIdsWithFilePath(oldDocument.FilePath).Skip(1).Any())
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes cannot modify linked source file '{oldDocument.FilePath}'.");
				

				var canonicalFile = ResolveCanonicalPath(oldDocument.FilePath);
				var relativePath = Path.GetRelativePath(canonicalRoot, canonicalFile);
				
				if(Path.IsPathRooted(relativePath)
					|| relativePath == ".."
					|| relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
					|| relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes cannot modify external or linked file '{oldDocument.FilePath}'.");
				
				var oldText = await oldDocument.GetTextAsync(cancellationToken);
				
				if(IsGeneratedFile(canonicalFile, relativePath, oldText))
					throw new UnsupportedCodeFixChangeException(
						$"Phase 1 code fixes cannot modify generated source file '{oldDocument.FilePath}'.");
				
				EnsureFileWritable(oldDocument.FilePath);
				
				changedDocumentCount++;
			}
		}
		
		if(changedDocumentCount == 0)
			throw new UnsupportedCodeFixChangeException("The selected code fix contains no supported C# file modifications.");
	}
	
	private static bool IsGeneratedFile(string canonicalPath, string relativePath, SourceText text)
	{
		var segments = relativePath.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		
		if(segments.Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
			|| segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
			|| segment.Equals("generated", StringComparison.OrdinalIgnoreCase)))
			return true;
		
		var fileName = Path.GetFileName(canonicalPath);
		
		if(fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
			return true;
		
		var headerLength = Math.Min(text.Length, 2048);
		var header = text.ToString(TextSpan.FromBounds(0, headerLength));
		
		return header.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
			|| header.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase)
			|| header.Contains("auto-generated by", StringComparison.OrdinalIgnoreCase);
	}
	
	private static void EnsureFileWritable(string path)
	{
		try {
			
			if(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
				throw new UnauthorizedAccessException("The file has the read-only attribute.");
			
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Write,
				FileShare.ReadWrite | FileShare.Delete);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			throw new UnsupportedCodeFixChangeException(
				$"Phase 1 code fixes require writable source files; '{path}' cannot be opened for writing: {ex.Message}");
		}
	}
	

	private static string ResolveCanonicalPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var pathRoot = Path.GetPathRoot(fullPath)
			?? throw new UnsupportedCodeFixChangeException($"Path '{path}' has no filesystem root.");
		var current = pathRoot;
		var segments = fullPath[pathRoot.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		
		foreach(var segment in segments) {
			current = Path.Combine(current, segment);
			FileSystemInfo info = Directory.Exists(current)
				? new DirectoryInfo(current)
				: new FileInfo(current)
			;
			var target = info.ResolveLinkTarget(returnFinalTarget: true);
			
			if(target is not null)
				current = target.FullName;
		}
		
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
	}
	
	private static async Task<IReadOnlyDictionary<string, PreviewFileState>> BuildPreviewFileStatesAsync(
		Solution baseSolution,
		Solution newSolution,
		CancellationToken cancellationToken)
	{
		var states = new Dictionary<string, PreviewFileState>(StringComparer.OrdinalIgnoreCase);
		
		foreach(var projectChange in newSolution.GetChanges(baseSolution).GetProjectChanges()) {
			
			foreach(var docId in projectChange.GetChangedDocuments()) {
				
				var oldDocument = baseSolution.GetDocument(docId);
				var newDocument = newSolution.GetDocument(docId);
				
				if(oldDocument?.FilePath is null || newDocument is null)
					continue;
				
				if(!File.Exists(oldDocument.FilePath))
					throw new IOException($"Affected file '{oldDocument.FilePath}' no longer exists while creating the preview.");
				
				var originalBytes = await File.ReadAllBytesAsync(oldDocument.FilePath, cancellationToken);
				var intendedBytes = await EncodeChangedDocumentAsync(
					oldDocument,
					newDocument,
					originalBytes,
					cancellationToken);
				states[oldDocument.FilePath] = new PreviewFileState(
					ExpectedFileState.Exists,
					ComputeHash(originalBytes),
					intendedBytes);
			}
			
			foreach(var docId in projectChange.GetAddedDocuments()) {
				
				var doc = newSolution.GetDocument(docId);
				
				if(doc?.FilePath is null)
					continue;
				
				if(!baseSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty) {
					
					if(!File.Exists(doc.FilePath))
						throw new IOException($"Affected linked file '{doc.FilePath}' no longer exists while creating the preview.");
					
					var originalBytes = await File.ReadAllBytesAsync(doc.FilePath, cancellationToken);
					states[doc.FilePath] = new PreviewFileState(
						ExpectedFileState.Exists,
						ComputeHash(originalBytes),
						await EncodeNewDocumentAsync(doc, cancellationToken));
					
					continue;
				}
				
				if(File.Exists(doc.FilePath))
					throw new IOException($"New file '{doc.FilePath}' already exists while creating the preview.");
				
				states[doc.FilePath] = new PreviewFileState(
					ExpectedFileState.Absent,
					null,
					await EncodeNewDocumentAsync(doc, cancellationToken));
			}
			
			foreach(var docId in projectChange.GetRemovedDocuments()) {
				
				var doc = baseSolution.GetDocument(docId);
				
				if(doc?.FilePath is null || !newSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty)
					continue;
				
				if(!File.Exists(doc.FilePath))
					throw new IOException($"Affected file '{doc.FilePath}' no longer exists while creating the preview.");
				
				var originalBytes = await File.ReadAllBytesAsync(doc.FilePath, cancellationToken);
				states[doc.FilePath] = new PreviewFileState(
					ExpectedFileState.Exists,
					ComputeHash(originalBytes),
					null);
			}
		}
		
		return states;
	}
	
	private static async Task<byte[]> EncodeChangedDocumentAsync(
		Document oldDocument,
		Document newDocument,
		byte[] originalBytes,
		CancellationToken cancellationToken)
	{
		var oldText = await oldDocument.GetTextAsync(cancellationToken);
		var newText = await newDocument.GetTextAsync(cancellationToken);
		var encoding = CreateStrictEncoding(oldText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		var preamble = FindOriginalPreamble(originalBytes, encoding);
		var decoded = encoding.GetString(originalBytes, preamble.Length, originalBytes.Length - preamble.Length);
		
		if(!string.Equals(decoded, oldText.ToString(), StringComparison.Ordinal))
			throw new UnsupportedFileEncodingException(
				$"Encoding for '{oldDocument.FilePath}' could not be preserved exactly.");
		
		var content = encoding.GetBytes(newText.ToString());
		
		if(preamble.Length == 0)
			return content;
		
		var result = new byte[preamble.Length + content.Length];
		preamble.CopyTo(result, 0);
		content.CopyTo(result, preamble.Length);
		
		return result;
	}
	
	private static async Task<byte[]> EncodeNewDocumentAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var text = await document.GetTextAsync(cancellationToken);
		var encoding = CreateStrictEncoding(text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		
		return encoding.GetBytes(text.ToString());
	}
	
	private static Encoding CreateStrictEncoding(Encoding encoding)
	{
		var strict = (Encoding) encoding.Clone();
		strict.DecoderFallback = DecoderFallback.ExceptionFallback;
		strict.EncoderFallback = EncoderFallback.ExceptionFallback;
		
		return strict;
	}
	
	private static byte[] FindOriginalPreamble(byte[] bytes, Encoding encoding)
	{
		var preamble = encoding.CodePage switch {
			12000 => new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
			12001 => new byte[] { 0x00, 0x00, 0xFE, 0xFF },
			1200  => new byte[] { 0xFF, 0xFE },
			1201  => new byte[] { 0xFE, 0xFF },
			65001 => new byte[] { 0xEF, 0xBB, 0xBF },
			_     => encoding.GetPreamble()
		};
		
		return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)
			? preamble
			: [];
	}
	

	private static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
	
	private static Diagnostic? SelectDiagnostic(
		ImmutableArray<Diagnostic> diagnostics,
		string fullPath,
		SourceText text,
		int position,
		int line,
		string? diagnosticId,
		out Diagnostic[] matches)
	{
		var sameFile = diagnostics
			.Where(d => d.Location.IsInSource
				&& string.Equals(d.Location.SourceTree?.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)
				&& (diagnosticId is null || string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase)))
			.ToArray()
		;
		
		var exact = sameFile
			.Where(d => d.Location.SourceSpan.Contains(position) || d.Location.SourceSpan.Start == position)
			.ToArray()
		;
		
		matches = exact.Length > 0
			? exact
			: sameFile.Where(d => text.Lines.GetLinePosition(d.Location.SourceSpan.Start).Line + 1 == line).ToArray()
		;
		
		return matches.Length == 1 ? matches[0] : null;
	}
	
	private static async Task<ImmutableArray<Diagnostic>> GetDocumentDiagnosticsAsync(Project project, Document document, CancellationToken cancellationToken)
	{
		var compilation = await project.GetCompilationAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		if(compilation is null)
			return [];
		
		var tree = await document.GetSyntaxTreeAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		if(tree is null)
			return [];
		
		var builder = ImmutableArray.CreateBuilder<Diagnostic>();
		builder.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken));
		
		var analyzers = await GetAnalyzersAsync(project, cancellationToken);
		
		if(analyzers.Length > 0) {
			
			var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
			var analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken)
				.ConfigureAwait(false)
			;
			
			builder.AddRange(analyzerDiagnostics.Where(d => d.Location.SourceTree == tree));
		}
		
		return builder.ToImmutable();
	}
	
	private static async Task<ImmutableArray<DiagnosticAnalyzer>> GetAnalyzersAsync(Project project, CancellationToken cancellationToken)
	{
		var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
		
		foreach(var reference in project.AnalyzerReferences) {
			
			builder.AddRange(reference.GetAnalyzers(project.Language));
		}
		
		return builder.ToImmutable();
	}
}

internal sealed class UnsupportedFileEncodingException : Exception
{
	public UnsupportedFileEncodingException(string message)
		: base(message)
	{ }
}


internal sealed class UnsupportedCodeFixChangeException : Exception
{
	public UnsupportedCodeFixChangeException(string message)
		: base(message)
	{ }
}


internal sealed record PreviewCodeFixResult : ToolResult, IToolError
{
	public PreviewCodeFixResult(
		string? token,
		string? diagnosticId,
		string? diff,
		CodeFixActionChoice[] actions,
		string message,
		string? error)
	{
		Token        = token;
		DiagnosticId = diagnosticId;
		Diff         = diff;
		Actions      = actions;
		Message      = message;
		Error        = error;
	}
	
	public string?               Token        { get; }
	public string?               DiagnosticId { get; }
	public string?               Diff         { get; }
	public CodeFixActionChoice[] Actions      { get; }
	public string                Message      { get; }
}

internal sealed record CodeFixActionChoice(
	int     Index,
	string  DiagnosticId,
	string  DiagnosticMessage,
	string? Title,
	string? Provider,
	string? EquivalenceKey
);

internal sealed class UnsupportedCodeActionOperationException : Exception
{
	public UnsupportedCodeActionOperationException(string message)
		: base(message)
	{ }
}

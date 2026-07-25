#if DEBUG
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Tools;

/// <summary>
///     Harness-only provider enabled explicitly through ROSLYNMCP_TEST_CODE_FIXES.
///     Marker type names select deterministic action and filesystem edge cases.
/// </summary>
internal sealed class CodeFixTestProvider : CodeFixProvider
{
	public override ImmutableArray<string> FixableDiagnosticIds => ["CS0246"];
	public override FixAllProvider? GetFixAllProvider() => null;
	
	public override async Task RegisterCodeFixesAsync(CodeFixContext context)
	{
		var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
		
		if(root is null)
			return;
		
		var typeNode = root.FindNode(context.Span, getInnermostNodeForTie: true);
		var marker = typeNode.ToString();
		var diagnostic = context.Diagnostics[0];
		
		switch(marker) {
			
			case "TestMultipleActions":
				RegisterReplacement(context, diagnostic, typeNode, "object", "Use object", "test_object");
				RegisterReplacement(context, diagnostic, typeNode, "string", "Use string", "test_string");
				RegisterReplacement(context, diagnostic, typeNode, "dynamic", "Use dynamic", "test_dynamic");
				
				break;
			
			case "TestMultiWrite":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Change two files",
						ct => ChangeTwoFilesAsync(context.Document, typeNode, ct),
						"test_multi_write"),
					diagnostic);
				
				break;
			
			case "TestMixed":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Change, create, and delete files",
						ct => CreateMixedSolutionAsync(context.Document, typeNode, ct),
						"test_mixed"),
					diagnostic);
				
				break;
			
			case "TestPartial":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Write one file then fail",
						ct => CreatePartialFailureSolutionAsync(context.Document, typeNode, ct),
						"test_partial"),
					diagnostic);
				
				break;
			
			case "TestLinkedDivergent":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Create divergent linked documents",
						ct => CreateDivergentLinkedSolutionAsync(context.Document, typeNode, ct),
						"test_linked_divergent"),
					diagnostic);
				
				break;
			
			case "TestAdditionalDocument":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Add additional document",
						ct => AddAdditionalDocumentAsync(context.Document, ct),
						"test_additional_document"),
					diagnostic);
				
				break;
			
			case "TestAnalyzerConfig":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Add analyzer config document",
						ct => AddAnalyzerConfigDocumentAsync(context.Document, ct),
						"test_analyzer_config"),
					diagnostic);
				
				break;
			
			case "TestAddedProject":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Add project",
						ct => AddProjectAsync(context.Document, ct),
						"test_added_project"),
					diagnostic);
				
				break;
			
			case "TestMetadataReference":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Remove metadata reference",
						ct => RemoveMetadataReferenceAsync(context.Document, ct),
						"test_metadata_reference"),
					diagnostic);
				
				break;
			
			case "TestProjectOptions":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Change project compilation options",
						ct => ChangeProjectOptionsAsync(context.Document, ct),
						"test_project_options"),
					diagnostic);
				
				break;
			
			case "TestExternalPath":
				context.RegisterCodeFix(
					CodeAction.Create(
						"Move changed document outside workspace",
						ct => ChangeExternalPathAsync(context.Document, typeNode, ct),
						"test_external_path"),
					diagnostic);
				
				break;
			
			case "TestNoOperations":
				context.RegisterCodeFix(
					new FixedOperationsCodeAction("Return no operations", []),
					diagnostic);
				
				break;
			
			case "TestCustomOperation":
				context.RegisterCodeFix(
					new FixedOperationsCodeAction("Return custom operation", [new TestCustomOperation()]),
					diagnostic);
				
				break;
			
			case "TestMultipleOperations":
				var changedSolution = await ReplaceTypeAsync(context.Document, typeNode, "object", context.CancellationToken);
				context.RegisterCodeFix(
					new FixedOperationsCodeAction(
						"Return multiple apply operations",
						[new ApplyChangesOperation(changedSolution), new ApplyChangesOperation(changedSolution)]),
					diagnostic);
				
				break;
			
			case "TestThrowOperations":
				context.RegisterCodeFix(new ThrowingCodeAction(), diagnostic);
				
				break;
			
			default:
				RegisterReplacement(context, diagnostic, typeNode, "object", "Use object", "test_object");
				
				break;
		}
	}
	
	static void RegisterReplacement(
		CodeFixContext context,
		Diagnostic diagnostic,
		SyntaxNode typeNode,
		string replacement,
		string title,
		string equivalenceKey)
	{
		context.RegisterCodeFix(
			CodeAction.Create(
				title,
				ct => ReplaceTypeAsync(context.Document, typeNode, replacement, ct),
				equivalenceKey),
			diagnostic);
	}
	
	static async Task<Solution> ReplaceTypeAsync(
		Document document,
		SyntaxNode typeNode,
		string replacement,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken)
			?? throw new InvalidOperationException("Test document has no syntax root.");
		var replacementNode = Microsoft.CodeAnalysis.CSharp.SyntaxFactory
			.ParseTypeName(replacement)
			.WithTriviaFrom(typeNode)
		;
		
		return document.WithSyntaxRoot(root.ReplaceNode(typeNode, replacementNode)).Project.Solution;
	}
	
	static async Task<Solution> ChangeTwoFilesAsync(
		Document document,
		SyntaxNode typeNode,
		CancellationToken cancellationToken)
	{
		var solution = await ReplaceTypeAsync(document, typeNode, "object", cancellationToken);
		var second = solution.Projects
			.SelectMany(project => project.Documents)
			.First(candidate => candidate.Name == "_CodeFixSecond_.cs")
		;
		var secondText = await second.GetTextAsync(cancellationToken);
		
		return solution.WithDocumentText(
			second.Id,
			SourceText.From(secondText.ToString().Replace("before", "after", StringComparison.Ordinal)));
	}
	
	static Task<Solution> AddAdditionalDocumentAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		var solution = document.Project.Solution.AddAdditionalDocument(
			DocumentId.CreateNewId(document.Project.Id),
			"_CodeFixAdditional_.txt",
			SourceText.From("additional content\n"),
			filePath: Path.Combine(directory, "_CodeFixAdditional_.txt"));
		
		return Task.FromResult(solution);
	}
	
	static Task<Solution> AddAnalyzerConfigDocumentAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		var solution = document.Project.Solution.AddAnalyzerConfigDocument(
			DocumentId.CreateNewId(document.Project.Id),
			"_CodeFix.editorconfig",
			SourceText.From("root = true\n"),
			filePath: Path.Combine(directory, "_CodeFix.editorconfig"));
		
		return Task.FromResult(solution);
	}
	
	static Task<Solution> AddProjectAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var projectInfo = ProjectInfo.Create(
			ProjectId.CreateNewId(),
			VersionStamp.Create(),
			"AddedByCodeFix",
			"AddedByCodeFix",
			LanguageNames.CSharp);
		
		return Task.FromResult(document.Project.Solution.AddProject(projectInfo));
	}
	
	static Task<Solution> RemoveMetadataReferenceAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var reference = document.Project.MetadataReferences.FirstOrDefault()
			?? throw new InvalidOperationException("Test project has no metadata reference.");
		
		return Task.FromResult(document.Project.Solution.RemoveMetadataReference(document.Project.Id, reference));
	}
	

	static Task<Solution> ChangeProjectOptionsAsync(
		Document document,
		CancellationToken cancellationToken)
	{
		var options = document.Project.CompilationOptions
			?? throw new InvalidOperationException("Test project has no compilation options.");
		var solution = document.Project.Solution.WithProjectCompilationOptions(
			document.Project.Id,
			options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
		
		return Task.FromResult(solution);
	}
	

	static async Task<Solution> ChangeExternalPathAsync(
		Document document,
		SyntaxNode typeNode,
		CancellationToken cancellationToken)
	{
		var solution = await ReplaceTypeAsync(document, typeNode, "object", cancellationToken);
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		var parent = Path.GetDirectoryName(directory)
			?? throw new InvalidOperationException("Test document directory has no parent.");
		
		return solution.WithDocumentFilePath(
			document.Id,
			Path.Combine(parent, $"_{Path.GetFileName(directory)}_External.cs"));
	}
	

	static async Task<Solution> CreateMixedSolutionAsync(
		Document document,
		SyntaxNode typeNode,
		CancellationToken cancellationToken)
	{
		var solution = await ReplaceTypeAsync(document, typeNode, "object", cancellationToken);
		var project = solution.GetProject(document.Project.Id)
			?? throw new InvalidOperationException("Test project disappeared.");
		var removed = project.Documents.First(candidate => candidate.Name == "_CodeFixRemoved_.cs");
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		var addedId = DocumentId.CreateNewId(project.Id);
		
		solution = solution.RemoveDocument(removed.Id);
		
		return solution.AddDocument(
			addedId,
			"_CodeFixCreated_.cs",
			SourceText.From("// created by code-fix harness\nclass CreatedByCodeFix { }\n"),
			filePath: Path.Combine(directory, "_CodeFixCreated_.cs"));
	}
	
	static async Task<Solution> CreatePartialFailureSolutionAsync(
		Document document,
		SyntaxNode typeNode,
		CancellationToken cancellationToken)
	{
		var solution = await ReplaceTypeAsync(document, typeNode, "object", cancellationToken);
		var project = solution.GetProject(document.Project.Id)
			?? throw new InvalidOperationException("Test project disappeared.");
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		
		return solution.AddDocument(
			DocumentId.CreateNewId(project.Id),
			"_CodeFixInvalidTarget_.cs",
			SourceText.From("class InvalidTarget { }\n"),
			filePath: directory);
	}
	
	static async Task<Solution> CreateDivergentLinkedSolutionAsync(
		Document document,
		SyntaxNode typeNode,
		CancellationToken cancellationToken)
	{
		var solution = await ReplaceTypeAsync(document, typeNode, "object", cancellationToken);
		var project = solution.GetProject(document.Project.Id)
			?? throw new InvalidOperationException("Test project disappeared.");
		var directory = Path.GetDirectoryName(document.FilePath!)
			?? throw new InvalidOperationException("Test document has no directory.");
		var linkedPath = Path.Combine(directory, "_CodeFixLinked_.cs");
		
		solution = solution.AddDocument(
			DocumentId.CreateNewId(project.Id),
			"_CodeFixLinkedA_.cs",
			SourceText.From("class LinkedA { }\n"),
			filePath: linkedPath);
		
		return solution.AddDocument(
			DocumentId.CreateNewId(project.Id),
			"_CodeFixLinkedB_.cs",
			SourceText.From("class LinkedB { }\n"),
			filePath: linkedPath);
	}
	
	sealed class FixedOperationsCodeAction(
		string title,
		ImmutableArray<CodeActionOperation> operations) : CodeAction
	{
		public override string Title => title;
		public override string EquivalenceKey => $"test_operations_{title}";

		protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(
			CancellationToken cancellationToken) =>
			Task.FromResult<IEnumerable<CodeActionOperation>>(operations);
	}
	
	sealed class ThrowingCodeAction : CodeAction
	{
		public override string Title => "Throw while calculating operations";
		public override string EquivalenceKey => "test_throw_operations";

		protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Harness-requested action calculation failure.");
	}
	
	sealed class TestCustomOperation : CodeActionOperation;
}
#endif

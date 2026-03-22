using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Analyzers;

/// <summary>
/// Code fix provider that replaces <c>IntPtr</c> with <c>nint</c> and <c>UIntPtr</c> with <c>nuint</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PreferNintOverIntPtrCodeFixProvider)), Shared]
public sealed class PreferNintOverIntPtrCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["RMCP001", "RMCP002"];

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if(root is null)
            return;

        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var node = root.FindToken(diagnosticSpan.Start).Parent?.AncestorsAndSelf()
            .OfType<IdentifierNameSyntax>()
            .FirstOrDefault();

        if(node is null)
            return;

        var replacement = diagnostic.Id switch
        {
            "RMCP001" => "nint",
            "RMCP002" => "nuint",
            _ => null
        };

        if(replacement is null)
            return;

        var title = $"Replace with '{replacement}'";

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancellationToken => ReplaceWithNativeIntAsync(
                    context.Document,
                    node,
                    replacement,
                    cancellationToken
                ),
                equivalenceKey: title
            ),
            diagnostic
        );
    }

    private static async Task<Document> ReplaceWithNativeIntAsync(
        Document document,
        IdentifierNameSyntax identifierName,
        string replacement,
        CancellationToken cancellationToken
    )
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);

        if(root is null)
            return document;

        // Create the new identifier name (nint or nuint)
        var newIdentifier = SyntaxFactory.IdentifierName(replacement)
            .WithTriviaFrom(identifierName);

        var newRoot = root.ReplaceNode(identifierName, newIdentifier);

        return document.WithSyntaxRoot(newRoot);
    }
}

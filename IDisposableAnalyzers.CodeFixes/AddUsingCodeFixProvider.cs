namespace IDisposableAnalyzers
{
    using System.Collections.Immutable;
    using System.Composition;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Editing;
    using Microsoft.CodeAnalysis.Formatting;

    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddUsingCodeFixProvider))]
    [Shared]
    internal class AddUsingCodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
            IDISP001DisposeCreated.DiagnosticId,
            IDISP004DontIgnoreReturnValueOfTypeIDisposable.DiagnosticId);

        /// <inheritdoc/>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
                                          .ConfigureAwait(false);

            foreach (var diagnostic in context.Diagnostics)
            {
                var token = syntaxRoot.FindToken(diagnostic.Location.SourceSpan.Start);
                if (string.IsNullOrEmpty(token.ValueText) ||
                    token.IsMissing)
                {
                    continue;
                }

                var node = syntaxRoot.FindNode(diagnostic.Location.SourceSpan);
                if (diagnostic.Id == IDISP001DisposeCreated.DiagnosticId)
                {
                    var statement = node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
                    if (statement.Parent is BlockSyntax block)
                    {
                        context.RegisterCodeFix(
                            CodeAction.Create(
                                "Add using to end of block.",
                                cancellationToken => ApplyAddUsingFixAsync(context, block, statement,cancellationToken),
                                nameof(AddUsingCodeFixProvider)),
                            diagnostic);
                    }
                }

                if (diagnostic.Id == IDISP004DontIgnoreReturnValueOfTypeIDisposable.DiagnosticId)
                {
                    var statement = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
                    if (statement.Parent is BlockSyntax block)
                    {
                        context.RegisterCodeFix(
                            CodeAction.Create(
                                "Add using to end of block.",
                                cancellationToken => ApplyAddUsingFixAsync(context, block, statement,cancellationToken),
                                nameof(AddUsingCodeFixProvider)),
                            diagnostic);
                    }
                }
            }
        }

        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        private static async Task<Document> ApplyAddUsingFixAsync(CodeFixContext context, BlockSyntax block, LocalDeclarationStatementSyntax statement, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);
            var statements = block.Statements
                                  .Where(s => s.SpanStart > statement.SpanStart)
                                  .ToArray();
            foreach (var statementSyntax in statements)
            {
                editor.RemoveNode(statementSyntax);
            }

            editor.ReplaceNode(
                statement,
                SyntaxFactory.UsingStatement(
                    declaration: statement.Declaration,
                    expression: null,
                    statement: SyntaxFactory.Block(SyntaxFactory.List(statements))
                                            .WithAdditionalAnnotations(Formatter.Annotation)));
            return editor.GetChangedDocument();
        }

        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        private static async Task<Document> ApplyAddUsingFixAsync(CodeFixContext context, BlockSyntax block, ExpressionStatementSyntax statement, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);
            var statements = block.Statements
                                  .Where(s => s.SpanStart > statement.SpanStart)
                                  .ToArray();
            foreach (var statementSyntax in statements)
            {
                editor.RemoveNode(statementSyntax);
            }

            editor.ReplaceNode(
                statement,
                SyntaxFactory.UsingStatement(
                    declaration: null,
                    expression: statement.Expression,
                    statement: SyntaxFactory.Block(SyntaxFactory.List(statements))
                                            .WithAdditionalAnnotations(Formatter.Annotation)));
            return editor.GetChangedDocument();
        }
    }
}
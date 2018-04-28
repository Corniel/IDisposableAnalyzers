namespace IDisposableAnalyzers
{
    using System;
    using System.Threading;
    using Gu.Roslyn.AnalyzerExtensions;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    internal static partial class Disposable
    {
        internal static Result IsArgumentDisposedByInvocationReturnValue(MemberAccessExpressionSyntax memberAccess, SemanticModel semanticModel, CancellationToken cancellationToken, PooledSet<SyntaxNode> visited = null)
        {
            var symbol = SemanticModelExt.GetSymbolSafe(semanticModel, memberAccess, cancellationToken);
            if (symbol is IMethodSymbol method)
            {
                if (method.ReturnType.Name == "ConfiguredTaskAwaitable")
                {
                    return Result.Yes;
                }

                if (method.ContainingType.DeclaringSyntaxReferences.Length == 0)
                {
                    return method.ReturnsVoid ||
                           !IsAssignableTo(method.ReturnType)
                        ? Result.No
                        : Result.AssumeYes;
                }

                if (method.IsExtensionMethod &&
                    method.ReducedFrom is IMethodSymbol reducedFrom)
                {
                    var parameter = reducedFrom.Parameters[0];
                    return CheckReturnValues(parameter, memberAccess.Parent, semanticModel, cancellationToken, visited);
                }
            }

            if (SymbolExt.IsEither<IFieldSymbol, IPropertySymbol>(symbol))
            {
                return Result.No;
            }

            return Result.Unknown;
        }

        internal static Result IsArgumentDisposedByReturnValue(ArgumentSyntax argument, SemanticModel semanticModel, CancellationToken cancellationToken, PooledSet<SyntaxNode> visited = null)
        {
            if (argument?.Parent is ArgumentListSyntax argumentList)
            {
                if (argumentList.Parent is InvocationExpressionSyntax invocation &&
                    SemanticModelExt.GetSymbolSafe(semanticModel, invocation, cancellationToken) is IMethodSymbol method)
                {
                    if (method.ContainingType.DeclaringSyntaxReferences.Length == 0)
                    {
                        return method.ReturnsVoid ||
                               !IsAssignableTo(method.ReturnType)
                            ? Result.No
                            : Result.AssumeYes;
                    }

                    if (invocation.TryGetMatchingParameter(argument, semanticModel, cancellationToken, out var parameter))
                    {
                        return CheckReturnValues(parameter, invocation, semanticModel, cancellationToken, visited);
                    }

                    return Result.Unknown;
                }

                if (argumentList.Parent is ObjectCreationExpressionSyntax ||
                    argumentList.Parent is ConstructorInitializerSyntax)
                {
                    if (TryGetAssignedFieldOrProperty(argument, semanticModel, cancellationToken, out var member, out var ctor) &&
                        member != null)
                    {
                        var initializer = argument.FirstAncestorOrSelf<ConstructorInitializerSyntax>();
                        if (initializer != null)
                        {
                            if (SemanticModelExt.GetDeclaredSymbolSafe(semanticModel, initializer.Parent, cancellationToken) is IMethodSymbol chainedCtor &&
                                chainedCtor.ContainingType != member.ContainingType)
                            {
                                if (TryGetDisposeMethod(chainedCtor.ContainingType, Search.TopLevel, out var disposeMethod))
                                {
                                    return IsMemberDisposed(member, disposeMethod, semanticModel, cancellationToken)
                                        ? Result.Yes
                                        : Result.No;
                                }
                            }
                        }

                        return IsMemberDisposed(member, ctor.ContainingType, semanticModel, cancellationToken);
                    }

                    if (ctor == null)
                    {
                        return Result.AssumeYes;
                    }

                    if (ctor.ContainingType.DeclaringSyntaxReferences.Length == 0)
                    {
                        return IsAssignableTo(ctor.ContainingType) ? Result.AssumeYes : Result.No;
                    }

                    if (ctor.ContainingType.Is(KnownSymbol.NinjectStandardKernel))
                    {
                        return Result.Yes;
                    }

                    return Result.No;
                }
            }

            return Result.Unknown;
        }

        internal static Result IsArgumentAssignedToDisposable(ArgumentSyntax argument, SemanticModel semanticModel, CancellationToken cancellationToken, PooledSet<SyntaxNode> visited = null)
        {
            if (argument?.Parent is ArgumentListSyntax argumentList)
            {
                if (argumentList.Parent is InvocationExpressionSyntax invocation &&
                    SemanticModelExt.GetSymbolSafe(semanticModel, invocation, cancellationToken) is IMethodSymbol method)
                {
                    if (method == KnownSymbol.CompositeDisposable.Add)
                    {
                        return Result.Yes;
                    }

                    if (TryGetAssignedFieldOrProperty(argument, method, semanticModel, cancellationToken, out _))
                    {
                        return Result.Yes;
                    }

                    if (method.TrySingleDeclaration(cancellationToken, out BaseMethodDeclarationSyntax declaration) &&
                        method.TryFindParameter(argument, out var parameter))
                    {
                        using (visited = visited.IncrementUsage())
                        {
                            using (var walker = InvocationWalker.Borrow(declaration))
                            {
                                foreach (var nested in walker)
                                {
                                    if (nested.ArgumentList != null &&
                                        EnumerableExt.TryFirst(nested.ArgumentList.Arguments, x => x.Expression is IdentifierNameSyntax identifierName && identifierName.Identifier.ValueText == parameter.Name, out var nestedArg))
                                    {
                                        switch (IsArgumentAssignedToDisposable(nestedArg, semanticModel, cancellationToken, visited))
                                        {
                                            case Result.Unknown:
                                                break;
                                            case Result.Yes:
                                                return Result.Yes;
                                            case Result.AssumeYes:
                                                return Result.AssumeYes;
                                            case Result.No:
                                                break;
                                            case Result.AssumeNo:
                                                break;
                                            default:
                                                throw new ArgumentOutOfRangeException();
                                        }
                                    }
                                }
                            }
                        }
                    }

                    return Result.No;
                }
            }

            return Result.No;
        }

        private static Result CheckReturnValues(IParameterSymbol parameter, SyntaxNode memberAccess, SemanticModel semanticModel, CancellationToken cancellationToken, PooledSet<SyntaxNode> visited)
        {
            var result = Result.No;
            using (var returnWalker = ReturnValueWalker.Borrow(memberAccess, Search.Recursive, semanticModel, cancellationToken))
            {
                using (visited = visited.IncrementUsage())
                {
                    if (!visited.Add(memberAccess))
                    {
                        return Result.Unknown;
                    }

                    foreach (var returnValue in returnWalker)
                    {
                        switch (CheckReturnValue(returnValue))
                        {
                            case Result.Unknown:
                                return Result.Unknown;
                            case Result.Yes:
                                if (result == Result.No)
                                {
                                    result = Result.Yes;
                                }

                                break;
                            case Result.AssumeYes:
                                result = Result.AssumeYes;
                                break;
                            case Result.No:
                                return Result.No;
                            case Result.AssumeNo:
                                return Result.AssumeNo;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                }
            }

            return result;

            Result CheckReturnValue(ExpressionSyntax returnValue)
            {
                if (returnValue is ObjectCreationExpressionSyntax nestedObjectCreation)
                {
                    if (nestedObjectCreation.TryGetMatchingArgument(parameter, out var nestedArgument))
                    {
                        return IsArgumentDisposedByReturnValue(nestedArgument, semanticModel, cancellationToken, visited);
                    }

                    return Result.No;
                }

                if (returnValue is InvocationExpressionSyntax nestedInvocation)
                {
                    if (nestedInvocation.TryGetMatchingArgument(parameter, out var nestedArgument))
                    {
                        return IsArgumentDisposedByReturnValue(nestedArgument, semanticModel, cancellationToken, visited);
                    }

                    return Result.No;
                }

                if (returnValue is MemberAccessExpressionSyntax nestedMemberAccess)
                {
                    return IsArgumentDisposedByInvocationReturnValue(nestedMemberAccess, semanticModel, cancellationToken, visited);
                }

                return Result.Unknown;
            }
        }

        private static bool TryGetAssignedFieldOrProperty(ArgumentSyntax argument, SemanticModel semanticModel, CancellationToken cancellationToken, out ISymbol member, out IMethodSymbol ctor)
        {
            if (TryGetConstructor(argument, semanticModel, cancellationToken, out ctor))
            {
                return TryGetAssignedFieldOrProperty(argument, ctor, semanticModel, cancellationToken, out member);
            }

            member = null;
            return false;
        }

        private static bool TryGetConstructor(ArgumentSyntax argument, SemanticModel semanticModel, CancellationToken cancellationToken, out IMethodSymbol ctor)
        {
            var objectCreation = SyntaxNodeExt.FirstAncestor<ObjectCreationExpressionSyntax>(argument);
            if (objectCreation != null)
            {
                ctor = SemanticModelExt.GetSymbolSafe(semanticModel, objectCreation, cancellationToken) as IMethodSymbol;
                return ctor != null;
            }

            var initializer = SyntaxNodeExt.FirstAncestor<ConstructorInitializerSyntax>(argument);
            if (initializer != null)
            {
                ctor = SemanticModelExt.GetSymbolSafe(semanticModel, initializer, cancellationToken);
                return ctor != null;
            }

            ctor = null;
            return false;
        }

        private static bool TryGetAssignedFieldOrProperty(ArgumentSyntax argument, IMethodSymbol method, SemanticModel semanticModel, CancellationToken cancellationToken, out ISymbol member)
        {
            member = null;
            if (method == null)
            {
                return false;
            }

            if (method.TrySingleDeclaration(cancellationToken, out BaseMethodDeclarationSyntax methodDeclaration) &&
                methodDeclaration.TryGetMatchingParameter(argument, out var parameter))
            {
                var parameterSymbol = SemanticModelExt.GetDeclaredSymbolSafe(semanticModel, parameter, cancellationToken);
                if (AssignmentExecutionWalker.FirstWith(parameterSymbol, methodDeclaration.Body, Search.TopLevel, semanticModel, cancellationToken, out var assignment))
                {
                    member = SemanticModelExt.GetSymbolSafe(semanticModel, assignment.Left, cancellationToken);
                    if (member is IFieldSymbol ||
                        member is IPropertySymbol)
                    {
                        return true;
                    }
                }

                if (methodDeclaration is ConstructorDeclarationSyntax ctor &&
                    ctor.Initializer is ConstructorInitializerSyntax initializer &&
                    initializer.ArgumentList != null &&
                    initializer.ArgumentList.Arguments.TrySingle(x => x.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == parameter.Identifier.ValueText, out var chainedArgument))
                {
                    var chained = SemanticModelExt.GetSymbolSafe(semanticModel, ctor.Initializer, cancellationToken);
                    return TryGetAssignedFieldOrProperty(chainedArgument, chained, semanticModel, cancellationToken, out member);
                }
            }

            return false;
        }
    }
}

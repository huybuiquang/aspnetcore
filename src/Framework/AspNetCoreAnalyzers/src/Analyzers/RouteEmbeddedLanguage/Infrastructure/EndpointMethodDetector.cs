// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;

internal static class EndpointMethodDetector
{
    public static (IMethodSymbol? Symbol, bool IsMinimal) FindEndpointMethod(SyntaxToken token, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (token.Parent is not LiteralExpressionSyntax)
        {
            return default;
        }

        var container = token.TryFindContainer();
        if (container is null)
        {
            return default;
        }

        if (container.Parent.IsKind(SyntaxKind.Argument))
        {
            // We're an argument in a method call. See if we're a MapXXX method.
            return (FindMapMethod(semanticModel, container, cancellationToken), true);
        }
        else if (container.Parent.IsKind(SyntaxKind.AttributeArgument))
        {
            // We're an argument in an attribute. See if attribute is on a controller method.
            return (FindMvcMethod(semanticModel, container, cancellationToken), false);
        }

        return default;
    }

    private static IMethodSymbol? FindMvcMethod(SemanticModel semanticModel, SyntaxNode container, CancellationToken cancellationToken)
    {
        var argument = container.Parent;
        if (argument.Parent is not AttributeArgumentListSyntax argumentList)
        {
            return null;
        }

        if (argumentList.Parent is not AttributeSyntax attribute)
        {
            return null;
        }

        if (attribute.Parent is not AttributeListSyntax attributeList)
        {
            return null;
        }

        if (attributeList.Parent is not MethodDeclarationSyntax methodDeclaration)
        {
            return null;
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken);

        if (methodSymbol.ContainingType is not ITypeSymbol typeSymbol)
        {
            return null;
        }

        if (!MvcDetector.IsController(typeSymbol, semanticModel))
        {
            return null;
        }

        if (!MvcDetector.IsAction(methodSymbol, semanticModel))
        {
            return null;
        }

        return methodSymbol;
    }

    private static IMethodSymbol? FindMapMethod(SemanticModel semanticModel, SyntaxNode container, CancellationToken cancellationToken)
    {
        var argument = container.Parent;
        if (argument.Parent is not BaseArgumentListSyntax argumentList ||
            argumentList.Parent is null)
        {
            return null;
        }

        // Get the symbol as long if it's not null or if there is only one candidate symbol
        var method = GetMethodInfo(semanticModel, argumentList.Parent, cancellationToken);

        if (!method.Name.StartsWith("Map", StringComparison.Ordinal))
        {
            return null;
        }

        var delegateSymbol = semanticModel.Compilation.GetTypeByMetadataName("System.Delegate");
        var endpointRouteBuilderSymbol = semanticModel.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Routing.IEndpointRouteBuilder");

        var delegateArgument = method.Parameters.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(delegateSymbol, a.Type));
        if (delegateArgument == null)
        {
            return null;
        }

        if (!method.Parameters.Any(
            a => SymbolEqualityComparer.Default.Equals(a.Type, endpointRouteBuilderSymbol) ||
                a.Type.Implements(endpointRouteBuilderSymbol)))
        {
            return null;
        }

        var delegateIndex = method.Parameters.IndexOf(delegateArgument);
        if (delegateIndex >= argumentList.Arguments.Count)
        {
            return null;
        }

        var item = argumentList.Arguments[delegateIndex];

        return GetMethodInfo(semanticModel, item.Expression, cancellationToken);
    }

    private static IMethodSymbol? GetMethodInfo(SemanticModel semanticModel, SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        var delegateSymbolInfo = semanticModel.GetSymbolInfo(syntaxNode, cancellationToken);
        var delegateSymbol = delegateSymbolInfo.Symbol;
        if (delegateSymbol == null && delegateSymbolInfo.CandidateSymbols.Length == 1)
        {
            delegateSymbol = delegateSymbolInfo.CandidateSymbols[0];
        }

        return delegateSymbol as IMethodSymbol;
    }
}

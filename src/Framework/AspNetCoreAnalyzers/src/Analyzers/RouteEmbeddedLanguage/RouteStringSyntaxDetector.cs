// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal static class RouteStringSyntaxDetector
{
    public static bool IsRouteStringSyntaxToken(SyntaxToken token, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (!IsAnyStringLiteral(token.RawKind))
        {
            return false;
        }

        if (!TryGetStringFormat(token, semanticModel, cancellationToken, out var identifier))
        {
            return false;
        }

        if (identifier != "Route")
        {
            return false;
        }

        return true;
    }

    private static bool IsAnyStringLiteral(int rawKind)
    {
        return rawKind == (int)SyntaxKind.StringLiteralToken ||
               rawKind == (int)SyntaxKind.SingleLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.MultiLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8StringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8SingleLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8MultiLineRawStringLiteralToken;
    }

    private static bool TryGetStringFormat(SyntaxToken token, SemanticModel semanticModel, CancellationToken cancellationToken, [NotNullWhen(true)] out string identifier)
    {
        if (token.Parent is not LiteralExpressionSyntax)
        {
            identifier = null;
            return false;
        }

        var container = TryFindContainer(token);
        if (container is null)
        {
            identifier = null;
            return false;
        }

        if (container.Parent.IsKind(SyntaxKind.Argument))
        {
            if (IsArgumentToParameterWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
            {
                return true;
            }
        }
        //else if (container.Parent.IsKind(SyntaxKind.AttributeArgument))
        //{
        //    if (IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
        //    {
        //        return true;
        //    }
        //}

        identifier = null;
        return false;
    }

    private static bool IsArgumentToParameterWithMatchingStringSyntaxAttribute(
        SemanticModel semanticModel,
        SyntaxNode argument,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out string? identifier)
    {
        var parameter = FindParameterForArgument(semanticModel, argument, cancellationToken);
        return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    }

    //private static bool IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(
    //    SemanticModel semanticModel,
    //    SyntaxNode argument,
    //    CancellationToken cancellationToken,
    //    [NotNullWhen(true)] out string? identifier)
    //{
    //    var parameter = FindParameterForAttributeArgument(semanticModel, argument, cancellationToken);
    //    return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    //}

    private static bool HasMatchingStringSyntaxAttribute(
        [NotNullWhen(true)] ISymbol? symbol,
        [NotNullWhen(true)] out string? identifier)
    {
        if (symbol != null)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (IsMatchingStringSyntaxAttribute(attribute, out identifier))
                {
                    return true;
                }
            }
        }

        identifier = null;
        return false;
    }

    private static bool IsMatchingStringSyntaxAttribute(
        AttributeData attribute,
        [NotNullWhen(true)] out string? identifier)
    {
        identifier = null;
        if (attribute.ConstructorArguments.Length == 0)
        {
            return false;
        }

        if (attribute.AttributeClass is not
            {
                Name: "StringSyntaxAttribute",
                ContainingNamespace:
                {
                    Name: nameof(CodeAnalysis),
                    ContainingNamespace:
                    {
                        Name: nameof(System.Diagnostics),
                        ContainingNamespace:
                        {
                            Name: nameof(System),
                            ContainingNamespace.IsGlobalNamespace: true,
                        }
                    }
                }
            })
        {
            return false;
        }

        var argument = attribute.ConstructorArguments[0];
        if (argument.Kind != TypedConstantKind.Primitive || argument.Value is not string argString)
        {
            return false;
        }

        identifier = argString;
        return true;
    }

    private static IParameterSymbol FindParameterForArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
        => DetermineParameter((ArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    //private static IParameterSymbol FindParameterForAttributeArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
    //    => DetermineParameter((AttributeArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    /// <summary>
    /// Returns the parameter to which this argument is passed. If <paramref name="allowParams"/>
    /// is true, the last parameter will be returned if it is params parameter and the index of
    /// the specified argument is greater than the number of parameters.
    /// </summary>
    private static IParameterSymbol? DetermineParameter(
        ArgumentSyntax argument,
        SemanticModel semanticModel,
        bool allowParams = false,
        CancellationToken cancellationToken = default)
    {
        if (argument.Parent is not BaseArgumentListSyntax argumentList ||
            argumentList.Parent is null)
        {
            return null;
        }

        // Get the symbol as long if it's not null or if there is only one candidate symbol
        var symbolInfo = semanticModel.GetSymbolInfo(argumentList.Parent, cancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol == null && symbolInfo.CandidateSymbols.Length == 1)
        {
            symbol = symbolInfo.CandidateSymbols[0];
        }

        if (symbol == null)
        {
            return null;
        }

        var parameters = GetParameters(symbol);

        // Handle named argument
        if (argument.NameColon != null && !argument.NameColon.IsMissing)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            return parameters.FirstOrDefault(p => p.Name == name);
        }

        // Handle positional argument
        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0)
        {
            return null;
        }

        if (index < parameters.Length)
        {
            return parameters[index];
        }

        if (allowParams)
        {
            var lastParameter = parameters.LastOrDefault();
            if (lastParameter == null)
            {
                return null;
            }

            if (lastParameter.IsParams)
            {
                return lastParameter;
            }
        }

        return null;
    }

    private static ImmutableArray<IParameterSymbol> GetParameters(ISymbol? symbol)
        => symbol switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol nt => nt.Parameters,
            _ => ImmutableArray<IParameterSymbol>.Empty,
        };

    private static SyntaxNode? TryFindContainer(SyntaxToken token)
    {
        var node = WalkUpParentheses(GetRequiredParent(token));

        // if we're inside some collection-like initializer, find the instance actually being created. 
        if (IsAnyInitializerExpression(node.Parent, out var instance))
        {
            node = WalkUpParentheses(instance);
        }

        return node;
    }

    private static SyntaxNode GetRequiredParent(SyntaxToken token)
        => token.Parent ?? throw new InvalidOperationException("Token's parent was null");

    [return: NotNullIfNotNull("node")]
    private static SyntaxNode? WalkUpParentheses(SyntaxNode? node)
    {
        while (IsParenthesizedExpression(node?.Parent))
        {
            node = node.Parent;
        }

        return node;
    }

    private static bool IsParenthesizedExpression([NotNullWhen(true)] SyntaxNode? node)
        => node?.RawKind == (int)SyntaxKind.ParenthesizedExpression;

    private static bool IsAnyInitializerExpression([NotNullWhen(true)] SyntaxNode? node, [NotNullWhen(true)] out SyntaxNode? creationExpression)
    {
        if (node is InitializerExpressionSyntax
            {
                Parent: BaseObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax
            })
        {
            creationExpression = node.Parent;
            return true;
        }

        creationExpression = null;
        return false;
    }

    public static IMethodSymbol? GetTargetMethod(SyntaxToken token, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (token.Parent is not LiteralExpressionSyntax)
        {
            return null;
        }

        var container = TryFindContainer(token);
        if (container is null)
        {
            return null;
        }

        if (container.Parent.IsKind(SyntaxKind.Argument))
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

            if (method.ContainingType is not
                {
                    Name: "EndpointRouteBuilderExtensions",
                    ContainingNamespace:
                    {
                        Name: "Builder",
                        ContainingNamespace:
                        {
                            Name: "AspNetCore",
                            ContainingNamespace:
                            {
                                Name: "Microsoft",
                                ContainingNamespace.IsGlobalNamespace: true,
                            }
                        }
                    }
                })
            {
                return null;
            }

            var delegateArgument = method.Parameters.FirstOrDefault(a => a.Type.Name == "Delegate"
                && a.Type.ContainingNamespace.Name == "System"
                && (a.Type.ContainingNamespace.ContainingNamespace?.IsGlobalNamespace ?? true));
            if (delegateArgument == null)
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

        return null;
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

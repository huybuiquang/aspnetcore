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
            if (IsArgumentWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
            {
                return true;
            }
        }
        else if (container.Parent.IsKind(SyntaxKind.AttributeArgument))
        {
            if (IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
            {
                return true;
            }
        }
        else
        {
            var statement = container.FirstAncestorOrSelf<SyntaxNode>(n => n is StatementSyntax);
            if (IsSimpleAssignmentStatement(statement))
            {
                GetPartsOfAssignmentStatement(statement, out var left, out var right);
                if (container == right &&
                    IsFieldOrPropertyWithMatchingStringSyntaxAttribute(
                        semanticModel, left, cancellationToken, out identifier))
                {
                    return true;
                }
            }

            if (container.Parent?.IsKind(SyntaxKind.EqualsValueClause) ?? false)
            {
                if (container.Parent.Parent?.IsKind(SyntaxKind.VariableDeclarator) ?? false)
                {
                    var variableDeclarator = container.Parent.Parent;
                    var symbol =
                        semanticModel.GetDeclaredSymbol(variableDeclarator, cancellationToken) ??
                        semanticModel.GetDeclaredSymbol(GetRequiredParent(GetIdentifierOfVariableDeclarator(variableDeclarator)), cancellationToken);

                    if (IsFieldOrPropertyWithMatchingStringSyntaxAttribute(symbol, out identifier))
                    {
                        return true;
                    }
                }
                else if (IsEqualsValueOfPropertyDeclaration(container.Parent))
                {
                    var property = GetRequiredParent(container.Parent);
                    var symbol = semanticModel.GetDeclaredSymbol(property, cancellationToken);

                    if (IsFieldOrPropertyWithMatchingStringSyntaxAttribute(symbol, out identifier))
                    {
                        return true;
                    }
                }
            }
        }

        identifier = null;
        return false;
    }

    public static bool IsEqualsValueOfPropertyDeclaration(SyntaxNode? node)
        => node?.Parent is PropertyDeclarationSyntax propertyDeclaration && propertyDeclaration.Initializer == node;

    private static SyntaxToken GetIdentifierOfVariableDeclarator(SyntaxNode node)
        => ((VariableDeclaratorSyntax)node).Identifier;

    private static bool IsFieldOrPropertyWithMatchingStringSyntaxAttribute(
        SemanticModel semanticModel,
        SyntaxNode left,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out string? identifier)
    {
        var symbol = semanticModel.GetSymbolInfo(left, cancellationToken).Symbol;
        return IsFieldOrPropertyWithMatchingStringSyntaxAttribute(symbol, out identifier);
    }

    private static bool IsFieldOrPropertyWithMatchingStringSyntaxAttribute(
        ISymbol? symbol, [NotNullWhen(true)] out string? identifier)
    {
        identifier = null;
        return symbol is IFieldSymbol or IPropertySymbol &&
            HasMatchingStringSyntaxAttribute(symbol, out identifier);
    }

    public static void GetPartsOfAssignmentStatement(
        SyntaxNode statement, out SyntaxNode left, out SyntaxNode right)
    {
        GetPartsOfAssignmentExpressionOrStatement(
            ((ExpressionStatementSyntax)statement).Expression, out left, out _, out right);
    }

    public static void GetPartsOfAssignmentExpressionOrStatement(
        SyntaxNode statement, out SyntaxNode left, out SyntaxToken operatorToken, out SyntaxNode right)
    {
        var expression = statement;
        if (statement is ExpressionStatementSyntax expressionStatement)
        {
            expression = expressionStatement.Expression;
        }

        var assignment = (AssignmentExpressionSyntax)expression;
        left = assignment.Left;
        operatorToken = assignment.OperatorToken;
        right = assignment.Right;
    }

    public static bool IsSimpleAssignmentStatement([NotNullWhen(true)] SyntaxNode? statement)
        => statement is ExpressionStatementSyntax exprStatement &&
           exprStatement.Expression.IsKind(SyntaxKind.SimpleAssignmentExpression);

    private static bool IsArgumentWithMatchingStringSyntaxAttribute(
        SemanticModel semanticModel,
        SyntaxNode argument,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out string? identifier)
    {
        var parameter = FindParameterForArgument(semanticModel, argument, cancellationToken);
        return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    }

    private static bool IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(
        SemanticModel semanticModel,
        SyntaxNode argument,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out string? identifier)
    {
        // First, see if this is an `X = "..."` argument that is binding to a field/prop on the attribute.
        var fieldOrProperty = FindFieldOrPropertyForAttributeArgument(semanticModel, argument, cancellationToken);
        if (fieldOrProperty != null)
        {
            return HasMatchingStringSyntaxAttribute(fieldOrProperty, out identifier);
        }

        // Otherwise, see if it's a normal named/position argument to the attribute.
        var parameter = FindParameterForAttributeArgument(semanticModel, argument, cancellationToken);
        return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    }

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

    private static ISymbol FindFieldOrPropertyForAttributeArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
        => argument is AttributeArgumentSyntax { NameEquals.Name: var name }
            ? GetAnySymbol(semanticModel.GetSymbolInfo(name, cancellationToken))
            : null;

    private static IParameterSymbol FindParameterForArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
        => DetermineParameter((ArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    private static IParameterSymbol FindParameterForAttributeArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
        => DetermineParameter((AttributeArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    public static ISymbol? GetAnySymbol(SymbolInfo info)
        => info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

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

    /// <summary>
    /// Returns the parameter to which this argument is passed. If <paramref name="allowParams"/>
    /// is true, the last parameter will be returned if it is params parameter and the index of
    /// the specified argument is greater than the number of parameters.
    /// </summary>
    /// <remarks>
    /// Returns null if the <paramref name="argument"/> is a named argument.
    /// </remarks>
    public static IParameterSymbol DetermineParameter(
        this AttributeArgumentSyntax argument,
        SemanticModel semanticModel,
        bool allowParams = false,
        CancellationToken cancellationToken = default)
    {
        // if argument is a named argument it can't map to a parameter.
        if (argument.NameEquals != null)
        {
            return null;
        }

        if (argument.Parent is not AttributeArgumentListSyntax argumentList)
        {
            return null;
        }

        if (argumentList.Parent is not AttributeSyntax invocableExpression)
        {
            return null;
        }

        var symbol = semanticModel.GetSymbolInfo(invocableExpression, cancellationToken).Symbol;
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

    public static SyntaxNode GetRequiredParent(SyntaxNode node)
        => node.Parent ?? throw new InvalidOperationException("Node's parent was null");

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
            return FindMapMethod(semanticModel, container, cancellationToken);
        }
        else if (container.Parent.IsKind(SyntaxKind.AttributeArgument))
        {
            return FindMvcMethod(semanticModel, container, cancellationToken);
        }

        return null;
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

        if (!MvcHelpers.IsController(typeSymbol))
        {
            return null;
        }

        if (!MvcHelpers.IsAction(methodSymbol))
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

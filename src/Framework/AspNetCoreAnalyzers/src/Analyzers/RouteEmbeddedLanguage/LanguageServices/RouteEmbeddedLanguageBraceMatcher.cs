// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using RoutePatternToken = Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common.EmbeddedSyntaxToken<Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.RoutePatternKind>;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.LanguageServices;

[ExportAspNetCoreEmbeddedLanguageBraceMatcher(name: "Route", language: LanguageNames.CSharp)]
internal class RouteEmbeddedLanguageBraceMatcher : IAspNetCoreEmbeddedLanguageBraceMatcher
{
    public AspNetBraceMatchingResult? FindBraces(SemanticModel semanticModel, SyntaxToken token, int position, CancellationToken cancellationToken)
    {
        var virtualChars = AspNetCoreCSharpVirtualCharService.Instance.TryConvertToVirtualChars(token);
        var tree = RoutePatternParser.TryParse(virtualChars);
        if (tree == null)
        {
            return null;
        }

        return GetMatchingBraces(tree, position);
    }

    private static AspNetBraceMatchingResult? GetMatchingBraces(RoutePatternTree tree, int position)
    {
        var virtualChar = tree.Text.Find(position);
        if (virtualChar == null)
        {
            return null;
        }

        var ch = virtualChar.Value;
        return ch.Value switch
        {
            '{' or '}' => FindParameterBraces(tree, ch),
            '(' or ')' => FindPolicyParens(tree, ch),
            _ => null,
        };
    }

    private static AspNetBraceMatchingResult? FindParameterBraces(RoutePatternTree tree, AspNetCoreVirtualChar ch)
    {
        var node = FindParameterNode(tree.Root, ch);
        return node == null ? null : CreateResult(node.OpenBraceToken, node.CloseBraceToken);
    }

    private static AspNetBraceMatchingResult? FindPolicyParens(RoutePatternTree tree, AspNetCoreVirtualChar ch)
    {
        var node = FindPolicyFragmentEscapedNode(tree.Root, ch);
        return node == null ? null : CreateResult(node.OpenParenToken, node.CloseParenToken);
    }

    private static RoutePatternParameterNode? FindParameterNode(RoutePatternNode node, AspNetCoreVirtualChar ch)
        => FindNode<RoutePatternParameterNode>(node, ch, (parameter, c) =>
                parameter.OpenBraceToken.VirtualChars.Contains(c) || parameter.CloseBraceToken.VirtualChars.Contains(c));

    private static RoutePatternPolicyFragmentEscapedNode? FindPolicyFragmentEscapedNode(RoutePatternNode node, AspNetCoreVirtualChar ch)
        => FindNode<RoutePatternPolicyFragmentEscapedNode>(node, ch, (fragment, c) =>
                fragment.OpenParenToken.VirtualChars.Contains(c) || fragment.CloseParenToken.VirtualChars.Contains(c));

    private static TNode? FindNode<TNode>(RoutePatternNode node, AspNetCoreVirtualChar ch, Func<TNode, AspNetCoreVirtualChar, bool> predicate)
        where TNode : RoutePatternNode
    {
        if (node is TNode nodeMatch && predicate(nodeMatch, ch))
        {
            return nodeMatch;
        }

        foreach (var child in node)
        {
            if (child.IsNode)
            {
                var result = FindNode(child.Node, ch, predicate);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static AspNetBraceMatchingResult? CreateResult(RoutePatternToken open, RoutePatternToken close)
        => open.IsMissing || close.IsMissing
            ? null
            : new AspNetBraceMatchingResult(open.VirtualChars[0].Span, close.VirtualChars[0].Span);

    // IAspNetCoreEmbeddedLanguageBraceMatcher is internal and tests don't have access to it. Provide a way to get its assembly.
    // Just for unit tests. Don't use in production code.
    internal static class TestAccessor
    {
        public static Assembly ExternalAccessAssembly => typeof(IAspNetCoreEmbeddedLanguageBraceMatcher).Assembly;
    }
}

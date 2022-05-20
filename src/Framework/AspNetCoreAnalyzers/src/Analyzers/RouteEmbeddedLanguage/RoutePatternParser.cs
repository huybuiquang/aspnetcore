// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using static RoutePatternHelpers;
using RoutePatternToken = EmbeddedSyntaxToken<RoutePatternKind>;

internal partial struct RoutePatternParser
{
    private RoutePatternLexer _lexer;
    private RoutePatternToken _currentToken;

    private RoutePatternParser(AspNetCoreVirtualCharSequence text) : this()
    {
        _lexer = new RoutePatternLexer(text);

        // Get the first token.  It is allowed to have trivia on it.
        ConsumeCurrentToken();
    }

    /// <summary>
    /// Returns the latest token the lexer has produced, and then asks the lexer to 
    /// produce the next token after that.
    /// </summary>
    private RoutePatternToken ConsumeCurrentToken()
    {
        var previous = _currentToken;
        _currentToken = _lexer.ScanNextToken();
        return previous;
    }

    /// <summary>
    /// Given an input text, and set of options, parses out a fully representative syntax tree 
    /// and list of diagnostics.  Parsing should always succeed, except in the case of the stack 
    /// overflowing.
    /// </summary>
    public static RoutePatternTree? TryParse(AspNetCoreVirtualCharSequence text)
    {
        var parser = new RoutePatternParser(text);
        return parser.ParseTree();
    }

    private RoutePatternTree ParseTree()
    {
        var rootParts = ParseRootParts();

        Debug.Assert(_lexer.Position == _lexer.Text.Length);
        Debug.Assert(_currentToken.Kind == RoutePatternKind.EndOfFile);

        var root = new RoutePatternCompilationUnit(rootParts, _currentToken);

        var routeParameters = new Dictionary<string, RouteParameter>(StringComparer.OrdinalIgnoreCase);
        var seenDiagnostics = new HashSet<EmbeddedDiagnostic>();
        var diagnostics = new List<EmbeddedDiagnostic>();

        CollectDiagnostics(root, seenDiagnostics, diagnostics);
        ValidateStart(root, diagnostics);
        ValidateNoConsecutiveParameters(root, diagnostics);
        ValidateNoConsecutiveSeparators(root, diagnostics);
        ValidateCatchAllParameters(root, diagnostics);
        ValidateParameterParts(root, diagnostics, routeParameters);

        return new RoutePatternTree(_lexer.Text, root, diagnostics.ToImmutableArray(), routeParameters.ToImmutableDictionary());
    }

    private static void ValidateStart(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        if (root.ChildCount > 1 &&
            root.ChildAt(0).Node is var firstNode &&
            firstNode.Kind == RoutePatternKind.Segment)
        {
            if (firstNode.ChildCount > 0 &&
                firstNode.ChildAt(0).Node is var segmentPart &&
                segmentPart.Kind == RoutePatternKind.Literal)
            {
                var literalNode = (RoutePatternLiteralNode)segmentPart;
                var startText = literalNode.LiteralToken.Value.ToString();

                // Route pattern starts with tilde
                if (startText[0] == '~')
                {
                    // Report problem if either:
                    // 1. There is more text. It can't be a slash.
                    // 2. There are more segment parameters. It can't be a slash.
                    if (startText.Length > 1 ||
                        firstNode.ChildCount > 2)
                    {
                        diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_InvalidRouteTemplate, segmentPart.GetSpan()));
                        return;
                    }

                    // No problem if tilde is followed by slash.
                    if (root.ChildCount > 2 &&
                        root.ChildAt(1).Node is var secondNode &&
                        secondNode.Kind == RoutePatternKind.Seperator)
                    {
                        return;
                    }

                    // Tilde by itself.
                    diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_InvalidRouteTemplate, segmentPart.GetSpan()));
                }
            }
        }
    }

    private static void ValidateCatchAllParameters(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        RoutePatternParameterNode? catchAllParameterNode = null;
        foreach (var part in root)
        {
            if (part.Kind == RoutePatternKind.Segment)
            {
                if (catchAllParameterNode != null)
                {
                    // Validate that there aren't segments following catch-all.
                    diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CatchAllMustBeLast, catchAllParameterNode.GetSpan()));
                    break;
                }

                // Check that segment doesn't have catch-all in a complex segment.
                foreach (var segmentPart in part.Node)
                {
                    if (segmentPart.Kind == RoutePatternKind.Parameter)
                    {
                        var catchAllParameterPart = (RoutePatternCatchAllParameterPartNode)segmentPart.Node.GetChildNode(RoutePatternKind.CatchAll);
                        if (catchAllParameterPart != null)
                        {
                            catchAllParameterNode = (RoutePatternParameterNode)segmentPart.Node;
                            if (part.Node.ChildCount > 1)
                            {
                                diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CannotHaveCatchAllInMultiSegment, catchAllParameterNode.GetSpan()));
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ValidateNoConsecutiveParameters(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        RoutePatternNode previousNode = null;
        foreach (var part in root)
        {
            if (part.Kind == RoutePatternKind.Segment)
            {
                foreach (var segmentPart in part.Node)
                {
                    if (previousNode != null && previousNode.Kind == RoutePatternKind.Parameter)
                    {
                        var previousParameterNode = (RoutePatternParameterNode)previousNode;
                        var isOptional = previousParameterNode.GetChildNode(RoutePatternKind.Optional) != null;
                        if (isOptional)
                        {
                            var message = Resources.FormatTemplateRoute_OptionalParameterHasTobeTheLast(
                                part.Node.ToString(),
                                previousParameterNode.GetChildNode(RoutePatternKind.ParameterName).ToString(),
                                segmentPart.Node.ToString());
                            diagnostics.Add(new EmbeddedDiagnostic(message, part.Node.GetSpan()));                            
                        }
                    }

                    if (segmentPart.Kind == RoutePatternKind.Parameter && previousNode != null)
                    {
                        var parameterNode = (RoutePatternParameterNode)segmentPart.Node;
                        var isOptional = parameterNode.GetChildNode(RoutePatternKind.Optional) != null;
                        if (isOptional)
                        {
                            // Optional parameter must either be in its own segment or follow a period.
                            // e.g. {filename}.{ext?}
                            if (previousNode.Kind != RoutePatternKind.Literal || ((RoutePatternLiteralNode)previousNode).LiteralToken.Value.ToString() != ".")
                            {
                                var message = Resources.FormatTemplateRoute_OptionalParameterCanbBePrecededByPeriod(
                                    part.Node.ToString(),
                                    parameterNode.GetChildNode(RoutePatternKind.ParameterName).ToString(),
                                    previousNode.ToString());
                                diagnostics.Add(new EmbeddedDiagnostic(message, parameterNode.GetSpan()));
                            }
                        }
                        else
                        {
                            if (previousNode.Kind == RoutePatternKind.Parameter)
                            {
                                diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CannotHaveConsecutiveParameters, parameterNode.GetSpan()));
                            }
                        }
                    }
                    previousNode = segmentPart.Node;
                }
                previousNode = null;
            }
        }
    }

    private static void ValidateParameterParts(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics, Dictionary<string, RouteParameter> routeParameters)
    {
        foreach (var part in root)
        {
            if (part.Kind == RoutePatternKind.Segment)
            {
                foreach (var segmentPart in part.Node)
                {
                    if (segmentPart.Kind == RoutePatternKind.Parameter)
                    {
                        var parameterNode = (RoutePatternParameterNode)segmentPart.Node;
                        var hasOptional = false;
                        var hasCatchAll = false;
                        var encodeSlashes = true;
                        string? name = null;
                        string? defaultValue = null;
                        List<string> policies = new List<string>();
                        foreach (var parameterPart in parameterNode)
                        {
                            switch (parameterPart.Kind)
                            {
                                case RoutePatternKind.ParameterName:
                                    var parameterNameNode = (RoutePatternNameParameterPartNode)parameterPart.Node;
                                    if (!parameterNameNode.ParameterNameToken.IsMissing)
                                    {
                                        name = parameterNameNode.ParameterNameToken.Value.ToString();
                                    }
                                    break;
                                case RoutePatternKind.Optional:
                                    hasOptional = true;
                                    break;
                                case RoutePatternKind.DefaultValue:
                                    var defaultValueNode = (RoutePatternDefaultValueParameterPartNode)parameterPart.Node;
                                    if (!defaultValueNode.DefaultValueToken.IsMissing)
                                    {
                                        defaultValue = defaultValueNode.DefaultValueToken.Value.ToString();
                                    }
                                    break;
                                case RoutePatternKind.CatchAll:
                                    var catchAllNode = (RoutePatternCatchAllParameterPartNode)parameterPart.Node;
                                    encodeSlashes = catchAllNode.AsteriskToken.VirtualChars.Length == 1;
                                    hasCatchAll = true;
                                    break;
                                case RoutePatternKind.ParameterPolicy:
                                    policies.Add(parameterPart.Node.ToString());
                                    break;
                            }
                        }

                        var routeParameter = new RouteParameter(name, encodeSlashes, defaultValue, hasOptional, hasCatchAll, policies.ToImmutableArray());
                        if (routeParameter.DefaultValue != null && routeParameter.IsOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_OptionalCannotHaveDefaultValue, parameterNode.GetSpan()));
                        }
                        if (routeParameter.IsCatchAll && routeParameter.IsOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CatchAllCannotBeOptional, parameterNode.GetSpan()));
                        }

                        if (routeParameter.Name != null)
                        {
                            if (!routeParameters.ContainsKey(routeParameter.Name))
                            {
                                routeParameters.Add(routeParameter.Name, routeParameter);
                            }
                            else
                            {
                                diagnostics.Add(new EmbeddedDiagnostic(Resources.FormatTemplateRoute_RepeatedParameter(routeParameter.Name), parameterNode.GetSpan()));
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ValidateNoConsecutiveSeparators(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        RoutePatternSegmentSeperatorNode? previousNode = null;
        foreach (var part in root)
        {
            if (part.Kind == RoutePatternKind.Seperator)
            {
                var currentNode = (RoutePatternSegmentSeperatorNode)part.Node;
                if (previousNode != null)
                {
                    diagnostics.Add(
                        new EmbeddedDiagnostic(
                            Resources.TemplateRoute_CannotHaveConsecutiveSeparators,
                            EmbeddedSyntaxHelpers.GetSpan(previousNode.SeperatorToken, currentNode.SeperatorToken)));
                }
                previousNode = currentNode;
            }
            else
            {
                previousNode = null;
            }
        }
    }

    private void CollectDiagnostics(RoutePatternNode node, HashSet<EmbeddedDiagnostic> seenDiagnostics, List<EmbeddedDiagnostic> diagnostics)
    {
        foreach (var child in node)
        {
            if (child.IsNode)
            {
                CollectDiagnostics(child.Node, seenDiagnostics, diagnostics);
            }
            else
            {
                var token = child.Token;
                AddUniqueDiagnostics(seenDiagnostics, token.Diagnostics, diagnostics);
            }
        }
    }

    /// <summary>
    /// It's very common to have duplicated diagnostics.  For example, consider "((". This will
    /// have two 'missing )' diagnostics, both at the end.  Reporting both isn't helpful, so we
    /// filter duplicates out here.
    /// </summary>
    private static void AddUniqueDiagnostics(
        HashSet<EmbeddedDiagnostic> seenDiagnostics, ImmutableArray<EmbeddedDiagnostic> from, List<EmbeddedDiagnostic> to)
    {
        foreach (var diagnostic in from)
        {
            if (seenDiagnostics.Add(diagnostic))
            {
                to.Add(diagnostic);
            }
        }
    }

    private ImmutableArray<RoutePatternRootPartNode> ParseRootParts()
    {
        var result = new List<RoutePatternRootPartNode>();

        while (_currentToken.Kind != RoutePatternKind.EndOfFile)
        {
            result.Add(ParseRootPart());
        }

        return result.ToImmutableArray();
    }

    private RoutePatternRootPartNode ParseRootPart()
        => _currentToken.Kind switch
        {
            RoutePatternKind.SlashToken => ParseSegmentSeperator(),
            _ => ParseSegment(),
        };

    private RoutePatternSegmentNode ParseSegment()
    {
        var result = new List<RoutePatternNode>();

        while (_currentToken.Kind != RoutePatternKind.EndOfFile &&
            _currentToken.Kind != RoutePatternKind.SlashToken)
        {
            result.Add(ParsePart());
        }

        return new(result.ToImmutableArray());
    }

    private RoutePatternSegmentPartNode ParsePart()
    {
        if (_currentToken.Kind == RoutePatternKind.OpenBraceToken)
        {
            var openBraceToken = _currentToken;

            ConsumeCurrentToken();

            if (_currentToken.Kind != RoutePatternKind.OpenBraceToken)
            {
                return ParseParameter(openBraceToken);
            }
            else
            {
                MoveBackBeforePreviousScan();
            }
        }

        return ParseLiteral();
    }

    private RoutePatternLiteralNode ParseLiteral()
    {
        MoveBackBeforePreviousScan();

        var literal = _lexer.TryScanLiteral();

        ConsumeCurrentToken();

        // A token must be returned because we've already checked the first character.
        return new(literal.Value);
    }

    private void MoveBackBeforePreviousScan()
    {
        if (_currentToken.Kind != RoutePatternKind.EndOfFile)
        {
            // Move back to un-consume whatever we just consumed.
            _lexer.Position--;
        }
    }

    //private RoutePatternSegmentSeperatorNode ParseReplacement()
    //{
    //    throw new NotImplementedException();
    //}

    private RoutePatternParameterNode ParseParameter(RoutePatternToken openBraceToken)
    {
        var result = new RoutePatternParameterNode(
            openBraceToken,
            ParseParameterParts(),
            ConsumeToken(RoutePatternKind.CloseBraceToken, Resources.TemplateRoute_MismatchedParameter));

        return result;
    }

    private RoutePatternToken ConsumeToken(RoutePatternKind kind, string? error)
    {
        if (_currentToken.Kind == kind)
        {
            return ConsumeCurrentToken();
        }

        var result = CreateMissingToken(kind);
        if (error == null)
        {
            return result;
        }

        return result.AddDiagnosticIfNone(new EmbeddedDiagnostic(error, GetTokenStartPositionSpan(_currentToken)));
    }

    private ImmutableArray<RoutePatternParameterPartNode> ParseParameterParts()
    {
        var parts = new List<RoutePatternParameterPartNode>();

        // Catch-all, e.g. {*name}
        if (_currentToken.Kind == RoutePatternKind.AsteriskToken)
        {
            var firstAsteriskToken = _currentToken;
            ConsumeCurrentToken();

            // Unescaped catch-all, e.g. {**name}
            if (_currentToken.Kind == RoutePatternKind.AsteriskToken)
            {
                parts.Add(new RoutePatternCatchAllParameterPartNode(
                    CreateToken(
                        RoutePatternKind.AsteriskToken,
                        AspNetCoreVirtualCharSequence.FromBounds(firstAsteriskToken.VirtualChars, _currentToken.VirtualChars))));
                ConsumeCurrentToken();
            }
            else
            {
                parts.Add(new RoutePatternCatchAllParameterPartNode(firstAsteriskToken));
            }
        }

        MoveBackBeforePreviousScan();

        var parameterName = _lexer.TryScanParameterName();
        if (parameterName != null)
        {
            parts.Add(new RoutePatternNameParameterPartNode(parameterName.Value));
        }
        else
        {
            if (_currentToken.Kind != RoutePatternKind.EndOfFile)
            {
                parts.Add(new RoutePatternNameParameterPartNode(
                    CreateMissingToken(RoutePatternKind.ParameterNameToken).AddDiagnosticIfNone(
                        new EmbeddedDiagnostic(Resources.FormatTemplateRoute_InvalidParameterName(""), _currentToken.GetFullSpan().Value))));
            }
        }

        ConsumeCurrentToken();

        // Parameter policy, e.g. {name:int}
        while (_currentToken.Kind != RoutePatternKind.EndOfFile)
        {
            switch (_currentToken.Kind)
            {
                case RoutePatternKind.ColonToken:
                    parts.Add(ParsePolicy());
                    break;
                case RoutePatternKind.QuestionMarkToken:
                    parts.Add(new RoutePatternOptionalParameterPartNode(ConsumeCurrentToken()));
                    break;
                case RoutePatternKind.EqualsToken:
                    parts.Add(ParseDefaultValue());
                    break;
                case RoutePatternKind.CloseBraceToken:
                default:
                    return parts.ToImmutableArray();
            }
        }

        return parts.ToImmutableArray();
    }

    private RoutePatternDefaultValueParameterPartNode ParseDefaultValue()
    {
        var equalsToken = _currentToken;
        var defaultValue = _lexer.TryScanDefaultValue() ?? CreateMissingToken(RoutePatternKind.DefaultValueToken);
        ConsumeCurrentToken();
        return new(equalsToken, defaultValue);
    }

    private RoutePatternPolicyParameterPartNode ParsePolicy()
    {
        var colonToken = ConsumeCurrentToken();

        var fragments = new List<RoutePatternNode>();
        while (_currentToken.Kind != RoutePatternKind.EndOfFile &&
            _currentToken.Kind != RoutePatternKind.CloseBraceToken &&
            _currentToken.Kind != RoutePatternKind.ColonToken &&
            _currentToken.Kind != RoutePatternKind.QuestionMarkToken &&
            _currentToken.Kind != RoutePatternKind.EqualsToken)
        {
            MoveBackBeforePreviousScan();

            if (_currentToken.Kind == RoutePatternKind.OpenParenToken)
            {
                var openParenPosition = ConsumeCurrentToken();
                var escapedPolicyFragment = _lexer.TryScanEscapedPolicyFragment();
                if (escapedPolicyFragment != null)
                {
                    ConsumeCurrentToken();

                    fragments.Add(new RoutePatternPolicyFragmentEscapedNode(
                        openParenPosition,
                        escapedPolicyFragment.Value,
                        _currentToken.Kind == RoutePatternKind.EndOfFile
                            ? CreateMissingToken(RoutePatternKind.CloseParenToken)
                            : ConsumeCurrentToken()));
                    continue;
                }
            }

            var policyFragment = _lexer.TryScanUnescapedPolicyFragment();
            if (policyFragment == null)
            {
                break;
            }

            fragments.Add(new RoutePatternPolicyFragment(policyFragment.Value));
            ConsumeCurrentToken();
        }

        return new(colonToken, fragments.ToImmutableArray());
    }

    private RoutePatternSegmentSeperatorNode ParseSegmentSeperator()
        => new(ConsumeCurrentToken());

    private TextSpan GetTokenStartPositionSpan(RoutePatternToken token)
    {
        return token.Kind == RoutePatternKind.EndOfFile
            ? new TextSpan(_lexer.Text.Last().Span.End, 0)
            : new TextSpan(token.VirtualChars[0].Span.Start, 0);
    }
}

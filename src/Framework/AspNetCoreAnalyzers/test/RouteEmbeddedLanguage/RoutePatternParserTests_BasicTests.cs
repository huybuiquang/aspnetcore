// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Text.RegularExpressions;
using Xunit;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

// These tests were created by trying to enumerate all codepaths in the lexer/parser.
public partial class RoutePatternParserTests
{
    [Fact]
    public void TestEmpty()
    {
        Test(@"""""", @"");
    }

    [Fact]
    public void TestSingleLiteral()
    {
        Test(@"""hello""", @"");
    }

    [Fact]
    public void TestSingleLiteralWithQuestionMark()
    {
        Test(@"""hel?lo""", @"");
    }

    [Fact]
    public void TestSlashSeperatedLiterals()
    {
        Test(@"""hello/world""", @"");
    }

    [Fact]
    public void TestDuplicateParameterNames()
    {
        Test(@"""{a}/{a}""", @"");
    }

    [Fact]
    public void TestSlashSeperatedSegments()
    {
        Test(@"""{a}/{b}""", @"");
    }

    [Fact]
    public void TestCatchAllParameterFollowedBySlash()
    {
        Test(@"""{*a}/""", @"");
    }

    [Fact]
    public void TestCatchAllParameterNotLast()
    {
        Test(@"""{*a}/{b}""", @"");
    }

    [Fact]
    public void TestCatchAllAndOptional()
    {
        Test(@"""{*a?}""", @"");
    }

    [Fact]
    public void TestCatchAllParameterComplexSegment()
    {
        Test(@"""a{*a}""", @"");
    }

    [Fact]
    public void TestPeriodSeperatedLiterals()
    {
        Test(@"""hello.world""", @"");
    }

    [Fact]
    public void TestSimpleParameter()
    {
        Test(@"""{id}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicy()
    {
        Test(@"""{id:foo}""", @"");
    }

    [Fact]
    public void TestParameterWithDefault()
    {
        Test(@"""{id=Home}""", @"");
    }

    [Fact]
    public void TestParameterWithDefaultContainingPolicyChars()
    {
        Test(@"""{id=Home=Controller:int()}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyArgument()
    {
        Test(@"""{id:foo(wee)}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyArgumentEmpty()
    {
        Test(@"""{id:foo()}""", @"");
    }

    [Fact]
    public void TestParameterOptional()
    {
        Test(@"""{id?}""", @"");
    }

    [Fact]
    public void TestParameterDefaultValue()
    {
        Test(@"""{id=Home}""", @"");
    }

    [Fact]
    public void TestParameterDefaultValueAndOptional()
    {
        Test(@"""{id=Home?}""", @"");
    }

    [Fact]
    public void TestParameterQuestionMarkBeforeEscapedClose()
    {
        Test(@"""{id?}}}""", @"");
    }

    [Fact]
    public void TestUnbalancedBracesInComplexSegment()
    {
        Test(@"""a{foob{bar}c""", @"");
    }

    [Fact]
    public void TestComplexSegment()
    {
        Test(@"""a{foo}b{bar}c""", @"");
    }

    [Fact]
    public void TestConsecutiveParameters()
    {
        Test(@"""{a}{b}""", @"");
    }

    [Fact]
    public void TestUnescapedOpenBrace()
    {
        Test(@"""{a{b}""", @"");
    }

    [Fact]
    public void TestInvalidCharsAndUnescapedOpenBrace()
    {
        Test(@"""{a/{b}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyAndOptional()
    {
        Test(@"""{id:foo?}""", @"");
    }

    [Fact]
    public void TestParameterWithMultiplePolicies()
    {
        Test(@"""{id:foo:bar}""", @"");
    }

    [Fact]
    public void TestPolicyWithEscapedFragmentParameterIncomplete()
    {
        Test(@"""{id:foo(hi""", @"");
    }

    [Fact]
    public void TestPolicyWithEscapedFragmentIncomplete()
    {
        Test(@"""{id:foo(hi}""", @"");
    }

    [Fact]
    public void TestPolicyWithMultipleFragments()
    {
        Test(@"""{id:foo(hi)bar}""", @"");
    }

    [Fact]
    public void TestCatchAllParameter()
    {
        Test(@"""{*id}""", @"");
    }

    [Fact]
    public void TestCatchAllUnescapedParameter()
    {
        Test(@"""{**id}""", @"");
    }

    [Fact]
    public void TestEmptyParameter()
    {
        Test(@"""{}""", @"");
    }

    [Fact]
    public void TestParameterWithEscapedPolicyArgument()
    {
        Test(@"""{ssn:regex(^\\d{{3}}-\\d{{2}}-\\d{{4}}$)}""", @"");
    }

    [Fact]
    public void TestParameterWithEscapedPolicyArgumentIncomplete()
    {
        Test(@"""{ssn:regex(^\\d{{3}}-\\d{{2}}-\\d{{4}""", @"");
    }

    [Fact]
    public void TestParameterWithOpenBraceInEscapedPolicyArgument()
    {
        Test(@"""{ssn:regex(^\\d{3}})}""", @"");
    }

    [Fact]
    public void TestParameterWithInvalidName()
    {
        Test(@"""{3}}-\\d{{2}}-\\d{{4}""", @"");
    }

    [Fact]
    public void TestInvalidCloseBrace()
    {
        Test(@"""-\\d{{2}}-\\d{{4}""", @"");
    }

    [Fact]
    public void TestEscapedBraces()
    {
        Test(@"""{{2}}""", @"");
    }

    [Fact]
    public void TestInvalidCloseBrace2()
    {
        Test(@"""{2}}""", @"");
    }

    [Fact]
    public void TestOptionalParameterPrecededByParameter()
    {
        Test(@"""{p1}{p2?}""", @"");
    }

    [Fact]
    public void TestOptionalParameterPrecededByLiteral()
    {
        Test(@"""{p1}-{p2?}""", @"");
    }

    [Fact]
    public void TestParameterColonStart()
    {
        Test(@"""{:hi}""", @"");
    }

    [Fact]
    public void TestParameterCatchAllColonStart()
    {
        Test(@"""{**:hi}""", @"");
    }

    [Fact]
    public void TestTilde()
    {
        Test(@"""~""", @"");
    }

    [Fact]
    public void TestTwoTildes()
    {
        Test(@"""~~""", @"");
    }

    [Fact]
    public void TestTildeSlash()
    {
        Test(@"""~/""", @"");
    }

    [Fact]
    public void TestTildeParameter()
    {
        Test(@"""~{id}""", @"");
    }
}

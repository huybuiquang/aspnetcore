// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Analyzer.Testing;
using Microsoft.AspNetCore.Analyzers.RenderTreeBuilder;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;
using static Microsoft.CodeAnalysis.Editor.UnitTests.Classification.FormattedClassifications;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

public class RouteEmbeddedLanguageTests
{
    private readonly ITestOutputHelper _output;

    private TestDiagnosticAnalyzerRunner Runner { get; } = new(new RenderTreeBuilderAnalyzer());

    protected async Task TestAsync(
        string code,
        params FormattedClassification[] expected)
    {
        MarkupTestFile.GetSpans(code, out var rewrittenCode, out ImmutableArray<TextSpan> spans);
        Assert.True(spans.Length == 1);

        var actual = await Runner.GetClassificationSpansAsync(spans.Single(), rewrittenCode);

        foreach (var item in actual)
        {
            _output.WriteLine(item.ClassificationType + " " + item.TextSpan);
        }
    }

    public RouteEmbeddedLanguageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RenderTreeBuilderInvocationWithNonConstantArgument_ProducesDiagnostics()
    {
        await TestAsync(
@"
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

class Program
{
    [StringSyntax(StringSyntaxAttribute.Regex)]
    private string field;

    void Goo()
    {
        [|this.field = @""$\a(?#comment)"";|]
    }
}" + EmbeddedLanguagesTestConstants.StringSyntaxAttributeCodeCSharp,
Field("field"),
Regex.Anchor("$"),
Regex.OtherEscape("\\"),
Regex.OtherEscape("a"),
Regex.Comment("(?#comment)"));
    }

    [Fact]
    public async Task sdfsdfsdf()
    {
        await TestAsync(
@"
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

class Program
{
    [StringSyntax(""Route"")]
    private string field;

    void Goo()
    {
        [|this.field = @""{id?}"";|]
    }
}" + EmbeddedLanguagesTestConstants.StringSyntaxAttributeCodeCSharp,
Field("field"),
Regex.Anchor("$"),
Regex.OtherEscape("\\"),
Regex.OtherEscape("a"),
Regex.Comment("(?#comment)"));
    }
}

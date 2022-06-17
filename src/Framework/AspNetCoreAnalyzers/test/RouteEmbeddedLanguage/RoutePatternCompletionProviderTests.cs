// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Analyzer.Testing;
using Microsoft.AspNetCore.Analyzers.RenderTreeBuilder;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.CodeAnalysis.Completion;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

public partial class RoutePatternCompletionProviderTests
{
    private TestDiagnosticAnalyzerRunner Runner { get; } = new(new RoutePatternAnalyzer());

    [Fact]
    public async Task Insertion_Literal_NoItems()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System.Diagnostics.CodeAnalysis;

class Program
{
    static void Main()
    {
        M(@""hi$$"");
    }

    static void M([StringSyntax(""Route"")] string p)
    {
    }
}
");

        // Assert
        Assert.Empty(result.Completions.Items);
    }

    [Fact]
    public async Task Insertion_PolicyColon_ReturnPolicies()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System.Diagnostics.CodeAnalysis;

class Program
{
    static void Main()
    {
        M(@""{hi:$$"");
    }

    static void M([StringSyntax(""Route"")] string p)
    {
    }
}
");

        // Assert
        Assert.NotEmpty(result.Completions.Items);
        Assert.Equal("alpha", result.Completions.Items[0].DisplayText);

        // Getting description is currently broken in Roslyn.
        //var description = await result.Service.GetDescriptionAsync(result.Document, result.Completions.Items[0]);
        //Assert.Equal("int", description.Text);
    }

    [Fact]
    public async Task Insertion_ParameterOpenBrace_UnsupportedMethod_NoItems()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System.Diagnostics.CodeAnalysis;

class Program
{
    static void Main()
    {
        M(@""{$$"");
    }

    static void M([StringSyntax(""Route"")] string p)
    {
    }
}
");

        // Assert
        Assert.Empty(result.Completions.Items);

        //var description = await result.Service.GetDescriptionAsync(result.Document, result.Completions.Items[0]);
        //Assert.Equal("int", description.Text);
    }

    [Fact]
    public async Task Insertion_ParameterOpenBrace_EndpointMapGet_HasDelegate_ReturnDelegateParameterItem()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        EndpointRouteBuilderExtensions.MapGet(null, @""{$$"", (string id) => "");
    }
}

namespace Microsoft.AspNetCore.Builder
{
    public static class EndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, [StringSyntax(""Route"")] string pattern, Delegate handler)
        {
            return null;
        }
    }
}
");

        // Assert
        Assert.Collection(
            result.Completions.Items,
            i => Assert.Equal("id", i.DisplayText));
    }

    [Fact]
    public async Task Insertion_ParameterOpenBrace_EndpointMapGet_HasMethod_ReturnDelegateParameterItem()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        EndpointRouteBuilderExtensions.MapGet(null, @""{$$"", ExecuteGet);
    }

    static string ExecuteGet(string id)
    {
        return """";
    }
}

namespace Microsoft.AspNetCore.Builder
{
    public static class EndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, [StringSyntax(""Route"")] string pattern, Delegate handler)
        {
            return null;
        }
    }
}
");

        // Assert
        Assert.Collection(
            result.Completions.Items,
            i => Assert.Equal("id", i.DisplayText));
    }

    [Fact]
    public async Task Insertion_ParameterOpenBrace_EndpointMapGet_NullDelegate_ReturnDelegateParameterItem()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        EndpointRouteBuilderExtensions.MapGet(null, @""{$$"", null);
    }
}

namespace Microsoft.AspNetCore.Builder
{
    public static class EndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, [StringSyntax(""Route"")] string pattern, Delegate handler)
        {
            return null;
        }
    }
}
");

        // Assert
        Assert.Empty(result.Completions.Items);
    }

    [Fact]
    public async Task Insertion_ParameterOpenBrace_ControllerAction_HasParameter_ReturnActionParameterItem()
    {
        // Arrange & Act
        var result = await GetCompletionsAndServiceAsync(@"
using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
    }
}

public class TestController
{
    [HttpGet(@""{$$"")]
    public object TestAction(int id)
    {
        return null;
    }
}

class HttpGet : Attribute
{
    public HttpGet([StringSyntax(""Route"")] string pattern)
    {
    }
}
");

        // Assert
        Assert.Collection(
            result.Completions.Items,
            i => Assert.Equal("id", i.DisplayText));
    }

    private async Task<CompletionResult> GetCompletionsAndServiceAsync(string source)
    {
        MarkupTestFile.GetPosition(source, out var output, out int cursorPosition);

        var completions = await Runner.GetCompletionsAndServiceAsync(cursorPosition, output);

        return completions;
    }
}

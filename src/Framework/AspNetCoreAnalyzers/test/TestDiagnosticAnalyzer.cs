// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.AspNetCore.Analyzer.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Composition;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

namespace Microsoft.AspNetCore.Analyzers;

public class TestDiagnosticAnalyzerRunner : DiagnosticAnalyzerRunner
{
    public TestDiagnosticAnalyzerRunner(DiagnosticAnalyzer analyzer)
    {
        Analyzer = analyzer;
    }

    public DiagnosticAnalyzer Analyzer { get; }

    public async Task<ClassifiedSpan[]> GetClassificationSpansAsync(TextSpan textSpan, params string[] sources)
    {
        var project = CreateProjectWithReferencesInBinDir(GetType().Assembly, sources);
        var doc = project.Solution.GetDocument(project.Documents.First().Id);

        var result = await Classifier.GetClassifiedSpansAsync(doc, textSpan, CancellationToken.None);

        return result.ToArray();
    }

    public async Task<CompletionList> GetCompletionsAsync(int caretPosition, params string[] sources)
    {
        return (await GetCompletionsAndServiceAsync(caretPosition, sources)).Completions;
    }

    public async Task<CompletionResult> GetCompletionsAndServiceAsync(int caretPosition, params string[] sources)
    {
        var project = CreateProjectWithReferencesInBinDir(GetType().Assembly, sources);
        var doc = project.Solution.GetDocument(project.Documents.First().Id);

        var completionService = CompletionService.GetService(doc);
        var result = await completionService.GetCompletionsAsync(doc, caretPosition, CompletionTrigger.Invoke);

        return new(doc, completionService, result);
    }

    public Task<Diagnostic[]> GetDiagnosticsAsync(params string[] sources)
    {
        var project = CreateProjectWithReferencesInBinDir(GetType().Assembly, sources);

        return GetDiagnosticsAsync(project);
    }

    private static readonly Lazy<IExportProviderFactory> ExportProviderFactory;

    static TestDiagnosticAnalyzerRunner()
    {
        ExportProviderFactory = new Lazy<IExportProviderFactory>(
            () =>
            {
#pragma warning disable VSTHRD011 // Use AsyncLazy<T>
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                var assemblies = MefHostServices.DefaultAssemblies.ToList();
                assemblies.Add(RoutePatternClassifier.TestAccessor.ExternalAccessAssembly);

                var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
                var parts = Task.Run(() => discovery.CreatePartsAsync(assemblies)).GetAwaiter().GetResult();
                var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(parts); //.WithDocumentTextDifferencingService();

                var configuration = CompositionConfiguration.Create(catalog);
                var runtimeComposition = RuntimeComposition.CreateRuntimeComposition(configuration);
                return runtimeComposition.CreateExportProviderFactory();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
#pragma warning restore VSTHRD011 // Use AsyncLazy<T>
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static AdhocWorkspace CreateWorkspace()
    {
        var exportProvider = ExportProviderFactory.Value.CreateExportProvider();
        var host = MefHostServices.Create(exportProvider.AsCompositionContext());
        return new AdhocWorkspace(host);
    }

    public static Project CreateProjectWithReferencesInBinDir(Assembly testAssembly, params string[] source)
    {
        // The deps file in the project is incorrect and does not contain "compile" nodes for some references.
        // However these binaries are always present in the bin output. As a "temporary" workaround, we'll add
        // every dll file that's present in the test's build output as a metadatareference.

        Func<Workspace> createWorkspace = CreateWorkspace;

        var project = DiagnosticProject.Create(testAssembly, source, createWorkspace, typeof(RoutePatternClassifier));
        foreach (var assembly in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            if (!project.MetadataReferences.Any(c => string.Equals(Path.GetFileNameWithoutExtension(c.Display), Path.GetFileNameWithoutExtension(assembly), StringComparison.OrdinalIgnoreCase)))
            {
                project = project.AddMetadataReference(MetadataReference.CreateFromFile(assembly));
            }
        }

        return project;
    }

    public Task<Diagnostic[]> GetDiagnosticsAsync(Project project)
    {
        return GetDiagnosticsAsync(new[] { project }, Analyzer, Array.Empty<string>());
    }

    protected override CompilationOptions ConfigureCompilationOptions(CompilationOptions options)
    {
        return options.WithOutputKind(OutputKind.ConsoleApplication);
    }
}

public record CompletionResult(Document Document, CompletionService Service, CompletionList Completions);

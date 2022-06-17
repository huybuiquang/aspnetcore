// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using RoutePatternToken = Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common.EmbeddedSyntaxToken<Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.RoutePatternKind>;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

[ExportCompletionProvider(nameof(RoutePatternCompletionProvider), LanguageNames.CSharp)]
[Shared]
public class RoutePatternCompletionProvider : CompletionProvider
{
    private const string StartKey = nameof(StartKey);
    private const string LengthKey = nameof(LengthKey);
    private const string NewTextKey = nameof(NewTextKey);
    private const string NewPositionKey = nameof(NewPositionKey);
    private const string DescriptionKey = nameof(DescriptionKey);

    // Always soft-select these completion items.  Also, never filter down.
    private static readonly CompletionItemRules s_rules = CompletionItemRules.Create(
        selectionBehavior: CompletionItemSelectionBehavior.SoftSelection,
        filterCharacterRules: ImmutableArray.Create(CharacterSetModificationRule.Create(CharacterSetModificationKind.Replace, Array.Empty<char>())));

    public ImmutableHashSet<char> TriggerCharacters { get; } = ImmutableHashSet.Create(
        ':', // policy name
        '{'); // parameter name

    public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
    {
        Debugger.Launch();

        if (trigger.Kind is CompletionTriggerKind.Invoke or
            CompletionTriggerKind.InvokeAndCommitIfUnique)
        {
            return true;
        }

        if (trigger.Kind == CompletionTriggerKind.Insertion)
        {
            return TriggerCharacters.Contains(trigger.Character);
        }

        return false;
    }

    public override Task<CompletionDescription> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
    {
        Debugger.Launch();

        if (!item.Properties.TryGetValue(DescriptionKey, out var description))
        {
            return Task.FromResult<CompletionDescription>(null);
        }

        return Task.FromResult(CompletionDescription.Create(
            ImmutableArray.Create(new TaggedText(TextTags.Text, description))));
    }

    public override Task<CompletionChange> GetChangeAsync(Document document, CompletionItem item, char? commitKey, CancellationToken cancellationToken)
    {
        Debugger.Launch();

        // These values have always been added by us.
        var startString = item.Properties[StartKey];
        var lengthString = item.Properties[LengthKey];
        var newText = item.Properties[NewTextKey];

        // This value is optionally added in some cases and may not always be there.
        item.Properties.TryGetValue(NewPositionKey, out var newPositionString);

        return Task.FromResult(CompletionChange.Create(
            new TextChange(new TextSpan(int.Parse(startString, CultureInfo.InvariantCulture), int.Parse(lengthString, CultureInfo.InvariantCulture)), newText),
            newPositionString == null ? null : int.Parse(newPositionString, CultureInfo.InvariantCulture)));
    }

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        Debugger.Launch();

        if (context.Trigger.Kind is not CompletionTriggerKind.Invoke and
            not CompletionTriggerKind.InvokeAndCommitIfUnique and
            not CompletionTriggerKind.Insertion)
        {
            return;
        }

        var position = context.Position;
        var (tree, stringToken, semanticModel) = await TryGetTreeAndTokenAtPositionAsync(
            context.Document, position, context.CancellationToken).ConfigureAwait(false);

        if (tree == null ||
            position <= stringToken.SpanStart ||
            position >= stringToken.Span.End)
        {
            return;
        }

        var routePatternCompletionContext = new EmbeddedCompletionContext(context, tree, stringToken, semanticModel);
        ProvideCompletions(routePatternCompletionContext);

        if (routePatternCompletionContext.Items.Count == 0)
        {
            return;
        }

        foreach (var embeddedItem in routePatternCompletionContext.Items)
        {
            var change = embeddedItem.Change;
            var textChange = change.TextChange;

            var properties = ImmutableDictionary.CreateBuilder<string, string>();
            properties.Add(StartKey, textChange.Span.Start.ToString(CultureInfo.InvariantCulture));
            properties.Add(LengthKey, textChange.Span.Length.ToString(CultureInfo.InvariantCulture));
            properties.Add(NewTextKey, textChange.NewText);
            properties.Add(DescriptionKey, embeddedItem.FullDescription);
            //properties.Add(AbstractAggregateEmbeddedLanguageCompletionProvider.EmbeddedProviderName, Name);

            if (change.NewPosition != null)
            {
                properties.Add(NewPositionKey, change.NewPosition.ToString());
            }

            // Keep everything sorted in the order we just produced the items in.
            var sortText = routePatternCompletionContext.Items.Count.ToString("0000", CultureInfo.InvariantCulture);
            context.AddItem(CompletionItem.Create(
                displayText: embeddedItem.DisplayText,
                inlineDescription: embeddedItem.InlineDescription,
                sortText: sortText,
                properties: properties.ToImmutable(),
                rules: s_rules));
        }

        context.IsExclusive = true;
    }

    private void ProvideCompletions(EmbeddedCompletionContext context)
    {
        // First, act as if the user just inserted the previous character.  This will cause us
        // to complete down to the set of relevant items based on that character. If we get
        // anything, we're done and can just show the user those items.  If we have no items to
        // add *and* the user was explicitly invoking completion, then just add the entire set
        // of suggestions to help the user out.
        ProvideCompletionsBasedOffOfPrecedingCharacter(context);

        if (context.Items.Count > 0)
        {
            // We added items.  Nothing else to do here.
            return;
        }

        if (context.Trigger.Kind == CompletionTriggerKind.Insertion)
        {
            // The user was typing a character, and we had nothing to add for them.  Just bail
            // out immediately as we cannot help in this circumstance.
            return;
        }

        // We added no items, but the user explicitly asked for completion.  Add all the
        // items we can to help them out.
        var virtualChar = context.Tree.Text.Find(context.Position);
    }

    private void ProvideCompletionsBasedOffOfPrecedingCharacter(EmbeddedCompletionContext context)
    {
        var previousVirtualCharOpt = context.Tree.Text.Find(context.Position - 1);
        if (previousVirtualCharOpt == null)
        {
            // We didn't have a previous character.  Can't determine the set of 
            // regex items to show.
            return;
        }

        var previousVirtualChar = previousVirtualCharOpt.Value;
        var result = FindToken(context.Tree.Root, previousVirtualChar);
        if (result == null)
        {
            return;
        }

        var (parent, token) = result.Value;
        switch (token.Kind)
        {
            case RoutePatternKind.ColonToken:
                ProvidePolicyNameCompletions(context);
                return;
            case RoutePatternKind.OpenBraceToken:
                ProvideParameterCompletions(context);
                return;
        }

        // There are two major cases we need to consider in regex completion.  Specifically
        // if we're in a character class (i.e. `[...]`) or not. In a character class, most
        // constructs are not special (i.e. a `(` is just a paren, and not the start of a
        // grouping construct).
        //
        // So first figure out if we're in a character class.  And then decide what sort of
        // completion we want depending on the previous character.
        //var inCharacterClass = IsInCharacterClass(context.Tree.Root, previousVirtualChar);
        //switch (token.Kind)
        //{
        //    case RegexKind.BackslashToken:
        //        ProvideBackslashCompletions(context, inCharacterClass, parent);
        //        return;
        //    case RegexKind.OpenBracketToken:
        //        ProvideOpenBracketCompletions(context, inCharacterClass, parent);
        //        return;
        //    case RegexKind.OpenParenToken:
        //        ProvideOpenParenCompletions(context, inCharacterClass, parent);
        //        return;
        //}

        // see if we have ```\p{```.  If so, offer property categories. This isn't handled 
        // in the above switch because when you just have an incomplete `\p{` then the `{` 
        // will be handled as a normal character and won't have a token for it.
        //if (previousVirtualChar.Value == '{')
        //{
        //    ProvidePolicyNameCompletions(context);
        //    return;
        //}
    }

    private void ProvideParameterCompletions(EmbeddedCompletionContext context)
    {
        var method = RouteStringSyntaxDetector.GetTargetMethod(context.StringToken, context.SemanticModel, context.CancellationToken);
        if (method != null)
        {
            foreach (var parameter in method.Parameters)
            {
                context.AddIfMissing(parameter.Name, suffix: null, description: null, parentOpt: null);
            }
        }
    }

    private void ProvidePolicyNameCompletions(EmbeddedCompletionContext context)
    {
        context.AddIfMissing("int", "Integer constraint", "Matches any 32-bit integer.", parentOpt: null);
        context.AddIfMissing("bool", "Boolean constraint", "Matches true or false. Case-insensitive.", parentOpt: null);
        context.AddIfMissing("datetime", "DateTime constraint", "Matches a valid DateTime value in the invariant culture.", parentOpt: null);
        context.AddIfMissing("decimal", "Decimal constraint", "Matches a valid decimal value in the invariant culture.", parentOpt: null);
        context.AddIfMissing("double", "Double constraint", "Matches a valid double value in the invariant culture.", parentOpt: null);
        context.AddIfMissing("float", "Float constraint", "Matches a valid float value in the invariant culture.", parentOpt: null);
        context.AddIfMissing("guid", "Guid constraint", "Matches a valid Guid value.", parentOpt: null);
        context.AddIfMissing("long", "Long constraint", "Matches any 64-bit integer.", parentOpt: null);
        context.AddIfMissing("minlength", "Minimum length constraint", "Matches a string with a length greater than, or equal to, the constraint argument.", parentOpt: null);
        context.AddIfMissing("maxlength", "Maximum length constraint", "Matches a string with a length less than, or equal to, the constraint argument.", parentOpt: null);
        context.AddIfMissing("length", "Length constraint", @"The string length constraint supports one or two constraint arguments.

If there is one argument the string length must equal the argument. For example, length(10) matches a string with exactly 10 characters.

If there are two arguments then the string length must be greater than, or equal to, the first argument and less than, or equal to, the second argument. For example, length(8,16) matches a string at least 8 and no more than 16 characters long.", parentOpt: null);
        context.AddIfMissing("min", "Minimum constraint", "Matches an integer with a value greater than, or equal to, the constraint argument.", parentOpt: null);
        context.AddIfMissing("max", "Maximum constraint", "Matches an integer with a value less than, or equal to, the constraint argument.", parentOpt: null);
        context.AddIfMissing("range", "Range constraint", "Matches an integer with a value greater than, or equal to, the first constraint argument and less than, or equal to, the second constraint argument.", parentOpt: null);
        context.AddIfMissing("alpha", "Alphabet constraint", "Matches a string that contains only lowercase or uppercase letters A through Z in the English alphabet.", parentOpt: null);
        context.AddIfMissing("regex", "Regular expression constraint", "Matches a string to the regular expression constraint argument.", parentOpt: null);
        context.AddIfMissing("required", "Required constraint", "	Used to enforce that a non-parameter value is present during URL generation.", parentOpt: null);
    }

    internal static async ValueTask<(RoutePatternTree tree, SyntaxToken token, SemanticModel model)> TryGetTreeAndTokenAtPositionAsync(
        Document document, int position, CancellationToken cancellationToken)
    {
        var root = await GetSyntaxRootAsync(document, cancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return default;
        }
        var token = root.FindToken(position);

        var semanticModel = await GetSemanticModelAsync(document, cancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
        {
            return default;
        }

        if (!RouteStringSyntaxDetector.IsRouteStringSyntaxToken(token, semanticModel, cancellationToken))
        {
            return default;
        }

        var virtualChars = AspNetCoreCSharpVirtualCharService.Instance.TryConvertToVirtualChars(token);
        if (virtualChars.IsDefault())
        {
            return default;
        }

        var tree = RoutePatternParser.TryParse(virtualChars);
        if (tree == null)
        {
            return default;
        }

        return (tree, token, semanticModel);
    }

    public static async ValueTask<SyntaxNode?> GetSyntaxRootAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.TryGetSyntaxRoot(out var root))
        {
            return root;
        }

        return await document.GetSyntaxRootAsync(cancellationToken);
    }

    public static async ValueTask<SemanticModel?> GetSemanticModelAsync(Document document, CancellationToken cancellationToken)
    {
        if (document.TryGetSemanticModel(out var semanticModel))
        {
            return semanticModel;
        }

        return await document.GetSemanticModelAsync(cancellationToken);
    }

    private (RoutePatternNode parent, RoutePatternToken Token)? FindToken(RoutePatternNode parent, AspNetCoreVirtualChar ch)
    {
        foreach (var child in parent)
        {
            if (child.IsNode)
            {
                var result = FindToken(child.Node, ch);
                if (result != null)
                {
                    return result;
                }
            }
            else
            {
                if (child.Token.VirtualChars.Contains(ch))
                {
                    return (parent, child.Token);
                }
            }
        }

        return null;
    }

    private readonly struct RoutePatternItem
    {
        public readonly string DisplayText;
        public readonly string InlineDescription;
        public readonly string FullDescription;
        public readonly CompletionChange Change;

        public RoutePatternItem(
            string displayText, string inlineDescription, string fullDescription, CompletionChange change)
        {
            DisplayText = displayText;
            InlineDescription = inlineDescription;
            FullDescription = fullDescription;
            Change = change;
        }
    }

    private readonly struct EmbeddedCompletionContext
    {
        private readonly CompletionContext _context;
        private readonly HashSet<string> _names = new();

        public readonly RoutePatternTree Tree;
        public readonly SyntaxToken StringToken;
        public readonly SemanticModel SemanticModel;
        public readonly CancellationToken CancellationToken;
        public readonly int Position;
        public readonly CompletionTrigger Trigger;
        public readonly List<RoutePatternItem> Items = new();

        public EmbeddedCompletionContext(
            CompletionContext context,
            RoutePatternTree tree,
            SyntaxToken stringToken,
            SemanticModel semanticModel)
        {
            _context = context;
            Tree = tree;
            StringToken = stringToken;
            SemanticModel = semanticModel;
            Position = _context.Position;
            Trigger = _context.Trigger;
            CancellationToken = _context.CancellationToken;
        }

        public void AddIfMissing(
            string displayText, string suffix, string description,
            RoutePatternNode parentOpt, int? positionOffset = null, string insertionText = null)
        {
            var replacementStart = parentOpt != null
                ? parentOpt.GetSpan().Start
                : Position;

            var replacementSpan = TextSpan.FromBounds(replacementStart, Position);
            var newPosition = replacementStart + positionOffset;

            insertionText ??= displayText;
            var escapedInsertionText = EscapeText(insertionText, StringToken);

            if (escapedInsertionText != insertionText)
            {
                newPosition += escapedInsertionText.Length - insertionText.Length;
            }

            AddIfMissing(new RoutePatternItem(
                displayText, suffix, description,
                CompletionChange.Create(
                    new TextChange(replacementSpan, escapedInsertionText),
                    newPosition)));
        }

        public void AddIfMissing(RoutePatternItem item)
        {
            if (_names.Add(item.DisplayText))
            {
                Items.Add(item);
            }
        }

        public static string EscapeText(string text, SyntaxToken token)
        {
            // This function is called when Completion needs to escape something its going to
            // insert into the user's string token.  This means that we only have to escape
            // things that completion could insert.  In this case, the only regex character
            // that is relevant is the \ character, and it's only relevant if we insert into
            // a normal string and not a verbatim string.  There are no other regex characters
            // that completion will produce that need any escaping. 
            Debug.Assert(token.IsKind(SyntaxKind.StringLiteralToken));
            return token.IsVerbatimStringLiteral()
                ? text
                : text.Replace(@"\", @"\\");
        }
    }
}

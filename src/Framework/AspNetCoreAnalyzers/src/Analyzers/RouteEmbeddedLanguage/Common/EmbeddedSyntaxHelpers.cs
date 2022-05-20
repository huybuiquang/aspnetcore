// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.EmbeddedLanguages.VirtualChars;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;

internal static class EmbeddedSyntaxHelpers
{
    public static TextSpan GetSpan<TSyntaxKind>(EmbeddedSyntaxToken<TSyntaxKind> token1, EmbeddedSyntaxToken<TSyntaxKind> token2) where TSyntaxKind : struct
        => GetSpan(token1.VirtualChars[0], token2.VirtualChars.Last());

    public static TextSpan GetSpan(AspNetCoreVirtualCharSequence virtualChars)
        => GetSpan(virtualChars[0], virtualChars.Last());

    public static TextSpan GetSpan(AspNetCoreVirtualChar firstChar, AspNetCoreVirtualChar lastChar)
        => TextSpan.FromBounds(firstChar.Span.Start, lastChar.Span.End);
}

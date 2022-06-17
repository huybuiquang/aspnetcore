// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal static class MvcHelpers
{
    private const string ControllerTypeNameSuffix = "Controller";

    // Replicates logic from ControllerFeatureProvider.IsController.
    // https://github.com/dotnet/aspnetcore/blob/785cf9bd845a8d28dce3a079c4fedf4a4c2afe57/src/Mvc/Mvc.Core/src/Controllers/ControllerFeatureProvider.cs#L39
    public static bool IsController(ITypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsReferenceType)
        {
            return false;
        }

        if (typeSymbol.IsAbstract)
        {
            return false;
        }

        // We only consider public top-level classes as controllers. IsPublic returns false for nested
        // classes, regardless of visibility modifiers
        if (typeSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        // Has generic arguments
        if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
        {
            return false;
        }

        if (HasMvcAttribute(typeSymbol, "NonControllerAttribute"))
        {
            return false;
        }

        if (!typeSymbol.Name.EndsWith(ControllerTypeNameSuffix, StringComparison.OrdinalIgnoreCase) &&
            !HasMvcAttribute(typeSymbol, "ControllerAttribute"))
        {
            return false;
        }

        return true;
    }

    //private static bool HasControllerAttribute(ITypeSymbol typeSymbol)
    //{
    //    foreach (var item in typeSymbol.GetAttributes())
    //    {
    //        if (item.AttributeClass is
    //            {
    //                Name: "ControllerAttribute",
    //                ContainingNamespace:
    //                {
    //                    Name: "Mvc",
    //                    ContainingNamespace:
    //                    {
    //                        Name: "AspNetCore",
    //                        ContainingNamespace:
    //                        {
    //                            Name: "Microsoft",
    //                            ContainingNamespace.IsGlobalNamespace: true,
    //                        }
    //                    }
    //                }
    //            })
    //        {
    //            return true;
    //        }
    //    }

    //    return false;
    //}

    private static bool HasMvcAttribute(ISymbol symbol, string attributeName)
    {
        foreach (var item in symbol.GetAttributes())
        {
            if (item.AttributeClass.Name == attributeName &&
                item.AttributeClass is
                {
                    ContainingNamespace:
                    {
                        Name: "Mvc",
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
                return true;
            }
        }

        return false;
    }

    // Replicates logic from DefaultApplicationModelProvider.IsAction.
    // https://github.com/dotnet/aspnetcore/blob/785cf9bd845a8d28dce3a079c4fedf4a4c2afe57/src/Mvc/Mvc.Core/src/ApplicationModels/DefaultApplicationModelProvider.cs#L393
    public static bool IsAction(IMethodSymbol methodSymbol)
    {
        if (methodSymbol == null)
        {
            throw new ArgumentNullException(nameof(methodSymbol));
        }

        // The SpecialName bit is set to flag members that are treated in a special way by some compilers
        // (such as property accessors and operator overloading methods).
        if (methodSymbol.MethodKind is not MethodKind.Ordinary or MethodKind.DeclareMethod)
        {
            return false;
        }

        if (HasMvcAttribute(methodSymbol, "NonActionAttribute"))
        {
            return false;
        }

        // Overridden methods from Object class, e.g. Equals(Object), GetHashCode(), etc., are not valid.
        if (methodSymbol.OriginalDefinition.ContainingType is
            {
                Name: "Object",
                ContainingNamespace:
                {
                    Name: "System",
                    IsGlobalNamespace: true
                }
            })
        {
            return false;
        }

        // Dispose method implemented from IDisposable is not valid
        if (IsIDisposableMethod(methodSymbol))
        {
            return false;
        }

        if (methodSymbol.IsStatic)
        {
            return false;
        }

        if (methodSymbol.IsAbstract)
        {
            return false;
        }

        if (methodSymbol.IsGenericMethod)
        {
            return false;
        }

        return methodSymbol.DeclaredAccessibility == Accessibility.Public;
    }

    private static bool IsIDisposableMethod(IMethodSymbol methodSymbol)
    {
        // Ideally we do not want Dispose method to be exposed as an action. However there are some scenarios where a user
        // might want to expose a method with name "Dispose" (even though they might not be really disposing resources)
        // Example: A controller deriving from MVC's Controller type might wish to have a method with name Dispose,
        // in which case they can use the "new" keyword to hide the base controller's declaration.

        // Find where the method was originally declared
        var baseMethodInfo = methodSymbol.OriginalDefinition;
        var declaringType = baseMethodInfo.ContainingType;

        return false;
            //(typeof(IDisposable).IsAssignableFrom(declaringType) &&
            // declaringType.GetInterfaceMap(typeof(IDisposable)).TargetMethods[0] == baseMethodInfo);
    }
}

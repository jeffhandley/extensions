// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.LocalAnalyzers.Utilities;

namespace Microsoft.Extensions.LocalAnalyzers;

/// <summary>
/// Flags the two <em>always-on</em> ways an <c>[Experimental]</c> API leaks into the JSON metadata that
/// System.Text.Json's source generator produces for a consumer. When a consumer writes
/// <c>[JsonSerializable(typeof(List&lt;AIContent&gt;))]</c>, the generator walks the whole object graph and
/// emits a reference to every serialized member and every statically registered polymorphic derived type. If
/// any of those is <c>[Experimental]</c>, the consumer's own build reports the experimental diagnostic (for
/// example MEAI001) even though the consumer never touched the experimental API.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer is a precise, low-noise <em>tripwire</em> for the two hazards a consumer cannot opt out of:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>LA0009</b> — an externally visible, non-<c>[JsonIgnore]</c>'d, <c>[Experimental]</c> member on a type that
/// participates in a <c>[JsonPolymorphic]</c> hierarchy (the #7549 shape). Fix: mark the public accessor
/// <c>[JsonIgnore]</c> and route the value through a non-experimental internal member.
/// </description></item>
/// <item><description>
/// <b>LA0010</b> — an <c>[Experimental]</c> type registered as a static <c>[JsonDerivedType]</c> on a
/// non-experimental base (the #6900 shape). Fix: register the derived type at runtime instead of statically.
/// </description></item>
/// </list>
/// <para>
/// Both rules are intentionally scoped to the polymorphic, statically registered contract because that is the
/// vector a consumer is forced through. The general case — <em>any</em> experimental symbol reachable from a
/// serialization root, including non-polymorphic DTOs — is a whole-graph reachability property that a
/// symbol-local analyzer cannot decide without unacceptable false positives; it is covered holistically by the
/// real source-generation backstop in Microsoft.Extensions.AI.Stabilization.Tests, which compiles the actual
/// public serialization roots with the experimental diagnostic promoted to an error.
/// </para>
/// <para>
/// The analyzer no-ops on any assembly that does not reference System.Text.Json.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExperimentalJsonSerializationAnalyzer : DiagnosticAnalyzer
{
    private const string ExperimentalAttributeFullName = "global::System.Diagnostics.CodeAnalysis.ExperimentalAttribute";
    private const string JsonIncludeAttributeFullName = "global::System.Text.Json.Serialization.JsonIncludeAttribute";

    private const string JsonPolymorphicAttributeMetadataName = "System.Text.Json.Serialization.JsonPolymorphicAttribute";
    private const string JsonIgnoreAttributeMetadataName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonDerivedTypeAttributeMetadataName = "System.Text.Json.Serialization.JsonDerivedTypeAttribute";

    // System.Text.Json.Serialization.JsonIgnoreCondition.Always == 1. Only this condition fully excludes the
    // member; every other condition (Never, WhenWritingDefault, WhenWritingNull) still serializes it, so the
    // experimental member can still leak.
    private const int JsonIgnoreConditionAlways = 1;

    private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics = ImmutableArray.Create(
        DiagDescriptors.ExperimentalMemberMustBeJsonIgnored,
        DiagDescriptors.ExperimentalTypeMustNotBeJsonDerivedType);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        _ = context ?? throw new ArgumentNullException(nameof(context));

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;

        var jsonPolymorphic = compilation.GetTypeByMetadataName(JsonPolymorphicAttributeMetadataName);
        var jsonIgnore = compilation.GetTypeByMetadataName(JsonIgnoreAttributeMetadataName);
        var jsonDerivedType = compilation.GetTypeByMetadataName(JsonDerivedTypeAttributeMetadataName);

        if (jsonPolymorphic is not null && jsonIgnore is not null)
        {
            context.RegisterSymbolAction(ctx => AnalyzeMember(ctx, jsonPolymorphic, jsonIgnore), SymbolKind.Property, SymbolKind.Field);
        }

        if (jsonDerivedType is not null)
        {
            context.RegisterSymbolAction(ctx => AnalyzeDerivedTypeRegistrations(ctx, jsonDerivedType), SymbolKind.NamedType);
        }
    }

    private static void AnalyzeMember(SymbolAnalysisContext context, INamedTypeSymbol jsonPolymorphic, INamedTypeSymbol jsonIgnore)
    {
        var member = context.Symbol;

        if (member.IsStatic
            || !member.IsExternallyVisible()
            || !member.HasAttribute(ExperimentalAttributeFullName)
            || !IsSerializedInstanceMember(member))
        {
            return;
        }

        var containingType = member.ContainingType;
        if (containingType is null
            || containingType.IsContaminated(ExperimentalAttributeFullName)
            || !ParticipatesInPolymorphicContract(containingType, jsonPolymorphic)
            || IsEffectivelyJsonIgnored(member, jsonIgnore))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagDescriptors.ExperimentalMemberMustBeJsonIgnored,
            member.Locations.Length > 0 ? member.Locations[0] : Location.None,
            member.Name,
            containingType.Name));
    }

    private static void AnalyzeDerivedTypeRegistrations(SymbolAnalysisContext context, INamedTypeSymbol jsonDerivedType)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // If the base itself is experimental, the whole hierarchy is experimental and a consumer must already
        // opt in to reach it; static registration is not the forced-on-consumer leak this rule targets.
        if (type.IsContaminated(ExperimentalAttributeFullName))
        {
            return;
        }

        foreach (var attribute in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, jsonDerivedType)
                || attribute.ConstructorArguments.Length == 0)
            {
                continue;
            }

            var derivedTypeArgument = attribute.ConstructorArguments[0];
            if (derivedTypeArgument.Kind != TypedConstantKind.Type
                || derivedTypeArgument.Value is not INamedTypeSymbol derivedType
                || !derivedType.IsContaminated(ExperimentalAttributeFullName))
            {
                continue;
            }

            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                ?? (type.Locations.Length > 0 ? type.Locations[0] : Location.None);

            context.ReportDiagnostic(Diagnostic.Create(
                DiagDescriptors.ExperimentalTypeMustNotBeJsonDerivedType,
                location,
                derivedType.Name,
                type.Name));
        }
    }

    private static bool IsSerializedInstanceMember(ISymbol member)
    {
        // Properties serialize by default (excluding indexers). Public fields serialize only when explicitly
        // opted in with [JsonInclude], so an experimental public field without it cannot leak.
        return member switch
        {
            IPropertySymbol property => !property.IsIndexer,
            IFieldSymbol field => field.HasAttribute(JsonIncludeAttributeFullName),
            _ => false,
        };
    }

    private static bool ParticipatesInPolymorphicContract(INamedTypeSymbol type, INamedTypeSymbol jsonPolymorphic)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.HasAttribute(jsonPolymorphic))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEffectivelyJsonIgnored(ISymbol member, INamedTypeSymbol jsonIgnore)
    {
        foreach (var attribute in member.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, jsonIgnore))
            {
                continue;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "Condition")
                {
                    // [JsonIgnore(Condition = ...)]: only Always fully excludes the member.
                    return namedArgument.Value.Value is int condition && condition == JsonIgnoreConditionAlways;
                }
            }

            // [JsonIgnore] with no Condition defaults to Always, which fully excludes the member.
            return true;
        }

        return false;
    }
}

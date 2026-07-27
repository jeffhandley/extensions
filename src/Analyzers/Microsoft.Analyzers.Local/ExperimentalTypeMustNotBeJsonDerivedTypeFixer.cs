// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.Extensions.LocalAnalyzers;

/// <summary>
/// Code fix for <see cref="DiagDescriptors.ExperimentalTypeMustNotBeJsonDerivedType"/> (LA0010). Removes the static
/// <c>[JsonDerivedType(typeof(SomeExperimentalType), ...)]</c> registration that leaks an <c>[Experimental]</c> type
/// into consumers' source-generated JSON metadata.
/// </summary>
/// <remarks>
/// <para>
/// This rule is not specific to <c>AIContent</c>: it fires for an <c>[Experimental]</c> type statically registered as
/// a <c>[JsonDerivedType]</c> on <em>any</em> non-experimental polymorphic base. Unlike the LA0009 member fix, the
/// corrective action here is inherently cross-file: after the static attribute is removed the type still has to be
/// registered at runtime so it round-trips through its owner's serialization, and that registration lives on the
/// <c>JsonSerializerOptions</c> that builds the contract, not on the base type.
/// </para>
/// <para>
/// The concrete runtime API depends on the root. For the <c>AIContent</c> hierarchy that motivated the rule
/// (dotnet/extensions #6900) it is <c>options.AddAIContentType&lt;TContent&gt;("discriminator")</c> in
/// <c>AIJsonUtilities.DefaultOptions</c>. For any other polymorphic root it is the general System.Text.Json contract
/// customization path: a <c>JsonSerializerOptions.TypeInfoResolver</c> modifier that, for the base type's
/// <c>JsonTypeInfo</c>, appends a <c>JsonDerivedType</c> entry for the experimental type to
/// <c>JsonPolymorphismOptions.DerivedTypes</c>. A single-document Roslyn fixer cannot deterministically synthesize
/// either: it owns neither the discriminator-string convention nor the options-construction site, and guessing either
/// would produce silently wrong serialization.
/// </para>
/// <para>
/// The fix therefore automates only the part that is safe and deterministic — deleting the leaking static
/// <c>[JsonDerivedType]</c> attribute — and defers the runtime re-registration to the author, who is directed to it by
/// the diagnostic's description and this fix's title. This is deliberately honest about what can be mechanized rather
/// than over-promising an unreliable cross-file transform.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ExperimentalTypeMustNotBeJsonDerivedTypeFixer))]
[Shared]
public sealed class ExperimentalTypeMustNotBeJsonDerivedTypeFixer : CodeFixProvider
{
    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagDescriptors.ExperimentalTypeMustNotBeJsonDerivedType.Id);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        // The diagnostic is reported at the [JsonDerivedType(...)] attribute application itself.
        var attribute = root?.FindNode(context.Span)?.FirstAncestorOrSelf<AttributeSyntax>();
        if (attribute is null || attribute.Parent is not AttributeListSyntax attributeList)
        {
            return;
        }

        var action = CodeAction.Create(
            Resources.ExperimentalTypeMustNotBeJsonDerivedTypeFixTitle,
            _ => Task.FromResult(RemoveRegistration(context.Document, root!, attribute, attributeList)),
            equivalenceKey: nameof(ExperimentalTypeMustNotBeJsonDerivedTypeFixer));

        context.RegisterCodeFix(action, context.Diagnostics);
    }

    private static Document RemoveRegistration(Document document, SyntaxNode root, AttributeSyntax attribute, AttributeListSyntax attributeList)
    {
        SyntaxNode? newRoot;

        if (attributeList.Attributes.Count > 1)
        {
            // The registration shares a bracketed list with other attributes; drop just this one and keep the rest.
            var newList = attributeList.WithAttributes(attributeList.Attributes.Remove(attribute));
            newRoot = root.ReplaceNode(attributeList, newList);
        }
        else
        {
            // The registration is the only attribute in its bracketed list (the AIContent shape has one
            // JsonDerivedType attribute per line). Remove the whole list with its leading indentation and
            // trailing newline so no blank line is left behind.
            newRoot = root.RemoveNode(attributeList, SyntaxRemoveOptions.KeepNoTrivia);
        }

        return document.WithSyntaxRoot(newRoot!);
    }
}

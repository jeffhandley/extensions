// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Microsoft.Extensions.LocalAnalyzers;

/// <summary>
/// Code fix for <see cref="DiagDescriptors.ExperimentalMemberMustBeJsonIgnored"/> (LA0009). Mechanically applies the
/// internal-shadow pattern to an <c>[Experimental]</c> property on a <c>[JsonPolymorphic]</c> hierarchy so the value
/// keeps round-tripping through the package's own serialization while it stops leaking into consumers'
/// source-generated JSON metadata.
/// </summary>
/// <remarks>
/// The flagged property <c>Foo</c> is rewritten into two members:
/// <list type="number">
/// <item><description>
/// A public wrapper <c>Foo</c> that keeps the original leading trivia (XML docs) and <c>[Experimental]</c> attribute,
/// gains <c>[JsonIgnore]</c>, and delegates to the internal member.
/// </description></item>
/// <item><description>
/// An <c>internal</c> member <c>FooCore</c> that keeps the original accessors, bodies, and initializer, drops
/// <c>[Experimental]</c>, and gains <c>[JsonInclude]</c> plus <c>[JsonPropertyName("foo")]</c> so it carries the JSON.
/// </description></item>
/// </list>
/// The fix is offered only for properties. The analyzer can also fire on a <c>[JsonInclude]</c> field, but converting a
/// field into the accessor/backing-member pair is out of scope, so no fix is offered there.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ExperimentalMemberMustBeJsonIgnoredFixer))]
[Shared]
public sealed class ExperimentalMemberMustBeJsonIgnoredFixer : CodeFixProvider
{
    private const string ExperimentalAttributeSimpleName = "Experimental";
    private const string JsonIgnoreAttributeSimpleName = "JsonIgnore";
    private const string JsonIncludeAttributeSimpleName = "JsonInclude";
    private const string JsonPropertyNameAttributeSimpleName = "JsonPropertyName";
    private const string CoreSuffix = "Core";

    private static readonly string[] _internalMemberComment =
    {
        "// Including public, experimental properties leaks them into the source-generated",
        "// JSON metadata of any consumer, also forcing that consumer to suppress the",
        "// experimental diagnostic. The public property is annotated with [JsonIgnore] and",
        "// it routes its value through this internal property. The internal property is",
        "// annotated with [JsonInclude] and a [JsonPropertyName] to match the public",
        "// property's name, making it available in the default serialization options.",
    };

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagDescriptors.ExperimentalMemberMustBeJsonIgnored.Id);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        // Only a property can be rewritten into the accessor/internal-member pair. The analyzer may also flag a
        // [JsonInclude] field, but there is no mechanical field->property fix, so no code fix is offered there.
        var property = root?.FindNode(context.Span)?.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
        if (property is null)
        {
            return;
        }

        var action = CodeAction.Create(
            Resources.ExperimentalMemberMustBeJsonIgnoredFixTitle,
            _ => Task.FromResult(Apply(context.Document, root!, property)),
            equivalenceKey: nameof(ExperimentalMemberMustBeJsonIgnoredFixer));

        context.RegisterCodeFix(action, context.Diagnostics);
    }

    private static Document Apply(Document document, SyntaxNode root, PropertyDeclarationSyntax property)
    {
        var originalName = property.Identifier.ValueText;
        var coreName = originalName + CoreSuffix;
        var jsonName = ToCamelCase(originalName);
        var indent = GetIndentation(property);

        var publicProperty = BuildPublicWrapper(property, originalName, coreName, indent);
        var coreProperty = BuildInternalMember(property, coreName, jsonName, indent);

        var newRoot = root.ReplaceNode(property, new SyntaxNode[] { publicProperty, coreProperty });
        return document.WithSyntaxRoot(newRoot);
    }

    // Public wrapper: keeps the original leading trivia (XML docs), carries the [Experimental] attribute(s) plus a new
    // [JsonIgnore], and delegates to the internal member with whichever accessors the original declared.
    private static PropertyDeclarationSyntax BuildPublicWrapper(PropertyDeclarationSyntax property, string originalName, string coreName, string indent)
    {
        var attributes = EnumerateAttributes(property, keep: name => IsNamed(name, ExperimentalAttributeSimpleName)).ToList();
        attributes.Add(Attribute(IdentifierName(JsonIgnoreAttributeSimpleName)));

        return PropertyDeclaration(
                LayOutAttributes(attributes, indent),
                TokenList(Token(SyntaxKind.PublicKeyword).WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(Space)),
                property.Type.WithoutTrivia().WithTrailingTrivia(Space),
                explicitInterfaceSpecifier: null,
                Identifier(originalName).WithTrailingTrivia(EndOfLine("\n")),
                BuildDelegatingAccessorList(property, coreName, indent))
            .WithLeadingTrivia(property.GetLeadingTrivia());
    }

    // Internal member: keeps the original accessors, bodies, and initializer verbatim, renamed with the "Core" suffix
    // and made internal; drops [Experimental]/[JsonIgnore] and gains [JsonInclude] + [JsonPropertyName("...")]. An
    // explanatory comment replaces the original leading trivia (the XML docs moved to the public wrapper).
    private static PropertyDeclarationSyntax BuildInternalMember(PropertyDeclarationSyntax property, string coreName, string jsonName, string indent)
    {
        var attributes = EnumerateAttributes(property, keep: name =>
            !IsNamed(name, ExperimentalAttributeSimpleName) &&
            !IsNamed(name, JsonIgnoreAttributeSimpleName) &&
            !IsNamed(name, JsonIncludeAttributeSimpleName) &&
            !IsNamed(name, JsonPropertyNameAttributeSimpleName)).ToList();

        attributes.Add(Attribute(IdentifierName(JsonIncludeAttributeSimpleName)));
        attributes.Add(Attribute(IdentifierName(JsonPropertyNameAttributeSimpleName))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(
                AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(jsonName)))))));

        return property
            .WithAttributeLists(LayOutAttributes(attributes, indent))
            .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword).WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(Space)))
            .WithIdentifier(Identifier(coreName).WithTriviaFrom(property.Identifier))
            .WithLeadingTrivia(BuildCommentTrivia(indent));
    }

    // Lays out one attribute per line at the member indent. The first attribute's leading indent is supplied by the
    // member's own leading trivia (XML docs / comment), so it starts with no leading whitespace of its own.
    private static SyntaxList<AttributeListSyntax> LayOutAttributes(List<AttributeSyntax> attributes, string indent)
    {
        var lists = new List<AttributeListSyntax>(attributes.Count);
        for (var i = 0; i < attributes.Count; i++)
        {
            var leading = i == 0 ? TriviaList() : TriviaList(Whitespace(indent));
            lists.Add(AttributeList(SingletonSeparatedList(attributes[i]))
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(EndOfLine("\n")));
        }

        return List(lists);
    }

    // Builds the public wrapper's accessor list in the Allman, one-accessor-per-line layout, delegating each accessor
    // to the internal member and mirroring whichever accessors the original declared.
    private static AccessorListSyntax BuildDelegatingAccessorList(PropertyDeclarationSyntax property, string coreName, string indent)
    {
        var accessorIndent = indent + "    ";
        var (hasGet, hasSet, hasInit) = InspectAccessors(property);

        var accessors = new List<AccessorDeclarationSyntax>();

        if (hasGet)
        {
            accessors.Add(AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithExpressionBody(ArrowExpressionClause(IdentifierName(coreName)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(Whitespace(accessorIndent))
                .WithTrailingTrivia(EndOfLine("\n")));
        }

        if (hasSet || hasInit)
        {
            accessors.Add(AccessorDeclaration(hasInit ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(ArrowExpressionClause(
                    AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(coreName), IdentifierName("value"))))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(Whitespace(accessorIndent))
                .WithTrailingTrivia(EndOfLine("\n")));
        }

        return AccessorList(
            Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine("\n")),
            List(accessors),
            Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine("\n")));
    }

    private static (bool hasGet, bool hasSet, bool hasInit) InspectAccessors(PropertyDeclarationSyntax property)
    {
        var hasGet = property.ExpressionBody is not null;
        var hasSet = false;
        var hasInit = false;

        if (property.AccessorList is not null)
        {
            foreach (var accessor in property.AccessorList.Accessors)
            {
                switch (accessor.Kind())
                {
                    case SyntaxKind.GetAccessorDeclaration:
                        hasGet = true;
                        break;
                    case SyntaxKind.SetAccessorDeclaration:
                        hasSet = true;
                        break;
                    case SyntaxKind.InitAccessorDeclaration:
                        hasInit = true;
                        break;
                }
            }
        }

        return (hasGet, hasSet, hasInit);
    }

    private static IEnumerable<AttributeSyntax> EnumerateAttributes(PropertyDeclarationSyntax property, Func<NameSyntax, bool> keep)
        => property.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => keep(attribute.Name))
            .Select(attribute => attribute.WithoutTrivia());

    private static SyntaxTriviaList BuildCommentTrivia(string indent)
    {
        var trivia = new List<SyntaxTrivia> { EndOfLine("\n") };

        foreach (var line in _internalMemberComment)
        {
            trivia.Add(Whitespace(indent));
            trivia.Add(Comment(line));
            trivia.Add(EndOfLine("\n"));
        }

        trivia.Add(Whitespace(indent));
        return TriviaList(trivia);
    }

    private static string GetIndentation(PropertyDeclarationSyntax property)
    {
        var trivia = property.GetLeadingTrivia();
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            if (trivia[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                return trivia[i].ToString();
            }

            if (trivia[i].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                break;
            }
        }

        return "    ";
    }

    private static bool IsNamed(NameSyntax attributeName, string simpleName)
    {
        var name = attributeName switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => attributeName.ToString(),
        };

        return string.Equals(name, simpleName, StringComparison.Ordinal)
            || string.Equals(name, simpleName + "Attribute", StringComparison.Ordinal);
    }

    private static string ToCamelCase(string name)
        => name.Length > 0
            ? char.ToLowerInvariant(name[0]) + name.Substring(1)
            : name;
}

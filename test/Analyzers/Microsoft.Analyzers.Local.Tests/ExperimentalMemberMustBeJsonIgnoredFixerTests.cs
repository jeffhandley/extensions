// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET8_0_OR_GREATER

using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.Extensions.LocalAnalyzers.Resource.Test;
using Xunit;

namespace Microsoft.Extensions.LocalAnalyzers.Test;

public class ExperimentalMemberMustBeJsonIgnoredFixerTests
{
    private const string Usings = @"
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
";

    private static readonly Assembly[] _stjReferences = { typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly };

    [Fact]
    public async Task AutoPropertyWithInitializerAndDocs()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    /// <summary>Gets or sets the flag.</summary>
    /// <remarks>Defaults to true.</remarks>
    [Experimental(""X001"", UrlFormat = ""u"")]
    public bool Flag { get; set; } = true;
}
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    /// <summary>Gets or sets the flag.</summary>
    /// <remarks>Defaults to true.</remarks>
    [Experimental(""X001"", UrlFormat = ""u"")]
    [JsonIgnore]
    public bool Flag
    {
        get => FlagCore;
        set => FlagCore = value;
    }

    // Including public, experimental properties leaks them into the source-generated
    // JSON metadata of any consumer, also forcing that consumer to suppress the
    // experimental diagnostic. The public property is annotated with [JsonIgnore] and
    // it routes its value through this internal property. The internal property is
    // annotated with [JsonInclude] and a [JsonPropertyName] to match the public
    // property's name, making it available in the default serialization options.
    [JsonInclude]
    [JsonPropertyName(""flag"")]
    internal bool FlagCore { get; set; } = true;
}
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalMemberMustBeJsonIgnoredFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task PreservesPreexistingNonExperimentalAttribute()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonPropertyOrder(2)]
    public bool Flag { get; set; }
}
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonIgnore]
    public bool Flag
    {
        get => FlagCore;
        set => FlagCore = value;
    }

    // Including public, experimental properties leaks them into the source-generated
    // JSON metadata of any consumer, also forcing that consumer to suppress the
    // experimental diagnostic. The public property is annotated with [JsonIgnore] and
    // it routes its value through this internal property. The internal property is
    // annotated with [JsonInclude] and a [JsonPropertyName] to match the public
    // property's name, making it available in the default serialization options.
    [JsonPropertyOrder(2)]
    [JsonInclude]
    [JsonPropertyName(""flag"")]
    internal bool FlagCore { get; set; }
}
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalMemberMustBeJsonIgnoredFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task PreservesFullPropertyBodies()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
public class Base
{
    private bool _flag;

    /// <summary>Docs.</summary>
    [Experimental(""X001"")]
    public bool Flag
    {
        get { return _flag; }
        set { _flag = value; }
    }
}
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
public class Base
{
    private bool _flag;

    /// <summary>Docs.</summary>
    [Experimental(""X001"")]
    [JsonIgnore]
    public bool Flag
    {
        get => FlagCore;
        set => FlagCore = value;
    }

    // Including public, experimental properties leaks them into the source-generated
    // JSON metadata of any consumer, also forcing that consumer to suppress the
    // experimental diagnostic. The public property is annotated with [JsonIgnore] and
    // it routes its value through this internal property. The internal property is
    // annotated with [JsonInclude] and a [JsonPropertyName] to match the public
    // property's name, making it available in the default serialization options.
    [JsonInclude]
    [JsonPropertyName(""flag"")]
    internal bool FlagCore
    {
        get { return _flag; }
        set { _flag = value; }
    }
}
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalMemberMustBeJsonIgnoredFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task PreservesExpressionBodiedReadOnlyProperty()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
public class Base
{
    [Experimental(""X001"")]
    public bool Flag => true;
}
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
public class Base
{
    [Experimental(""X001"")]
    [JsonIgnore]
    public bool Flag
    {
        get => FlagCore;
    }

    // Including public, experimental properties leaks them into the source-generated
    // JSON metadata of any consumer, also forcing that consumer to suppress the
    // experimental diagnostic. The public property is annotated with [JsonIgnore] and
    // it routes its value through this internal property. The internal property is
    // annotated with [JsonInclude] and a [JsonPropertyName] to match the public
    // property's name, making it available in the default serialization options.
    [JsonInclude]
    [JsonPropertyName(""flag"")]
    internal bool FlagCore => true;
}
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalMemberMustBeJsonIgnoredFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task DoesNotOfferFixForField()
    {
        // The analyzer flags a [JsonInclude] experimental field, but converting a field into the accessor/backing-
        // member pair is out of scope, so no fix is offered and the source is left unchanged.
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
public class Base
{
    [Experimental(""X001"")]
    [JsonInclude]
    public bool Flag;
}
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalMemberMustBeJsonIgnoredFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(source.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public void UtilityMethods()
    {
        var fixer = new ExperimentalMemberMustBeJsonIgnoredFixer();
        Assert.Single(fixer.FixableDiagnosticIds);
        Assert.Equal(DiagDescriptors.ExperimentalMemberMustBeJsonIgnored.Id, fixer.FixableDiagnosticIds[0]);
        Assert.Equal(WellKnownFixAllProviders.BatchFixer, fixer.GetFixAllProvider());
    }
}

#endif

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET8_0_OR_GREATER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.LocalAnalyzers.Resource.Test;
using Xunit;

namespace Microsoft.Extensions.LocalAnalyzers.Test;

public class ExperimentalJsonSerializationAnalyzerTests
{
    private const string LA0009 = "LA0009";
    private const string LA0010 = "LA0010";

    private const string Usings = @"
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
";

    // Brings System.Text.Json attributes into the analyzed compilation. System.Diagnostics.CodeAnalysis.ExperimentalAttribute
    // is resolved from the base (corelib) references on net8.0+.
    private static readonly Assembly[] _stjReferences = { typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly };

    [Fact]
    public async Task ExperimentalMember_OnPolymorphicDerivedType_WithoutJsonIgnore_Flags()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    public bool Flag { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(1, Count(diagnostics, LA0009));
        Assert.Equal(0, Count(diagnostics, LA0010));
    }

    [Fact]
    public async Task ExperimentalMember_OnPolymorphicBaseType_WithoutJsonIgnore_Flags()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base
{
    [Experimental(""X001"")]
    public bool Flag { get; set; }
}

public sealed class Derived : Base { }
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(1, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_WithBareJsonIgnore_DoesNotFlag()
    {
        var source = Usings + @"
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

    [JsonInclude]
    [JsonPropertyName(""flag"")]
    internal bool FlagCore { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_WithConditionalJsonIgnore_StillFlags()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(1, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_WithAlwaysJsonIgnore_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public string? Value { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_OnNonPolymorphicType_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

public sealed class Dto
{
    [Experimental(""X001"")]
    public bool Flag { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_ThatIsStatic_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    public static bool Flag { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_ThatIsNotExternallyVisible_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonInclude]
    internal bool Flag { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalIndexer_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    public bool this[int index] => index > 0;
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalField_WithJsonInclude_Flags()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    [JsonInclude]
    public bool Flag;
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(1, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalField_WithoutJsonInclude_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

public sealed class Derived : Base
{
    [Experimental(""X001"")]
    public bool Flag;
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalMember_OnExperimentalType_DoesNotFlagMemberRule()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Derived), ""derived"")]
public class Base { }

[Experimental(""X002"")]
public sealed class Derived : Base
{
    [Experimental(""X001"")]
    public bool Flag { get; set; }
}
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task ExperimentalType_AsStaticJsonDerivedType_Flags()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(ExperimentalDerived), ""exp"")]
public class Base { }

[Experimental(""X002"")]
public sealed class ExperimentalDerived : Base { }
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(1, Count(diagnostics, LA0010));
        Assert.Equal(0, Count(diagnostics, LA0009));
    }

    [Fact]
    public async Task StableType_AsStaticJsonDerivedType_DoesNotFlag()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(StableDerived), ""stable"")]
public class Base { }

public sealed class StableDerived : Base { }
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0010));
    }

    [Fact]
    public async Task ExperimentalType_OnExperimentalBase_DoesNotFlagTypeRule()
    {
        var source = Usings + @"
namespace Example;

[Experimental(""X003"")]
[JsonPolymorphic]
[JsonDerivedType(typeof(ExperimentalDerived), ""exp"")]
public class Base { }

[Experimental(""X002"")]
public sealed class ExperimentalDerived : Base { }
";
        var diagnostics = await AnalyzeAsync(source);

        Assert.Equal(0, Count(diagnostics, LA0010));
    }

    [Fact]
    public async Task WithoutSystemTextJson_Analyzer_NoOps()
    {
        // No System.Text.Json reference: the analyzer resolves no attribute symbols and registers no actions.
        var source = @"
using System.Diagnostics.CodeAnalysis;

namespace Example;

public sealed class Dto
{
    [Experimental(""X001"")]
    public bool Flag { get; set; }
}
";
        var diagnostics = await RoslynTestUtils.RunAnalyzer(
            new ExperimentalJsonSerializationAnalyzer(),
            references: null,
            new[] { source });

        Assert.Equal(0, Count(diagnostics, LA0009));
        Assert.Equal(0, Count(diagnostics, LA0010));
    }

    [Fact]
    public void Initialize_WithNull_Throws()
    {
        var analyzer = new ExperimentalJsonSerializationAnalyzer();
        Assert.Throws<ArgumentNullException>(() => analyzer.Initialize(null!));
    }

    private static int Count(IReadOnlyList<Diagnostic> diagnostics, string id) =>
        diagnostics.Count(d => d.Id == id);

    private static Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string source) =>
        RoslynTestUtils.RunAnalyzer(new ExperimentalJsonSerializationAnalyzer(), _stjReferences, new[] { source });
}

#endif

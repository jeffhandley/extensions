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

public class ExperimentalTypeMustNotBeJsonDerivedTypeFixerTests
{
    private const string Usings = @"
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
";

    private static readonly Assembly[] _stjReferences = { typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly };

    [Fact]
    public async Task RemovesStaticRegistrationForExperimentalDerivedType()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable"")]
[JsonDerivedType(typeof(Exp), ""exp"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class Exp : Base { }
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class Exp : Base { }
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalTypeMustNotBeJsonDerivedTypeFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task RemovesStaticRegistration_ForNonAIContentRoot()
    {
        // LA0010 and this fixer key off the [JsonPolymorphic]/[JsonDerivedType] contract shape, not off AIContent. A
        // polymorphic root from a completely unrelated domain is detected and fixed identically; only the runtime
        // re-registration the author performs afterward differs by root (a JsonSerializerOptions.TypeInfoResolver
        // modifier for an arbitrary root, versus the AIContent-specific AddAIContentType helper).
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(EmailNotification), ""email"")]
[JsonDerivedType(typeof(SmsNotification), ""sms"")]
public abstract class Notification { }

public sealed class EmailNotification : Notification { }

[Experimental(""PKG001"")]
public sealed class SmsNotification : Notification { }
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(EmailNotification), ""email"")]
public abstract class Notification { }

public sealed class EmailNotification : Notification { }

[Experimental(""PKG001"")]
public sealed class SmsNotification : Notification { }
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalTypeMustNotBeJsonDerivedTypeFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task RemovesOnlyExperimentalRegistration_WhenSharedInOneAttributeList()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable""), JsonDerivedType(typeof(Exp), ""exp"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class Exp : Base { }
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class Exp : Base { }
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalTypeMustNotBeJsonDerivedTypeFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task RemovesEveryExperimentalRegistration()
    {
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(ExpOne), ""expOne"")]
[JsonDerivedType(typeof(Stable), ""stable"")]
[JsonDerivedType(typeof(ExpTwo), ""expTwo"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class ExpOne : Base { }

[Experimental(""X002"")]
public sealed class ExpTwo : Base { }
";

        var expected = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable"")]
public class Base { }

public sealed class Stable : Base { }

[Experimental(""X002"")]
public sealed class ExpOne : Base { }

[Experimental(""X002"")]
public sealed class ExpTwo : Base { }
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalTypeMustNotBeJsonDerivedTypeFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public async Task DoesNotChangeStableRegistration()
    {
        // The analyzer does not fire for a stable derived type, so the fixer has nothing to do and the source is
        // left unchanged.
        var source = Usings + @"
namespace Example;

[JsonPolymorphic]
[JsonDerivedType(typeof(Stable), ""stable"")]
public class Base { }

public sealed class Stable : Base { }
";

        var actual = await RoslynTestUtils.RunAnalyzerAndFixer(
            new ExperimentalJsonSerializationAnalyzer(),
            new ExperimentalTypeMustNotBeJsonDerivedTypeFixer(),
            _stjReferences,
            new[] { source });

        Assert.Equal(source.Replace("\r\n", "\n", StringComparison.Ordinal), actual[0]);
    }

    [Fact]
    public void UtilityMethods()
    {
        var fixer = new ExperimentalTypeMustNotBeJsonDerivedTypeFixer();
        Assert.Single(fixer.FixableDiagnosticIds);
        Assert.Equal(DiagDescriptors.ExperimentalTypeMustNotBeJsonDerivedType.Id, fixer.FixableDiagnosticIds[0]);
        Assert.Equal(WellKnownFixAllProviders.BatchFixer, fixer.GetFixAllProvider());
    }
}

#endif

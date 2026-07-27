// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Microsoft.Extensions.AI;

public partial class ExperimentalApiLeakGuardTests
{
    // The real guarantee here is compile-time: this project treats MEAI001 as an error, so if any experimental
    // member had leaked into ExperimentalApiLeakGuard's source-generated metadata, the project would not have
    // built. This test simply exercises the generated context at runtime to prove it is wired up and that the
    // guarded roots round-trip.
    [Fact]
    public void PublicSerializationRoots_RoundtripThroughSourceGeneratedContext()
    {
        List<AIContent> contents =
        [
            new TextContent("hello"),
            new UsageContent(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2 }),
        ];

        string json = JsonSerializer.Serialize(contents, ExperimentalApiLeakGuard.Default.ListAIContent);
        List<AIContent>? roundtripped = JsonSerializer.Deserialize(json, ExperimentalApiLeakGuard.Default.ListAIContent);

        Assert.NotNull(roundtripped);
        Assert.Equal(2, roundtripped.Count);
        Assert.IsType<TextContent>(roundtripped[0]);
        Assert.IsType<UsageContent>(roundtripped[1]);
    }

    // Holistic guard against experimental APIs leaking into a consumer's source-generated JSON.
    //
    // This project treats MEAI001 as an error and applies no experimental-API suppression (see the csproj), so it
    // stands in for a consumer whose build fails on experimental usage. Every Microsoft.Extensions.AI experiment
    // shares the single diagnostic id MEAI001, so enforcing that one id here covers the entire experimental surface.
    //
    // The roots below mirror the package's own public serialization entry points (AIJsonUtilities.JsonContext). The
    // System.Text.Json source generator eagerly walks the full object graph reachable from them - every
    // [JsonDerivedType] of the polymorphic contracts and every property of every reachable type - and emits an
    // accessor for each serialized member. If any reachable member is [Experimental] and not [JsonIgnore]'d, the
    // generated code references it and this project fails to build.
    //
    // This makes the guard broad without referencing any experimental type or member itself: it catches an
    // experimental member leaking anywhere reachable from these roots - polymorphic derived type or plain property
    // alike. New content types, annotations, options, and members are covered for free because they hang off the
    // existing roots. The one maintenance point is a brand-new top-level serializable type: when one is added to the
    // package's own JsonContext (AIJsonUtilities), mirror it here with another [JsonSerializable] so its graph is
    // guarded too.
    [JsonSerializable(typeof(List<AIContent>))] // the exact consumer scenario that regressed
    [JsonSerializable(typeof(ChatResponse))]
    [JsonSerializable(typeof(ChatResponseUpdate))]
    [JsonSerializable(typeof(ChatOptions))]
    [JsonSerializable(typeof(Embedding))]
    internal sealed partial class ExperimentalApiLeakGuard : JsonSerializerContext;
}

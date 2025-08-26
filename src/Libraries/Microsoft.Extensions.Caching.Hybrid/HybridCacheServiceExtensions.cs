// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Hybrid.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Configuration extension methods for <see cref="HybridCache"/>.
/// </summary>
public static class HybridCacheServiceExtensions
{
    /// <summary>
    /// Adds support for multi-tier caching services.
    /// </summary>
    /// <returns>A builder instance that allows further configuration of the <see cref="HybridCache"/> system.</returns>
    public static IHybridCacheBuilder AddHybridCache(this IServiceCollection services, Action<HybridCacheOptions> setupAction)
    {
        _ = Throw.IfNull(setupAction);
        _ = AddHybridCache(services);
        _ = services.Configure(setupAction);
        return new HybridCacheBuilder(services);
    }

    /// <summary>
    /// Adds support for multi-tier caching services.
    /// </summary>
    /// <returns>A builder instance that allows further configuration of the <see cref="HybridCache"/> system.</returns>
    public static IHybridCacheBuilder AddHybridCache(this IServiceCollection services)
    {
        _ = Throw.IfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        _ = services.AddOptions().AddMemoryCache();
        services.TryAddSingleton<IHybridCacheSerializerFactory, DefaultJsonSerializerFactory>();
        services.TryAddSingleton<IHybridCacheSerializer<string>>(InbuiltTypeSerializer.Instance);
        services.TryAddSingleton<IHybridCacheSerializer<byte[]>>(InbuiltTypeSerializer.Instance);

        services.TryAddSingleton<HybridCache, DefaultHybridCache>();
        return new HybridCacheBuilder(services);
    }

    // Add a keyed cache instance
    public static IHybridCacheBuilder AddKeyedHybridCache(this IServiceCollection services, object? serviceKey, Action<HybridCacheOptions> setupAction)
    {
        _ = Throw.IfNull(setupAction);
        _ = AddKeyedHybridCache(services, serviceKey);
        //_ = services.Configure(serviceKey, setupAction);
        return new HybridCacheBuilder(services);
    }

    public static IHybridCacheBuilder AddKeyedHybridCache(this IServiceCollection services, object? serviceKey)
    {
        _ = Throw.IfNull(services);
        services.TryAddKeyedSingleton(TimeProvider.System, serviceKey);
        //_ = services.AddOptions<HybridCacheOptions>(serviceKey);
        _ = services.AddMemoryCache();
        services.TryAddSingleton<IHybridCacheSerializerFactory, DefaultJsonSerializerFactory>();
        services.TryAddSingleton<IHybridCacheSerializer<string>>(InbuiltTypeSerializer.Instance);
        services.TryAddSingleton<IHybridCacheSerializer<byte[]>>(InbuiltTypeSerializer.Instance);

        services.TryAddKeyedSingleton<HybridCache, DefaultHybridCache>(serviceKey);
        return new HybridCacheBuilder(services);
    }
}

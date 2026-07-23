// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A deterministic <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for tests and local mock scenarios.
/// </summary>
/// <remarks>
/// The generated vectors are repeatable pseudo-embeddings. They use lexical overlap to exercise vector data flows,
/// but they do not represent semantic similarity.
/// </remarks>
public class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static float[] CreateVector(string? value, int dimensions)
    {
        var vector = new float[dimensions];
        if (value is null || value.Length == 0)
        {
            return vector;
        }

        // Hash each case-insensitive token and each three-character shingle into the vector. Shared words and word
        // fragments therefore contribute to the same dimensions, providing deterministic lexical similarity.
        int tokenStart = -1;
        for (int i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && char.IsLetterOrDigit(value[i]))
            {
                tokenStart = tokenStart < 0 ? i : tokenStart;
            }
            else if (tokenStart >= 0)
            {
                AddTokenFeatures(value, tokenStart, i - tokenStart, vector);
                tokenStart = -1;
            }
        }

        // Normalize to unit length so cosine similarity compares lexical overlap rather than document length.
        float squaredMagnitude = 0;
        foreach (float component in vector)
        {
            squaredMagnitude += component * component;
        }

        if (squaredMagnitude > 0)
        {
            float scale = 1 / (float)Math.Sqrt(squaredMagnitude);
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }

    private static void AddTokenFeatures(string value, int tokenStart, int tokenLength, float[] vector)
    {
        AddFeature(value, tokenStart, tokenLength, vector);

        for (int i = tokenStart; i <= tokenStart + tokenLength - 3; i++)
        {
            AddFeature(value, i, 3, vector);
        }
    }

    private static void AddFeature(string value, int start, int length, float[] vector)
    {
        uint hash = 2_166_136_261;
        unchecked
        {
            for (int i = start; i < start + length; i++)
            {
                hash = (hash ^ char.ToLowerInvariant(value[i])) * 16_777_619;
            }
        }

        vector[(int)(hash % (uint)vector.Length)] += 1;
    }

    private readonly int _dimensions;

    /// <summary>Initializes a new instance of the <see cref="MockEmbeddingGenerator"/> class.</summary>
    /// <param name="dimensions">The number of values in each generated vector.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimensions"/> is less than one.</exception>
    public MockEmbeddingGenerator(int dimensions)
    {
        _dimensions = Throw.IfLessThan(dimensions, 1, nameof(dimensions));
    }

    /// <inheritdoc />
    public virtual Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(values);

        GeneratedEmbeddings<Embedding<float>> embeddings = [];
        foreach (string value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings.Add(new Embedding<float>(CreateVector(value, _dimensions)));
        }

        return Task.FromResult(embeddings);
    }

    /// <inheritdoc />
    public virtual object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }

}

using System;

namespace Quant.Core.Numerics;

/// <summary>
/// Small deterministic PRNG (PCG-XSH-RR 32-bit). Used by the Monte Carlo and slippage
/// models so that a given seed reproduces a given run exactly — a randomised robustness
/// test that cannot be reproduced cannot be acted on.
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;
    private ulong _state;
    private readonly ulong _increment;

    public Pcg32(ulong seed = 42UL, ulong sequence = 54UL)
    {
        _increment = (sequence << 1) | 1UL;
        _state = 0;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong old = _state;
        _state = (old * Multiplier) + _increment;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform double in [0,1).</summary>
    public double NextDouble() => NextUInt() * (1.0 / 4294967296.0);

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + (int)(NextDouble() * (maxExclusive - minInclusive));
    }

    /// <summary>Standard normal via Box-Muller (one value per call; the pair's second half is discarded).</summary>
    public double NextGaussian()
    {
        double u1 = 1.0 - NextDouble();
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>In-place Fisher-Yates shuffle.</summary>
    public void Shuffle<T>(T[] array)
    {
        if (array == null) return;
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = NextInt(0, i + 1);
            T tmp = array[i];
            array[i] = array[j];
            array[j] = tmp;
        }
    }
}

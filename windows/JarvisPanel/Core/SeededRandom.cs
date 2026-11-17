namespace JarvisPanel.Core;

/// <summary>
/// SplitMix64 — small, fast and seedable. Deterministic seeds make the particle
/// simulation reproducible, matching the Swift implementation bit for bit.
/// </summary>
public struct SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed)
    {
        _state = seed;
    }

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double Unit() => (Next() >> 11) / (double)(1UL << 53);

    /// <summary>Uniform in [a, b).</summary>
    public double Range(double a, double b) => a + Unit() * (b - a);
}

using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Deterministic seeded value noise (dossier S2: no external dependency).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every sample is a pure function of its coordinates and the seed. Lattice
    /// values come from a 64-bit FNV-1a hash of the integer lattice cell
    /// <c>(x, y)</c> mixed with the seed, finished with the MurmurHash3
    /// <c>fmix64</c> avalanche so that adjacent seeds decorrelate; the top
    /// 53 bits of the result map exactly onto <c>[0, 1)</c> in
    /// <see cref="double"/>.
    /// </para>
    /// <para>
    /// There is no clock, no <c>System.Random</c> and no mutable state, so
    /// results are identical across calls and instances.
    /// </para>
    /// </remarks>
    public static class Noise
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private const double Inverse53Bit = 1.0 / 9007199254740992.0;

        /// <summary>
        /// Smoothed value noise in <c>[0, 1]</c> at <c>(x, y)</c> for
        /// <paramref name="seed"/>.
        /// </summary>
        public static double Value2D(double x, double y, long seed)
        {
            int x0 = FloorToInt(x);
            int y0 = FloorToInt(y);
            double u = Fade(x - x0);
            double v = Fade(y - y0);

            double n00 = Lattice(x0, y0, seed);
            double n10 = Lattice(x0 + 1, y0, seed);
            double n01 = Lattice(x0, y0 + 1, seed);
            double n11 = Lattice(x0 + 1, y0 + 1, seed);

            double top = n00 + ((n10 - n00) * u);
            double bottom = n01 + ((n11 - n01) * u);
            return top + ((bottom - top) * v);
        }

        /// <summary>
        /// Fractal Brownian motion in <c>[0, 1]</c>: sums
        /// <paramref name="octaves"/> value-noise octaves, each with doubled
        /// frequency, halved amplitude and a distinct seed offset.
        /// </summary>
        public static double Fbm2D(double x, double y, long seed, int octaves)
        {
            double sum = 0.0;
            double amplitude = 1.0;
            double total = 0.0;
            double frequency = 1.0;

            for (int i = 0; i < octaves; i++)
            {
                sum += amplitude * Value2D(x * frequency, y * frequency, seed + i);
                total += amplitude;
                amplitude *= 0.5;
                frequency *= 2.0;
            }

            return total > 0.0 ? sum / total : 0.0;
        }

        private static double Fade(double t)
        {
            return t * t * t * ((t * ((t * 6.0) - 15.0)) + 10.0);
        }

        private static double Lattice(int x, int y, long seed)
        {
            unchecked
            {
                ulong hash = OffsetBasis;
                hash = (hash ^ (ulong)(uint)x) * Prime;
                hash = (hash ^ (ulong)(uint)y) * Prime;
                hash = (hash ^ (ulong)seed) * Prime;
                return (Avalanche(hash) >> 11) * Inverse53Bit;
            }
        }

        // MurmurHash3 fmix64: full avalanche of the low-entropy FNV state.
        private static ulong Avalanche(ulong value)
        {
            unchecked
            {
                value ^= value >> 33;
                value *= 0xFF51AFD7ED558CCDUL;
                value ^= value >> 33;
                value *= 0xC4CEB9FE1A85EC53UL;
                value ^= value >> 33;
                return value;
            }
        }

        private static int FloorToInt(double value)
        {
            if (value <= int.MinValue)
            {
                return int.MinValue;
            }

            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Floor(value);
        }
    }
}

using System;
using UnityEngine;

namespace LivingPlanet
{
    public static class Noise
    {
        static readonly int[] perm = new int[512];

        static Noise() { Seed(1); }

        public static void Seed(int seed)
        {
            var r = new System.Random(seed);
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            for (int i = 255; i > 0; i--)
            {
                int j = r.Next(i + 1);
                int t = p[i]; p[i] = p[j]; p[j] = t;
            }
            for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
        }

        static float Fade(float t) { return t * t * t * (t * (t * 6f - 15f) + 10f); }
        static float Lerp(float t, float a, float b) { return a + t * (b - a); }

        static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        public static float Perlin(float x, float y, float z)
        {
            float fx = (float)Math.Floor(x), fy = (float)Math.Floor(y), fz = (float)Math.Floor(z);
            int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
            x -= fx; y -= fy; z -= fz;
            float u = Fade(x), v = Fade(y), w = Fade(z);
            int A = perm[X] + Y, AA = perm[A] + Z, AB = perm[A + 1] + Z;
            int B = perm[X + 1] + Y, BA = perm[B] + Z, BB = perm[B + 1] + Z;
            return Lerp(w,
                Lerp(v,
                    Lerp(u, Grad(perm[AA], x, y, z), Grad(perm[BA], x - 1, y, z)),
                    Lerp(u, Grad(perm[AB], x, y - 1, z), Grad(perm[BB], x - 1, y - 1, z))),
                Lerp(v,
                    Lerp(u, Grad(perm[AA + 1], x, y, z - 1), Grad(perm[BA + 1], x - 1, y, z - 1)),
                    Lerp(u, Grad(perm[AB + 1], x, y - 1, z - 1), Grad(perm[BB + 1], x - 1, y - 1, z - 1))));
        }

        public static float Fbm(Vector3 p, int octaves, float lacunarity = 2.03f, float gain = 0.5f)
        {
            float sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Perlin(p.x, p.y, p.z);
                norm += amp;
                amp *= gain;
                p *= lacunarity;
            }
            return sum / norm;
        }

        // 0..1, sharp crests
        public static float Ridged(Vector3 p, int octaves, float lacunarity = 2.1f, float gain = 0.5f)
        {
            float sum = 0, amp = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Math.Abs(Perlin(p.x, p.y, p.z)) * 1.15f;
                if (n < 0) n = 0;
                sum += amp * n * n;
                norm += amp;
                amp *= gain;
                p *= lacunarity;
            }
            return sum / norm;
        }
    }

    public static class Par
    {
        public static void For(int count, Action<int, int> body)
        {
            int parts = Math.Max(1, Environment.ProcessorCount * 2);
            int chunk = (count + parts - 1) / parts;
            System.Threading.Tasks.Parallel.For(0, parts, p =>
            {
                int s = p * chunk;
                int e = Math.Min(count, s + chunk);
                if (s < e) body(s, e);
            });
        }
    }

    internal sealed class PlanetRandom : System.Random
    {
        uint state;

        public PlanetRandom(int seed)
        {
            state = unchecked((uint)seed);
            if (state == 0) state = 0x6d2b79f5u;
        }

        public int State
        {
            get { return unchecked((int)state); }
            set
            {
                if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
                state = unchecked((uint)value);
            }
        }

        uint NextUInt()
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        protected override double Sample() { return NextUInt() / 4294967296.0; }
        public override double NextDouble() { return Sample(); }
        public override int Next() { return (int)(Sample() * int.MaxValue); }
        public override int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            return (int)(Sample() * maxValue);
        }
        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            return (int)(minValue + (long)(Sample() * ((long)maxValue - minValue)));
        }
        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(NextUInt() >> 24);
        }
    }
}

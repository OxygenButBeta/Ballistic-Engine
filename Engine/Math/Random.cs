using OpenTK.Mathematics;

namespace BallisticEngine;

// Game-facing random (Unity's `Random` / UnityEngine.Random). A single shared, seedable stream so
// gameplay can be made deterministic by setting Random.Seed (e.g. for replays/tests). Named to
// shadow System.Random inside game scripts on purpose — `Random.Range(...)` should mean THIS.
//
// NOT thread-safe: like Unity's, intended for the main game thread (Tick/FixedTick/OnBegin). For
// background work create your own System.Random.
public static class Random {
    static System.Random rng = new();

    // Re-seeds the shared stream. Set this once at startup (or per run) for reproducible sequences.
    public static int Seed {
        set => rng = new System.Random(value);
    }

    // Resets to a fresh time-seeded stream (undoes a fixed Seed).
    public static void Randomize() => rng = new System.Random();

    // [0,1] inclusive double mapped to float, matching Unity's `Random.value` (inclusive of 1).
    public static float Value => (float)rng.NextDouble();

    // Float in [min, max] (inclusive), Unity semantics.
    public static float Range(float min, float max) => min + (max - min) * (float)rng.NextDouble();

    // Int in [min, max) — max EXCLUSIVE, Unity semantics (the asymmetry trips everyone once).
    public static int Range(int min, int max) => min >= max ? min : rng.Next(min, max);

    // -1..1 on each axis.
    public static float Signed => (float)(rng.NextDouble() * 2.0 - 1.0);

    public static bool Bool => rng.NextDouble() < 0.5;

    // Uniform point inside the unit sphere (rejection sample — exact, no clustering at center).
    public static Vector3 InsideUnitSphere {
        get {
            Vector3 p;
            do {
                p = new Vector3(Signed, Signed, Signed);
            } while (p.LengthSquared > 1f);
            return p;
        }
    }

    // Uniform direction on the unit sphere surface.
    public static Vector3 OnUnitSphere {
        get {
            Vector3 p = InsideUnitSphere;
            float len = p.Length;
            return len < Mathf.Epsilon ? Vector3.UnitY : p / len;
        }
    }

    // Uniform point inside the unit circle (XY plane).
    public static Vector2 InsideUnitCircle {
        get {
            Vector2 p;
            do {
                p = new Vector2(Signed, Signed);
            } while (p.LengthSquared > 1f);
            return p;
        }
    }

    // Uniform random rotation.
    public static Quaternion Rotation {
        get {
            // Shoemake's method: uniform unit quaternion.
            float u1 = Value, u2 = Value, u3 = Value;
            float s1 = MathF.Sqrt(1f - u1), s2 = MathF.Sqrt(u1);
            return new Quaternion(
                s1 * MathF.Sin(MathF.Tau * u2),
                s1 * MathF.Cos(MathF.Tau * u2),
                s2 * MathF.Sin(MathF.Tau * u3),
                s2 * MathF.Cos(MathF.Tau * u3));
        }
    }

    // Random HDR-safe color (full-saturation hue at value 1) — handy for debug visuals.
    public static Vector3 ColorHsv {
        get {
            float h = Value * 6f;
            int i = (int)h;
            float f = h - i;
            float q = 1f - f;
            return i switch {
                0 => new Vector3(1f, f, 0f),
                1 => new Vector3(q, 1f, 0f),
                2 => new Vector3(0f, 1f, f),
                3 => new Vector3(0f, q, 1f),
                4 => new Vector3(f, 0f, 1f),
                _ => new Vector3(1f, 0f, q),
            };
        }
    }
}

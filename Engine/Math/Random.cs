
namespace BallisticEngine;

public static class Random {
    static System.Random rng = new();

    public static int Seed {
        set => rng = new System.Random(value);
    }

    public static void Randomize() => rng = new System.Random();

    public static float Value => (float)rng.NextDouble();

    public static float Range(float min, float max) => min + (max - min) * (float)rng.NextDouble();

    public static int Range(int min, int max) => min >= max ? min : rng.Next(min, max);

    public static float Signed => (float)(rng.NextDouble() * 2.0 - 1.0);

    public static bool Bool => rng.NextDouble() < 0.5;

    public static Vector3 InsideUnitSphere {
        get {
            Vector3 p;
            do {
                p = new Vector3(Signed, Signed, Signed);
            } while (p.LengthSquared() > 1f);
            return p;
        }
    }

    public static Vector3 OnUnitSphere {
        get {
            Vector3 p = InsideUnitSphere;
            float len = p.Length();
            return len < Mathf.Epsilon ? Vector3.UnitY : p / len;
        }
    }

    public static Vector2 InsideUnitCircle {
        get {
            Vector2 p;
            do {
                p = new Vector2(Signed, Signed);
            } while (p.LengthSquared() > 1f);
            return p;
        }
    }

    public static Quaternion Rotation {
        get {
            float u1 = Value, u2 = Value, u3 = Value;
            float s1 = MathF.Sqrt(1f - u1), s2 = MathF.Sqrt(u1);
            return new Quaternion(
                s1 * MathF.Sin(MathF.Tau * u2),
                s1 * MathF.Cos(MathF.Tau * u2),
                s2 * MathF.Sin(MathF.Tau * u3),
                s2 * MathF.Cos(MathF.Tau * u3));
        }
    }

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

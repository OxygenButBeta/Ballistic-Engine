using System.Globalization;
using System.Text;

namespace BallisticEngine;

public sealed class ColorGradient {
    public struct ColorKey {
        public float Time;
        public Vector3 Color;
        public ColorKey(float time, Vector3 color) { Time = time; Color = color; }
    }

    public struct AlphaKey {
        public float Time;
        public float Alpha;
        public AlphaKey(float time, float alpha) { Time = time; Alpha = alpha; }
    }

    readonly List<ColorKey> colorKeys = new();
    readonly List<AlphaKey> alphaKeys = new();

    public IReadOnlyList<ColorKey> ColorKeys => colorKeys;
    public IReadOnlyList<AlphaKey> AlphaKeys => alphaKeys;
    public int ColorKeyCount => colorKeys.Count;
    public int AlphaKeyCount => alphaKeys.Count;

    public bool IsEmpty => colorKeys.Count == 0 && alphaKeys.Count == 0;

    public ColorGradient() { }

    public ColorGradient(Vector3 start, Vector3 end) {
        AddColorKey(0f, start);
        AddColorKey(1f, end);
        AddAlphaKey(0f, 1f);
        AddAlphaKey(1f, 1f);
    }

    public int AddColorKey(float time, Vector3 color) {
        var k = new ColorKey(Math.Clamp(time, 0f, 1f), color);
        int i = colorKeys.FindIndex(x => x.Time > k.Time);
        if (i < 0) { colorKeys.Add(k); return colorKeys.Count - 1; }
        colorKeys.Insert(i, k);
        return i;
    }

    public int AddAlphaKey(float time, float alpha) {
        var k = new AlphaKey(Math.Clamp(time, 0f, 1f), Math.Clamp(alpha, 0f, 1f));
        int i = alphaKeys.FindIndex(x => x.Time > k.Time);
        if (i < 0) { alphaKeys.Add(k); return alphaKeys.Count - 1; }
        alphaKeys.Insert(i, k);
        return i;
    }

    public void RemoveColorKey(int index) { if ((uint)index < (uint)colorKeys.Count) colorKeys.RemoveAt(index); }
    public void RemoveAlphaKey(int index) { if ((uint)index < (uint)alphaKeys.Count) alphaKeys.RemoveAt(index); }

    public int MoveColorKey(int index, float time, Vector3 color) {
        if ((uint)index >= (uint)colorKeys.Count) return index;
        colorKeys.RemoveAt(index);
        return AddColorKey(time, color);
    }

    public int MoveAlphaKey(int index, float time, float alpha) {
        if ((uint)index >= (uint)alphaKeys.Count) return index;
        alphaKeys.RemoveAt(index);
        return AddAlphaKey(time, alpha);
    }

    public void Clear() { colorKeys.Clear(); alphaKeys.Clear(); }

    public Vector3 EvaluateColor(float t) {
        int n = colorKeys.Count;
        if (n == 0) return Vector3.One;
        if (n == 1) return colorKeys[0].Color;
        if (t <= colorKeys[0].Time) return colorKeys[0].Color;
        if (t >= colorKeys[n - 1].Time) return colorKeys[n - 1].Color;

        int hi = UpperColor(t);
        ColorKey a = colorKeys[hi - 1], b = colorKeys[hi];
        float span = b.Time - a.Time;
        float f = span > 0f ? (t - a.Time) / span : 0f;
        return Vector3.Lerp(a.Color, b.Color, f);
    }

    public float EvaluateAlpha(float t) {
        int n = alphaKeys.Count;
        if (n == 0) return 1f;
        if (n == 1) return alphaKeys[0].Alpha;
        if (t <= alphaKeys[0].Time) return alphaKeys[0].Alpha;
        if (t >= alphaKeys[n - 1].Time) return alphaKeys[n - 1].Alpha;

        int hi = UpperAlpha(t);
        AlphaKey a = alphaKeys[hi - 1], b = alphaKeys[hi];
        float span = b.Time - a.Time;
        float f = span > 0f ? (t - a.Time) / span : 0f;
        return a.Alpha + (b.Alpha - a.Alpha) * f;
    }

    public Vector4 Evaluate(float t) {
        Vector3 rgb = EvaluateColor(t);
        return new Vector4(rgb, EvaluateAlpha(t));
    }

    int UpperColor(float t) {
        int lo = 1, hi = colorKeys.Count - 1;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (colorKeys[mid].Time <= t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    int UpperAlpha(float t) {
        int lo = 1, hi = alphaKeys.Count - 1;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (alphaKeys[mid].Time <= t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    public static ColorGradient Fire() {
        var g = new ColorGradient();
        g.AddColorKey(0f, new Vector3(1f, 0.95f, 0.6f));
        g.AddColorKey(0.4f, new Vector3(1f, 0.45f, 0.1f));
        g.AddColorKey(1f, new Vector3(0.3f, 0.05f, 0.02f));
        g.AddAlphaKey(0f, 1f);
        g.AddAlphaKey(0.8f, 1f);
        g.AddAlphaKey(1f, 0f);
        return g;
    }

    public static ColorGradient FadeOut(Vector3 color) {
        var g = new ColorGradient();
        g.AddColorKey(0f, color);
        g.AddColorKey(1f, color);
        g.AddAlphaKey(0f, 1f);
        g.AddAlphaKey(1f, 0f);
        return g;
    }

    public string ToCompactString() {
        var sb = new StringBuilder();
        sb.Append("c:");
        for (var i = 0; i < colorKeys.Count; i++) {
            if (i > 0) sb.Append(';');
            ColorKey k = colorKeys[i];
            sb.Append(F(k.Time)).Append(',').Append(F(k.Color.X)).Append(',').Append(F(k.Color.Y)).Append(',').Append(F(k.Color.Z));
        }
        sb.Append("|a:");
        for (var i = 0; i < alphaKeys.Count; i++) {
            if (i > 0) sb.Append(';');
            AlphaKey k = alphaKeys[i];
            sb.Append(F(k.Time)).Append(',').Append(F(k.Alpha));
        }
        return sb.ToString();

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    public static ColorGradient Parse(string s) {
        var g = new ColorGradient();
        if (string.IsNullOrWhiteSpace(s)) return g;

        string[] blocks = s.Split('|');
        foreach (string block in blocks) {
            if (block.StartsWith("c:", StringComparison.Ordinal)) {
                foreach (string seg in block.Substring(2).Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                    string[] f = seg.Split(',');
                    if (f.Length < 4) continue;
                    g.colorKeys.Add(new ColorKey(P(f[0]), new Vector3(P(f[1]), P(f[2]), P(f[3]))));
                }
            }
            else if (block.StartsWith("a:", StringComparison.Ordinal)) {
                foreach (string seg in block.Substring(2).Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                    string[] f = seg.Split(',');
                    if (f.Length < 2) continue;
                    g.alphaKeys.Add(new AlphaKey(P(f[0]), P(f[1])));
                }
            }
        }
        g.colorKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
        g.alphaKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return g;

        static float P(string t) =>
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}

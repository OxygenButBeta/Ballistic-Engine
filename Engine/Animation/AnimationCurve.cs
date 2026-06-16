using System.Globalization;
using System.Text;

namespace BallisticEngine;

// A keyframed scalar curve evaluated over time (Unity's AnimationCurve). A reusable primitive: any
// system that needs "a value that changes over a normalized or absolute parameter" uses one — particle
// size/color over lifetime, light flicker, custom easing, camera shake falloff, audio fades. Game code
// builds one from keyframes (or a preset) and calls Evaluate(t); the editor draws an interactive curve
// widget for ANY component member of this type with zero per-component wiring (the reflection inspector
// special-cases it), and it round-trips through scenes as a compact string (no nested-list serializer
// needed).
//
// Interpolation is cubic Hermite between adjacent keys using their out/in tangents (matches Unity's
// "free smooth" tangents). A key with both tangents = +/-Infinity is a CONSTANT (stepped) segment.
public sealed class AnimationCurve {
    // One control point. Tangents are in value-per-second (slope); set both to Infinity for a stepped
    // (constant) segment leaving this key.
    public struct Keyframe {
        public float Time;
        public float Value;
        public float InTangent;
        public float OutTangent;

        public Keyframe(float time, float value, float inTangent = 0f, float outTangent = 0f) {
            Time = time; Value = value; InTangent = inTangent; OutTangent = outTangent;
        }
    }

    // What happens outside the [firstKey.Time, lastKey.Time] range.
    public enum WrapMode { Clamp, Loop, PingPong }

    public WrapMode PreWrap { get; set; } = WrapMode.Clamp;
    public WrapMode PostWrap { get; set; } = WrapMode.Clamp;

    // Keys kept sorted by time. Public read-only view for the editor; mutate via AddKey/MoveKey/etc.
    readonly List<Keyframe> keys = new();
    public IReadOnlyList<Keyframe> Keys => keys;
    public int Count => keys.Count;

    public AnimationCurve() { }

    public AnimationCurve(params Keyframe[] initial) {
        foreach (Keyframe k in initial) AddKey(k);
    }

    // ---- Editing -------------------------------------------------------------

    // Adds a key, keeping the list time-sorted. Returns its index.
    public int AddKey(Keyframe key) {
        int i = keys.FindIndex(k => k.Time > key.Time);
        if (i < 0) { keys.Add(key); return keys.Count - 1; }
        keys.Insert(i, key);
        return i;
    }

    public int AddKey(float time, float value) => AddKey(new Keyframe(time, value));

    public void RemoveKey(int index) {
        if ((uint)index < (uint)keys.Count) keys.RemoveAt(index);
    }

    public void Clear() => keys.Clear();

    // Moves a key to a new time/value, re-sorting; returns its NEW index (the editor tracks selection
    // by re-querying). Tangents are preserved.
    public int MoveKey(int index, float time, float value) {
        if ((uint)index >= (uint)keys.Count) return index;
        Keyframe k = keys[index];
        k.Time = time; k.Value = value;
        keys.RemoveAt(index);
        return AddKey(k);
    }

    public void SetTangents(int index, float inTangent, float outTangent) {
        if ((uint)index >= (uint)keys.Count) return;
        Keyframe k = keys[index];
        k.InTangent = inTangent; k.OutTangent = outTangent;
        keys[index] = k;
    }

    // ---- Evaluation ----------------------------------------------------------

    public float Evaluate(float time) {
        int n = keys.Count;
        if (n == 0) return 0f;
        if (n == 1) return keys[0].Value;

        float first = keys[0].Time, last = keys[n - 1].Time;
        float range = last - first;

        // Outside the range: wrap per the pre/post mode.
        if (time < first) time = Wrap(time, first, last, range, PreWrap, out _);
        else if (time > last) time = Wrap(time, first, last, range, PostWrap, out _);

        if (time <= first) return keys[0].Value;
        if (time >= last) return keys[n - 1].Value;

        // Find the segment [a, b] containing time (binary search on key times).
        int hi = UpperKey(time);
        Keyframe a = keys[hi - 1], b = keys[hi];
        return EvaluateSegment(a, b, time);
    }

    static float EvaluateSegment(in Keyframe a, in Keyframe b, float time) {
        float dt = b.Time - a.Time;
        if (dt <= 0f) return b.Value;

        // A stepped segment: out tangent of `a` is Infinity -> hold a's value until b.
        if (float.IsInfinity(a.OutTangent) || float.IsInfinity(b.InTangent))
            return a.Value;

        float t = (time - a.Time) / dt;
        float t2 = t * t, t3 = t2 * t;

        // Cubic Hermite basis (tangents are slopes in value/second, scaled by the segment length).
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;

        return h00 * a.Value + h10 * dt * a.OutTangent + h01 * b.Value + h11 * dt * b.InTangent;
    }

    static float Wrap(float time, float first, float last, float range, WrapMode mode, out bool handled) {
        handled = true;
        if (range <= 0f) return first;
        switch (mode) {
            case WrapMode.Loop: {
                float r = (time - first) % range;
                if (r < 0f) r += range;
                return first + r;
            }
            case WrapMode.PingPong: {
                float r = (time - first) % (2f * range);
                if (r < 0f) r += 2f * range;
                return r <= range ? first + r : first + (2f * range - r);
            }
            default: // Clamp
                handled = false; // let the caller's edge return fire
                return time < first ? first : last;
        }
    }

    int UpperKey(float time) {
        int lo = 1, hi = keys.Count - 1;
        while (lo < hi) {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Time <= time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---- Presets -------------------------------------------------------------

    // A straight line value0 -> value1 over [0,1] (linear tangents).
    public static AnimationCurve Linear(float timeStart = 0f, float valueStart = 0f, float timeEnd = 1f, float valueEnd = 1f) {
        float slope = timeEnd > timeStart ? (valueEnd - valueStart) / (timeEnd - timeStart) : 0f;
        return new AnimationCurve(
            new Keyframe(timeStart, valueStart, slope, slope),
            new Keyframe(timeEnd, valueEnd, slope, slope));
    }

    // Smooth S-curve 0 -> 1 over [0,1] (flat tangents at both ends = ease in/out).
    public static AnimationCurve EaseInOut(float timeStart = 0f, float valueStart = 0f, float timeEnd = 1f, float valueEnd = 1f) {
        return new AnimationCurve(
            new Keyframe(timeStart, valueStart, 0f, 0f),
            new Keyframe(timeEnd, valueEnd, 0f, 0f));
    }

    // A flat line holding `value` over [0,1].
    public static AnimationCurve Constant(float value = 1f, float timeStart = 0f, float timeEnd = 1f) {
        return new AnimationCurve(
            new Keyframe(timeStart, value, 0f, 0f),
            new Keyframe(timeEnd, value, 0f, 0f));
    }

    // ---- Compact string serialization (one scalar; sidesteps the nested-list serializer) ----------
    // Format: "pre|post|t,v,in,out;t,v,in,out;...". Infinity tangents write as "inf"/"-inf".

    public string ToCompactString() {
        var sb = new StringBuilder();
        sb.Append((int)PreWrap).Append('|').Append((int)PostWrap).Append('|');
        for (var i = 0; i < keys.Count; i++) {
            if (i > 0) sb.Append(';');
            Keyframe k = keys[i];
            sb.Append(F(k.Time)).Append(',').Append(F(k.Value)).Append(',')
              .Append(F(k.InTangent)).Append(',').Append(F(k.OutTangent));
        }
        return sb.ToString();

        static string F(float v) =>
            float.IsPositiveInfinity(v) ? "inf" :
            float.IsNegativeInfinity(v) ? "-inf" :
            v.ToString("R", CultureInfo.InvariantCulture);
    }

    public static AnimationCurve Parse(string s) {
        var curve = new AnimationCurve();
        if (string.IsNullOrWhiteSpace(s)) return curve;

        string[] head = s.Split('|');
        string keyPart = s;
        if (head.Length == 3) {
            if (int.TryParse(head[0], out int pre)) curve.PreWrap = (WrapMode)pre;
            if (int.TryParse(head[1], out int post)) curve.PostWrap = (WrapMode)post;
            keyPart = head[2];
        }

        foreach (string segment in keyPart.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            string[] f = segment.Split(',');
            if (f.Length < 2) continue;
            float time = P(f[0]), value = P(f[1]);
            float inT = f.Length > 2 ? P(f[2]) : 0f;
            float outT = f.Length > 3 ? P(f[3]) : 0f;
            curve.keys.Add(new Keyframe(time, value, inT, outT));
        }
        curve.keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return curve;

        static float P(string t) =>
            t == "inf" ? float.PositiveInfinity :
            t == "-inf" ? float.NegativeInfinity :
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}

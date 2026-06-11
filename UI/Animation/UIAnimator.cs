using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// A small per-document tween engine. The UIDocument ticks it each frame; ported controllers drive it
// to animate selection slides, color fades, blood-seep fills, pulses — the "feel" of the menu. It is
// API-driven (not a USS `transition` parser) because the port's interaction logic already lives in a
// C# controller, so animating from there is natural and matches the JS→C# model.
//
// Two animation kinds:
//  * Tween — eases a float from its current value to a target over a duration. Re-targeting the same
//    (owner, channel) key retargets the existing tween (so flipping selection mid-slide is smooth).
//  * Loop — a continuous 0..1 phase (sine/linear) for pulses and ambient motion; runs until removed.
public sealed class UIAnimator
{
    public sealed class Tween
    {
        public object Owner;            // identity for retargeting (usually the VisualElement)
        public string Channel;          // which property, for retargeting ("tx", "opacity", ...)
        public float From, To, Elapsed, Duration;
        public Easing Ease;
        public Action<float> Apply;     // writes the eased value somewhere (e.g. el.Style.TranslateX)
        public bool Done => Elapsed >= Duration;
    }

    public sealed class Loop
    {
        public object Owner;
        public string Channel;
        public float Period;            // seconds per cycle
        public float Phase;             // current 0..1
        public bool PingPong;           // true = 0→1→0 (pulse); false = 0→1 sawtooth
        public Action<float> Apply;     // receives the 0..1 (ping-ponged) value each frame
    }

    readonly List<Tween> _tweens = new();
    readonly List<Loop> _loops = new();

    // Starts (or retargets) a tween on (owner, channel). `current` is the starting value (usually the
    // property's present value); `to` the target. Re-calling with the same key smoothly retargets.
    public void TweenTo(object owner, string channel, float current, float to, float duration,
        Action<float> apply, Easing ease = Easing.EaseOut)
    {
        var existing = _tweens.Find(t => ReferenceEquals(t.Owner, owner) && t.Channel == channel);
        if (existing != null)
        {
            // Retarget from wherever we are now, keeping motion continuous.
            existing.From = Sample(existing);
            existing.To = to;
            existing.Elapsed = 0f;
            existing.Duration = Math.Max(0.0001f, duration);
            existing.Ease = ease;
            existing.Apply = apply;
            return;
        }
        _tweens.Add(new Tween
        {
            Owner = owner, Channel = channel, From = current, To = to,
            Duration = Math.Max(0.0001f, duration), Ease = ease, Apply = apply,
        });
    }

    public void StartLoop(object owner, string channel, float period, bool pingPong, Action<float> apply)
    {
        var existing = _loops.Find(l => ReferenceEquals(l.Owner, owner) && l.Channel == channel);
        if (existing != null) { existing.Period = period; existing.PingPong = pingPong; existing.Apply = apply; return; }
        _loops.Add(new Loop { Owner = owner, Channel = channel, Period = Math.Max(0.01f, period), PingPong = pingPong, Apply = apply });
    }

    public void StopLoops(object owner) => _loops.RemoveAll(l => ReferenceEquals(l.Owner, owner));
    public void Clear() { _tweens.Clear(); _loops.Clear(); }

    // Advances every active animation by dt seconds and applies the results. Finished tweens snap to
    // their target and are removed.
    public void Tick(float dt)
    {
        for (int i = _tweens.Count - 1; i >= 0; i--)
        {
            var t = _tweens[i];
            t.Elapsed += dt;
            float v = Sample(t);
            t.Apply?.Invoke(v);
            if (t.Done) _tweens.RemoveAt(i);
        }

        foreach (var l in _loops)
        {
            l.Phase += dt / l.Period;
            l.Phase -= MathF.Floor(l.Phase); // wrap 0..1
            float v = l.PingPong ? 1f - MathF.Abs(l.Phase * 2f - 1f) : l.Phase; // triangle vs sawtooth
            l.Apply?.Invoke(v);
        }
    }

    static float Sample(Tween t)
    {
        float k = EasingFunctions.Apply(t.Ease, t.Elapsed / t.Duration);
        return t.From + (t.To - t.From) * k;
    }

    public bool HasActiveTweens => _tweens.Count > 0;
}

namespace BallisticEngine.UI;

public sealed class UIAnimator
{
    public sealed class Tween
    {
        public object Owner;
        public string Channel;
        public float From, To, Elapsed, Duration;
        public Easing Ease;
        public Action<float> Apply;
        public bool Done => Elapsed >= Duration;
    }

    public sealed class Loop
    {
        public object Owner;
        public string Channel;
        public float Period;
        public float Phase;
        public bool PingPong;
        public Action<float> Apply;
    }

    readonly List<Tween> _tweens = new();
    readonly List<Loop> _loops = new();

    public void TweenTo(object owner, string channel, float current, float to, float duration,
        Action<float> apply, Easing ease = Easing.EaseOut)
    {
        var existing = _tweens.Find(t => ReferenceEquals(t.Owner, owner) && t.Channel == channel);
        if (existing != null)
        {
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
            l.Phase -= MathF.Floor(l.Phase);
            float v = l.PingPong ? 1f - MathF.Abs(l.Phase * 2f - 1f) : l.Phase;
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

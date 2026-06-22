
namespace BallisticEngine;

[Component("Animator Controller", "Animation")]
public sealed class AnimatorController : Behaviour {
    [Tooltip("Default crossfade time (seconds) for transitions that don't override it.")]
    [Range(0f, 2f)]
    public float DefaultTransitionDuration { get; set; } = 0.15f;

    [Tooltip("Start playing the entry state automatically when the scene begins.")]
    public bool PlayOnAwake { get; set; } = true;

    public enum ParamKind { Bool, Trigger, Float, Int }

    public enum Compare { Greater, Less, Equals, NotEquals, True, False, IfTrigger }

    public sealed class State {
        public string Name;
        public AnimationClip Clip;
        public bool Loop = true;
        public float Speed = 1f;
        internal readonly List<Transition> transitions = new();

        public State(string name, AnimationClip clip) { Name = name; Clip = clip; }

        public Transition To(State target, string parameter, Compare compare, float threshold = 0f, float duration = -1f) {
            var t = new Transition(target, duration);
            t.AddCondition(parameter, compare, threshold);
            transitions.Add(t);
            return t;
        }

        public Transition OnFinished(State target, float duration = -1f) {
            var t = new Transition(target, duration) { OnClipEnd = true };
            transitions.Add(t);
            return t;
        }
    }

    public sealed class Condition {
        public string Parameter;
        public Compare Compare;
        public float Threshold;
    }

    public sealed class Transition {
        public State Target;
        public float Duration;
        public bool OnClipEnd;
        public readonly List<Condition> Conditions = new();

        public Transition(State target, float duration) { Target = target; Duration = duration; }

        public Transition AddCondition(string parameter, Compare compare, float threshold = 0f) {
            Conditions.Add(new Condition { Parameter = parameter, Compare = compare, Threshold = threshold });
            return this;
        }
    }

    readonly List<State> states = new();
    readonly Dictionary<string, float> floats = new();
    readonly Dictionary<string, ParamKind> paramKinds = new();
    readonly HashSet<string> triggers = new();

    State entryState;
    State currentState;
    Animator animator;

    [NotSerialized]
    public string CurrentStateName => currentState?.Name;

    public int StateCount => states.Count;
    public IReadOnlyList<State> States => states;

    public State AddState(string name, AnimationClip clip, bool loop = true, float speed = 1f) {
        var s = new State(name, clip) { Loop = loop, Speed = speed };
        states.Add(s);
        entryState ??= s;
        return s;
    }

    public void SetEntry(State state) => entryState = state;

    public State FindState(string name) {
        foreach (State s in states)
            if (s.Name == name) return s;
        return null;
    }

    public void SetBool(string name, bool value) { floats[name] = value ? 1f : 0f; paramKinds[name] = ParamKind.Bool; }
    public void SetFloat(string name, float value) { floats[name] = value; paramKinds[name] = ParamKind.Float; }
    public void SetInt(string name, int value) { floats[name] = value; paramKinds[name] = ParamKind.Int; }
    public void SetTrigger(string name) { triggers.Add(name); paramKinds[name] = ParamKind.Trigger; }
    public void ResetTrigger(string name) => triggers.Remove(name);

    public bool GetBool(string name) => floats.TryGetValue(name, out float v) && v != 0f;
    public float GetFloat(string name) => floats.TryGetValue(name, out float v) ? v : 0f;
    public int GetInt(string name) => floats.TryGetValue(name, out float v) ? (int)MathF.Round(v) : 0;
    public bool GetTrigger(string name) => triggers.Contains(name);

    public IReadOnlyDictionary<string, ParamKind> Parameters => paramKinds;

    public void DeclareParameter(string name, ParamKind kind) {
        paramKinds[name] = kind;
        if (kind != ParamKind.Trigger && !floats.ContainsKey(name)) floats[name] = 0f;
    }

    protected internal override void OnBegin() {
        animator = GetComponent<Animator>();
        if (PlayOnAwake)
            EnterState(entryState, instant: true);
    }

    public void Play(string stateName, float duration = -1f) {
        State s = FindState(stateName);
        if (s is not null) EnterState(s, instant: duration == 0f, durationOverride: duration);
    }

    void EnterState(State s, bool instant, float durationOverride = -1f) {
        if (s is null) return;
        currentState = s;
        animator ??= GetComponent<Animator>();
        if (animator is null || s.Clip is null) return;

        animator.Loop = s.Loop;
        animator.Speed = s.Speed;
        if (instant)
            animator.Play(s.Clip);
        else {
            float d = durationOverride >= 0f ? durationOverride
                    : DefaultTransitionDuration;
            animator.CrossFade(s.Clip, d);
        }
    }

    protected internal override void Tick(in float delta) {
        if (currentState is null) {
            if (entryState is not null) EnterState(entryState, instant: true);
            return;
        }

        foreach (Transition t in currentState.transitions) {
            if (Satisfied(t)) {
                ConsumeTriggers(t);
                EnterState(t.Target, instant: false, durationOverride: t.Duration);
                return;
            }
        }
    }

    bool Satisfied(Transition t) {
        if (t.OnClipEnd) {
            if (animator is null || currentState?.Clip is null) return false;
            if (currentState.Loop) return false;
            if (animator.Time < currentState.Clip.DurationSeconds) return false;
        }
        else if (t.Conditions.Count == 0) {
            return false;
        }

        foreach (Condition c in t.Conditions)
            if (!ConditionHolds(c)) return false;
        return true;
    }

    bool ConditionHolds(Condition c) {
        switch (c.Compare) {
            case Compare.IfTrigger: return triggers.Contains(c.Parameter);
            case Compare.True:      return GetBool(c.Parameter);
            case Compare.False:     return !GetBool(c.Parameter);
            case Compare.Greater:   return GetFloat(c.Parameter) > c.Threshold;
            case Compare.Less:      return GetFloat(c.Parameter) < c.Threshold;
            case Compare.Equals:    return MathF.Abs(GetFloat(c.Parameter) - c.Threshold) < 1e-4f;
            case Compare.NotEquals: return MathF.Abs(GetFloat(c.Parameter) - c.Threshold) >= 1e-4f;
            default: return false;
        }
    }

    void ConsumeTriggers(Transition t) {
        foreach (Condition c in t.Conditions)
            if (c.Compare == Compare.IfTrigger)
                triggers.Remove(c.Parameter);
    }
}

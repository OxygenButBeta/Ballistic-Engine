using OpenTK.Mathematics;

namespace BallisticEngine;

// A simple animation STATE MACHINE on top of Animator/CrossFade (Unity's AnimatorController, trimmed
// to the essentials). Instead of a script hand-calling Play/CrossFade, you declare named STATES (each
// wrapping a clip) and TRANSITIONS between them gated on PARAMETERS (bool / trigger / float / int).
// Gameplay code just pokes parameters — SetBool("Walking", true), SetTrigger("Jump") — and the graph
// crossfades to the matching state automatically. This is the layer that makes skeletal animation
// gameplay-usable: an idle/walk/run/jump graph instead of manual clip juggling.
//
// Requires an Animator on the same entity (it drives the actual sampling/skinning via CrossFade).
// Runtime/script-driven graph in v1 (states & transitions are built by script in OnBegin, like the
// programmatic half of Unity's API) — the scene serializer doesn't round-trip the nested lists yet;
// the editor exposes a live view + parameter pokers so you can drive and watch the graph without code.
[Component("Animator Controller", "Animation")]
public sealed class AnimatorController : Behaviour {
    [Tooltip("Default crossfade time (seconds) for transitions that don't override it.")]
    [Range(0f, 2f)]
    public float DefaultTransitionDuration { get; set; } = 0.15f;

    [Tooltip("Start playing the entry state automatically when the scene begins.")]
    public bool PlayOnAwake { get; set; } = true;

    // ---- Parameter kinds -----------------------------------------------------

    public enum ParamKind { Bool, Trigger, Float, Int }

    public enum Compare { Greater, Less, Equals, NotEquals, True, False, IfTrigger }

    // ---- States --------------------------------------------------------------

    public sealed class State {
        public string Name;
        public AnimationClip Clip;
        public bool Loop = true;
        public float Speed = 1f;
        internal readonly List<Transition> transitions = new();

        public State(string name, AnimationClip clip) { Name = name; Clip = clip; }

        // Adds an outgoing transition from this state to `target` when `condition` holds. Returns the
        // transition so AddCondition can chain more conditions (all must hold — AND).
        public Transition To(State target, string parameter, Compare compare, float threshold = 0f, float duration = -1f) {
            var t = new Transition(target, duration);
            t.AddCondition(parameter, compare, threshold);
            transitions.Add(t);
            return t;
        }

        // A transition that fires purely on the clip ending (no parameter) — for one-shots like Jump
        // that fall back to a loop state. Only meaningful for non-looping states.
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
        public float Duration; // -1 = use controller default
        public bool OnClipEnd;  // fire when the source clip finishes (one-shots)
        public readonly List<Condition> Conditions = new();

        public Transition(State target, float duration) { Target = target; Duration = duration; }

        public Transition AddCondition(string parameter, Compare compare, float threshold = 0f) {
            Conditions.Add(new Condition { Parameter = parameter, Compare = compare, Threshold = threshold });
            return this;
        }
    }

    // ---- Graph storage -------------------------------------------------------

    readonly List<State> states = new();
    readonly Dictionary<string, float> floats = new();   // also holds bool (0/1) and int (rounded)
    readonly Dictionary<string, ParamKind> paramKinds = new();
    readonly HashSet<string> triggers = new();

    State entryState;
    State currentState;
    Animator animator;

    [NotSerialized]
    public string CurrentStateName => currentState?.Name;

    public int StateCount => states.Count;
    public IReadOnlyList<State> States => states;

    // ---- Graph construction (script-facing, called in OnBegin) ----------------

    // Adds a state. The FIRST state added becomes the entry state unless SetEntry overrides it.
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

    // ---- Parameters (Unity's AnimatorController.SetBool/SetFloat/...) ---------

    public void SetBool(string name, bool value) { floats[name] = value ? 1f : 0f; paramKinds[name] = ParamKind.Bool; }
    public void SetFloat(string name, float value) { floats[name] = value; paramKinds[name] = ParamKind.Float; }
    public void SetInt(string name, int value) { floats[name] = value; paramKinds[name] = ParamKind.Int; }
    public void SetTrigger(string name) { triggers.Add(name); paramKinds[name] = ParamKind.Trigger; }
    public void ResetTrigger(string name) => triggers.Remove(name);

    public bool GetBool(string name) => floats.TryGetValue(name, out float v) && v != 0f;
    public float GetFloat(string name) => floats.TryGetValue(name, out float v) ? v : 0f;
    public int GetInt(string name) => floats.TryGetValue(name, out float v) ? (int)MathF.Round(v) : 0;
    public bool GetTrigger(string name) => triggers.Contains(name);

    // Editor-facing: the declared parameters (name + kind) so the inspector can render the right poker.
    public IReadOnlyDictionary<string, ParamKind> Parameters => paramKinds;

    // Declares a parameter up front (so the editor shows a poker even before it's been set). Optional —
    // Set* also auto-declares.
    public void DeclareParameter(string name, ParamKind kind) {
        paramKinds[name] = kind;
        if (kind != ParamKind.Trigger && !floats.ContainsKey(name)) floats[name] = 0f;
    }

    // ---- Lifecycle -----------------------------------------------------------

    protected internal override void OnBegin() {
        animator = GetComponent<Animator>();
        if (PlayOnAwake)
            EnterState(entryState, instant: true);
    }

    // Switches the active state and tells the Animator to (cross)fade to its clip.
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

        // Evaluate this state's outgoing transitions in declared order; first satisfied one wins.
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
            // Fire when the (non-looping) clip has reached its end.
            if (animator is null || currentState?.Clip is null) return false;
            if (currentState.Loop) return false; // a looping clip never "finishes"
            if (animator.Time < currentState.Clip.DurationSeconds) return false;
            // OnClipEnd may also carry parameter conditions — they must hold too.
        }
        else if (t.Conditions.Count == 0) {
            return false; // a non-OnClipEnd transition with no conditions never fires (avoids instant loops)
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

    // A trigger is one-shot: consumed when a transition that tests it fires.
    void ConsumeTriggers(Transition t) {
        foreach (Condition c in t.Conditions)
            if (c.Compare == Compare.IfTrigger)
                triggers.Remove(c.Parameter);
    }
}

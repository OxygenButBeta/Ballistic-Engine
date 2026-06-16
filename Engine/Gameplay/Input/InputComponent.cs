using System.Numerics;
using BallisticEngine.InputSystem;
using BallisticEngine.Gameplay.Input;

namespace BallisticEngine;

// The owner-local input EVENT source (plan §7). Created by PlayerController on the input authority ONLY
// (in SetupInput); never on a proxy. Subscribe with OnAction / OnAxis2 / OnAxis1 / OnKey; Sample(delta)
// — driven each frame by the owning PlayerController.Tick — evaluates the bound device controls and
// FIRES the callbacks. The dev sees only events; there is no TryGetInput / false-branch (§7.5).
//
// Why events kill the edge case (§7): "input not present" is the callback NOT firing, not a value you
// branch on. On a proxy no InputComponent exists, so the illegal "act on input you don't own" state is
// unrepresentable.
//
// The triggers (Press/Release/Hold) are resolved HERE from the action's definition, so the callback is
// bare (§7.6). Backend-agnostic: reads through IInputSource in our enums (EngineInputSource by default).
public sealed class InputComponent {
    readonly IInputSource source;

    // One state record per subscribed action — tracks edges + hold timing so triggers resolve.
    readonly List<ActionSub> actions = new();
    readonly List<KeySub> keys = new();

    public InputComponent(IInputSource source = null) =>
        this.source = source ?? EngineInputSource.Instance;

    // ---- subscription API (bare callbacks — trigger already resolved in the action) ---------------
    // Button action: fires per the action's Trigger (Press by default; Hold(0.5f) etc. from the def).
    public void OnAction(InputAction action, Action callback) {
        if (Validate(action, InputValueType.Button, nameof(OnAction)))
            actions.Add(new ActionSub(action, ActionCallback.Button(callback)));
    }

    // Button action with an EXPLICIT phase (Started/Canceled) — the optional fine-grained form (§7.2).
    public void OnAction(InputAction action, Phase phase, Action callback) {
        if (Validate(action, InputValueType.Button, nameof(OnAction)))
            actions.Add(new ActionSub(action, ActionCallback.Button(callback), phase));
    }

    // Axis2D action (WASD / stick): fires every frame with the composed Vector2 while non-zero, plus a
    // final zero on release so movement stops (the continuous-input contract).
    public void OnAxis2(InputAction action, Action<Vector2> callback) {
        if (Validate(action, InputValueType.Axis2D, nameof(OnAxis2)))
            actions.Add(new ActionSub(action, ActionCallback.Axis2(callback)));
    }

    // Axis1D action (trigger / scroll): float each frame.
    public void OnAxis1(InputAction action, Action<float> callback) {
        if (Validate(action, InputValueType.Axis1D, nameof(OnAxis1)))
            actions.Add(new ActionSub(action, ActionCallback.Axis1(callback)));
    }

    // Raw key — the F4 case (§7.3). No action, no rebind layer, still event-based + owner-routed (this
    // InputComponent only exists on the owner). Phase.Started = key-down edge, Phase.Canceled = key-up.
    public void OnKey(Key key, Phase phase, Action callback) =>
        keys.Add(new KeySub(key, phase, callback));

    // ---- per-frame evaluation (the polling→events bridge; §7.5) ------------------------------------
    public void Sample(in float delta) {
        bool enabled = source.Enabled;

        foreach (ActionSub sub in actions)
            sub.Evaluate(source, enabled, in delta);

        foreach (KeySub k in keys)
            k.Evaluate(source, enabled);
    }

    bool Validate(InputAction action, InputValueType expected, string method) {
        if (action is null) {
            Debugging.LogError($"InputComponent.{method}: action is null.");
            return false;
        }
        if (action.Value != expected) {
            Debugging.LogError(
                $"InputComponent.{method}: action '{action.Name}' is {action.Value}, not {expected}.");
            return false;
        }
        return true;
    }

    // ---- internal evaluation state ----------------------------------------------------------------
    sealed class ActionSub {
        readonly InputAction action;
        readonly ActionCallback callback;
        readonly Phase? explicitPhase;

        bool wasActive;        // last frame's button-active state (edge detection)
        float heldFor;         // seconds the button has been continuously active (Hold)
        bool holdFired;        // Hold fired this press (one-shot until release)
        bool lastEmittedNonZero; // Axis: did we emit a non-zero last frame (so we emit one final zero)

        public ActionSub(InputAction action, ActionCallback callback, Phase? explicitPhase = null) {
            this.action = action;
            this.callback = callback;
            this.explicitPhase = explicitPhase;
        }

        public void Evaluate(IInputSource source, bool enabled, in float delta) {
            switch (action.Value) {
                case InputValueType.Button: EvaluateButton(source, enabled, in delta); break;
                case InputValueType.Axis1D: EvaluateAxis1(source, enabled); break;
                case InputValueType.Axis2D: EvaluateAxis2(source, enabled); break;
            }
        }

        void EvaluateButton(IInputSource source, bool enabled, in float delta) {
            bool active = enabled && InputEval.ButtonActive(action, source);
            bool down = active && !wasActive;     // pressed edge
            bool up = !active && wasActive;       // released edge

            if (active) heldFor += delta; else heldFor = 0f;

            // Explicit-phase subscription (OnAction(a, Phase.X, cb)) — fire on the matching edge.
            if (explicitPhase is { } phase) {
                if (phase == Phase.Started && down) callback.Invoke();
                else if (phase == Phase.Canceled && up) callback.Invoke();
                wasActive = active;
                return;
            }

            // Trigger-resolved subscription — the action's own Trigger decides when it counts.
            switch (action.Trigger.Kind) {
                case TriggerKind.Press:
                    if (down) callback.Invoke();
                    break;
                case TriggerKind.Release:
                    if (up) callback.Invoke();
                    break;
                case TriggerKind.Hold:
                    if (active && !holdFired && heldFor >= action.Trigger.Param) {
                        callback.Invoke();
                        holdFired = true;
                    }
                    if (up) holdFired = false;
                    break;
                default:
                    // Tap/DoubleTap/Pulse declared but not yet implemented (P0) — fall back to Press so a
                    // definition using them still fires, with a one-time note left to a later input pass.
                    if (down) callback.Invoke();
                    break;
            }
            wasActive = active;
        }

        void EvaluateAxis1(IInputSource source, bool enabled) {
            float v = enabled ? InputEval.Axis1(action, source) : 0f;
            bool nonZero = v != 0f;
            if (nonZero || lastEmittedNonZero)
                callback.Invoke(v);
            lastEmittedNonZero = nonZero;
        }

        void EvaluateAxis2(IInputSource source, bool enabled) {
            Vector2 v = enabled ? InputEval.Axis2(action, source) : Vector2.Zero;
            bool nonZero = v != Vector2.Zero;
            if (nonZero || lastEmittedNonZero)
                callback.Invoke(v);
            lastEmittedNonZero = nonZero;
        }
    }

    readonly struct KeySub {
        readonly Key key;
        readonly Phase phase;
        readonly Action callback;
        // Edge state lives in a tiny mutable box (the struct is stored by value in the list, so we keep
        // the last-state in a holder we can update). Simpler: store KeySub as a class. Use a 1-elem box.
        readonly bool[] wasDown;

        public KeySub(Key key, Phase phase, Action callback) {
            this.key = key;
            this.phase = phase;
            this.callback = callback;
            wasDown = new bool[1];
        }

        public void Evaluate(IInputSource source, bool enabled) {
            bool down = enabled && source.IsKeyDown(key);
            if (phase == Phase.Started && down && !wasDown[0]) callback();
            else if (phase == Phase.Canceled && !down && wasDown[0]) callback();
            wasDown[0] = down;
        }
    }

    // A small tagged callback so one ActionSub can carry any of the three shapes without boxing in the
    // hot path (the delegate is stored once at subscribe).
    readonly struct ActionCallback {
        readonly Action button;
        readonly Action<float> axis1;
        readonly Action<Vector2> axis2;
        ActionCallback(Action b, Action<float> a1, Action<Vector2> a2) { button = b; axis1 = a1; axis2 = a2; }
        public static ActionCallback Button(Action cb) => new(cb, null, null);
        public static ActionCallback Axis1(Action<float> cb) => new(null, cb, null);
        public static ActionCallback Axis2(Action<Vector2> cb) => new(null, null, cb);
        public void Invoke() => button?.Invoke();
        public void Invoke(float v) => axis1?.Invoke(v);
        public void Invoke(Vector2 v) => axis2?.Invoke(v);
    }
}

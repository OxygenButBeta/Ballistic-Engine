using BallisticEngine.InputSystem;
using BallisticEngine.Gameplay.Input;
using BallisticEngine.Networking;

namespace BallisticEngine;

public sealed class InputComponent {
    readonly IInputSource source;

    readonly List<ActionSub> actions = new();
    readonly List<KeySub> keys = new();

    public InputComponent(IInputSource source = null) =>
        this.source = source ?? EngineInputSource.Instance;

    public void OnAction(InputAction action, Action callback) {
        if (Validate(action, InputValueType.Button, nameof(OnAction)))
            actions.Add(new ActionSub(action, ActionCallback.Button(callback)));
    }

    public void OnAction(InputAction action, Phase phase, Action callback) {
        if (Validate(action, InputValueType.Button, nameof(OnAction)))
            actions.Add(new ActionSub(action, ActionCallback.Button(callback), phase));
    }

    public void OnAxis2(InputAction action, Action<Vector2> callback) {
        if (Validate(action, InputValueType.Axis2D, nameof(OnAxis2)))
            actions.Add(new ActionSub(action, ActionCallback.Axis2(callback)));
    }

    public void OnAxis1(InputAction action, Action<float> callback) {
        if (Validate(action, InputValueType.Axis1D, nameof(OnAxis1)))
            actions.Add(new ActionSub(action, ActionCallback.Axis1(callback)));
    }

    public void OnKey(Key key, Phase phase, Action callback) =>
        keys.Add(new KeySub(key, phase, callback));

    public void Sample(in float delta) {
        bool enabled = source.Enabled;

        foreach (ActionSub sub in actions)
            sub.Evaluate(source, enabled, in delta);

        foreach (KeySub k in keys)
            k.Evaluate(source, enabled);
    }

    public NetworkInput Capture(uint seq) {
        bool enabled = source.Enabled;
        Vector2 move = Vector2.Zero;
        uint buttons = 0;
        int bit = 0;

        foreach (ActionSub sub in actions) {
            switch (sub.ValueType) {
                case InputValueType.Axis2D:
                    if (move == Vector2.Zero) move = enabled ? sub.SampleAxis2(source) : Vector2.Zero;
                    break;
                case InputValueType.Button:
                    if (bit < 32) {
                        if (enabled && sub.SampleButtonActive(source))
                            buttons |= 1u << bit;
                        bit++;
                    }
                    break;
            }
        }

        return new NetworkInput(seq, move, buttons);
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

    sealed class ActionSub {
        readonly InputAction action;
        readonly ActionCallback callback;
        readonly Phase? explicitPhase;

        bool wasActive;
        float heldFor;
        bool holdFired;
        bool lastEmittedNonZero;

        public ActionSub(InputAction action, ActionCallback callback, Phase? explicitPhase = null) {
            this.action = action;
            this.callback = callback;
            this.explicitPhase = explicitPhase;
        }

        public InputValueType ValueType => action.Value;

        public System.Numerics.Vector2 SampleAxis2(IInputSource source) => InputEval.Axis2(action, source);
        public bool SampleButtonActive(IInputSource source) => InputEval.ButtonActive(action, source);

        public void Evaluate(IInputSource source, bool enabled, in float delta) {
            switch (action.Value) {
                case InputValueType.Button: EvaluateButton(source, enabled, in delta); break;
                case InputValueType.Axis1D: EvaluateAxis1(source, enabled); break;
                case InputValueType.Axis2D: EvaluateAxis2(source, enabled); break;
            }
        }

        void EvaluateButton(IInputSource source, bool enabled, in float delta) {
            bool active = enabled && InputEval.ButtonActive(action, source);
            bool down = active && !wasActive;
            bool up = !active && wasActive;

            if (active) heldFor += delta; else heldFor = 0f;

            if (explicitPhase is { } phase) {
                if (phase == Phase.Started && down) callback.Invoke();
                else if (phase == Phase.Canceled && up) callback.Invoke();
                wasActive = active;
                return;
            }

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

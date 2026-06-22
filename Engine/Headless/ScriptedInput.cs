using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public sealed class ScriptedInput : IInputProvider {
    readonly record struct Span(int From, int To);

    readonly Dictionary<Keys, List<Span>> keys = new();
    readonly Dictionary<MouseButton, List<Span>> buttons = new();
    readonly List<(Span Span, Vector2 Delta)> mouseDeltas = new();
    readonly List<(Span Span, int Player, int Axis, float Value)> axes = new();
    readonly List<(Span Span, int Player, int Button)> gamepadButtons = new();

    public int CurrentStep { get; set; }

    public void AddKey(Keys key, int from, int to) => Add(keys, key, from, to);
    public void AddMouseButton(MouseButton button, int from, int to) => Add(buttons, button, from, to);
    public void AddMouseDelta(float dx, float dy, int from, int to) => mouseDeltas.Add((new Span(from, to), new Vector2(dx, dy)));
    public void AddAxis(int player, int axis, float value, int from, int to) => axes.Add((new Span(from, to), player, axis, value));
    public void AddGamepadButton(int player, int button, int from, int to) => gamepadButtons.Add((new Span(from, to), player, button));

    static void Add<T>(Dictionary<T, List<Span>> map, T key, int from, int to) where T : notnull {
        if (!map.TryGetValue(key, out List<Span> list))
            map[key] = list = new List<Span>();
        list.Add(new Span(from, to));
    }

    static bool Covers(List<Span> spans, int step) {
        foreach (Span s in spans)
            if (step >= s.From && step < s.To)
                return true;
        return false;
    }

    public bool IsKeyDown(Keys key) => keys.TryGetValue(key, out List<Span> s) && Covers(s, CurrentStep);
    public bool IsKeyPressed(Keys key) => IsKeyDown(key) &&
        !(keys.TryGetValue(key, out List<Span> s) && Covers(s, CurrentStep - 1));

    public bool IsMouseButtonDown(MouseButton button) => buttons.TryGetValue(button, out List<Span> s) && Covers(s, CurrentStep);
    public bool IsMouseButtonPressed(MouseButton button) => IsMouseButtonDown(button) &&
        !(buttons.TryGetValue(button, out List<Span> s) && Covers(s, CurrentStep - 1));

    public Vector2 ScrollDelta => Vector2.Zero;
    public Vector2 MousePosition => Vector2.Zero;

    public Vector2 MouseDelta {
        get {
            Vector2 total = Vector2.Zero;
            foreach ((Span span, Vector2 delta) in mouseDeltas)
                if (CurrentStep >= span.From && CurrentStep < span.To)
                    total += delta;
            return total;
        }
    }

    public bool IsGamepadConnected(int playerIndex) =>
        axes.Any(a => a.Player == playerIndex) || gamepadButtons.Any(b => b.Player == playerIndex);

    public bool IsGamepadButtonDown(int playerIndex, int button) {
        foreach ((Span span, int player, int b) in gamepadButtons)
            if (player == playerIndex && b == button && CurrentStep >= span.From && CurrentStep < span.To)
                return true;
        return false;
    }

    public bool IsGamepadButtonPressed(int playerIndex, int button) {
        if (!IsGamepadButtonDown(playerIndex, button))
            return false;
        foreach ((Span span, int player, int b) in gamepadButtons)
            if (player == playerIndex && b == button && CurrentStep - 1 >= span.From && CurrentStep - 1 < span.To)
                return false;
        return true;
    }

    public float GetGamepadAxis(int playerIndex, int axis) {
        foreach ((Span span, int player, int a, float value) in axes)
            if (player == playerIndex && a == axis && CurrentStep >= span.From && CurrentStep < span.To)
                return value;
        return 0f;
    }
}

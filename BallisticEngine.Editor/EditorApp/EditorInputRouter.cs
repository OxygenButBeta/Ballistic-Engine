using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace BallisticEngine.Editor;

internal sealed class EditorInputRouter {
    readonly InputActionChain<Keys> chain = new();
    readonly EditorInput input;

    public Func<string, bool> ActionEnabled;

    public EditorInputRouter(EditorInput input) =>
        this.input = input ?? throw new ArgumentNullException(nameof(input));

    public EditorInputRouter Bind(string id, KeyChord<Keys> chord, EditorInputContext context, Action invoke,
                                  int priority = 0) {
        chain.Add(id, chord, (int)context, priority, invoke);
        return this;
    }

    public void Build() => chain.Build();

    public bool Dispatch(EditorInputContext liveContexts) =>
        chain.Dispatch((int)liveContexts, IsChordActive, GateFor);

    bool GateFor(InputAction<Keys> a) => ActionEnabled is null || ActionEnabled(a.Id);

    bool IsChordActive(KeyChord<Keys> chord) =>
        input.KeyPressed(chord.Key) &&
        input.CtrlDown == chord.Ctrl &&
        input.ShiftDown == chord.Shift;

    public System.Collections.Generic.IReadOnlyList<InputConflict> Conflicts() => chain.CheckConflicts();
}

namespace BallisticEngine;

public sealed class InputAction<TKey> where TKey : struct {
    public InputAction(string id, KeyChord<TKey> chord, int context, int priority, Action invoke) {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Chord = chord;
        Context = context;
        Priority = priority;
        Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }

    public string Id { get; }
    public KeyChord<TKey> Chord { get; }
    public int Context { get; }
    public int Priority { get; }
    public Action Invoke { get; }
}

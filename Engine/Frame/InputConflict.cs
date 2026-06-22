namespace BallisticEngine;

public readonly struct InputConflict {
    public InputConflict(string idA, string idB, string chord, int context) {
        IdA = idA; IdB = idB; Chord = chord; Context = context;
    }
    public string IdA { get; }
    public string IdB { get; }
    public string Chord { get; }
    public int Context { get; }
    public override string ToString() => $"{Chord} in ctx {Context}: '{IdA}' vs '{IdB}'";
}

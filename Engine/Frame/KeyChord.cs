namespace BallisticEngine;

public readonly struct KeyChord<TKey> : IEquatable<KeyChord<TKey>> where TKey : struct {
    public KeyChord(TKey key, bool ctrl = false, bool shift = false, bool alt = false) {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public TKey Key { get; }
    public bool Ctrl { get; }
    public bool Shift { get; }
    public bool Alt { get; }

    public bool Equals(KeyChord<TKey> other) =>
        EqualityComparer<TKey>.Default.Equals(Key, other.Key) &&
        Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    public override bool Equals(object obj) => obj is KeyChord<TKey> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Key, Ctrl, Shift, Alt);

    public override string ToString() {
        string m = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
        return m + Key;
    }
}

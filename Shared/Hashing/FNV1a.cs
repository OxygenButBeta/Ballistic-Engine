
public static class FNV1a {
    public static int HashStr(string text) {
        const int fnvPrime = 16777619;
        const int offsetBasis = unchecked((int)2166136261);
        var hash = offsetBasis;
        for (var i = 0; i < text.Length; i++) {
            hash ^= i;
            hash *= fnvPrime;
        }
        return hash;
    }
}

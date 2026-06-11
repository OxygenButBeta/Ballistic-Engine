namespace BallisticEngine;

// A set of layers as a 32-bit mask (Unity's LayerMask). Bit i set = layer i is included. Used by
// physics queries (Raycast(..., LayerMask)) to restrict which layers a query can hit, and anywhere
// gameplay wants "is this entity's layer in my set". Implicitly converts to/from int so it drops
// into the same idioms as Unity.
//
// Build one from layer NAMES (LayerMask.GetMask("Enemy","Ground")) or from a raw value. The
// inspector renders an int-typed member named *Mask / *Layers as a multi-select dropdown.
public struct LayerMask {
    public int Value;

    public LayerMask(int value) => Value = value;

    public static implicit operator int(LayerMask mask) => mask.Value;
    public static implicit operator LayerMask(int value) => new(value);

    // A mask matching everything / nothing.
    public static LayerMask Everything => new(~0);
    public static LayerMask Nothing => new(0);

    // True if the given layer index is in this mask.
    public readonly bool Includes(int layer) => (uint)layer < 32 && (Value & (1 << layer)) != 0;

    // Builds a mask from one or more layer NAMES (Unity's LayerMask.GetMask). Unknown names are
    // skipped with a warning so a typo doesn't silently match nothing.
    public static LayerMask GetMask(params string[] layerNames) {
        int mask = 0;
        if (layerNames is null)
            return new LayerMask(mask);
        foreach (string name in layerNames) {
            int layer = LayerManager.NameToLayer(name);
            if (layer < 0) {
                Debugging.LogWarning($"LayerMask.GetMask: no layer named '{name}'.");
                continue;
            }
            mask |= 1 << layer;
        }
        return new LayerMask(mask);
    }

    // The single bit for a layer index.
    public static LayerMask FromLayer(int layer) =>
        new((uint)layer < 32 ? 1 << layer : 0);

    public static int NameToLayer(string name) => LayerManager.NameToLayer(name);
    public static string LayerToName(int layer) => LayerManager.NameOf(layer);

    public override readonly string ToString() => $"LayerMask(0x{Value:X8})";
}

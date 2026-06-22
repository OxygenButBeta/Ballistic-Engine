namespace BallisticEngine;

public struct LayerMask {
    public int Value;

    public LayerMask(int value) => Value = value;

    public static implicit operator int(LayerMask mask) => mask.Value;
    public static implicit operator LayerMask(int value) => new(value);

    public static LayerMask Everything => new(~0);
    public static LayerMask Nothing => new(0);

    public readonly bool Includes(int layer) => (uint)layer < 32 && (Value & (1 << layer)) != 0;

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

    public static LayerMask FromLayer(int layer) =>
        new((uint)layer < 32 ? 1 << layer : 0);

    public static int NameToLayer(string name) => LayerManager.NameToLayer(name);
    public static string LayerToName(int layer) => LayerManager.NameOf(layer);

    public override readonly string ToString() => $"LayerMask(0x{Value:X8})";
}

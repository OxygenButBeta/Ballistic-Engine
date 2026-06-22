namespace BallisticEngine;

public static class LayerManager {
    public const int LayerCount = 32;

    static readonly string[] names = new string[LayerCount];

    static readonly bool[,] collisionMatrix = new bool[LayerCount, LayerCount];

    static LayerManager() {
        ResetDefaults();
    }

    public static void ResetDefaults() {
        Array.Clear(names);
        names[0] = "Default";
        names[1] = "TransparentFX";
        names[2] = "Ignore Raycast";
        names[4] = "Water";
        names[5] = "UI";

        for (int a = 0; a < LayerCount; a++)
            for (int b = 0; b < LayerCount; b++)
                collisionMatrix[a, b] = true;
    }

    public static string NameOf(int layer) =>
        (uint)layer < LayerCount ? names[layer] ?? string.Empty : string.Empty;

    public static int NameToLayer(string name) {
        if (string.IsNullOrEmpty(name))
            return -1;
        for (int i = 0; i < LayerCount; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public static void SetName(int layer, string name) {
        if ((uint)layer < LayerCount)
            names[layer] = name ?? string.Empty;
    }

    public static void SetNames(IReadOnlyList<string> values) {
        for (int i = 0; i < LayerCount; i++)
            names[i] = values is not null && i < values.Count ? values[i] ?? string.Empty : string.Empty;
    }

    public static IEnumerable<(int Index, string Name)> DefinedLayers() {
        for (int i = 0; i < LayerCount; i++)
            if (!string.IsNullOrEmpty(names[i]))
                yield return (i, names[i]);
    }

    public static bool ShouldCollide(int layerA, int layerB) {
        if ((uint)layerA >= LayerCount || (uint)layerB >= LayerCount)
            return true;
        return collisionMatrix[layerA, layerB];
    }

    public static void SetCollision(int layerA, int layerB, bool collide) {
        if ((uint)layerA >= LayerCount || (uint)layerB >= LayerCount)
            return;
        collisionMatrix[layerA, layerB] = collide;
        collisionMatrix[layerB, layerA] = collide;
    }

    public static bool GetCollision(int layerA, int layerB) => ShouldCollide(layerA, layerB);

    public static List<bool> ExportMatrix() {
        var flat = new List<bool>(LayerCount * (LayerCount + 1) / 2);
        for (int a = 0; a < LayerCount; a++)
            for (int b = a; b < LayerCount; b++)
                flat.Add(collisionMatrix[a, b]);
        return flat;
    }

    public static void ImportMatrix(IReadOnlyList<bool> flat) {
        if (flat is null)
            return;
        int k = 0;
        for (int a = 0; a < LayerCount; a++)
            for (int b = a; b < LayerCount && k < flat.Count; b++, k++)
                SetCollision(a, b, flat[k]);
    }
}

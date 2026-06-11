namespace BallisticEngine;

// Project-wide layer table + collision matrix (Unity's Tags & Layers > Layers and the
// Physics > Layer Collision Matrix). Exactly 32 layers (a layer is an index 0..31), so a set of
// layers fits in a single int bitmask — that's what LayerMask is.
//
// The collision matrix is a symmetric 32x32 bool table: CollisionMatrix[a,b] == true means bodies
// on layer a and layer b generate contacts. The physics backend consults ShouldCollide(a,b) in its
// narrowphase AllowContactGeneration, and Raycasts honor a LayerMask. Defaults: layers 0..7 named
// like Unity's builtins, everything collides with everything (all-true matrix).
//
// Populated from the project's layer settings at bootstrap (LayerSettings). The names drive the
// editor dropdowns; the matrix drives physics. Engine layer — no GL/asset deps.
public static class LayerManager {
    public const int LayerCount = 32;

    static readonly string[] names = new string[LayerCount];

    // collisionMatrix[a,b]: do layers a and b collide. Kept symmetric by Set.
    static readonly bool[,] collisionMatrix = new bool[LayerCount, LayerCount];

    static LayerManager() {
        ResetDefaults();
    }

    // Unity's reserved builtin layer names at their fixed indices; the rest are blank (unused).
    public static void ResetDefaults() {
        Array.Clear(names);
        names[0] = "Default";
        names[1] = "TransparentFX";
        names[2] = "Ignore Raycast";
        names[4] = "Water";
        names[5] = "UI";

        for (int a = 0; a < LayerCount; a++)
            for (int b = 0; b < LayerCount; b++)
                collisionMatrix[a, b] = true; // everything collides by default
    }

    // ---- Names --------------------------------------------------------------

    public static string NameOf(int layer) =>
        (uint)layer < LayerCount ? names[layer] ?? string.Empty : string.Empty;

    // Index of a named layer, or -1 if no layer carries that name (Unity's LayerMask.NameToLayer).
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

    // Replaces all 32 names at once (settings load). Extra entries are ignored; missing ones blank.
    public static void SetNames(IReadOnlyList<string> values) {
        for (int i = 0; i < LayerCount; i++)
            names[i] = values is not null && i < values.Count ? values[i] ?? string.Empty : string.Empty;
    }

    // Every defined (non-empty) layer with its index — for editor dropdowns.
    public static IEnumerable<(int Index, string Name)> DefinedLayers() {
        for (int i = 0; i < LayerCount; i++)
            if (!string.IsNullOrEmpty(names[i]))
                yield return (i, names[i]);
    }

    // ---- Collision matrix ---------------------------------------------------

    // Do bodies on these two layers generate contacts? Consulted by the physics narrowphase.
    public static bool ShouldCollide(int layerA, int layerB) {
        if ((uint)layerA >= LayerCount || (uint)layerB >= LayerCount)
            return true;
        return collisionMatrix[layerA, layerB];
    }

    // Enable/disable collisions between two layers (kept symmetric). Unity's
    // Physics.IgnoreLayerCollision is the inverse of this.
    public static void SetCollision(int layerA, int layerB, bool collide) {
        if ((uint)layerA >= LayerCount || (uint)layerB >= LayerCount)
            return;
        collisionMatrix[layerA, layerB] = collide;
        collisionMatrix[layerB, layerA] = collide;
    }

    public static bool GetCollision(int layerA, int layerB) => ShouldCollide(layerA, layerB);

    // Serializes the upper triangle (incl. diagonal) of the matrix to a flat bool list for the
    // settings file, and back. Order is (0,0),(0,1)..(0,31),(1,1)..(1,31),...,(31,31).
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

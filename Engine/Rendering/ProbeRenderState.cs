namespace BallisticEngine;

// Editor-facing read-out + debug state for the (GL-era) auto-fit light-probe / reflection-probe GI.
//
// HISTORY: the diffuse light probes (IrradianceVolume) and local reflection probes (ReflectionVolume)
// were a baked-GI system of the OLD OpenGL renderer. The DX12 backend does GI a different way (IBL +
// screen-space / ray-traced one-bounce — see the unified GlobalIllumination volume), so it bakes NO
// probes and writes NONE of this state. When the GL renderer + its two baker classes were deleted, the
// editor still wanted ONE place to read "is a probe bake running / what's the grid / debug overlays" —
// so that surface moved here, defaulting to INERT (no bake, empty grid, overlays off). A future probe
// fallback (the no-RT path, GI plan P7) can repopulate these without the editor changing.
//
// Pure statics, BCL-only (lives in the engine so AssetDatabase + the editor + any renderer can touch it
// without a cross-assembly dependency on a renderer type).
public static class ProbeRenderState {
    // ---- Live bake progress (a baker writes these; the editor's BusyOverlay + Stats panel read them).
    // Inert on DX12 — IsBaking stays false, so the bake badge never shows. ----
    public static bool IsBaking { get; set; }
    public static float BakeProgress { get; set; }       // 0..1
    public static string BakeStatus { get; set; } = "Baking light probes";
    public static bool CancelRequested { get; set; }     // overlay Cancel → baker aborts next slice

    // ---- Cache directories (Library/ProbeVolumes, Library/ReflectionProbes), assigned by
    // AssetDatabase.Initialize so the engine needn't know the project layout. null = persistence off.
    // Kept so a future probe fallback has a home; unused while no baker runs. ----
    public static string ProbeCacheDirectory { get; set; }
    public static string ReflectionCacheDirectory { get; set; }

    // ---- GI summary the renderer publishes each frame (the editor Stats overlay's "Global Illumination"
    // section). Plain statics so the editor needn't be handed PostFX. Inert/default on DX12. ----
    public static bool ProbesEnabled = true, ReflectionsEnabled = true, LumenEnabled;
    public static float ProbeIntensity = 1f, ReflectionIntensity = 1f, LumenIntensity = 1f;
    public static int ProbeGridX, ProbeGridY, ProbeGridZ;
    public static int ProbeOccupiedCount, ProbeTotalCount;
    public static int ReflectionCapturedCount, ReflectionTotalCount;

    // ---- Debug overlays: draw the probe / reflection grids in the Scene view. Sources: an editor
    // toolbar toggle (*ShowAll) OR a volume override (*ShowFromVolume, set by the renderer from PostFX).
    // *ShowActive = either. DrawProbes/DrawReflections render the latest published Viz grid; no-op while
    // Viz is null (the DX12 case — nothing is baked to draw). ----
    public static bool ProbeShowAll =
        System.Environment.GetEnvironmentVariable("BALLISTIC_PROBE_DEBUG") == "1";
    public static bool ProbeShowFromVolume;
    public static bool ProbeShowActive => ProbeShowAll || ProbeShowFromVolume;

    public static bool ReflectionShowAll =
        System.Environment.GetEnvironmentVariable("BALLISTIC_REFLPROBE_DEBUG") == "1";
    public static bool ReflectionShowFromVolume;
    public static bool ReflectionShowActive => ReflectionShowAll || ReflectionShowFromVolume;

    public static bool AnyDebugActive => ProbeShowActive || ReflectionShowActive;

    // Published probe-grid visualizations (a baker fills these; the editor gizmo pass draws them).
    public sealed class ProbeVizData {
        public int Px, Py, Pz;
        public Vector3 Min, Size;
        public Vector3[] Colors;
        public bool[] Captured;
    }
    public sealed class ReflectionVizData {
        public int Px, Py, Pz;
        public Vector3 Min, Size;
        public bool[] Captured;
    }
    public static ProbeVizData ProbeViz { get; set; }
    public static ReflectionVizData ReflectionViz { get; set; }

    // Draw the light-probe grid (GREEN = occupied/near geometry, RED = empty air) from the latest Viz.
    // No-op while no Viz is published (DX12). Updates the occupancy counters for the Stats panel.
    public static void DrawProbes(IGizmos gizmos) {
        ProbeVizData viz = ProbeViz;
        if (!ProbeShowActive || viz is null)
            return;
        int px = viz.Px, py = viz.Py, pz = viz.Pz;
        if ((long)px * py * pz > 20000)   // safety: never flood the draw list
            return;
        Vector3 center = viz.Min + viz.Size * 0.5f;
        gizmos.Color = new Vector3(0.75f, 1f, 0.6f);
        gizmos.DrawWireCube(center, viz.Size, Quaternion.Identity);

        const float arm = 0.12f;
        int occupied = 0;
        for (var z = 0; z < pz; z++)
        for (var y = 0; y < py; y++)
        for (var x = 0; x < px; x++) {
            var i = (z * py + y) * px + x;
            var p = viz.Min + new Vector3(
                (x + 0.5f) / px * viz.Size.X, (y + 0.5f) / py * viz.Size.Y, (z + 0.5f) / pz * viz.Size.Z);
            float reach = arm;
            bool isOccupied = viz.Captured is null || i >= viz.Captured.Length || viz.Captured[i];
            if (isOccupied) { gizmos.Color = new Vector3(0.2f, 1f, 0.3f); occupied++; }
            else { gizmos.Color = new Vector3(0.85f, 0.15f, 0.12f); reach = arm * 0.5f; }
            gizmos.DrawLine(p - Vector3.UnitX * reach, p + Vector3.UnitX * reach);
            gizmos.DrawLine(p - Vector3.UnitY * reach, p + Vector3.UnitY * reach);
            gizmos.DrawLine(p - Vector3.UnitZ * reach, p + Vector3.UnitZ * reach);
        }
        ProbeOccupiedCount = occupied;
        ProbeTotalCount = px * py * pz;
    }

    // Draw the reflection-probe grid (CYAN = captured local cube, dim BLUE = empty cell → skybox).
    // No-op while no Viz is published (DX12). Updates the captured counters for the Stats panel.
    public static void DrawReflections(IGizmos gizmos) {
        ReflectionVizData viz = ReflectionViz;
        if (!ReflectionShowActive || viz is null)
            return;
        int px = viz.Px, py = viz.Py, pz = viz.Pz;
        if ((long)px * py * pz > 20000)
            return;
        Vector3 center = viz.Min + viz.Size * 0.5f;
        gizmos.Color = new Vector3(0.6f, 0.85f, 1f);
        gizmos.DrawWireCube(center, viz.Size, Quaternion.Identity);

        const float arm = 0.15f;
        int captured = 0;
        for (var z = 0; z < pz; z++)
        for (var y = 0; y < py; y++)
        for (var x = 0; x < px; x++) {
            var i = (z * py + y) * px + x;
            var p = viz.Min + new Vector3(
                (x + 0.5f) / px * viz.Size.X, (y + 0.5f) / py * viz.Size.Y, (z + 0.5f) / pz * viz.Size.Z);
            float reach = arm;
            bool isCaptured = viz.Captured is null || i >= viz.Captured.Length || viz.Captured[i];
            if (isCaptured) { gizmos.Color = new Vector3(0.15f, 0.85f, 1f); captured++; }
            else { gizmos.Color = new Vector3(0.2f, 0.25f, 0.5f); reach = arm * 0.5f; }
            gizmos.DrawLine(p - Vector3.UnitX * reach, p + Vector3.UnitX * reach);
            gizmos.DrawLine(p - Vector3.UnitY * reach, p + Vector3.UnitY * reach);
            gizmos.DrawLine(p - Vector3.UnitZ * reach, p + Vector3.UnitZ * reach);
        }
        ReflectionCapturedCount = captured;
        ReflectionTotalCount = px * py * pz;
    }
}

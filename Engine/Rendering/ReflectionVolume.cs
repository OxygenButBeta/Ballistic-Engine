using OpenTK.Mathematics;

namespace BallisticEngine;

// Baked reflection probe volume: a 3D grid of LOCAL specular cubemaps spanning the scene. Each
// occupied grid cell captures the surrounding geometry into a cubemap, GGX-prefilters it (roughness
// per mip), and stores it in a GPU cube-map array the PBR shader samples for glossy reflections.
// This is the specular sibling of IrradianceVolume (which does the same for diffuse SH) - it gives
// a metal object in an interior the reflection of the ROOM it's in, not just the global sky.
//
// Like IrradianceVolume: drop ONE component, Fit To Scene, Bake, and forget it. The grid is SPARSE
// (only cells touching geometry get a real cubemap; empty-air cells fall back to the global skybox
// IBL), bake-on-load, and cached to Library/ReflectionProbes so later loads skip the bake.
//
// The bake renders the scene 6x per occupied cell, so it's an offline-style operation: tick `Bake`
// (or save it ticked - it then re-bakes on scene load) and watch the console for [ReflectionVolume]
// progress. Reflections drive the SAME busy-overlay channel as IrradianceVolume (IsBaking/etc.).
public class ReflectionVolume : SceneBehaviour {
    public static ReflectionVolume Active { get; private set; }

    [Header("Bounds")]
    [Tooltip("World-space centre of the probe grid.")]
    public Vector3 Center { get; set; } = new(0f, 10f, 0f);

    [Tooltip("World-space size the grid spans. Probes sit at cell centres inside this box.")]
    public Vector3 Size { get; set; } = new(70f, 30f, 70f);

    [Header("Resolution")]
    // Reflection cells are MUCH heavier than diffuse SH probes (a full 6-face scene render + a
    // ~1 MB prefiltered cubemap each), so the defaults are far coarser than IrradianceVolume's.
    [Range(2, 32)]
    [Tooltip("Probe count along X. More = finer local reflections, longer bake, more VRAM.")]
    public int ProbesX { get; set; } = 6;
    [Range(2, 16)]
    public int ProbesY { get; set; } = 3;
    [Range(2, 32)]
    public int ProbesZ { get; set; } = 6;

    [Header("Quality")]
    // Master switch is the component's IsEnabled toggle (the checkbox at the top of the inspector,
    // shared by every SceneBehaviour): off = the renderer ignores this volume entirely and glossy
    // surfaces fall back to the global skybox reflection - no re-bake needed. These dials below
    // tune the LIVE look and need no re-bake either; only Bounds/Resolution changes do.

    [Range(0f, 2f)]
    [Tooltip("Strength of the local reflections. 1 = physically matched to the sky reflection it " +
             "replaces; lower fades toward the global skybox; >1 over-drives them.")]
    public float Intensity { get; set; } = 1f;

    [Tooltip("Blend local reflections OVER the global skybox by Intensity instead of hard-replacing " +
             "it. Softens the seam at cell edges and where a probe didn't capture much.")]
    public bool BlendWithSky { get; set; } = true;

    [Header("Debug")]
    [Tooltip("Draw a marker at every grid cell when this volume is selected: bright = a captured " +
             "local probe, faint gray = empty-air cell that falls back to the skybox.")]
    public bool ShowProbes { get; set; } = true;

    // The pending-bake flag stays serialized (saved true = the volume re-bakes on scene load)
    // but is hidden from the inspector - the [Button] below is the user-facing trigger.
    [HideInInspector]
    public bool Bake { get; set; } = true;

    // LEGACY (unused): the cache key is now DERIVED from scene + grid settings, so nothing
    // has to be saved into the scene for baked data to survive a reload. Kept so scenes
    // serialized while this existed still deserialize. Mirrors IrradianceVolume.
    [HideInInspector]
    public string CacheId { get; set; } = "";

    // One-shot guard: the renderer auto-restores the cache (or auto-bakes on a miss) the first
    // time it sees this component instance. Internal = never serialized.
    internal bool CacheChecked;

    // Set by the button (not load): bypasses the cache and re-captures the reflections.
    internal bool ForceRebake;

    // Set by the Clear button; the renderer drops the GPU cube array, the viz, and the cache file.
    internal bool ClearRequested;

    // Deterministic cache key: scene name + grid layout, hashed with FNV-1a (System.HashCode is
    // randomized per process and would orphan the cache every run). Reopening the scene derives
    // the same key and finds the same file - no save required. Mirrors IrradianceVolume.
    public string DeriveCacheKey(string sceneName) {
        var hash = 2166136261u;
        void Mix(int v) {
            unchecked {
                for (var i = 0; i < 4; i++) {
                    hash ^= (byte)(v >> (i * 8));
                    hash *= 16777619u;
                }
            }
        }
        void MixF(float v) => Mix(BitConverter.SingleToInt32Bits(v));

        MixF(Center.X); MixF(Center.Y); MixF(Center.Z);
        MixF(Size.X); MixF(Size.Y); MixF(Size.Z);
        Mix(ProbesX); Mix(ProbesY); Mix(ProbesZ);

        var safe = string.Concat((sceneName ?? "scene").Where(char.IsLetterOrDigit));
        if (safe.Length == 0)
            safe = "scene";
        return $"{safe}_{hash:x8}";
    }

    [Button("Bake Reflections")]
    public void BakeNow() {
        Bake = true;
        ForceRebake = true;
    }

    [Button("Clear Baked Data")]
    public void ClearBakedData() {
        ClearRequested = true;
    }

    public static void DeleteCache(string id) {
        var path = CachePath(id);
        try {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Reflection cache '{path}' failed to delete: {exception.Message}");
        }
    }

    // Wraps the volume around everything currently renderable (union of world AABBs from the CPU
    // mesh bounds) with a little headroom, so the grid never wastes probes on empty sky far above
    // the scene or misses geometry at the edges. Identical in purpose to IrradianceVolume.FitToScene.
    [Button("Fit To Scene")]
    public void FitToScene() {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var any = false;
        foreach (IStaticMeshRenderer renderer in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (!renderer.IsRenderable || !renderer.IsActive)
                continue;
            renderer.SharedMesh.GetLocalBounds(out Vector3 lMin, out Vector3 lMax);
            Matrix4 world = renderer.Transform.WorldMatrix;
            for (var c = 0; c < 8; c++) {
                var corner = new Vector3(
                    (c & 1) == 0 ? lMin.X : lMax.X,
                    (c & 2) == 0 ? lMin.Y : lMax.Y,
                    (c & 4) == 0 ? lMin.Z : lMax.Z);
                Vector3 w = (new Vector4(corner, 1f) * world).Xyz;
                lo = Vector3.ComponentMin(lo, w);
                hi = Vector3.ComponentMax(hi, w);
            }
            any = true;
        }

        if (!any)
            return;
        const float padding = 2f;
        Center = (lo + hi) * 0.5f;
        Size = Vector3.ComponentMax(hi - lo + new Vector3(padding * 2f), Vector3.One * 2f);
    }

    // ---- Live bake progress: reflections SHARE IrradianceVolume's overlay channel (IsBaking/
    // BakeProgress/BakeStatus/CancelRequested) so the editor's BusyOverlay needs zero new wiring
    // and the two bakes can't fight over a visible overlay. See plan Part 8. ----

    // Baked-cell visualization for the selected-gizmo view: which cells actually captured geometry
    // (got a real cube) vs were skipped as empty air and fall back to the skybox.
    public sealed class ReflectionVizData {
        public int Px, Py, Pz;
        public Vector3 Min, Size;
        public bool[] Captured;
    }

    public static ReflectionVizData Viz { get; set; }

    // Library/ReflectionProbes, assigned by AssetDatabase.Initialize (the engine layer doesn't
    // know the project layout). null = persistence disabled.
    public static string CacheDirectory { get; set; }

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    // Selected-only on purpose: a scene-spanning box drawn permanently is just noise.
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.6f, 0.85f, 1f);
        gizmos.DrawWireCube(Center, Size, Quaternion.Identity);

        if (!ShowProbes)
            return;

        // Cell markers (cell centres), capped so a dense grid can't flood the draw list.
        var px = Math.Clamp(ProbesX, 2, 64);
        var py = Math.Clamp(ProbesY, 2, 64);
        var pz = Math.Clamp(ProbesZ, 2, 64);
        if (px * py * pz > 4096)
            return;

        Vector3 size = Vector3.ComponentMax(Size, Vector3.One * 0.5f);
        Vector3 min = Center - size * 0.5f;

        // Baked data available for THIS grid: captured cells draw bright, skipped empty-air cells
        // draw as faint gray dots (those fall back to the global skybox reflection).
        ReflectionVizData viz = Viz;
        var vizMatches = viz is not null && viz.Px == px && viz.Py == py && viz.Pz == pz &&
                         (viz.Min - min).LengthSquared < 1e-3f;

        const float arm = 0.12f;
        for (var z = 0; z < pz; z++)
        for (var y = 0; y < py; y++)
        for (var x = 0; x < px; x++) {
            var p = min + new Vector3(
                (x + 0.5f) / px * size.X, (y + 0.5f) / py * size.Y, (z + 0.5f) / pz * size.Z);

            var reach = arm;
            if (vizMatches) {
                var i = (z * py + y) * px + x;
                if (!viz.Captured[i]) {
                    gizmos.Color = new Vector3(0.3f, 0.3f, 0.32f); // skipped: empty air -> skybox
                    reach = arm * 0.4f;
                }
                else {
                    gizmos.Color = new Vector3(0.6f, 0.85f, 1f); // captured local cubemap
                }
            }
            else {
                gizmos.Color = new Vector3(0.45f, 0.7f, 1f); // not baked yet
            }

            gizmos.DrawLine(p - Vector3.UnitX * reach, p + Vector3.UnitX * reach);
            gizmos.DrawLine(p - Vector3.UnitY * reach, p + Vector3.UnitY * reach);
            gizmos.DrawLine(p - Vector3.UnitZ * reach, p + Vector3.UnitZ * reach);
        }
    }

    // ---- Baked-data persistence (Library/ReflectionProbes/<CacheId>.brp) ----
    // Layout: magic 'BRP1' | i32 px,py,pz | center xyz | size xyz | i32 faceRes | i32 mipCount |
    // i32 occupiedCount | i32[px*py*pz] cellToLayer (-1 = empty/sky cell) | per occupied layer:
    // 6 faces x (sum over mips of mipSize^2 * 4) RGBA16F floats, in (layer, face, mip) order.
    // Grid/bounds/faceRes/mipCount mismatch rejects the file (stale cache).

    // "BRP2": bumped from BRP1 when the cell->layer map gained the nearest-occupied fill + the
    // camera-independent cap. Old BRP1 caches hold the unfilled, camera-dependent map (the blocky
    // local<->sky cliff), so they must be rejected and re-baked rather than loaded.
    const uint CacheMagic = 0x32505242; // "BRP2"

    static string CachePath(string id) =>
        CacheDirectory is null || string.IsNullOrEmpty(id) ? null : Path.Combine(CacheDirectory, id + ".brp");

    // Floats per cube layer = 6 faces * sum over mips of (mipSize^2 * 4 channels).
    public static int FloatsPerLayer(int faceRes, int mipCount) {
        var perFace = 0;
        for (var mip = 0; mip < mipCount; mip++) {
            var s = Math.Max(1, faceRes >> mip);
            perFace += s * s * 4;
        }
        return perFace * 6;
    }

    // cubeTexels[layer] holds one occupied cell's full mip chain (FloatsPerLayer floats).
    public static bool TryLoadCache(string id, int px, int py, int pz, Vector3 center, Vector3 size,
        int faceRes, int mipCount, out int[] cellToLayer, out float[][] cubeTexels) {
        cellToLayer = null;
        cubeTexels = null;
        var path = CachePath(id);
        try {
            if (path is null || !File.Exists(path))
                return false;

            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt32() != CacheMagic)
                return false;
            if (reader.ReadInt32() != px || reader.ReadInt32() != py || reader.ReadInt32() != pz)
                return false;
            var cachedCenter = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var cachedSize = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if ((cachedCenter - center).LengthSquared > 1e-4f || (cachedSize - size).LengthSquared > 1e-4f)
                return false;
            if (reader.ReadInt32() != faceRes || reader.ReadInt32() != mipCount)
                return false;

            var occupiedCount = reader.ReadInt32();
            var cellCount = px * py * pz;
            var map = new int[cellCount];
            var mapBytes = reader.ReadBytes(cellCount * sizeof(int));
            if (mapBytes.Length != cellCount * sizeof(int))
                return false;
            Buffer.BlockCopy(mapBytes, 0, map, 0, mapBytes.Length);

            var floatsPerLayer = FloatsPerLayer(faceRes, mipCount);
            var layers = new float[occupiedCount][];
            for (var l = 0; l < occupiedCount; l++) {
                var bytes = reader.ReadBytes(floatsPerLayer * sizeof(float));
                if (bytes.Length != floatsPerLayer * sizeof(float))
                    return false;
                layers[l] = new float[floatsPerLayer];
                Buffer.BlockCopy(bytes, 0, layers[l], 0, bytes.Length);
            }

            cellToLayer = map;
            cubeTexels = layers;
            return true;
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Reflection cache '{path}' failed to load: {exception.Message}");
            return false;
        }
    }

    public static void SaveCache(string id, int px, int py, int pz, Vector3 center, Vector3 size,
        int faceRes, int mipCount, int[] cellToLayer, float[][] cubeTexels) {
        var path = CachePath(id);
        if (path is null)
            return;
        try {
            Directory.CreateDirectory(CacheDirectory);
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(CacheMagic);
            writer.Write(px);
            writer.Write(py);
            writer.Write(pz);
            writer.Write(center.X); writer.Write(center.Y); writer.Write(center.Z);
            writer.Write(size.X); writer.Write(size.Y); writer.Write(size.Z);
            writer.Write(faceRes);
            writer.Write(mipCount);
            writer.Write(cubeTexels.Length);

            var cellCount = px * py * pz;
            var mapBytes = new byte[cellCount * sizeof(int)];
            Buffer.BlockCopy(cellToLayer, 0, mapBytes, 0, mapBytes.Length);
            writer.Write(mapBytes);

            var floatsPerLayer = FloatsPerLayer(faceRes, mipCount);
            var layerBytes = new byte[floatsPerLayer * sizeof(float)];
            foreach (float[] layer in cubeTexels) {
                Buffer.BlockCopy(layer, 0, layerBytes, 0, layerBytes.Length);
                writer.Write(layerBytes);
            }
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Reflection cache '{path}' failed to save: {exception.Message}");
        }
    }
}

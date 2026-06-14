using OpenTK.Mathematics;

namespace BallisticEngine;

// Baked irradiance probe volume: a 3D grid of light probes spanning the scene. Each probe
// captures the full lighting (sun + shadows + sky + bounce surfaces) at its position as an
// L1 spherical-harmonic irradiance, stored in 3D textures the PBR shader samples with
// hardware trilinear filtering (= free probe interpolation). This replaces the single global
// sky irradiance with POSITION-AWARE ambient: a corridor knows it's lit by dim corridor
// light, the sunlit rotunda knows it's full of warm bounce - indirect light that screen-space
// GI can't provide because its sources are off-screen.
//
// The bake renders the scene 6x per probe, so it's an offline-style operation: tick `Bake`
// (or save it ticked - it then re-bakes on scene load) and watch the console for progress.
public class IrradianceVolume : SceneBehaviour {
    public static IrradianceVolume Active { get; private set; }

    [Header("Bounds")]
    [Tooltip("World-space centre of the probe grid.")]
    public Vector3 Center { get; set; } = new(0f, 10f, 0f);

    [Tooltip("World-space size the grid spans. Probes sit at cell centres inside this box.")]
    public Vector3 Size { get; set; } = new(70f, 30f, 70f);

    [Header("Resolution")]
    [Range(2, 32)]
    [Tooltip("Probe count along X. More = finer indirect variation, longer bake.")]
    public int ProbesX { get; set; } = 12;
    [Range(2, 16)]
    public int ProbesY { get; set; } = 5;
    [Range(2, 32)]
    public int ProbesZ { get; set; } = 12;

    [Header("Debug")]
    [Tooltip("Render a small lit sphere at every baked probe, shaded by that probe's stored " +
             "SH - the exact data the PBR shader samples. The fastest way to see where light " +
             "leaks, which probes captured bounce, and which were skipped as sky.")]
    public bool ShowProbes { get; set; }

    // The pending-bake flag stays serialized (saved true = the volume re-bakes on scene load)
    // but is hidden from the inspector - the [Button] below is the user-facing trigger.
    [HideInInspector]
    public bool Bake { get; set; } = true;

    // LEGACY (unused): the cache key is now DERIVED from scene + grid settings, so nothing
    // has to be saved into the scene for baked data to survive a reload. Kept so scenes
    // serialized while this existed still deserialize.
    [HideInInspector]
    public string CacheId { get; set; } = "";

    // One-shot guard: the renderer auto-restores the cache (or auto-bakes on a miss) the
    // first time it sees this component instance. Internal = never serialized.
    internal bool CacheChecked;

    // Deterministic cache key: scene name + grid layout, hashed with FNV-1a (System.HashCode
    // is randomized per process and would orphan the cache every run). Reopening the scene
    // derives the same key and finds the same file - no save required.
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

    // Set by the button (not load): bypasses the cache and re-captures lighting.
    internal bool ForceRebake;

    // Set by the Clear button; the renderer drops the GPU textures, the viz, and the cache file.
    internal bool ClearRequested;

    [Button("Bake Probes")]
    public void BakeNow() {
        Bake = true;
        ForceRebake = true;
    }

    [Button("Clear Baked Data")]
    public void ClearBakedData() {
        Bake = false;
        ForceRebake = false;
        ClearRequested = true;
    }

    public static void DeleteCache(string id) {
        var path = CachePath(id);
        try {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Probe cache '{path}' failed to delete: {exception.Message}");
        }
    }

    // Wraps the volume around everything currently renderable (union of world AABBs from the
    // CPU mesh bounds) with a little headroom, so the grid never wastes probes on empty sky
    // far above the scene or misses geometry at the edges.
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

    // ---- Live bake progress (written by the renderer's time-sliced bake; read by the
    // editor's busy overlay) ----
    public static bool IsBaking { get; set; }
    public static float BakeProgress { get; set; }       // 0..1
    public static string BakeStatus { get; set; } = "Baking light probes";

    // Set by the overlay's Cancel button; the renderer aborts the job on its next slice.
    public static bool CancelRequested { get; set; }

    // Baked-probe visualization for the selected-gizmo view: PRE-EXPOSED average irradiance
    // per probe (display-ready after a simple tonemap) + whether the probe actually captured
    // geometry or was skipped as empty air / written from cache.
    public sealed class ProbeVizData {
        public int Px, Py, Pz;
        public Vector3 Min, Size;
        public Vector3[] Colors;
        public bool[] Captured;
    }

    public static ProbeVizData Viz { get; set; }

    // Library/ProbeVolumes, assigned by AssetDatabase.Initialize (the engine layer doesn't
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
        gizmos.Color = new Vector3(0.75f, 1f, 0.6f);
        gizmos.DrawWireCube(Center, Size, Quaternion.Identity);

        // Probe markers (cell centres), capped so a dense grid can't flood the draw list.
        var px = Math.Clamp(ProbesX, 2, 64);
        var py = Math.Clamp(ProbesY, 2, 64);
        var pz = Math.Clamp(ProbesZ, 2, 64);
        // 8192 cap (was 4096): the SunTemple auto-fit grid is 24x12x24 = 6912, which the old cap hid
        // entirely — so the probe markers never drew and you couldn't SEE where the probes are or how
        // many fall in empty air. This is the debug view for the "6k probes, most in empty space" work.
        if (px * py * pz > 8192)
            return;

        Vector3 size = Vector3.ComponentMax(Size, Vector3.One * 0.5f);
        Vector3 min = Center - size * 0.5f;

        // Baked data available for THIS grid: paint each probe with its captured irradiance
        // (simple display tonemap); skipped empty-air probes draw as faint gray dots.
        ProbeVizData viz = Viz;
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
                    // EMPTY-AIR probe (no geometry near it): drawn dim RED so the wasted cells are
                    // obvious at a glance — this is the "most of the 6k is empty space" we're hunting.
                    gizmos.Color = new Vector3(0.8f, 0.15f, 0.12f);
                    reach = arm * 0.5f;
                }
                else {
                    // OCCUPIED probe (near geometry, worth capturing): bright GREEN so you can see
                    // exactly where the useful probes sit vs the empty-air red ones.
                    gizmos.Color = new Vector3(0.2f, 1f, 0.3f);
                }
            }
            else {
                gizmos.Color = new Vector3(0.45f, 0.9f, 1f); // not baked yet
            }

            gizmos.DrawLine(p - Vector3.UnitX * reach, p + Vector3.UnitX * reach);
            gizmos.DrawLine(p - Vector3.UnitY * reach, p + Vector3.UnitY * reach);
            gizmos.DrawLine(p - Vector3.UnitZ * reach, p + Vector3.UnitZ * reach);
        }
    }

    // DEBUG: draw the latest published probe grid (Viz) WITHOUT needing the volume selected — works
    // for the IMPLICIT DEFAULT volume too (which is never in the hierarchy, so can't be selected).
    // Toggled by the editor "Show Probes" debug switch. Empty-air probes draw dim RED, occupied bright
    // GREEN, so the "most of the grid is empty space" is obvious. This is the visual the probe-density
    // rework is built on: SEE where the points are before changing how they're placed.
    public static bool DebugShowAll =
        System.Environment.GetEnvironmentVariable("BALLISTIC_PROBE_DEBUG") == "1";
    public static void DebugDrawProbes(IGizmos gizmos) {
        ProbeVizData viz = Viz;
        if (!DebugShowAll || viz is null)
            return;
        int px = viz.Px, py = viz.Py, pz = viz.Pz;
        if ((long)px * py * pz > 20000) // safety: don't flood the draw list past a sane cap
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
        DebugOccupiedCount = occupied;
        DebugTotalCount = px * py * pz;
    }
    public static int DebugOccupiedCount, DebugTotalCount;

    // ---- Baked-data persistence (Library/ProbeVolumes/<CacheId>.bpv) ----
    // Layout: magic 'BPV1' | i32 px,py,pz | center xyz | size xyz | 4 SH channels of
    // (px*py*pz*4) floats. Grid/bounds mismatches reject the file (stale cache).

    const uint CacheMagic = 0x31565042; // "BPV1"

    static string CachePath(string id) =>
        CacheDirectory is null || string.IsNullOrEmpty(id) ? null : Path.Combine(CacheDirectory, id + ".bpv");

    public static bool TryLoadCache(string id, int px, int py, int pz, Vector3 center, Vector3 size,
        float[][] sh) {
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

            var floats = px * py * pz * 4;
            for (var t = 0; t < 4; t++) {
                var bytes = reader.ReadBytes(floats * sizeof(float));
                if (bytes.Length != floats * sizeof(float))
                    return false;
                Buffer.BlockCopy(bytes, 0, sh[t], 0, bytes.Length);
            }
            return true;
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Probe cache '{path}' failed to load: {exception.Message}");
            return false;
        }
    }

    public static void SaveCache(string id, int px, int py, int pz, Vector3 center, Vector3 size,
        float[][] sh) {
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
            var floats = px * py * pz * 4;
            var bytes = new byte[floats * sizeof(float)];
            for (var t = 0; t < 4; t++) {
                Buffer.BlockCopy(sh[t], 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
            }
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Probe cache '{path}' failed to save: {exception.Message}");
        }
    }
}

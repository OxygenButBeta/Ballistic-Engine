
namespace BallisticEngine;

// An entity component that contributes a VolumeProfile to the blended post-process stack
// (Unity's Volume). Global volumes apply everywhere; local volumes apply inside an oriented
// box around the entity (BoxSize scaled by the transform), fading in over BlendDistance
// metres outside it. Higher Priority wins where volumes overlap; Weight scales the whole
// contribution. The renderer evaluates the result through VolumeManager every frame.
[Component("Volume")]
public class Volume : Behaviour {
    [Tooltip("Global volumes affect rendering everywhere; local volumes only within their box.")]
    public bool IsGlobal { get; set; } = true;

    [Tooltip("Volumes with higher priority override lower ones where they overlap.")]
    public float Priority { get; set; }

    [Range(0f, 1f)]
    [Tooltip("Master strength of this volume's contribution (1 = full effect).")]
    public float Weight { get; set; } = 1f;

    [Tooltip("Local volumes only: metres outside the box over which the volume fades in.")]
    public float BlendDistance { get; set; }

    [Tooltip("Local volumes only: size of the volume's box, scaled by the transform.")]
    public Vector3 BoxSize { get; set; } = new(10f, 10f, 10f);

    [Tooltip("The shared .volume asset holding this volume's overrides.")]
    public VolumeProfile Profile { get; set; }

    // ================= GI DEBUG (editor gizmos) =================
    // Tick these to visualise the GI system in the Scene view. They only do anything when this volume's
    // profile carries a GlobalIllumination override (the source of the GI dials), and [ShowIf("HasGi")]
    // hides the whole group on a volume with no GI override. NOT [NotSerialized]: that attribute drops a
    // member from BOTH save AND the inspector (ComponentReflection.InspectorMembers derives from the
    // serializable set), so the toggles would never appear — they are plain serialised members that
    // default off (writing false/12 to YAML is harmless; they're switches, not runtime-only state). The
    // gizmos re-derive the camera-centered DDGI grid on the CPU (GiDebugGrid) — placement-only; real probe
    // irradiance colour is a deferred GPU-readback stage.

    [FoldoutGroup("GI Debug", defaultOpen: false)]
    [Tooltip("Draw the cascaded GI volume bound — the finite ~30m world-radiance grid box + its clipmap " +
             "fade band. Outside it, GI falls to IBL/sky (intentional). Camera-centered, so it follows the view.")]
    [ShowIf("HasGi")]
    public bool ShowCascadeBounds { get; set; }

    [FoldoutGroup("GI Debug", defaultOpen: false)]
    [Tooltip("Draw the DDGI world-probe grid (off-screen far-field cache). Frustum-culled + distance-limited " +
             "so 2048 probes don't flood the view — only probes near the camera/in front are marked.")]
    [ShowIf("HasGi")]
    public bool ShowProbeGrid { get; set; }

    [FoldoutGroup("GI Debug", defaultOpen: false)]
    [Tooltip("Draw each visible DDGI probe as a small wire sphere (instead of a cross marker). Heavier — " +
             "kept tightly distance-limited. Sphere colour is a placeholder until the GPU irradiance " +
             "readback lands (stage 2); for now it shows probe placement only.")]
    [ShowIf("ShowProbeGrid")]
    public bool ShowProbeSpheres { get; set; }

    [FoldoutGroup("GI Debug", defaultOpen: false)]
    [Range(2f, 30f)]
    [Tooltip("How far from the camera (metres) to draw probe markers. Keeps the probe-grid gizmo cheap and " +
             "readable — distant probes are culled.")]
    [ShowIf("ShowProbeGrid")]
    public float ProbeDrawDistance { get; set; } = 12f;

    [FoldoutGroup("GI Debug", defaultOpen: false)]
    [Tooltip("Draw sample reflection rays off this volume's origin — green where the surface would take a " +
             "SHARP re-shade-at-hit reflection (roughness below the split threshold), amber where it falls " +
             "to the blurry cache. Visualises the RT-reflection roughness split.")]
    [ShowIf("HasGi")]
    public bool ShowReflectionRays { get; set; }

    // True when the bound profile carries a GI override — drives the [ShowIf] on every debug toggle so the
    // GI Debug group is empty/hidden on volumes that don't touch GI. (Reflection accessor; editor-only path.)
    public bool HasGi => Profile is not null && Profile.Has(typeof(GlobalIllumination));

    // The reflection roughness-split threshold (mirror of DxrReflections.hlsl: sharp RT below ~0.45, fading
    // to the cache/diffuse by MAX_ROUGHNESS 0.6). Used only to colour the reflection-ray debug gizmo.
    const float ReflectionSharpRoughness = 0.45f;

    // OnAttach/OnDetach (not OnEnabled) so volumes work in the editor outside play mode.
    protected internal override void OnAttach() => VolumeManager.Register(this);

    protected internal override void OnDetach() => VolumeManager.Unregister(this);

    // 1 inside the box (or always, when global), 0 beyond BlendDistance outside it.
    internal float ComputeInterpFactor(Vector3 cameraPosition) {
        if (IsGlobal)
            return 1f;

        Matrix4 world = transform.WorldMatrix;
        Vector3 scale = world.ExtractScale();
        var half = new Vector3(
            MathF.Abs(BoxSize.X * scale.X),
            MathF.Abs(BoxSize.Y * scale.Y),
            MathF.Abs(BoxSize.Z * scale.Z)) * 0.5f;

        Vector3 local = Vector3.Transform(
            cameraPosition - world.ExtractTranslation(),
            Quaternion.Inverse(transform.WorldRotation));
        Vector3 outside = Vector3.Max(
            new Vector3(MathF.Abs(local.X), MathF.Abs(local.Y), MathF.Abs(local.Z)) - half,
            Vector3.Zero);

        float distance = outside.Length();
        if (distance <= 0f)
            return 1f;

        return BlendDistance > 0f ? Math.Clamp(1f - distance / BlendDistance, 0f, 1f) : 0f;
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        if (IsGlobal)
            return;

        Vector3 center = transform.WorldPosition;
        Vector3 scale = transform.WorldMatrix.ExtractScale();
        Quaternion rotation = transform.WorldRotation;
        var size = new Vector3(BoxSize.X * scale.X, BoxSize.Y * scale.Y, BoxSize.Z * scale.Z);

        gizmos.Color = new Vector3(0.4f, 0.9f, 0.5f);
        gizmos.DrawWireCube(center, size, rotation);

        if (BlendDistance > 0f) {
            gizmos.Color = new Vector3(0.4f, 0.9f, 0.5f) * 0.5f;
            gizmos.DrawWireCube(center, size + new Vector3(BlendDistance * 2f), rotation);
        }
    }

    // GI Debug gizmos — drawn whenever their toggle is on (not gated on selection), so you can tick a box
    // and watch the GI structure live. All no-ops unless the toggle is set, so a volume with the group
    // collapsed costs nothing. The grid is re-derived camera-centered on the CPU (GiDebugGrid), matching
    // the DX12 DDGI placement.
    public override void OnDrawGizmos(IGizmos gizmos) {
        if (!HasGi)
            return;

        if (ShowCascadeBounds || ShowProbeGrid)
            DrawDdgiGrid(gizmos);

        if (ShowReflectionRays)
            DrawReflectionRays(gizmos);
    }

    void DrawDdgiGrid(IGizmos gizmos) {
        Vector3 spacing = GiDebugGrid.DefaultSpacing;
        Vector3 origin = GiDebugGrid.Snap(gizmos.CameraPosition, spacing);
        Vector3 covered = GiDebugGrid.CoveredSize(spacing);
        Vector3 center = origin + covered * 0.5f;

        if (ShowCascadeBounds) {
            // Finite cascaded GI volume bound (cyan) + a faint clipmap fade band just inside the edge.
            gizmos.Color = new Vector3(0.3f, 0.8f, 1f);
            gizmos.DrawWireCube(center, covered, Quaternion.Identity);
            gizmos.Color = new Vector3(0.3f, 0.8f, 1f) * 0.4f;
            gizmos.DrawWireCube(center, covered * 0.85f, Quaternion.Identity);
        }

        if (!ShowProbeGrid)
            return;

        // Distance-limited + per-axis ranged so we never iterate all 2048 probes / flood the view with
        // 245k line calls: only probes within ProbeDrawDistance of the camera are marked. We clamp the
        // per-axis probe index range to the cells around the camera before the triple loop.
        Vector3 cam = gizmos.CameraPosition;
        float d = ProbeDrawDistance;
        float d2 = d * d;

        int x0 = ProbeRangeMin(cam.X, origin.X, spacing.X, d, GiDebugGrid.ProbesX);
        int x1 = ProbeRangeMax(cam.X, origin.X, spacing.X, d, GiDebugGrid.ProbesX);
        int y0 = ProbeRangeMin(cam.Y, origin.Y, spacing.Y, d, GiDebugGrid.ProbesY);
        int y1 = ProbeRangeMax(cam.Y, origin.Y, spacing.Y, d, GiDebugGrid.ProbesY);
        int z0 = ProbeRangeMin(cam.Z, origin.Z, spacing.Z, d, GiDebugGrid.ProbesZ);
        int z1 = ProbeRangeMax(cam.Z, origin.Z, spacing.Z, d, GiDebugGrid.ProbesZ);

        // Placeholder colour (stage 2 = real irradiance from the GPU atlas readback). Yellow-green probes.
        gizmos.Color = new Vector3(0.7f, 0.9f, 0.4f);
        float markerR = MathF.Min(spacing.X, MathF.Min(spacing.Y, spacing.Z)) * 0.12f;

        for (int px = x0; px <= x1; px++)
        for (int py = y0; py <= y1; py++)
        for (int pz = z0; pz <= z1; pz++) {
            Vector3 p = GiDebugGrid.ProbePosition(origin, spacing, px, py, pz);
            if ((p - cam).LengthSquared() > d2)
                continue;
            if (ShowProbeSpheres)
                gizmos.DrawWireSphere(p, markerR);
            else
                DrawCrossMarker(gizmos, p, markerR);
        }
    }

    // A cheap 3-line "+" cross marker (6 DrawLine calls' worth via 3 axes), far cheaper than a 120-line
    // wire sphere — used for the dense probe grid so the per-frame cost stays bounded.
    static void DrawCrossMarker(IGizmos gizmos, Vector3 p, float r) {
        gizmos.DrawLine(p - new Vector3(r, 0, 0), p + new Vector3(r, 0, 0));
        gizmos.DrawLine(p - new Vector3(0, r, 0), p + new Vector3(0, r, 0));
        gizmos.DrawLine(p - new Vector3(0, 0, r), p + new Vector3(0, 0, r));
    }

    // Lowest probe index on an axis whose cell can be within `dist` of the camera (clamped to [0,count-1]).
    static int ProbeRangeMin(float cam, float origin, float spacing, float dist, int count) =>
        Math.Clamp((int)MathF.Floor((cam - dist - origin) / spacing), 0, count - 1);

    static int ProbeRangeMax(float cam, float origin, float spacing, float dist, int count) =>
        Math.Clamp((int)MathF.Ceiling((cam + dist - origin) / spacing), 0, count - 1);

    // Sample reflection rays off the volume origin, coloured by the roughness-split: green = sharp
    // re-shade-at-hit (below the split threshold), amber = the blurry cache branch. A static rough sweep
    // across a few directions so you can see where reflections stay crisp vs fall to the cache.
    void DrawReflectionRays(IGizmos gizmos) {
        Vector3 o = transform.WorldPosition;
        const int rays = 8;
        const float len = 4f;
        for (int i = 0; i < rays; i++) {
            float t = i / (float)(rays - 1);
            float roughness = t;                       // sweep roughness 0..1 across the fan
            float a = t * MathF.Tau * 0.5f - MathF.PI * 0.5f;   // fan them out in a half-circle
            var dir = new Vector3(MathF.Cos(a), 0.35f, MathF.Sin(a));
            dir = Vector3.Normalize(dir) * len;
            gizmos.Color = roughness < ReflectionSharpRoughness
                ? new Vector3(0.3f, 0.95f, 0.4f)       // sharp RT reflection
                : new Vector3(0.95f, 0.7f, 0.2f);      // falls to the cache (blurry)
            gizmos.DrawRay(o, dir);
        }
    }
}

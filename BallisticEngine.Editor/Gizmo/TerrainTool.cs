using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Unity-style terrain sculpting in the Scene view. Active only while ARMED (the Inspector's terrain
// brush palette toggles Armed, like selecting Unity's terrain paint tab) so a selected terrain
// doesn't hijack normal click-to-select. While armed it raycasts the mouse against the terrain
// heightfield, draws a brush ring on the surface, and on left-drag applies the current brush via the
// engine-layer TerrainSculpt math, rebuilds the mesh live, and persists the asset on stroke end.
//
// State is static (one active stroke at a time, like ColliderHandles). The brush math, raycast, and
// save all live in the engine/asset layers — this file is purely the editor interaction + drawing.
internal static class TerrainTool {
    // ---- Brush settings (edited by the Inspector palette) ----------------------
    public static bool Armed;
    public static TerrainSculpt.Brush Brush = TerrainSculpt.Brush.Raise;
    public static float Radius = 8f;       // world units
    public static float Strength = 0.4f;   // world height delta per dab at full falloff (Raise/Lower)
    public static float TargetHeight = 0.3f; // normalized [0,1], for Flatten/Set

    // ---- Stroke state ----------------------------------------------------------
    static bool sculpting;
    static Terrain activeTerrain;
    static bool hadHit;
    static Vector3 lastHitLocal;

    // True while a sculpt stroke is in progress — joins the editor's gizmoBusy check so a stroke
    // never also fires click-to-select or starts the transform gizmo.
    public static bool IsInteracting => sculpting;

    // Draws the brush + handles a stroke for the given terrain. Returns true if the terrain changed
    // this frame (caller marks the scene dirty). viewHovered should already exclude gizmo interaction.
    public static bool Draw(Terrain terrain, IViewProjectionProvider camera,
        SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        if (terrain is null || terrain.Terrain3D is null) {
            EndStrokeIfActive();
            return false;
        }
        if (!Armed) {
            EndStrokeIfActive();
            return false;
        }

        TerrainAsset asset = terrain.Terrain3D;
        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();

        // Mouse ray -> terrain-local space (sculpt + raycast math is all local; matches the mesh).
        Matrix4 world = terrain.transform.WorldMatrix;
        if (!TryInvert(world, out Matrix4 invWorld)) {
            EndStrokeIfActive();
            return false;
        }

        SysVec2 mouse = ImGui.GetMousePos();
        GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 worldOrigin, out Vector3 worldDir);
        Vector3 localOrigin = Vector3.TransformPosition(worldOrigin, invWorld);
        Vector3 localDir = Vector3.TransformVector(worldDir, invWorld).Normalized();

        hadHit = false;
        if (viewHovered || sculpting) {
            if (TerrainSculpt.Raycast(asset, localOrigin, localDir, out Vector3 localHit)) {
                hadHit = true;
                lastHitLocal = localHit;
            }
        }

        // Draw the brush ring at the surface (world-space, follows the relief under the cursor).
        if (hadHit)
            DrawBrushRing(asset, world, vp, viewMin, viewSize, draw, lastHitLocal);

        bool changed = false;

        // Begin a stroke on left-press over a hit (and not while flying / over a popup).
        if (!sculpting && hadHit && viewHovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().WantTextInput) {
            EditorUndo.Push($"Sculpt Terrain ({Brush})");
            sculpting = true;
            activeTerrain = terrain;
        }

        // Apply the brush each frame the stroke is held and the ray still hits the surface.
        if (sculpting && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            if (hadHit && ReferenceEquals(activeTerrain, terrain)) {
                bool dabbed = TerrainSculpt.Apply(asset, Brush, lastHitLocal, Radius, Strength, TargetHeight);
                if (dabbed) {
                    asset.BumpRevision();
                    terrain.Rebuild();
                    changed = true;
                }
            }
        }
        else if (sculpting) {
            // Released (or mouse up): finalize the stroke and persist the sculpted heights.
            EndStroke();
        }

        return changed;
    }

    static void EndStroke() {
        if (activeTerrain?.Terrain3D is { } asset)
            AssetDatabase.SaveTerrain(asset);
        sculpting = false;
        activeTerrain = null;
    }

    static void EndStrokeIfActive() {
        if (sculpting)
            EndStroke();
    }

    // A ring of segments around the brush, each point lifted to the terrain surface so it hugs the
    // relief (and a small center cross). Projected through the same vp as every other gizmo.
    static void DrawBrushRing(TerrainAsset asset, Matrix4 world, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, Vector3 centerLocal) {
        const int segments = 48;
        float halfX = asset.Size.X * 0.5f, halfZ = asset.Size.Y * 0.5f;
        uint color = ImGui.GetColorU32(BrushColor());

        SysVec2 prev = default;
        bool hasPrev = false;
        for (int i = 0; i <= segments; i++) {
            float a = i / (float)segments * MathF.Tau;
            float lx = centerLocal.X + MathF.Cos(a) * Radius;
            float lz = centerLocal.Z + MathF.Sin(a) * Radius;
            float ly = TerrainSculpt.SurfaceHeight(asset, lx, lz, halfX, halfZ) + 0.05f;
            Vector3 wp = Vector3.TransformPosition(new Vector3(lx, ly, lz), world);

            if (GizmoMath.Project(wp, vp, viewMin, viewSize, out SysVec2 px)) {
                if (hasPrev)
                    draw.AddLine(prev, px, color, 1.6f);
                prev = px;
                hasPrev = true;
            }
            else {
                hasPrev = false;
            }
        }

        // Center marker.
        Vector3 cWorld = Vector3.TransformPosition(
            new Vector3(centerLocal.X,
                TerrainSculpt.SurfaceHeight(asset, centerLocal.X, centerLocal.Z, halfX, halfZ) + 0.05f,
                centerLocal.Z), world);
        if (GizmoMath.Project(cWorld, vp, viewMin, viewSize, out SysVec2 cpx))
            draw.AddCircleFilled(cpx, 2.5f, color);
    }

    static System.Numerics.Vector4 BrushColor() => Brush switch {
        TerrainSculpt.Brush.Lower => new System.Numerics.Vector4(0.95f, 0.45f, 0.30f, 0.95f),
        TerrainSculpt.Brush.Smooth => new System.Numerics.Vector4(0.45f, 0.80f, 0.95f, 0.95f),
        TerrainSculpt.Brush.Flatten => new System.Numerics.Vector4(0.85f, 0.80f, 0.40f, 0.95f),
        TerrainSculpt.Brush.Set => new System.Numerics.Vector4(0.80f, 0.55f, 0.95f, 0.95f),
        _ => new System.Numerics.Vector4(0.45f, 0.95f, 0.55f, 0.95f), // Raise
    };

    static bool TryInvert(Matrix4 m, out Matrix4 inverse) {
        if (MathF.Abs(m.Determinant) < 1e-12f) {
            inverse = Matrix4.Identity;
            return false;
        }
        inverse = Matrix4.Invert(m);
        return true;
    }
}

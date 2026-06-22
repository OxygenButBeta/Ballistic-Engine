using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class TerrainTool {
    public static bool Armed;
    public static TerrainSculpt.Brush Brush = TerrainSculpt.Brush.Raise;
    public static float Radius = 8f;
    public static float Strength = 0.4f;
    public static float TargetHeight = 0.3f;

    static bool sculpting;
    static Terrain activeTerrain;
    static bool hadHit;
    static Vector3 lastHitLocal;

    static float[] strokeBeforeHeights;

    public static bool IsInteracting => sculpting;

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

        Matrix4 world = terrain.transform.WorldMatrix;
        if (!TryInvert(world, out Matrix4 invWorld)) {
            EndStrokeIfActive();
            return false;
        }

        SysVec2 mouse = ImGui.GetMousePos();
        GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 worldOrigin, out Vector3 worldDir);
        Vector3 localOrigin = Vector3.Transform(worldOrigin, invWorld);
        Vector3 localDir = Vector3.TransformNormal(worldDir, invWorld).Normalized();

        hadHit = false;
        if (viewHovered || sculpting) {
            if (TerrainSculpt.Raycast(asset, localOrigin, localDir, out Vector3 localHit)) {
                hadHit = true;
                lastHitLocal = localHit;
            }
        }

        if (hadHit)
            DrawBrushRing(asset, world, vp, viewMin, viewSize, draw, lastHitLocal);

        bool changed = false;

        if (!sculpting && hadHit && viewHovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().WantTextInput) {
            strokeBeforeHeights = (float[])asset.Heights.Clone();
            sculpting = true;
            activeTerrain = terrain;
        }

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
            EndStroke();
        }

        return changed;
    }

    static void EndStroke() {
        Terrain terrain = activeTerrain;
        if (terrain?.Terrain3D is { } asset) {
            AssetDatabase.SaveTerrain(asset);

            float[] before = strokeBeforeHeights;
            if (before is not null && before.Length == asset.Heights.Length &&
                !HeightsEqual(before, asset.Heights)) {
                float[] after = (float[])asset.Heights.Clone();
                EditorCommands.EditAsset($"Sculpt Terrain ({Brush})",
                    applyOld: () => RestoreHeights(terrain, asset, before),
                    applyNew: () => RestoreHeights(terrain, asset, after),
                    mutate: () => { });
            }
        }
        strokeBeforeHeights = null;
        sculpting = false;
        activeTerrain = null;
    }

    static void RestoreHeights(Terrain terrain, TerrainAsset asset, float[] heights) {
        Array.Copy(heights, asset.Heights, asset.Heights.Length);
        asset.BumpRevision();
        terrain?.Rebuild();
        AssetDatabase.SaveTerrain(asset);
    }

    static bool HeightsEqual(float[] a, float[] b) {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    static void EndStrokeIfActive() {
        if (sculpting)
            EndStroke();
    }

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
            Vector3 wp = Vector3.Transform(new Vector3(lx, ly, lz), world);

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

        Vector3 cWorld = Vector3.Transform(
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
        _ => new System.Numerics.Vector4(0.45f, 0.95f, 0.55f, 0.95f),
    };

    static bool TryInvert(Matrix4 m, out Matrix4 inverse) {
        if (MathF.Abs(m.GetDeterminant()) < 1e-12f) {
            inverse = Matrix4.Identity;
            return false;
        }
        inverse = m.Inverted();
        return true;
    }
}

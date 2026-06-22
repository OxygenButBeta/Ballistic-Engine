using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Terrain))]
internal sealed class TerrainPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) => DrawTerrainBrushSection((Terrain)ctx.Behaviour);

    static void DrawTerrainBrushSection(Terrain terrain) {
        gui.Spacing();

        if (terrain.Terrain3D is null) {
            gui.TextDisabled("Assign a Terrain asset to sculpt (or create one: Assets > New Terrain).");
            TerrainTool.Armed = false;
            return;
        }

        EditorDecoration.DrawSectionHeader("Sculpt");

        bool armed = TerrainTool.Armed;
        if (gui.Checkbox("Enable Brush", ref armed))
            TerrainTool.Armed = armed;
        if (gui.IsItemHovered())
            gui.Tooltip("Left-drag in the Scene view to sculpt. While on, clicks paint instead of selecting.");

        if (!armed)
            return;

        string[] modes = ["Raise", "Lower", "Smooth", "Flatten", "Set"];
        int mode = (int)TerrainTool.Brush;
        gui.SetNextItemWidth(-1);
        if (gui.Combo("##terrainbrush", ref mode, modes))
            TerrainTool.Brush = (TerrainSculpt.Brush)mode;

        float radius = TerrainTool.Radius;
        if (gui.SliderFloat("Radius", ref radius, 0.5f, 60f, "%.1f"))
            TerrainTool.Radius = radius;

        float strength = TerrainTool.Strength;
        if (gui.SliderFloat("Strength", ref strength, 0.01f, 2f, "%.2f"))
            TerrainTool.Strength = strength;

        if (TerrainTool.Brush is TerrainSculpt.Brush.Flatten or TerrainSculpt.Brush.Set) {
            float target = TerrainTool.TargetHeight;
            if (gui.SliderFloat("Target Height", ref target, 0f, 1f, "%.2f"))
                TerrainTool.TargetHeight = target;
            if (gui.IsItemHovered())
                gui.Tooltip("Normalized height (x HeightScale) the brush levels toward.");
        }

        gui.TextDisabled("Pick Lower to dig; Smooth/Flatten to level.");
    }
}

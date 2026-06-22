using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.UI;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.Preview.ComponentPreviewGuiAccess;

namespace BallisticEngine.Editor.Inspector.Preview;

[ComponentPreview(typeof(Renderer))]
internal sealed class RendererPreview : IComponentPreview {
    public void Draw(in ComponentPreviewContext ctx) {
        var renderer = (Renderer)ctx.Behaviour;
        DrawSubMeshMaterials(renderer, ctx.Panel);
    }

    static void DrawSubMeshMaterials(Renderer renderer, InspectorPanel panel) {
        Mesh mesh = renderer.SharedMesh;
        if (mesh?.SubMeshes is not { Length: > 1 } subMeshes)
            return;

        int only = renderer.SubMeshIndex;
        if (only >= 0 && only < subMeshes.Length) {
            EditorDecoration.DrawSectionHeader("Material");
            DrawSlotRow(renderer, panel, subMeshes[only], only);
            return;
        }

        EditorDecoration.DrawSectionHeader($"Materials ({subMeshes.Length})");
        const int ScrollThreshold = 8;
        bool scroll = subMeshes.Length > ScrollThreshold;
        if (scroll) {
            float rowH = gui.FrameHeightWithSpacing + gui.TextLineHeightWithSpacing;
            gui.BeginChild("##submatscroll", new SysVec2(0, Math.Min(10, subMeshes.Length) * rowH),
                border: true);
        }
        for (var i = 0; i < subMeshes.Length; i++)
            DrawSlotRow(renderer, panel, subMeshes[i], i);
        if (scroll)
            gui.EndChild();
    }

    static void DrawSlotRow(Renderer renderer, InspectorPanel panel, SubMeshData sub, int i) {
        gui.PushId(i);
        string label = string.IsNullOrEmpty(sub.Name) ? $"Submesh {i}" : sub.Name;
        gui.TextUnformatted(label);
        if (gui.IsItemHovered() && !string.IsNullOrEmpty(sub.MaterialRef))
            gui.Tooltip($"{label}\nBaked: {sub.MaterialRef}");

        Material baked = string.IsNullOrEmpty(sub.MaterialRef) ? null
            : AssetDatabase.LoadRef<Material>(sub.MaterialRef);
        panel.DrawSubMeshMaterialSlot(renderer, i, baked);
        gui.PopId();
    }
}

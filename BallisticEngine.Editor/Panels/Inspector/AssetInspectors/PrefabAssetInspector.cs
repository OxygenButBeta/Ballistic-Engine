using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".prefab")]
internal sealed class PrefabAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawPrefabInspector(ctx.Panel, ctx.Path);

    static void DrawPrefabInspector(InspectorPanel panel, string path) {
        PrefabAsset prefab = AssetDatabase.Load<PrefabAsset>(path);
        if (prefab is null) {
            gui.TextDisabled("Could not load prefab.");
            return;
        }

        if (gui.Button($"{EditorIcons.Add}  Instantiate into Scene", new SysVec2(-1, 0))) {
            EditorCommands.Structural("Instantiate Prefab", () => {
                Entity root = prefab.Instantiate();
                if (root is not null)
                    panel.Select(root);
                panel.MarkViewportDirty();
            });
        }

        gui.Spacing();
        gui.TextDisabled($"Contents ({prefab.Entities.Count} entit{(prefab.Entities.Count == 1 ? "y" : "ies")})");
        gui.Separator();
        foreach (var doc in prefab.Entities) {
            float indent = doc.Transform?.Parent is null ? 0 : 16f;
            if (indent > 0) gui.Indent(indent);
            gui.TextUnformatted($"{EditorIcons.Package}  {doc.Name}");
            if (indent > 0) gui.Unindent(indent);
        }
    }
}

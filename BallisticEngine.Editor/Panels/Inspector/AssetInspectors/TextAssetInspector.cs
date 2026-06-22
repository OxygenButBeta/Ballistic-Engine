using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".shader")]
[AssetInspector(".glsl")]
[AssetInspector(".cubemap")]
internal sealed class TextAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawTextAssetHint(ctx.Path);

    static void DrawTextAssetHint(string path) {
        gui.TextDisabled("Edit this file in a text editor.");
        if (gui.Button($"{EditorIcons.FolderOpen}  Show in Explorer", new SysVec2(-1, 0)))
            System.Diagnostics.Process.Start("explorer.exe",
                $"/select,\"{AssetDatabase.Project.ResolveAbsolute(path)}\"");
    }
}

using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".scene")]
internal sealed class SceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawSceneAssetActions(ctx.Path);

    static void DrawSceneAssetActions(string path) {
        if (gui.Button($"{EditorIcons.Play}  Open Scene", new SysVec2(-1, 0)))
            SceneCommands.Open(path);
    }
}

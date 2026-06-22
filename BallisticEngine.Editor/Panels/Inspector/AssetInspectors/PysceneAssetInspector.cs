using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".pyscene")]
internal sealed class PysceneAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) =>
        gui.TextWrapped("Falcor scene. On import it generates a sibling .scene you can open.");
}

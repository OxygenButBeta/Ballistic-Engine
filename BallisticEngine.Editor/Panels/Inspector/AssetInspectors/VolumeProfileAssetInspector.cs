using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;
using static BallisticEngine.Editor.Inspector.AssetInspectors.AssetInspectorGuiAccess;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

[AssetInspector(".volume")]
internal sealed class VolumeProfileAssetInspector : IAssetInspector {
    public void Draw(in AssetInspectorContext ctx) => DrawVolumeProfileAsset(ctx.Guid);

    static void DrawVolumeProfileAsset(Guid guid) {
        var profile = AssetDatabase.Load<VolumeProfile>(guid);
        if (profile is null) {
            gui.TextDisabled("Unreadable volume profile.");
            return;
        }

        if (VolumeProfileEditor.Draw(profile))
            VolumeProfileEditor.SaveToAsset(profile);
    }
}
